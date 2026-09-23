# 预滤波环境贴图（Term 1）：从渲染方程到代码

本文处理 split-sum 的**第一个因子**。第二个因子（环境 BRDF）见配套文档 `IBL_Specular_EnvBRDF_Derivation.md`；两份文档共用符号，本文引用该文档的结论时会注明出处。文中所有公式都做过独立验算，恒等变形与近似分别标注。

路线如下，每一步都给出它产出的结果：

| # | 做什么 | 结果 | 出处 |
|---|---|---|---|
| 1 | 起点：渲染方程里的镜面项 | $L_o=\int_\Omega f_rL_i\,(n\cdot l)\,d\omega_l$ | §1.1 |
| 2 | $(n\cdot l)$ 相消，提出 $1/(4(n\cdot v))$ | $L_o=\frac{1}{4(n\cdot v)}\int_\Omega DFG\,L_i\,d\omega_l$ | §1.2 |
| 3 | 拆成两个积分之比 | 第一个因子 × 第二个因子 | §2.1 |
| 4 | 近似一：$f_r\to D$ | 权重变成 $D(h)\,(n\cdot l)$ | §2.2 |
| 5 | 近似二：$n=v=R$ | $C(R,\alpha)$ 的定义 | §2.3–2.4 |
| 6 | 备料：pdf 与归一化证明 | $p_h(h)=D(h)\,(n\cdot h)$ | §3 |
| 7 | 备料：反解 CDF | $\cos\theta_h=\sqrt{\dfrac{1-\xi}{1+\xi(\alpha^2-1)}}$ | §4 |
| 8 | 备料：雅可比 | $p_l(l)=\dfrac{D(h)(n\cdot h)}{4(v\cdot h)}$ | §5 |
| 9 | 蒙特卡洛展开 | $C\approx\dfrac{\sum_k L_i(l_k)(n\cdot l_k)}{\sum_k (n\cdot l_k)}$ | §6 |
| 10 | 写代码：表、边界、烘焙、合成 | 一张带 mip 的 cubemap | §7 |
| 11 | 验证 | 四条不变量 | §7.5 |

推导主线上只有两处近似：第 4 步与第 5 步；第 3 步的"拆分"是恒等式，第 9 步走完之后，代码里的权重只剩一个 `NdotL`。

---

## 1. 起点：渲染方程与维度不匹配

### 1.1 渲染方程与镜面项

$$L_o(p,\omega_o)=\int_\Omega f_r(p,\omega_i,\omega_o)\,L_i(p,\omega_i)\,(n\cdot\omega_i)\,d\omega_i$$

以下把入射方向 $\omega_i$ 记作 $l$、出射方向 $\omega_o$（视线方向）记作 $v$；表面各向同性、$f_r$ 与位置 $p$ 无关，故省略 $p$。镜面部分用 Cook-Torrance BRDF：

$$f_r(l,v)=\frac{D(h)\,F(v,h)\,G(l,v,h)}{4\,(n\cdot l)(n\cdot v)},\qquad h=\frac{l+v}{\|l+v\|}$$

被积函数里的 $(n\cdot\omega_i)$ 来自辐照度的定义：一束截面为 $A$ 的平行光以入射角 $\theta$ 斜射到表面，照亮的面片面积是 $A/\cos\theta$，故单位面积接收到的辐照度等于通量乘 $\cos\theta$。

### 1.2 第一步：$(n\cdot l)$ 相消

代入 $f_r$：

$$\begin{aligned}
L_o
&=\int_\Omega \frac{D(h)F(v,h)G(l,v,h)}{4\,(n\cdot l)(n\cdot v)}\,L_i(l)\,(n\cdot l)\,d\omega_l\\[2pt]
&=\int_\Omega \frac{D(h)F(v,h)G(l,v,h)}{4\,(n\cdot v)}\,L_i(l)\,d\omega_l\\[2pt]
&=\frac{1}{4\,(n\cdot v)}\int_\Omega D(h)\,F(v,h)\,G(l,v,h)\,L_i(l)\,d\omega_l
\end{aligned}$$

分子末尾的 $(n\cdot l)$ 与归一化分母里的 $(n\cdot l)$ 互为倒数，精确相消；$1/(4(n\cdot v))$ 与积分变量 $l$ 无关，可以提到积分号外。这是恒等变形，不是近似。

两点提醒：

1. 约掉之后**被积函数里已经不含 $(n\cdot l)$**。后面 §2.2 的公式里还会出现一个 $(n\cdot l)$，那不是这里"剩下来"的这一对：把 $f_r$ 换成 $D$ 之后，$f_r$ 分母里的那个 $1/(n\cdot l)$ 不存在了，渲染方程自带的这个余弦就不再被抵消，于是留在权重里。
2. 提出去的是**被积函数**里的因子，不减少结果对外部参数的依赖。剩余被积函数里仍然含 $n\cdot v$：$D(h)$ 的 $h$ 依赖 $l+v$，Smith 的 $G$ 也是显式含 $n\cdot v$ 的函数。

### 1.3 为什么不能直接预计算：维度不匹配

固定环境贴图后（烘焙时环境光是已知、固定的），镜面项是三个量的函数：

| 输入 | 自由度 |
|---|---|
| 法线方向 $n$（单位向量） | 2 |
| 视线方向 $v$（单位向量） | 2 |
| 粗糙度 $\alpha$ | 1 |
| 合计 | **5** |

而预滤波的**输出形式**是一张 cubemap：

| 输出 | 自由度 |
|---|---|
| 方向 $R$（单位向量） | 2 |
| 粗糙度 $\alpha$（mip） | 1 |
| 合计 | **3** |

**输入 5 维、输出 3 维，差的 2 维必须消掉。** 缺的正是 $n$ 与 $v$ 的**相对取向**：$n$、$v$ 是两个独立方向，而预滤波结果只能按一个方向索引。要为所有 $(n,v)$ 组合都预计算，等于为每个法线方向备份一整套环境卷积，代价随环境数量线性增长，实时不可用。

出路只有一条：把 $n$ 与 $v$ 合并成一个方向，即 §2.3 的 $n=v=R$。

---

## 2. 拆分：从"乘积的积分"到两个可存表的量

### 2.1 拆分本身是恒等式，不是近似

记两个积分

$$A=\int_\Omega L_i(l)\,f_r(l,v)\,(n\cdot l)\,d\omega_l,\qquad B=\int_\Omega f_r(l,v)\,(n\cdot l)\,d\omega_l$$

只要 $B\neq0$，就有

$$L_o=\frac{A}{B}\cdot B$$

这是纯粹的代数因式分解，**没有任何近似**。Karis 把它叫作 "split sum approximation"，容易读成"拆分是近似"——真正引入近似的是 §2.2 与 §2.3 那两步替换。

**第一个因子是 $L_i$ 的加权平均。** 把 $A$、$B$ 相除，共同的权重因子提出来：

$$\frac{A}{B}=\frac{\int_\Omega L_i(l)\,\underbrace{f_r(l,v)\,(n\cdot l)}_{w(l)}\,d\omega_l}{\int_\Omega \underbrace{f_r(l,v)\,(n\cdot l)}_{w(l)}\,d\omega_l}=\frac{\int_\Omega L_i\,w\,d\omega}{\int_\Omega w\,d\omega}$$

$w(l)=f_r(l,v)\,(n\cdot l)$ 是**权重的密度函数**，分母是所有权重的总和。这行式子说的是：把每个入射方向 $l$ 的环境光亮度 $L_i(l)$ 按 $w(l)$ 的比重相加，再除以总比重——也就是对整个半球做一次连续加权平均。三点用处：

