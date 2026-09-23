# PBR 间接光照 (IBL) 参考：间接漫反射部分

本文只讨论间接光照中的**漫反射（Indirect Diffuse）**。间接高光（Prefiltered Environment Map + BRDF LUT 的 Split-Sum 方案）暂不涉及。

球谐函数更全面的数学背景（复数/实数 SH 的区别、卷积定理、Light Probe、失效场景等）见配套文档 `SphericalHarmonics_Reference.md`；本文第四节按照"什么是球谐 → 系数怎么求 → 离散化 → 代码实现"的顺序，给出本项目球谐烘焙代码所需要的完整推导，两份文档配合阅读。

---

## 一、问题背景

直接光只有一个明确的入射方向 $\mathbf{L}$，而环境光来自物体周围整个半球方向的辐射总和，本质上是一个积分：

$$L_o(p, w_o) = \int_{\Omega} f_r(p, w_i, w_o)\, L_i(p, w_i)\, (n \cdot w_i)\, dw_i$$

这个积分无法在实时渲染的每个像素里逐光线累加求解，必须在离线阶段预先计算好结果，运行时只做查表或代入解析式。间接漫反射的预计算存在两种常见实现方式：

1. 把积分结果直接存成一张**辐照度贴图（Irradiance Cubemap）**，运行时用法线采样这张贴图。
2. 把这张辐照度贴图（或原始积分本身）投影到**球谐函数（Spherical Harmonics, SH）**的系数上，运行时用法线代入基函数解析式求和，不需要任何纹理采样。

下面分别说明两种方法的原理、离线计算过程和运行时用法。

---

## 二、间接漫反射的物理基础

漫反射表面把入射光均匀地向各个方向散射，观察结果与视角无关。对 Lambert 漫反射模型，BRDF 项 $f_r$ 是常数 $\dfrac{Albedo}{\pi}$，可以从积分中提出：

$$L_o = \frac{Albedo}{\pi} \int_{\Omega} L_i(p, w_i)\, (n \cdot w_i)\, dw_i$$

把积分部分单独定义为**辐照度（Irradiance）**：

$$E(n) = \int_{\Omega} L_i(w_i)\, (n \cdot w_i)\, dw_i$$

则最终漫反射颜色为 $Albedo \times \dfrac{E(n)}{\pi}$。可以看到，$E(n)$ 只依赖法线方向 $n$，与视角无关，也与具体材质无关（Albedo 是后乘的）——这正是它可以被预先算好、和材质解耦存储的原因。

积分坐标系换成球坐标（方位角 $\phi$、天顶角 $\theta$，以法线为极轴）后：

$$E(n) = \int_{0}^{2\pi}\int_{0}^{\frac{\pi}{2}} L_i(\phi,\theta)\,\cos\theta \sin\theta \, d\theta\, d\phi$$

其中 $\sin\theta$ 的来源见附录 B，$\cos\theta$ 是 $n\cdot w_i$。

以下两种方法都是在求解这个 $E(n)$。

---

## 三、方法一：Irradiance Cubemap 直接采样

### 3.1 离线烘焙：半球卷积

对 Cubemap 的每一个像素（对应一个方向 $n$），以 $n$ 为极轴建立局部切线空间（tangent/binormal/normal），在切线空间内对 $\phi,\theta$ 做双重循环采样原始环境贴图（Skybox），按 $\cos\theta\sin\theta$ 加权累加，最后除以采样次数归一化。这是附录 C 的黎曼和近似的直接实现，对应 `IBLBakerCS.compute` 的 `CSMainDiffuse`：

```hlsl
float3 tangent   = ...;              // 以法线 dir 为极轴构建局部坐标系
float3 binormal  = normalize(cross(dir, tangent));
float3 irradiance = float3(0,0,0);

for (phi = 0; phi < 2*PI; phi += sampleDelta)
{
    for (theta = 0; theta < 0.5*PI; theta += sampleDelta)
    {
        float3 dirTS = float3(sin(theta)*cos(phi), sin(theta)*sin(phi), cos(theta));
        float3 dirWS = TangentToWorld(dirTS, tangent, binormal, dir);
        irradiance += Skybox.Sample(dirWS) * cos(theta) * sin(theta);
    }
}
irradiance *= PI / (N1 * N2);   // 见附录 C，PI 来自 Δθ·Δφ 中未被采样次数抵消的部分
```