1. 权重非负（$w=\frac{DFG}{4(n\cdot v)}\ge0$），所以加权平均的结果必然落在 $\min L_i$ 与 $\max L_i$ 之间，反射不会凭空放大能量。这是 §7.5 的验证之一。
2. 分母 $\int w\,d\omega$ 就是这个平均的归一化因子；§6 会看到它在代码里变形为 `totalweight`。
3. "预滤波 = 模糊"有了确切含义：模糊就是邻域加权平均，权重形状由 $w$ 决定；粗糙度变大 → $w$ 展宽 → 模糊范围变大。

**第二个因子是纯 BRDF 积分。** 这里要区分被积函数的变量和积分结果的参数：

| | 依赖 |
|---|---|
| 被积函数 $f_r(l,v)\,(n\cdot l)$ | $l$ 与 $v$（$f_r$ 通过 $h=\mathrm{normalize}(l+v)$ 同时依赖两者） |
| 积分结果 $B$ | $(n\cdot v,\alpha)$ |

$l$ 被积掉了，$v$ 绕 $n$ 的方位角也不进结果，所以 $B$ 只依赖 $(n\cdot v,\alpha)$（论证见配套文档 §3.1）。由此得到 Karis 的拆分形式：

$$L_o\approx\underbrace{\frac{\int_\Omega L_i(l)\,f_r(l,v)\,(n\cdot l)\,d\omega_l}{\int_\Omega f_r(l,v)\,(n\cdot l)\,d\omega_l}}_{\text{第一个因子：}L_i\text{ 的加权平均，权重 }w=f_r(l,v)(n\cdot l)}\times\underbrace{\int_\Omega f_r(l,v)\,(n\cdot l)\,d\omega_l}_{\text{第二个因子：只依赖 }(n\cdot v,\alpha)}$$

- 右边第二个因子与环境光、与 $n$ 的朝向都无关 → 可存成二维查找表。**不在本文范围内。**
- 右边第一个因子仍是 $(n,v)$ **相对取向**的函数（$n\cdot v$ 加上 $v$ 绕 $n$ 的方位角），无法按单个方向索引，还不能存表。这正是 §1.3 留下的那 2 个自由度，也是本文要解决的问题。

> **写法提醒**：常被转载的版本把第一个因子的分母省掉，写成 $\int_\Omega L_i(l)D(h)(n\cdot l)\,d\omega_l$ 与第二个因子相乘（Karis 幻灯片上的记法是这种简写）。但那份幻灯片对应的实现，以及所有下游实现，都做了归一化——LearnOpenGL 预滤波着色器末尾的 `prefilteredColor / totalWeight` 就是被省掉的那个分母。漏掉它等于给结果乘上一个随粗糙度变化的系数，量级见 §2.4 附注 4。

### 2.2 近似一：用 $D$ 替换 $f_r$

镜面反射的能量几乎全部集中在 $D(h)$ 决定的那个锥形区域里。$L_i$ 在这个锥内变化很慢，而 $F$、$G$ 在这个锥内变化也远小于 $D$，所以可以用 $D$ 的形状代表整个 $f_r$ 的形状：

$$\frac{\int_\Omega L_i(l)\,f_r(l,v)\,(n\cdot l)\,d\omega_l}{\int_\Omega f_r(l,v)\,(n\cdot l)\,d\omega_l}\;\approx\;\frac{\int_\Omega L_i(l)\,D(h)\,(n\cdot l)\,d\omega_l}{\int_\Omega D(h)\,(n\cdot l)\,d\omega_l}$$

替换后权重从 $w=f_r(l,v)(n\cdot l)$ 变成 $w=D(h)(n\cdot l)$，形状由 $D$ 的波瓣决定。

这里 $(n\cdot l)$ 第一次以显式形式出现：左式里它以 $f_r(l,v)(n\cdot l)=\frac{DFG}{4(n\cdot v)}$ 的形式被约掉，而 $D$ 没有 $1/(n\cdot l)$ 这个分母，所以替换之后余弦不再被抵消，成为可见的权重。这就是 §2.4 定义式里的那个 $(n\cdot l)$，也是 §6 代码里 `NdotL` 的来源。

代价是丢掉了 $F$ 与 $G$ 的形状信息。Karis 对这一项的验证方式是**与全积分 ground truth 逐像素对比**，并未给出误差上界。

### 2.3 近似二：$n=v=R$（本文中误差最大的一步）

第一个因子依赖 $v$ 与 $n$ 的相对关系。要把它变成"只依赖一个方向"的量，必须把这两个方向合并成一个，消掉 §1.3 指出的那 2 个自由度。取

$$\boxed{n=v=R}$$

即假设视线垂直于表面、反射方向就是法线方向。三项合一后：

- 波瓣绕 $R$ 轴对称，形状只由 $\alpha$ 决定，与视角彻底解耦；
- 结果变成 $(R,\alpha)$ 的函数：$R$ 是一个方向，可按 cubemap 纹素枚举；$\alpha$ 分档存进 mip。

先把这个假设的性质说清楚：它不是"拟合"，也不是"压缩"，而是**只存了 5 维函数的一个 3 维切片**。运行时拿到任意一组 $(n,v,\alpha)$，都拿它去查这一张切片，误差就由此产生。

**这是整个预滤波中误差最大的一步**，超过 §2.2 的替换。代价是掠射角下真实波瓣的歪斜拉长被强行当成对称处理——看玻璃、水面等掠射高光时，形状不对（本应拉长成椭圆，结果变成圆斑）。Karis 对这一点的原话是与 split sum 拆分本身相比，这一步"is actually the larger source of error"，作为换取可预计算性的成本被接受。

### 2.4 预滤波项的最终定义

$$\boxed{C(R,\alpha)=\frac{\displaystyle\int_{\Omega_R} L_i(l)\,D(h)\,(n\cdot l)\,d\omega_l}{\displaystyle\int_{\Omega_R} D(h)\,(n\cdot l)\,d\omega_l}}$$

四点附注：

1. 积分域 $\Omega_R$ 是以 $R$ 为轴的上半球，$n$ 处处取 $R$；$h$ 由 $l$ 与 $R$ 共同决定，$h=\mathrm{normalize}(l+R)$。
2. 分母 $\int D(n\cdot l)\,d\omega_l$ **不是 1**。它是这个加权平均的归一化因子，必须跟着算；§6 会看到它在代码里由 `totalweight` 承担。
3. **归一化自检**：取常数环境光 $L_i\equiv L$，分子分母同时出现 $\int D(n\cdot l)\,d\omega_l$ 而相消，得 $C=L$——均匀环境下镜面反射恰好等于环境光亮度，与粗糙度无关。这是 §7.5 的第一条验证。
4. **省掉分母的后果**：结果变成 $L\displaystyle\int_{\Omega_R} D(n\cdot l)\,d\omega_l$。这个因子在低粗糙度时等于 4（偏亮近 4 倍），随粗糙度增大单调下降，到 $\alpha=1$ 时恰好为 1。也就是说平滑表面——镜面反射最显眼的地方——偏差最大。

---

## 3. 备料 A：pdf 与归一化证明

### 3.1 立体角

球面上一个面元对应的立体角为

$$d\omega=\sin\theta\,d\theta\,d\varphi$$

上半球总立体角：

$$\int_{\Omega^+}d\omega=\int_0^{2\pi}\!\!\int_0^{\pi/2}\sin\theta\,d\theta\,d\varphi=2\pi\cdot1=2\pi$$

注意这里的 $\sin\theta$：任何把 $\int_\Omega f\,d\omega$ 写成 $(\theta,\varphi)$ 重积分的场合都必须乘上它。它也是 §4 反解 CDF 时的关键因子。

### 3.2 蒙特卡洛估计器

$$I=\int_\Omega f(x)\,d\omega=\int_\Omega\frac{f(x)}{p(x)}\,p(x)\,d\omega\;\approx\;\frac1N\sum_{k=1}^N\frac{f(x_k)}{p(x_k)}$$

第二个等号是恒等变形（用了 $\int p\,d\omega=1$），只有 $\approx$ 是采样近似。**代码里所有 `sum += f(sample) / pdf(sample)` 的骨架都来自这里。** 无偏的前提是 $p(x)>0$ 在所有 $f(x)\neq0$ 处成立。

### 3.3 pdf 的两条硬性要求

$p$ 要能当 pdf 用，必须

$$p(x)\geq0,\qquad \int_\Omega p\,d\omega=1$$

第二条必须证明，不能假设。

### 3.4 归一化证明：为什么 $D$ 可以当 pdf

GGX（Walter 等人论文里该分布的代号，数学上与 Trowbridge-Reitz 1975 的分布相同）法线分布有两种等价写法：

$$D(h)=\frac{\alpha^2}{\pi\,(n\cdot h)^4\left(\alpha^2+\tan^2\theta_h\right)^2}=\frac{\alpha^2}{\pi\left[(n\cdot h)^2(\alpha^2-1)+1\right]^2}$$

本项目 `PBRMainLighting.hlsl:29-31` 用第二种，注意里面有两层平方：

```hlsl
float alpha = roughness * roughness;                        // α，不是 α²
float d_denom = NdotH * NdotH * (alpha * alpha - 1) + 1;    // alpha * alpha = α²
float D = (alpha * alpha) / max(PI * d_denom * d_denom, 1e-6);
```

**两层平方的来源是 Disney 的粗糙度重映射**：

$$\boxed{\alpha=\text{roughness}^2}$$

即 `a = roughness * roughness`（`IBLBakerCS.compute:125`）。读 GGX 代码时把 $\alpha$ 与 $\alpha^2$ 看混是最常见的错误来源。

现在验证归一化。令 $c=\cos\theta_h=n\cdot h$，$u=c^2(\alpha^2-1)+1$：

$$\int_{\Omega^+}D(h)\,(n\cdot h)\,d\omega_h=\int_0^{2\pi}\!\!\int_0^{\pi/2}\frac{\alpha^2\,c}{\pi\,u^2}\underbrace{\sin\theta\,d\theta\,d\varphi}_{d\omega_h}$$

$\varphi$ 积分给出 $2\pi$。换元：$du=2(\alpha^2-1)\,c\,(-\!\sin\theta)\,d\theta$，故 $c\sin\theta\,d\theta=\dfrac{-du}{2(\alpha^2-1)}$。积分限 $\theta:0\to\pi/2$ 对应 $u:\alpha^2\to1$：

$$=2\pi\cdot\frac{\alpha^2}{\pi}\int_{\alpha^2}^{1}\frac{1}{u^2}\cdot\frac{-du}{2(\alpha^2-1)}=\frac{\alpha^2}{\alpha^2-1}\int_{1}^{\alpha^2}\frac{du}{u^2}=\frac{\alpha^2}{\alpha^2-1}\Big[-\frac1u\Big]_{1}^{\alpha^2}=\frac{\alpha^2}{\alpha^2-1}\Big(1-\frac{1}{\alpha^2}\Big)=1$$

$$\boxed{\int_{\Omega^+}D(h)\,(n\cdot h)\,d\omega_h=1}$$

**注意**：$\int D\,d\omega_h\neq1$，归一化的是 $D\cdot(n\cdot h)$ 而不是 $D$ 单独。这一条决定了 pdf 里到底带几个 $(n\cdot h)$ 因子。

因此以**立体角**为测度的 pdf 是

$$\boxed{p_h(h)=D(h)\,(n\cdot h)}$$

直觉：$D$ 越大的方向（微表面法线越密集的方向）抽到的样本越多，这就是重要性采样。它把样本集中到贡献最大的区域，在同样样本数下方差远小于均匀采样。

---

## 4. 备料 B：GGX 的 $\theta$ 采样——反解 CDF

**第 1 步：写出 $(\theta,\varphi)$ 坐标下的密度。** 由 pdf 的坐标变换（乘雅可比 $\sin\theta$）：

$$p(\theta,\varphi)=p_h(h)\cdot\sin\theta=D(\cos\theta)\,\cos\theta\,\sin\theta$$

展开：

$$p(\theta,\varphi)=\frac{\alpha^2\cos\theta\sin\theta}{\pi\left(\cos^2\theta(\alpha^2-1)+1\right)^2}$$

**第 2 步：对 $\varphi$ 边缘化。** $p$ 与 $\varphi$ 无关，乘 $2\pi$：

$$p(\theta)=\frac{2\alpha^2\cos\theta\sin\theta}{\left(\cos^2\theta(\alpha^2-1)+1\right)^2}$$

**第 3 步：求累积分布。** 用与 §3.4 相同的换元 $u=\cos^2\theta'(\alpha^2-1)+1$，注意 $p(\theta)\,d\theta=-\dfrac{\alpha^2}{\alpha^2-1}\cdot\dfrac{du}{u^2}$，且 $\theta:0\to\theta$ 对应 $u:\alpha^2\to u(\theta)$：