这一步对 Cubemap 的每一个像素都要重复一次完整的双重循环，计算量集中在离线阶段，运行时不受影响。烘焙结果是一张分辨率不高、内容非常模糊的 Cubemap（因为 Irradiance 本身是低频信号），保存为 `irradianceMap`（对应 `IBLBaker.cs` 的 `PrefillterDiffuseCubemap`）。

### 3.2 运行时采样

运行时只需要按法线方向采样这张贴图即可：

```hlsl
float3 irradiance = SAMPLE_TEXTURECUBE(_IrradianceMap, sampler_IrradianceMap, normalWS);
float3 diffuse = albedo * kd * irradiance;
```

对应 `PBRIndirectLighting.hlsl` 中 `useSH = false` 的分支。这种方式每个像素只需要一次纹理采样，实现简单，但仍然占用一张 Cubemap 的显存和一次采样带宽。

---

## 四、方法二：球谐投影与重构

方法一得到的 Irradiance Map 本身是极度低频的信号（几乎没有高频细节）。既然如此，可以把这张图继续"压缩"：不再存一整张贴图，而是找一组数量很少的系数，能在运行时用一个解析式重新"拼"出这张图的近似值。球谐函数（Spherical Harmonics, SH）就是干这件事的工具。本节按"是什么 → 系数怎么求 → 怎么落地成代码"的顺序展开。

### 4.1 引入：从正交函数说起——什么是球谐函数

回忆傅里叶级数：任何一个周期信号 $f(t)$，都可以写成一堆正弦、余弦波（基函数）的加权和 $f(t)=\sum_n a_n\cos(nt)+b_n\sin(nt)$。这件事能成立、而且振幅系数 $a_n,b_n$ 能被唯一地"解出来"，靠的是三角函数族 $\{\cos(nt),\sin(nt)\}$ 的一个特殊性质——**正交性**：任取两个不同频率的基函数，把它们逐点相乘后在一个周期内积分，结果恒为 0；只有同一个基函数跟自己相乘再积分，结果才不为 0。这个"不同频率互不干扰"的性质，保证了每个频率的振幅可以被独立地"抠出来"（具体怎么抠，见 4.5 节；这里先接受这个结论）。

球谐函数就是把同样的"正交基分解"思想，从一维的时间轴搬到二维的**球面**上：球面上任意一个足够光滑（不太高频）的函数，都可以写成一组彼此正交的固定"基函数" $Y_i$ 的加权和。这组基函数具体长什么样、一共有几个，是数学上早就求解好的固定公式（本质是拉普拉斯方程在球坐标系下角度部分的解，详见 `SphericalHarmonics_Reference.md` 第 1 节），不需要我们自己构造。

一句话类比：**球谐函数之于球面上的函数，就相当于三角函数之于周期信号**——都是一套"万能积木"，把一个复杂函数拆成有限个数字（系数）。

### 4.2 一般式：用球谐基函数的线性组合表达一个球面函数

设 $f(\mathbf{d})$ 是定义在单位球面 $S^2$ 上的任意一个函数，自变量 $\mathbf{d}=(x,y,z)$ 是单位方向向量（$x^2+y^2+z^2=1$）。球谐展开的一般式是：

$$f(\mathbf{d}) \approx \sum_{i=0}^{N-1} c_i\, Y_i(\mathbf{d})$$

各符号含义：

- $Y_i(\mathbf{d})$：第 $i$ 个球谐**基函数**，公式固定、已知（4.3 节给出显式表达式）。下标 $i$ 把传统的双下标 $(l,m)$（$l$ 是**阶**，$m$ 是**次**）按固定顺序编号，方便写成数组；
- $c_i$：第 $i$ 个基函数对应的**组合系数**（待求量；如果 $f$ 是彩色的，则 R/G/B 通道各算一套 $c_i$）；
- $N$：保留的基函数个数，$N=9$ 就是只保留 $l=0,1,2$ 三阶（俗称"3 阶球谐"）；
- 用"$\approx$"而不是"$=$"：$f$ 理论上需要无穷多阶才能精确还原，只取前 $N$ 项必然丢弃 $l\ge3$ 的高阶分量，这是一种截断近似（第 4.8 节讨论其影响）。

本项目里，要展开的 $f$ 就是 3.1 节烘焙出来的 Irradiance Map，即 $E(\mathbf{d})$——把它看成定义在**整个球面** $S^2$ 上（而不再局限于某一法线对应的半球 $\Omega$）的普通函数，$\mathbf{d}$ 这里就是查询用的法线方向，对应第二节里的 $n$。

这是一个"基函数已知、系数未知"的待定式。下面先给出 $Y_i$ 的具体形式，再讲怎么求 $c_i$。

### 4.3 前三阶的 9 个球谐基函数

$l\le2$ 一共有 $\sum_{l=0}^{2}(2l+1)=1+3+5=9$ 个基函数，直角坐标下的显式公式如下（$\mathbf{d}=(x,y,z)$，$x^2+y^2+z^2=1$）：

| 索引 $i$ | $(l,m)$ | 基函数 $Y_i(x,y,z)$ | 常量 |
|---|---|---|---|
| 0 | (0,0) | $1$ | `0.282095` |
| 1 | (1,-1) | $y$ | `0.488603` |
| 2 | (1,0) | $z$ | `0.488603` |
| 3 | (1,1) | $x$ | `0.488603` |
| 4 | (2,-2) | $xy$ | `1.092548` |
| 5 | (2,-1) | $yz$ | `1.092548` |
| 6 | (2,0) | $2z^2-x^2-y^2$ | `0.315392` |
| 7 | (2,1) | $xz$ | `1.092548` |
| 8 | (2,2) | $x^2-y^2$ | `0.546274` |

完整的基函数是"常量 × 表达式"，常量的作用是让每个基函数满足正交**归一**（$\int Y_i Y_i\,d\omega=1$），常量的具体来源见 `SphericalHarmonics_Reference.md` 附录 C，此处不重复推导。

> 索引 6（$l=2,m=0$）常见的另一种写法是 $3z^2-1$（`SphericalHarmonics_Reference.md` 用的就是这种写法）。因为 $\mathbf{d}$ 是单位向量，$x^2+y^2+z^2=1$，代入即得 $3z^2-1=3z^2-(x^2+y^2+z^2)=2z^2-x^2-y^2$——两种写法完全等价，本项目代码用的是后一种。

### 4.4 基函数的内积性质：正交归一性

在球面上定义两个函数的"内积"为逐方向相乘后对整个球面积分：

$$\langle f, g\rangle \;\triangleq\; \int_{S^2} f(\mathbf{d})\, g(\mathbf{d})\, d\omega$$

球谐基函数最关键的性质，就是这组基相互**正交**且已经**归一化**：

$$\langle Y_i, Y_j\rangle = \int_{S^2} Y_i(\mathbf{d})\, Y_j(\mathbf{d})\, d\omega = \delta_{ij} = \begin{cases}1, & i=j\\[2pt] 0, & i\neq j\end{cases}$$

直觉解释：$Y_i$ 和 $Y_j$（$i\ne j$）是两种不同形状的"鼓包"，一个的正贡献恰好被另一个的正负交替部分抵消，逐点相乘后积分下来正负相消为 0；而 $Y_i$ 自己和自己相乘处处非负，积分自然不为 0（4.3 节表格里的常量，正是专门调出来让这个自内积精确等于 1 的）。这条性质是下一节"反解系数"的唯一依据。

### 4.5 如何求出系数：把已知的 $f(\mathbf{d})$ 投影到球谐基上

现在反过来：已知函数 $f(\mathbf{d})$（本项目里就是 $E(\mathbf{d})$），要求出 4.2 节展开式里的系数 $c_i$。

做法：给展开式两边同时乘上某个基函数 $Y_k(\mathbf{d})$，再对整个球面积分：

$$\int_{S^2} f(\mathbf{d})\,Y_k(\mathbf{d})\,d\omega
= \int_{S^2}\Big(\sum_{i=0}^{N-1} c_i\, Y_i(\mathbf{d})\Big) Y_k(\mathbf{d})\, d\omega
= \sum_{i=0}^{N-1} c_i \underbrace{\int_{S^2} Y_i(\mathbf{d})\,Y_k(\mathbf{d})\,d\omega}_{=\ \delta_{ik}\ (4.4\ \text{节})}
= c_k$$

等号右边的求和里，因为 4.4 节的正交归一性，除了 $i=k$ 那一项（值为 $c_k\times 1$）之外，其余各项都乘以 $0$ 消失，整条等式直接化简为 $c_k$——这就是"投影"（也叫编码）能够正确、且互不干扰地把每个系数解出来的全部原因。把哑标 $k$ 换回 $i$，得到系数公式：

$$c_i = \int_{S^2} f(\mathbf{d})\, Y_i(\mathbf{d})\, d\omega$$

代入本项目的场景（$f=E$，即 Irradiance Map）：