$$P(\theta)=\int_0^\theta p(\theta')\,d\theta'=\frac{\alpha^2}{\alpha^2-1}\int_{u(\theta)}^{\alpha^2}\frac{du}{u^2}=\frac{\alpha^2}{\alpha^2-1}\Big[-\frac1u\Big]_{u(\theta)}^{\alpha^2}=\frac{\alpha^2}{\alpha^2-1}\left(\frac{1}{u(\theta)}-\frac{1}{\alpha^2}\right)$$

自检：$\theta\to\pi/2$ 时 $u\to1$，$P=\frac{\alpha^2}{\alpha^2-1}\left(1-\frac{1}{\alpha^2}\right)=1$。

**第 4 步：逆变换采样。** 令 $P(\theta)=\xi$（$\xi\sim U[0,1)$）。因为 $P$ 在 $[0,\pi/2]$ 上单调，可以反解：

$$\frac{1}{u}=\frac{\xi(\alpha^2-1)+1}{\alpha^2}\quad\Longrightarrow\quad u=\frac{\alpha^2}{\xi(\alpha^2-1)+1}$$

代回 $u$ 的定义：

$$\cos^2\theta(\alpha^2-1)+1=\frac{\alpha^2}{\xi(\alpha^2-1)+1}$$

$$\cos^2\theta=\frac{\alpha^2-\xi(\alpha^2-1)-1}{(\alpha^2-1)\left[\xi(\alpha^2-1)+1\right]}=\frac{(\alpha^2-1)(1-\xi)}{(\alpha^2-1)\left[\xi(\alpha^2-1)+1\right]}$$

$$\boxed{\cos\theta=\sqrt{\frac{1-\xi}{1+\xi(\alpha^2-1)}}}$$

各向同性假设下方位角均匀：$\varphi=2\pi\xi'$。

**第 5 步：切空间 → 世界空间。** 需要以 $n$ 为 $z$ 轴的正交基。

### 4.1 对标代码

`IBLBakerCS.compute:123-141`：

```hlsl
float3 ImportanceSampleGGX(float2 xi,float3 n,float roughness)
{
    float a=roughness*roughness;                                // α = roughness²
    float phi=2*PI*xi.x;                                        // φ = 2πξ'
    float cosTheta=sqrt((1-xi.y)/(1+(a*a-1)*xi.y));             // 结论公式，a*a = α²
    float sinTheta=sqrt(1-cosTheta*cosTheta);                   // 三角恒等式

    float3 H;
    H.x=cos(phi)*sinTheta;                                      // 切空间单位向量
    H.y=sin(phi)*sinTheta;
    H.z=cosTheta;

    float3 up        = abs(n.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 tangent   = normalize(cross(up, n));                 // 正交基
    float3 bitangent = cross(n, tangent);

    float3 sampleVec = tangent * H.x + bitangent * H.y + n * H.z;  // 切空间 → 世界空间
    return normalize(sampleVec);
}
```

`up` 的三元判断是纯数值保护：$n$ 与 `up` 平行时 `cross` 退化为零向量，不含数学。

### 4.2 两条自检与两个陷阱

**自检一：** $\xi=0$ 时公式给出 $\cos\theta=1$，即 $h=n$，落在波瓣正中心。这是合法样本，也不会产生除零；§7.2 会用到这条性质。

**自检二：** $\alpha=1$ 时公式退化为 $\cos\theta=\sqrt{1-\xi}$，对应 $p(\theta)=\sin\theta$，即均匀半球——粗糙度取最大时 GGX 波瓣本来就是整个半球。

**陷阱一：同一个 $\xi$ 分量不能同时驱动 $\varphi$ 与 $\cos\theta$。** 常见错法是 $\varphi=2\pi\xi_x$ 而 $\cos\theta$ 也用 $\xi_x$：两个自由度被绑定成一条曲线，样本落在半球上的一条线上，结果系统性错误，且不报错、不崩。正确做法是 $\varphi$ 用 $\xi_x$、$\cos\theta$ 用 $\xi_y$。

**陷阱二：等价写法的分母符号。** $\cos\theta$ 还有等价写法

$$\cos\theta=\sqrt{\frac{1-\xi}{1+\xi(\alpha^2-1)}}\qquad\text{与}\qquad\cos\theta=\sqrt{\frac{\xi}{1-(1-\xi)(1-\alpha^2)}}$$

两者把 $\xi$ 换成 $1-\xi$ 即可互推；由于 $\xi$ 在 $[0,1)$ 上均匀，产出的分布相同。写错的典型形态是把分母里的符号写成 $1+(1-\xi)(1-\alpha^2)$：$\alpha<1$ 时波瓣被拉宽，等于换成了另一个分布的采样，同样不报错。

---

## 5. 备料 C：雅可比——从半角向量 $h$ 换到入射方向 $l$

采样器给的是 $h$，而 §2.4 的积分是对 $l$ 做的，所以必须做 pdf 的变量替换。

### 5.1 几何推导

固定 $v$，映射 $l\mapsto h=\dfrac{l+v}{\|l+v\|}$ 把入射半球双射到 $h$ 半球。

设 $\theta_l$ 为 $v$ 与 $l$ 的夹角，$\theta_h$ 为 $v$ 与 $h$ 的夹角。因为 $h$ 是 $v$ 与 $l$ 的角平分方向：

$$\theta_h=\frac{\theta_l}{2}$$

取 $l$ 附近一个微小面元，三个量在映射下的变化：

- 方位角 $\varphi$（绕 $v$ 测）**不变**：$h$ 与 $v$、$l$ 共面，旋转角相同
- 极角被压缩一半：$d\theta_h=\tfrac12 d\theta_l$
- $\sin\theta_h=\sin(\theta_l/2)$

于是

$$d\omega_h\approx\sin\theta_h\,d\theta_h\,d\varphi=\sin\frac{\theta_l}{2}\cdot\frac{d\theta_l}{2}\cdot d\varphi,\qquad d\omega_l=\sin\theta_l\,d\theta_l\,d\varphi$$

相除：

$$\frac{d\omega_h}{d\omega_l}=\frac{\sin(\theta_l/2)}{2\sin\theta_l}=\frac{\sin(\theta_l/2)}{2\cdot2\sin(\theta_l/2)\cos(\theta_l/2)}=\frac{1}{4\cos(\theta_l/2)}$$

而 $\cos(\theta_l/2)=v\cdot h$（$h$ 是 $v$ 与 $l$ 的角平分方向，$v$ 到 $h$ 的夹角正是 $\theta_l/2$）。所以

$$\boxed{\frac{d\omega_l}{d\omega_h}=4\,(v\cdot h)}$$

### 5.2 换算成 pdf

用概率守恒：

$$p_l(l)\,d\omega_l=p_h(h)\,d\omega_h\quad\Longrightarrow\quad p_l(l)=p_h(h)\cdot\frac{d\omega_h}{d\omega_l}=\frac{p_h(h)}{4(v\cdot h)}$$

代入 §3.4 的 $p_h(h)=D(h)(n\cdot h)$：

$$\boxed{p_l(l)=\frac{D(h)\,(n\cdot h)}{4\,(v\cdot h)}}$$

### 5.3 与权威实现对照

LearnOpenGL 预滤波着色器计算源 mip 时显式写出了这个 pdf：

```glsl
float D     = DistributionGGX(N, H, roughness);
float NdotH = max(dot(N, H), 0.0);
float HdotV = max(dot(H, V), 0.0);
float pdf   = D * NdotH / (4.0 * HdotV) + 0.0001;
```

逐项对上：`D` $=D(h)$、`NdotH` $=n\cdot h$、`HdotV` $=v\cdot h$。即

$$p_l(l)=\frac{D(h)\cdot(n\cdot h)}{4\,(v\cdot h)}$$

与 §5.2 的结论一致。

### 5.4 反向自检：pdf 归一化

$$\int_\Omega p_l\,d\omega_l=\int_\Omega\frac{p_h(h)}{4(v\cdot h)}d\omega_l=\int_\Omega p_h(h)\cdot\frac{d\omega_l}{4(v\cdot h)}=\int_\Omega p_h(h)\,d\omega_h=1$$

用了 5.1 的 $d\omega_l=4(v\cdot h)\,d\omega_h$，归一化成立，说明 5.1 的雅可比方向正确。若把方向写反成 $p_l=p_h\cdot 4(v\cdot h)$，这个自检立刻失败。

---

## 6. 蒙特卡洛展开：代码里的 `NdotL` 从哪来

现在把 §2.4 的定义式用 §3.2 的估计器展开，pdf 用 §5.2 的结果。

### 6.1 用同一批样本估计两个积分的比值

$C(R,\alpha)$ 是两个积分的比值，用**同一批样本**分别估计：

$$\frac{\displaystyle\int L_iD(n\cdot l)\,d\omega_l}{\displaystyle\int D(n\cdot l)\,d\omega_l}\;\approx\;\frac{\dfrac1N\sum_k\dfrac{L_i(l_k)D(h_k)\,(n\cdot l_k)}{p_l(l_k)}}{\dfrac1N\sum_k\dfrac{D(h_k)\,(n\cdot l_k)}{p_l(l_k)}}$$

### 6.2 $D$ 与 $p_l$ 精确相消

两个求和里出现同一个因子：

$$\frac{D(h_k)}{p_l(l_k)}=\frac{D(h_k)}{\dfrac{D(h_k)(n\cdot h_k)}{4(v\cdot h_k)}}=\frac{4\,(v\cdot h_k)}{n\cdot h_k}$$

**$D$ 完全消失**，因为重要性采样选的 pdf 本身就含 $D$。预滤波的权重里不应该出现 $D$。

让这个因子在分子分母里显式出现（此时它逐样本变化，还不能提出求和号）：

$$C\approx\frac{\sum_k L_i(l_k)\,(n\cdot l_k)\cdot\dfrac{4(v\cdot h_k)}{n\cdot h_k}}{\sum_k (n\cdot l_k)\cdot\dfrac{4(v\cdot h_k)}{n\cdot h_k}}$$

### 6.3 $n=v=R$ 让因子退化成常数

在 $n=v$ 之下，$h=\mathrm{normalize}(l+v)$ 满足 $v\cdot h=n\cdot h$（$v$ 与 $n$ 同向）。于是

$$\frac{4(v\cdot h_k)}{n\cdot h_k}=\frac{4(n\cdot h_k)}{n\cdot h_k}=4$$

**它与 $k$ 无关**——这一点很关键：如果 $v\cdot h\neq n\cdot h$，这个因子会随样本变化，就不能提到求和号外。既然它是常数，在比值里上下同倍，精确相消：

$$\boxed{C(R,\alpha)=\frac{\sum_k L_i(l_k)\,(n\cdot l_k)}{\sum_k (n\cdot l_k)}}$$

### 6.4 核心算式就是这几行

```hlsl
float NdotL = max(dot(n, L), 0.0);
if (NdotL > 0.0)
{
    prefilteredColor += _Skybox.SampleLevel(sampler_PointClamp, L, 0).rgb * NdotL;
    totalweight      += NdotL;
}
prefilteredColor = prefilteredColor / totalweight;
```

| 代码 | 本文符号 |
|---|---|
| `L` | $l_k$ |
| `H` | $h_k$ |
| `n`、`v` | $R$（§2.3 取 $n=v=R$） |
| `NdotL` | $(n\cdot l_k)$，既是分子里的权重、也是分母里的权重 |
| `totalweight` | $\sum_k (n\cdot l_k)$ |
| `prefilteredColor / totalweight` | 归一化，同时吃掉 §6.3 的常数 4 |

**权重里不含 $D$、不含 $4$、不含 $1/(n\cdot v)$**：$D$ 被 $p_l$ 约掉，$4$ 被归一化吸收。`NdotL` 既不是随手选的、也不是经验性的余弦加权，而是 §3.4、§4、§5 一路推下来之后剩下的唯一东西。

推导到这里结束。接下来是把它补成一段能跑的代码——过程中会撞上几个必须做决定的地方，逐个处理。

### 6.5 权重简化的两层近似

上面这条路是"先用 $D$ 替换 $f_r$（§2.2）"，所以 $D$ 与 $p_l$ 精确相消，剩下 $(n\cdot l)$ 作为权重。还有一条更保守的路：不做 §2.2 的替换，在比值里精确保留 $f_r$。此时单样本权重变成

$$w_k=\frac{f_r(l_k,v)(n\cdot l_k)}{p_l(l_k)}=\frac{D F G}{4(n\cdot v)}\cdot\frac{4(v\cdot h)}{D(n\cdot h)}=\frac{F_k\,G_k\,(v\cdot h_k)}{(n\cdot v)(n\cdot h_k)}\;\xrightarrow{\;n=v\;}\;F_k\,G_k$$

也就是说：**不做替换时权重是 $F_kG_k$**。Karis 的实现在此之上再把 $W=FG$ 近似成 $\langle n,l\rangle$：他的对照分析指出 Fresnel-Schlick 在 $45^\circ$ 以内近乎常数，于是 $F$ 这一项可以当常数，剩下的 $G$ 与 $(n\cdot l)$ 都随入射角逐单调变化；两种权重的结果差别很小，结论经 Karis 本人审阅。

两条路殊途同归，权重都归结为 $(n\cdot l)$。**预滤波贴图里没有 $F_0$ 的位置**：它先被 §2.2 的替换、再被本节的权重近似一并丢掉，所以同一张 cubemap 适用于所有材质、所有 $F_0$——$F_0$ 的贡献由 Term 2 的 $F_0A+B$ 单独承担。

### 6.6 两个容易混淆的细节

**（a）为什么能丢弃 $n\cdot l\le0$ 的样本而不引入偏差。**
被积函数是 $L_iD\cdot\max(0,n\cdot l)$，分子分母都含同一个 $\max(0,n\cdot l)$。$n\cdot l\le0$ 的样本对分子分母都贡献 0，丢弃与"保留但权重记 0"完全等价。

**（b）比值估计器的性质。**
分子与分母各自的蒙特卡洛估计是无偏的，但两者的比值在有限样本下不是严格无偏的（一阶近似无偏），属于**一致估计**：$N\to\infty$ 时收敛到真值。这是所有"归一化加权平均"型渲染器的共性。

---

## 7. 用代码落实

### 7.1 表长什么样

§2.4 的定义式有两个自变量：方向 $R$ 与粗糙度 $\alpha$。方向是球面上的量，用 cubemap 按方向索引；粗糙度分档，一档一个 mip。于是

| 表的坐标 | 存什么 |
|---|---|
| 面（6 个）+ uv | 方向 $R$：每个纹素一个方向 |
| mip 层（8 层） | 粗糙度 $\alpha$：每层一个值 |
| 纹素的值 | $C(R,\alpha)$，三通道 HDR 线性颜色 |

本项目取 128×128 每面、8 个 mip（`IBLBaker.cs:123-125`），于是 mip $m$ 的烘焙粗糙度是 `roughness = mip / (maxMip - 1)`，即 8 层从 0 到 1 线性分档（`IBLBaker.cs:134`）。运行时用同一个映射反向查（§7.4）。

物理上，粗糙度越大波瓣越宽、结果越低频，分辨率需求越低——每层减半正好同时省掉采样数与显存。

### 7.2 完整的预滤波 kernel 与三处边界

把 §4 的采样器与 §6.4 的循环拼起来，就是 `IBLBakerCS.compute:143-169` 的完整形态：

```hlsl
[numthreads(1,1,1)]
void CSMainSpecular_Prefilter(uint3 id : SV_DispatchThreadID)
{
    float2 uv  = ((float2)id.xy + 0.5f) / (float2)_Resolution;
    uint   index = id.y * _Resolution + id.x;
    float3 R   = DirectionFromCubemapTexel(_Face, uv);   // 输出方向，即 n = v = R

    float3 n = R;
    float3 v = n;

    const uint SAMPLE_COUNT = 1024;
    float  totalweight     = 0;
    float3 prefilterColor  = float3(0,0,0);

    for (uint i = 0; i < SAMPLE_COUNT; ++i)
    {
        float2 xi = Hammersley(i, SAMPLE_COUNT);
        float3 H  = ImportanceSampleGGX(xi, n, _FilterMapRoughness);   // 采样 h
        float3 L  = normalize(2 * dot(v, H) * H - v);                  // 求 l

        float NdotL = max(dot(n, L), 0);
        if (NdotL > 0.0)                                              // 域外样本贡献为 0
        {
            prefilterColor += _Skybox.SampleLevel(sampler_PointClamp, L, 0).rgb * NdotL;
            totalweight    += NdotL;
        }
    }
    _Result[index] = float4(prefilterColor / totalweight, 1.0);
}
```

三处像陷阱的地方，逐个查过：第一处必须处理，后两处其实不需要保护。

| 情况 | 出问题的位置 | 现象 | 处理 |
|---|---|---|---|
| $n\cdot l_k\le0$ | §6.4 分子与分母的权重 $(n\cdot l_k)$ | 不 clamp 会得到负权重 → 亮斑或暗斑 | `max(dot(n,L),0)` 之后再 `if (NdotL > 0)` 丢弃 |
| `totalweight` 为 0 | 除法那一行 | 除零 → NaN、整面黑 | 不需要保护，见下 |
| $\alpha=0$（mip 0） | §4 采样公式 $\cos\theta=\sqrt{(1-\xi)/(1+\xi(\alpha^2-1))}$ | 公式本身有定义（$\cos\theta\to1$） | 不需要特殊处理 |

第二行之所以不需要保护：Hammersley 的第一个样本 $\xi=(0,0)$（§4.2 自检一）必然给出 $H=n$、$L=n$、$NdotL=1$，故 `totalweight` 恒 $\ge1$，永远不会是 0。第三行同理：$\alpha=0$ 时公式给出 $\cos\theta=1$，$H$ 就是 $n$，$L$ 就是 $R$，$C=L_i(R)$——正好是 §7.5 要拿来对比的"mip 0 等于镜面查表"。

注意第一行里 `max(dot(n,L),0)` 与 `if (NdotL > 0)` 有重叠，但两者不能互相替代：`max` 保证权重不为负，`if` 决定是否计入。只写 `max`、不写 `if` 结果也对（累加 0 等价于丢弃）；反过来只写 `if (dot(n,L) > 0)` 也对。错的是两个都不要。

还有一处纯性能问题：kernel 本体是 `[numthreads(1,1,1)]`，所以 C# 侧必须 `Dispatch(size, size, 1)` 逐纹素派发（`IBLBaker.cs:143`）。结果正确，但每个线程组要跑完 1024 次循环，开销大。改成 `[numthreads(8,8,1)]` + `Dispatch(size/8, size/8, 1)` 即可（注意 mip 0 的 `size=128` 能被 8 整除，mip 6、7 的 `size=2`、`1` 不能——那时需要 kernel 内做越界判断）。

### 7.3 C# 侧：烘焙与存盘

`IBLBaker.cs:120-150` 的流程，逐层逐面派发：

```csharp
int bakeSize = 128;
outputCubemap = new Cubemap(bakeSize, TextureFormat.RGBAFloat, true);   // true: 带 mip
int maxMip = outputCubemap.mipmapCount;                                 // 128 → 8
outputCubemap.filterMode = FilterMode.Trilinear;                         // 运行时按 mip 插值
for (int mip = 0; mip < maxMip; mip++)
{
    int size = bakeSize >> mip;
    float roughness = (float)mip / (maxMip - 1);                         // §7.1 的映射
    Color[] tempColors = new Color[size * size];
    ComputeBuffer resultBuffer = new ComputeBuffer(size * size, sizeof(float) * 4);
    for (int face = 0; face < 6; face++)
    {
        iblBakerCS.SetInt("_Face", face);
        iblBakerCS.SetTexture(kernel, "_Skybox", envCubemap);
        iblBakerCS.SetInt("_Resolution", size);                          // 必须是当前 mip 的 size
        iblBakerCS.SetFloat("_FilterMapRoughness", roughness);
        iblBakerCS.SetBuffer(kernel, "_Result", resultBuffer);
        iblBakerCS.Dispatch(kernel, size, size, 1);
        resultBuffer.GetData(tempColors);
        outputCubemap.SetPixels(tempColors, (CubemapFace)face, mip);     // 与 kernel 的下标顺序一致
    }
    resultBuffer.Release();
}
outputCubemap.Apply(false);
```

四个要点：

- **`_Resolution` 必须是当前 mip 的尺寸。** kernel 用它同时算 `uv` 与 `index`；传错会让 uv 与写回位置错位。
- **索引顺序一致。** kernel 里 `index = id.y * _Resolution + id.x`，`SetPixels` 也从左下角开始逐行填充，两边天然对齐，读回时不要做纵向翻转。
- **格式必须是线性 HDR。** `TextureFormat.RGBAFloat` + `new Cubemap(..., true)` 不带 sRGB；用 8 位或 sRGB 格式会把高光截断，天空这类远超 1 的亮度会被压掉。
- **`Apply(false)`。** 不要触发 Unity 自动生成 mip——每一层都已经烘焙好了。

最后 `AssetDatabase.CreateAsset` 存盘并 `Shader.SetGlobalTexture("_PrefilterSpecMap", specularMap)`（`IBLBaker.cs:112-118`）。另一种写法是烘进 `RenderTexture`（带 mip 的 cubemap RT）再 `Graphics.CopyTexture` 到 `Cubemap`，可以省掉逐 mip 的 `GetData` 同步；本项目的 `ComputeBuffer` + `SetPixels` 路线更直白，代价是每次 `GetData` 都要等 GPU。

### 7.4 运行时：按粗糙度选 mip

`PBRIndirectLighting.hlsl:53-55`：

```hlsl
float3 R = reflect(-viewDirWS, normalWS);
float mipLevel = roughness * 7;                                            // = roughness * (mipmapCount - 1)
float3 filterSpecColor = SAMPLE_TEXTURE2D_LOD(_PrefilterSpecMap, sampler_PrefilterSpecMap, R, mipLevel).rgb;
```

- $R$ 是反射方向，也就是 §2.3 里那个被当作 $n=v$ 的方向。它必须归一化，且 `viewDirWS` 是**从表面指向相机**的方向。
- `roughness` 必须是 perceptual roughness，与 §7.3 里 `mip → roughness` 的约定同一套。写成 `roughness * 7` 而不是 `roughness * 8`：8 层 mip 的最大索引是 7。
- 采样器的 `filterMode = Trilinear`（§7.3 设过）+ cubemap 跨面插值开启，高粗糙度层（低分辨率 mip）才不会出现接缝（§8.4）。

拿到 $\text{Term}_1$ 之后与 $\text{Term}_2$ 合成：

```hlsl
float2 brdf = SAMPLE_TEXTURE2D(_BRDFLUT, sampler_BRDFLUT, float2(NdotV, roughness)).rg;
float3 specular = filterSpecColor * (_F0 * brdf.x + brdf.y);               // F0·A + B
```

**调试提醒**：如果球面反射看起来是"平"的，先检查材质的 `_Smoothness`。`_Smoothness = 0` → `roughness = 1` → 采样 mip 7，而 mip 7 每面只有 1×1——反射本来就是均匀色，这是正常现象，不是烘焙错误。

### 7.5 怎么验证烘出来的贴图

烘完先别急着看球。按下面四条查，任何一条不过，都不用往下查。

**（1）常数环境光 ⇒ $C=L$ 且与粗糙度无关。**

用一张纯色 HDR 天空盒烘焙，所有 mip、所有面的值都应当等于那个颜色（§2.4 附注 3）。这条直接验证归一化：

- 结果随粗糙度变亮（低粗糙度层偏亮近 4 倍）→ 分母被省了，或 `totalweight` 没累加；
- 整体偏暗或偏亮一个常数倍 → 源贴图的 sRGB/线性标记有问题，或格式被截断。

**（2）值域：$\min L_i\le C\le\max L_i$。**

权重非负（§2.1），$C$ 是 $L_i$ 的加权平均，不可能超出环境光的亮度范围。出现负值或远大于环境光最大亮度的值，几乎一定是权重写错（多了 $D$、或漏了分母）。

**（3）mip 0 等于源环境的镜面查表。**

$\alpha=0$ 时 §7.2 已证明 $H=n$、$L=R$，所以 mip 0 应当逐像素等于源 cubemap 在 $R$ 方向上取到的值（即把源贴图当作镜面反射查表）。把源贴图的 mip 0 与烘出的 mip 0 并排显示，形状应当一致——不一致就是面朝向映射（§8.4）出错。

**（4）单调变糊，且没有黑点与暗带。**

沿 mip 链看：层号越大越模糊、越低频。若高粗糙度层出现密集黑点或成片暗区，按顺序查：

- 源 mip 恒取 0 → 混叠噪点（§8.3）；
- `NdotL` 没有 clamp、或 `H` 未归一化 → 负权重；
- 跨面接缝（§8.4）→ 高粗糙度（低分辨率）层在面边界处出现线状暗痕。

### 7.6 常见错误对照

| 错误 | 症状 | 原因 |
|---|---|---|
| 权重里带 $D$ | 高粗糙度层整体过亮、噪 | $D$ 已被 $p_l$ 约掉（§6.2） |
| 没除 `totalweight` | 亮度随粗糙度暴涨（低粗糙度层最亮） | §2.4 附注 4、§6.3 的归一化被吃掉 |
| 不 clamp、也不判 $n\cdot l>0$ | 亮斑或暗斑 | 负权重进入累加（§6.6a） |
| $\varphi$ 与 $\cos\theta$ 用了同一个 $\xi$ 分量 | 结果系统性错误，不报错 | §4.2 陷阱一 |
| mip → roughness 用了非线性映射，运行时却按线性查 | 粗糙度响应错位 | 烘焙与运行时必须是同一套映射（§7.4） |
| `_Resolution` 传了 128 而不是当前 mip 的 size | 除 mip 0 外全部错位 | §7.3 |
| 源贴图恒取 mip 0 | 大粗糙度层亮斑噪点 | §8.3 |
| cubemap 面朝向 `uv → dir` 约定不匹配 | 某一面镜像或旋转 | §8.4 |
| 输出纹理带 sRGB 或 8 位格式 | 高光被截断、整体偏暗 | §7.3 |
| 运行时用 `SAMPLE_TEXTURE2D_LOD` 传了 $\alpha$ 而不是 roughness | 粗糙度响应被压缩到极窄区间 | 轴上的量与采样器内部 `a = roughness*roughness` 必须成套（§7.4） |

---

## 8. 采样与存储的工程细节

这些不改变数学，但决定结果质量。

### 8.1 Hammersley 序列

$$\xi_i=\left(\frac{i}{N},\ \Phi_2(i)\right)$$

$\Phi_2(i)$ 是 $i$ 的二进制"小数点反演"（van der Corput 序列）：把二进制位反转后放到小数点右边。例：$i=6=110_2\to011_2\to\Phi_2(6)=0.011_2=3/8$。

`IBLBakerCS.compute:107-115` 的五步位运算就是 32 位整体位反转，最后 `* 2.3283064365386963e-10` 即除 $2^{32}$。

**为什么不用伪随机**：纯随机会聚簇、留空洞，误差收敛率 $O(N^{-1/2})$；低差异序列在单位正方形上铺得更均匀，对光滑被积函数可接近 $O(N^{-1})$。同样精度下样本数能省一到两个数量级，所以 `SAMPLE_COUNT = 1024` 这种量级就够用。

### 8.2 roughness ↔ mip 映射

`IBLBaker.cs:134`：

```csharp
float roughness = (float)mip / (maxMip - 1);
```

线性映射。128 分辨率 cubemap 的 `maxMip = log2(128)+1 = 8`，于是 8 层依次对应 roughness 从 0 到 1。运行时反向，就是 §7.4 的 `roughness * 7`。

**线性映射不是唯一选择。** UE 用的是带曲线偏置的映射，在低粗糙度区间分得更细（那里结果变化最剧烈）。若低粗糙度区域出现明显条带，就是这里采样密度不足。

### 8.3 源贴图的 mip 选择（抗噪）

当前实现恒取 `SampleLevel(..., L, 0)`。1024 个样本分布在大范围方向上时，直接从高分辨率源贴图取单纹素会引入严重混叠（亮斑、噪点），大粗糙度层尤其明显；同时大量样本之间高度相关，等效样本数远小于 1024。

标准做法是按 pdf 反算合适的源 mip 再采样（LearnOpenGL / Chetan Jags 的做法）：

```hlsl
float pdf      = D * NdotH / (4.0 * HdotV) + 0.0001;
float saTexel  = 4.0 * PI / (6.0 * resolution * resolution);   // 源贴图单纹素立体角
float saSample = 1.0 / (float(SAMPLE_COUNT) * pdf + 0.0001);   // 当前样本代表的立体角
float mipLevel = roughness == 0.0 ? 0.0 : 0.5 * log2(saSample / saTexel);
```

推导思路：一个样本覆盖的立体角约 $1/(N\cdot p_l)$，一个纹素覆盖 $4\pi/(6\cdot\text{res}^2)$，两者面积比取 $\log_2$ 的一半就是 mip。这里出现的 `pdf` 正是 §5.2 的 $p_l$。

这属于采样质量问题而非数学正确性问题：数学上取 mip 0 依然是对的（无偏），只是方差大。另外要记得源贴图开启 `Trilinear` 并生成 mip，否则 `SampleLevel` 取不到中间层。

### 8.4 Cubemap 工程问题

- **面间接缝**：低 mip 采样时更明显。Unity 侧需保证 sampler 的 `filterMode = Trilinear`（`IBLBaker.cs:127` 已设置），以及 cubemap 采样跨面插值开启。
- **面朝向**：`DirectionFromCubemapTexel`（`IBLBakerCS.compute:15-53`）六个分支的 `uv → dir` 映射必须与源贴图的翻转约定一致。注意 `case 1`（-X）与 `case 0`（+X）的轴对应关系不同。**验证方法**：用一张上下左右不对称的 skybox 烘焙，若某一面出现镜像或旋转，就是这里。这是纯约定问题，与积分无关，但会让人误以为是算法错。

---

## 9. 已知偏差与可选改进

### 9.1 NDF 采样与 VNDF 采样在 $n=v$ 下等价

严格来说，采样 $p_h=D(n\cdot h)$ 忽略了遮挡，是"有偏"的；Heitz 2018 的 VNDF 采样用 $p_h=D_v(h)=\dfrac{G_1(v)\,(v\cdot h)\,D(h)}{n\cdot v}$ 才是物理正确的。

但在预滤波的 $n=v$ 假设下：

$$\frac{D(h)}{p_l^{\text{VNDF}}}=\frac{D(h)\cdot4(v\cdot h)}{D_v(h)}=\frac{D\cdot4(v\cdot h)\cdot(n\cdot v)}{G_1(v)\,(v\cdot h)\,D}=\frac{4\,(n\cdot v)}{G_1(v)}$$

取 $n=v$ 得 $n\cdot v=1$，且 Smith 的 $G_1$ 在 $n\cdot v=1$ 处 $G_1=1$，于是比值同样等于常数 $4$。

**即在 $n=v$ 下，NDF 采样与 VNDF 采样给出完全相同的权重。** 原因很直接：$n=v=R$ 把视角置于正前方，是遮挡最弱、两种分布最接近的位形。所以预滤波这一项用最朴素的 NDF 采样就是对的；而 Term 2（$n\cdot v$ 可以很小）必须小心 $k$ 的取值，见配套文档 §3.2 与 §6.3。

### 9.2 误差来源清单（按影响排序）

| 来源 | 性质 | 影响 | 能否修 |
|---|---|---|---|
| $n=v=R$（§2.3） | 近似，本文最大误差源 | 掠射角高光形状错误（本应拉长，变成圆斑） | 只能换算法（运行时波瓣拟合、更好的反射探针方案） |
| 用 $D$ 替换 $f_r(l,v)$（§2.2） | 近似 | 中低粗糙度下偏亮或偏暗；Karis 与 ground truth 对比确认量级可接受 | 无法在 split-sum 框架内修 |
| NDF 采样 vs VNDF 采样 | 严格说 NDF 采样有偏 | **在 $n=v$ 假设下两者等价，无误差** | 见 §9.1 |
| 权重 $FG\approx(n\cdot l)$（§6.5） | 近似 | 与保留 $FG$ 的版本差别很小 | 想更准可改回 $FG$ 权重 |
| 线性 roughness↔mip | 分辨率分配 | 低粗糙度条带 | 换曲线映射（§8.2） |
| 恒取源 mip 0（§8.3） | 方差，非偏差 | 亮斑噪点 | 加 pdf → mip 计算 |
| cubemap 面朝向（§8.4） | 约定错误 | 整面镜像/错位 | 校准 `uv → dir` |
| 比值估计器（§6.6b） | 有限样本下一阶近似无偏 | $N$ 大时消失 | 增大 $N$ |

单次散射 GGX 的能量损失（高粗糙度下高光偏暗偏灰）不属于本项误差：它来自 Term 2 的 $A+B<1$，见配套文档 §6.2 与 §4.1 的自检说明。

---

## 附录 A：符号表

| 符号 | 含义 | 代码 |
|---|---|---|
| $\alpha$ | GGX 粗糙度参数，$\alpha=\text{roughness}^2$ | `a` |
| $\alpha^2$ | $\alpha$ 的平方 | `a * a` |
| $n$ | 法线；预滤波中取 $n=R$ | `n` |
| $v$ | 视线方向；预滤波中取 $v=R$ | `v` |
| $R$ | 反射方向，也是 §2.3 中合并后的 $n=v$ | `dir` / `R` |
| $l$ | 入射方向 | `L` |
| $h$ | 半角向量 $=\mathrm{normalize}(l+v)$ | `H` |
| $n\cdot h$ | 半角向量与法线夹角余弦 | `NdotH` |
| $v\cdot h$ | 视线与半角向量夹角余弦 | `HdotV` |
| $n\cdot l$ | 入射方向与法线夹角余弦 | `NdotL` |
| $n\cdot v$ | 视线与法线夹角余弦 | `NdotV` |
| $f_r(l,v)$ | BRDF（Cook-Torrance） | `Cook_Torrance` |
| $w(l)$ | 加权平均的权重密度 $=f_r(l,v)\,(n\cdot l)$，替换后为 $D(h)(n\cdot l)$ | 无（被 pdf 约掉，代码里只剩 `NdotL`） |
| $C(R,\alpha)$ | 预滤波项，即本文要求的东西 | `prefilterColor / totalweight` |
| $D(h)$ | 法线分布函数 | `PBRMainLighting.hlsl:29-31` 的 `D` |
| $F(v,h)$ | 菲涅尔项 | `Fresnel` |
| $G$ | 几何遮蔽项（Smith） | `GeometryFunc`（预滤波里不出现） |
| $p_h$ | 半角向量的 pdf $=D(h)(n\cdot h)$ | 无（隐式，体现在采样器里） |
| $p_l$ | 入射方向的 pdf $=\frac{D(n\cdot h)}{4(v\cdot h)}$ | 无（仅 §8.3 算 mip 时出现） |
| $\xi$ | 低差异序列样本 $\in[0,1)^2$ | `xi` |
| $d\omega$ | 立体角微分 | 无（被样本数取代） |

## 附录 B：推导链依赖图

```
L_o = ∫ f_r L_i (n·l) dω_l                                   [1.1]
 ├─ (n·l) 与 BRDF 分母相消 ⇒ 1/(4(n·v)) ∫DFG L_i dω_l        [1.2]  恒等变形
 ├─ 维度不匹配：5 维输入 vs 3 维输出                           [1.3]
 ├─ 拆成比 ⇒ 第一因子（加权平均）× 第二因子                      [2.1]  恒等变形
 ├─ 近似一：f_r → D ⇒ 权重 = D(n·l)                           [2.2]  近似
 ├─ 近似二：n = v = R ⇒ C(R,α)                                [2.3–2.4]  近似（误差最大）
 ├─ dω = sinθ dθ dφ                                          [3.1]
 │    └─ ∫D(n·h)dω_h = 1  ← 归一化证明                        [3.4]
 │         └─ p_h(h) = D(h)(n·h)
 │              └─ 反解 CDF ⇒ cosθ = √((1-ξ)/(1+ξ(α²-1)))      [4]
 │                   └─ 切空间→世界空间 ⇒ H                     [4.1]
 │              └─ 雅可比 dω_l/dω_h = 4(v·h) ─────────┐        [5.1]
 │                                                     │
 │         p_l(l) = D(n·h)/(4(v·h)) ◄──────────────────┘        [5.2]
 ├─ D/p_l = 4(v·h)/(n·h) ⇒ n=v 时为常数 4                      [6.2–6.3]
 │    └─ 比值归一化 ⇒ 权重只剩 (n·l)                            [6.4]
 └─ 代码：面 × mip 的 cubemap，逐层逐面烘焙                      [7]
      └─ 运行时 mip = roughness × (mipmapCount-1)              [7.4]
```

## 附录 C：出处

1. **Brian Karis, "Real Shading in Unreal Engine 4", SIGGRAPH 2013 Course.**
   Split-Sum 的原始出处：预滤波项的定义、$n=v=R$ 假设、"与 ground truth 对比验证"的做法，以及"这一步的误差大于拆分本身"的说明均来自此文。
   课程页：https://blog.selfshadow.com/publications/s2013-shading-course/

2. **LearnOpenGL — Specular IBL.**
   §7 的代码形态、§5.3 的 pdf 写法、§2.1 的"写法提醒"（正文式子省分母而实现除 `totalWeight`）均对标该实现。
   https://learnopengl.com/PBR/IBL/Specular-IBL
   源码：https://github.com/JoeyDeVries/LearnOpenGL/blob/master/src/6.pbr/2.2.2.ibl_specular_textured/2.2.2.prefilter.fs

3. **Zero Radiance, "Alternative Take on the Split Sum Approximation for Cubemap Pre-filtering"（经 Brian Karis 审阅）.**
   归一化形式 $\frac{\int L_if\langle n,l\rangle}{\int f\langle n,l\rangle}$ 的出处；§6.5 关于权重 $W=FG\approx\langle n,l\rangle$ 的讨论依据此文的对照结果。
   https://zero-radiance.github.io/post/split-sum/

4. **Eric Heitz, "Sampling the GGX Distribution of Visible Normals", JCGT 2018.**
   §9.1 NDF 与 VNDF 等价性讨论所引用的正确采样方法。
   https://jcgt.org/published/0007/04/01/

5. **Bruce Walter et al., "Microfacet Models for Refraction through Rough Surfaces", EGSR 2007.**
   GGX 分布的原始出处（与 Trowbridge-Reitz 1975 等价）、半角向量与入射方向之间雅可比的经典来源。§5.1 给出了不依赖该论文的初等几何推导，并用 §5.4 的归一化自检验证方向。

6. **Sébastien Lagarde & Charles de Rousiers, "Moving Frostbite to PBR", SIGGRAPH 2014 Course.**
   §2.3 中掠射角高光形状失真的对比图来源。

7. **Chetan Jags / Padraic Hennessy, 环境图预滤波实现笔记.**
   §8.3 pdf → mip 反算的来源。
   https://placeholderart.wordpress.com/2015/07/28/implementation-notes-runtime-environment-map-filtering-for-image-based-lighting/

8. **Holger Dammertz, "Hammersley 半球采样笔记"；Brent Burley, "Physically Based Shading at Disney", SIGGRAPH 2012 Course.**
   §8.1 的 van der Corput 位反转实现、§3.4 的 $\alpha=\text{roughness}^2$ 重映射分别来自这两份资料。