$$c_i = \int_{S^2} E(\mathbf{d})\, Y_i(\mathbf{d})\, d\omega \qquad (i=0,1,\dots,8)$$

这就是接下来要在代码里算出来的目标：RGB 三个通道各算一套，一共 27 个数。

### 4.6 代码实现前必须解决的问题：积分的离散化

上面 $c_i=\int_{S^2}\cdots d\omega$ 里的积分号是对**连续**球面的积分，但计算机没有"连续球面"这种数据结构可以直接拿来积分，必须先把它转换成一个能在有限步内跑完的求和。这一节依次回答：**为什么**要离散化、**怎么**离散化。

#### 4.6.1 为什么：$E(\mathbf{d})$ 现在只以 Irradiance Map（离散 texel）的形式存在

$E(\mathbf{d})$ 不是一个手上握有解析表达式的函数——它是 3.1 节里用黎曼和暴力烘焙出来的一张 Cubemap，只在有限个离散的 texel 位置上有数值，texel 之间没有定义。换句话说，"读取 $E(\mathbf{d})$"这件事，代码里实际能做的只有"采样某个 texel 的颜色"，不可能对连续变化的 $\mathbf{d}$ 做解析积分。所以 4.5 节的系数公式必须被改写成一个对**所有 texel** 的求和，才能配合已经烘焙好的 Irradiance Map 算出来。

#### 4.6.2 怎么做：把"对立体角积分"改写成"对 texel 求和"

积分号本质是"无限细分、加权累加"；离散化就是把"无限细分"换成"按 texel 网格有限细分"：把整张 Cubemap 的所有 texel，看作球面 $S^2$ 上的一个有限划分，每个 texel 占据一小块立体角 $\Delta\Omega(\text{texel})$，于是：

$$c_i \approx \sum_{\text{texel}} E(\text{texel})\; Y_i\big(\text{dir}(\text{texel})\big)\; \Delta\Omega(\text{texel})$$

- $E(\text{texel})$：直接读取该 texel 存储的颜色（3.1 节烘焙的结果）；
- $\text{dir}(\text{texel})$：该 texel 中心对应的单位方向向量，代入 $Y_i$ 求值；
- $\Delta\Omega(\text{texel})$：该 texel 在球面上占据的立体角，充当"离散版的 $d\omega$"，即这个 texel 在求和里应占的权重。

最后一个问题：$\Delta\Omega(\text{texel})$ 该怎么精确算？Cubemap 把球面"套"在一个正方体的 6 个平面上，同样大小的正方形 texel，离面心近的立体角大、离棱角近的立体角小（可以类比世界地图的墨卡托投影：纬线附近被拉伸的程度不一样）。**不能**直接用 $\dfrac{4\pi}{6\times\text{resolution}^2}$（总立体角除以总 texel 数）当作每个 texel 的权重，否则边缘、角落的 texel 会被系统性地高估。下一节推导精确值。

#### 4.6.3 精确计算 $\Delta\Omega(\text{texel})$：牛顿-莱布尼兹公式推导

**第一步：写出立体角微元 $d\omega$。**

以 Cubemap 的 $z=1$ 那个面为例，面上一点的平面坐标记为 $(x,y)\in[-1,1]^2$（$z=1$ 固定）。该点到原点的距离 $r=\sqrt{x^2+y^2+1}$，从原点看向该点的方向与该面法线 $(0,0,1)$ 的夹角余弦为：

$$\cos\theta = \frac{(x,y,1)\cdot(0,0,1)}{r} = \frac{1}{r}$$

立体角微元的定义是"面积微元在垂直于视线方向上的投影，除以距离平方"：

$$d\omega = \frac{dA\,\cos\theta}{r^2} = \frac{dx\,dy}{r^3} = \frac{dx\,dy}{(x^2+y^2+1)^{3/2}}$$

**第二步：要求的是这个微元在矩形 texel 范围 $[x_0,x_1]\times[y_0,y_1]$ 上的二重定积分。**

$$\Delta\Omega = \int_{y_0}^{y_1}\int_{x_0}^{x_1} \frac{dx\,dy}{(x^2+y^2+1)^{3/2}}$$

逐 texel 做数值积分太慢——我们想要一个能直接代入端点求值的**闭式解**，这就要用到牛顿-莱布尼兹公式（微积分基本定理）。

**第三步：牛顿-莱布尼兹公式，及其在二重积分上的推广。**

一元形式：若 $\Phi'(x)=\varphi(x)$，则

$$\int_a^b \varphi(x)\,dx = \Phi(b) - \Phi(a)$$

对于二重积分，如果被积函数恰好是某个二元函数 $\Phi(x,y)$ 的混合偏导数，即 $\dfrac{\partial^2 \Phi}{\partial x\,\partial y} = \varphi(x,y)$，可以把牛顿-莱布尼兹公式连续用两次（先对 $x$ 固定 $y$、再对 $y$）：

$$\int_{y_0}^{y_1}\!\!\int_{x_0}^{x_1} \frac{\partial^2 \Phi}{\partial x\,\partial y}\, dx\,dy
= \int_{y_0}^{y_1}\Big[\frac{\partial \Phi}{\partial y}(x_1,y) - \frac{\partial \Phi}{\partial y}(x_0,y)\Big] dy
= \big[\Phi(x_1,y_1)-\Phi(x_0,y_1)\big] - \big[\Phi(x_1,y_0)-\Phi(x_0,y_0)\big]$$

即：

$$\int_{y_0}^{y_1}\!\!\int_{x_0}^{x_1} \frac{\partial^2 \Phi}{\partial x\partial y}\,dx\,dy = \Phi(x_1,y_1) - \Phi(x_0,y_1) - \Phi(x_1,y_0) + \Phi(x_0,y_0)$$

这就是"角点加减法"（inclusion–exclusion）的来源：矩形四个角各代一次 $\Phi$，对角线同号、邻边异号——本质就是牛顿-莱布尼兹公式在两个方向上各用一次的结果。

**第四步：找到满足 $\dfrac{\partial^2\Phi}{\partial x\partial y}=\dfrac{1}{(x^2+y^2+1)^{3/2}}$ 的 $\Phi(x,y)$。**

先固定 $x$，对 $y$ 积分一次（标准积分表结果 $\displaystyle\int \frac{dy}{(y^2+a^2)^{3/2}} = \frac{y}{a^2\sqrt{y^2+a^2}}$，这里 $a^2=x^2+1$）：

$$G(x,y) \triangleq \frac{\partial \Phi}{\partial x} = \int \frac{dy}{(x^2+y^2+1)^{3/2}} = \frac{y}{(x^2+1)\sqrt{x^2+y^2+1}}$$

再验证

$$\Phi(x,y) = \arctan\!\left(\frac{xy}{\sqrt{x^2+y^2+1}}\right)$$

确实满足 $\dfrac{\partial \Phi}{\partial x} = G(x,y)$：记 $u=\dfrac{xy}{\sqrt{x^2+y^2+1}}$，由 $\arctan$ 求导公式 $\dfrac{\partial \Phi}{\partial x}=\dfrac{1}{1+u^2}\dfrac{\partial u}{\partial x}$，分别算出

$$\frac{\partial u}{\partial x} = \frac{y(y^2+1)}{(x^2+y^2+1)^{3/2}},\qquad
1+u^2 = \frac{(x^2+1)(y^2+1)}{x^2+y^2+1}$$

两者相除，$(x^2+y^2+1)$ 与 $(y^2+1)$ 都能约掉，剩下

$$\frac{\partial \Phi}{\partial x} = \frac{y}{(x^2+1)\sqrt{x^2+y^2+1}} = G(x,y)\ \checkmark$$

对 $G(x,y)$ 再求一次 $\partial/\partial y$，同样约分后可验证结果精确等于第一步的被积函数 $\dfrac{1}{(x^2+y^2+1)^{3/2}}$。这说明 $\Phi$ 正是要找的二元原函数。

**第五步：代入牛顿-莱布尼兹公式，得到 texel 立体角的闭式解。**

$$\Delta\Omega(x_0,x_1,y_0,y_1) = \Phi(x_1,y_1) - \Phi(x_0,y_1) - \Phi(x_1,y_0) + \Phi(x_0,y_0)$$

其中 $\Phi(x,y)=\arctan\!\left(\dfrac{xy}{\sqrt{x^2+y^2+1}}\right)$。至此，"每个 texel 占多大立体角"被转化成了 4 次 `atan2` 求值——这正是 4.7 节代码里 `AreaElement` 的来历。

### 4.7 代码实现

#### 4.7.1 投影阶段：离线烘焙 9 个系数

对 Cubemap 的每个面、每个 texel 循环一次，按 4.6.2 的求和公式累加（对应 `SHBaker.cs` 的 `BakeSH`）。先是 4.6.3 推导结果对应的立体角权重：

```csharp
private float AreaElement(float x, float y)
{
    // 对应 4.6.3 第五步的 Φ(x,y) = arctan(xy / sqrt(x^2+y^2+1))
    return Mathf.Atan2(x * y, Mathf.Sqrt(x * x + y * y + 1));
}

private float GetDeltaSolidAngle(float u, float v, float resolution)
{
    float delta = 1f / resolution;   // texel 半宽在 [-1,1] 坐标系下的宽度
    u = u * 2f - 1;                  // 把 texel 中心从 [0,1] 映射到 [-1,1]
    v = v * 2f - 1;
    float x0 = u - delta, x1 = u + delta;
    float y0 = v - delta, y1 = v + delta;
    // 对应 4.6.3 第五步：Φ(x1,y1) - Φ(x0,y1) - Φ(x1,y0) + Φ(x0,y0)
    return AreaElement(x1, y1) - AreaElement(x0, y1) - AreaElement(x1, y0) + AreaElement(x0, y0);
}
```

> 两个容易出错的细节：`u,v` 原本在 `[0,1]`，必须先映射到 `[-1,1]`（`AreaElement` 是基于 $[-1,1]$ 面坐标系推导的）；映射 $x=2u-1$ 的导数是 2，所以 texel 半宽从 `[0,1]` 下的 $\dfrac{0.5}{\text{resolution}}$，映射到 `[-1,1]` 后要相应放大 2 倍，变成 $\dfrac{1}{\text{resolution}}$，否则算出来的立体角会整体偏小。

texel 中心方向 `dir(texel)`：取 texel 的 UV 中心 `(索引+0.5)/resolution`，代入该面的方向映射公式（6 个面各自的映射规则）。这套映射必须与烘焙 Irradiance Map 时使用的映射完全一致——如果两者不一致（例如某个面坐标轴符号写反），就会把某个 texel 的颜色错误地关联到另一个方向上，导致该面附近的球谐系数出现方向性错误：

```csharp
case 0://+x
    direction.x = 1;
    direction.y = v * -2f + 1;
    direction.z = u * -2f + 1;
    break;
case 1://-x
    direction.x = -1;
    direction.y = v * -2f + 1;
    direction.z = u *  2f - 1;
    break;
case 2://+y
    direction.y = 1;
    direction.x = u * 2f - 1;
    direction.z = v * 2f - 1;
    break;
case 3://-y
    direction.y = -1;
    direction.x = u *  2f - 1;
    direction.z = v * -2f + 1;
    break;
case 4://+z
    direction.z = 1;
    direction.x = u * 2f - 1;
    direction.y = v * -2f + 1;
    break;
case 5://-z
    direction.z = -1;
    direction.x = u * -2f + 1;
    direction.y = v * -2f + 1;
    break;
```

汇总起来，投影主循环就是 4.6.2 求和式 $c_i\approx\sum_{\text{texel}}E(\text{texel})\,Y_i(\text{dir})\,\Delta\Omega$ 的直接实现：

```csharp
float[] coeffR = new float[9], coeffG = new float[9], coeffB = new float[9];

for (int face = 0; face < 6; face++)
for (int v = 0; v < resolution; v++)
for (int u = 0; u < resolution; u++)
{
    float uu = (u + 0.5f) / resolution, vv = (v + 0.5f) / resolution;
    Vector3 dir = GetDirectionFromUV(face, uu, vv);       // 上面的六面映射表
    Color   E   = irradianceMap.GetPixel(face, u, v);     // 读取 3.1 节烘焙好的 Irradiance Map
    float   dOmega = GetDeltaSolidAngle(uu, vv, resolution);

    float[] Y = SHBasis9(dir);   // 见 4.3 节的表

    for (int i = 0; i < 9; i++)
    {
        coeffR[i] += E.r * Y[i] * dOmega;
        coeffG[i] += E.g * Y[i] * dOmega;
        coeffB[i] += E.b * Y[i] * dOmega;
    }
}
```

投影完成后，把 9 个系数（RGB）通过 `Shader.SetGlobalVectorArray` 传给 shader。

#### 4.7.2 重构阶段：运行时求值

Shader 侧只需要把世界空间法线代入 4.3 节的 9 个基函数，和对应系数相乘求和——这就是 4.2 节一般式 $f(\mathbf{d})\approx\sum_i c_i Y_i(\mathbf{d})$ 的直接实现：

```hlsl
float3 SampleBakeSH(float3 normalWS)
{
    float x = normalWS.x, y = normalWS.y, z = normalWS.z;
    float3 res = 0;
    res += _SHCoefficients[0] * _SH9ConstantPart[0];
    res += _SHCoefficients[1] * _SH9ConstantPart[1] * y;
    res += _SHCoefficients[2] * _SH9ConstantPart[2] * z;
    res += _SHCoefficients[3] * _SH9ConstantPart[3] * x;
    res += _SHCoefficients[4] * _SH9ConstantPart[4] * x * y;
    res += _SHCoefficients[5] * _SH9ConstantPart[5] * y * z;
    res += _SHCoefficients[6] * _SH9ConstantPart[6] * (2*z*z - x*x - y*y);
    res += _SHCoefficients[7] * _SH9ConstantPart[7] * x * z;
    res += _SHCoefficients[8] * _SH9ConstantPart[8] * (x*x - y*y);
    return res;
}
```

重构出的 `SampleBakeSH(n)` 结果就是 $E(\mathbf{n})$ 的近似值，后续用法与方法一相同：

```hlsl
float3 diffuse = albedo * kd * SampleBakeSH(normalWS);
```

两种路径在 `PBRIndirectLighting.hlsl` 中统一为一个函数，用布尔参数切换：

```hlsl
float3 BRDF_IndirectLighting(float3 albedo, float3 normalWS, float metallic, float NdotV, bool useSH = true)
{
    normalWS = normalize(normalWS);
    float3 ambient_diffuse = useSH
        ? SampleBakeSH(normalWS)
        : SAMPLE_TEXTURECUBE(_IrradianceMap, sampler_IrradianceMap, normalWS);

    float3 F  = Fresnel(albedo, metallic, NdotV);
    float3 kd = (1 - F) * (1 - metallic);
    return albedo * kd * ambient_diffuse;
}
```

`kd` 的作用是让间接漫反射也遵循能量守恒：被 Fresnel 反射走的能量（`F`）以及金属表面（不存在漫反射）都要从漫反射项里扣除。

### 4.8 实现中需要注意的问题

1. **法线必须是世界空间。** 球谐系数是针对 Cubemap 的世界空间方向烘焙的（4.7.1 里 `dir` 就是世界空间方向），4.3 节基函数里的 $x,y,z$ 对应的也是世界坐标轴。如果传入模型空间法线，物体旋转时法线会跟着模型一起转，但环境光本身固定在世界空间里，结果会变成环境光"贴"在物体表面一起旋转，而不是正确反映周围环境。
2. **投影和烘焙必须用同一套方向映射。** 4.7.1 中 `SHBaker.GetDirectionFromUV` 与烘焙 Irradiance Map 时 `IBLBakerCS.compute` 里的 `DirectionFromCubemapTexel` 必须是同一套面到方向的映射公式，理由见 4.7.1。
3. **立体角权重必须精确，不能用平均值代替。** 4.6.2 已经说明：texel 在球面上对应的立体角并不均匀，靠近面中心大、靠近棱角小；如果偷懒用 $\dfrac{4\pi}{6\times\text{resolution}^2}$ 当权重，相当于系统性地高估了角落 texel 的贡献，烘焙结果会有方向性偏差。
4. **这是有损压缩，只保留低频。** $N=9$ 只对应 $l\le2$，重构出来的 $E(\mathbf{n})$ 只是原 Irradiance Map 的低频近似——好在 Irradiance 本身就是低频信号（3.1 节烘焙出来的图肉眼看已经很模糊），近似造成的误差通常很小；但如果把同一套方法用在高频信号上（比如未做卷积模糊的原始 Skybox、或镜面反射），重构结果会明显失真甚至出现负值振铃，更完整的讨论见 `SphericalHarmonics_Reference.md` 第 8 节。
5. **常量不要乘重或漏乘。** 4.3 节表格里的"常量"已经是基函数定义的一部分，`_SH9ConstantPart` 在重构阶段必须原样乘一次；如果烘焙阶段的 `SHBasis9` 与运行时的 `_SH9ConstantPart` 用的不是同一套常量（例如一个归一化、一个未归一化），结果会整体偏亮或偏暗一个固定倍数。

---

## 五、两种方法的比较

| | 方法一：Irradiance Cubemap | 方法二：球谐 |
|---|---|---|
| 存储 | 一张模糊 Cubemap | 27 个 float（9 个 RGB 系数） |
| 运行时开销 | 1 次纹理采样 | 0 次采样，9 条 mad |
| 离线计算 | 每 texel 做一次半球双重积分 | 在方法一结果基础上再做一次投影积分 |
| 精度 | 保留了 Cubemap 分辨率允许的方向细节 | 只能表达极低频信号（前 3 阶），细节和方向性都比方法一弱 |
| 适用场景 | 对精度要求略高、可接受一次采样开销 | 移动端 / 对带宽敏感、系数可以方便地作为 uniform 传递 |

方法二依赖方法一的产出（先算出 Irradiance Map，再对它投影），也可以跳过中间的 Cubemap，直接对原始环境贴图做球谐投影；本项目采用前者，分两步实现（`IBLBaker.cs` 的两个按钮分别对应这两步）。

---

# 附录：数学推导

## 附录 A：积分符号 $\int$ 在代码里对应什么

$$L_o(p, w_o) = \int_{\Omega} L_i(p, w_i) \cdot f_r \cdot (n \cdot w_i)\, dw_i$$

- $\int_{\Omega} \dots dw_i$：沿半球 $\Omega$ 的所有方向遍历求和，对应代码里的 `for (每个方向 i)`。
- $L_i(p, w_i)$：$w_i$ 方向上的入射光颜色/亮度（采样天空盒得到）。
- $f_r$：BRDF，材质对光的响应。
- $n \cdot w_i$：法线与入射方向的夹角衰减，即 `NdotL`，也写作 $\cos\theta$。

## 附录 B：立体角 $d\omega$ 与 $\sin\theta$ 的来源

把半球方向换成球坐标（方位角 $\phi$、天顶角 $\theta$）后：

$$L_o = \int_{0}^{2\pi} \int_{0}^{\frac{\pi}{2}} L_i \cdot f_r \cdot \cos\theta \cdot \sin\theta \, d\theta\, d\phi$$

$\sin\theta$ 的来源：球面上取一个由 $d\theta$、$d\phi$ 划出的小网格，网格的高是 $d\theta$，宽是 $\sin\theta\, d\phi$（纬线圈的周长随 $\theta$ 从赤道到极点递减，递减比例正好是 $\sin\theta$）。所以这块小网格的面积（对应立体角 $d\omega$）为：

$$d\omega = \sin\theta\, d\theta\, d\phi$$

这个 $\sin\theta$ 与代表光照衰减的 $\cos\theta$（`NdotL`）是两个独立的因子，只是恰好出现在同一个积分里相乘。

## 附录 C：黎曼和——把积分写成循环

### C.1 方法一：半球双重循环（对应 `IBLBakerCS.compute`）

以纯漫反射环境光 $E$ 为例：

$$E = \int_{0}^{2\pi} \int_{0}^{\frac{\pi}{2}} L(\phi, \theta) \cos\theta \sin\theta \, d\theta\, d\phi$$

把 $\theta$ 切成 $n_2$ 份，$\phi$ 切成 $n_1$ 份，步长分别为 $\Delta\theta=\dfrac{\pi}{2n_2}$，$\Delta\phi=\dfrac{2\pi}{n_1}$，积分近似为：

$$E \approx \sum_{i=1}^{n_1}\sum_{j=1}^{n_2} L(\phi_i,\theta_j)\cos\theta_j \sin\theta_j\, \Delta\theta\, \Delta\phi$$

把两个 $\Delta$ 合并成常数 $\Delta\theta \cdot \Delta\phi = \dfrac{\pi^2}{n_1 n_2}$，提到求和外面：

$$E \approx \frac{\pi^2}{n_1 n_2} \sum_{i=1}^{n_1}\sum_{j=1}^{n_2} L(\phi_i,\theta_j)\cos\theta_j \sin\theta_j$$

这就是两层嵌套 `for` 循环：以法线为极轴，按 $\phi,\theta$ 打点采样天空盒，乘以 $\cos\theta\sin\theta$ 累加，最后乘常数项归一化。

### C.2 方法二：Cubemap texel 循环与精确立体角

方法二把投影积分 $c_i=\int_{S^2}E(\mathbf{d})Y_i(\mathbf{d})\,d\omega$ 离散成对 Cubemap 所有 texel 的求和、以及每个 texel 精确立体角 $\Delta\Omega(\text{texel})$ 的牛顿-莱布尼兹公式推导，篇幅较长，已整合进正文 **4.6 节**（"代码实现前必须解决的问题：积分的离散化"），此处不再重复，直接阅读正文即可。
