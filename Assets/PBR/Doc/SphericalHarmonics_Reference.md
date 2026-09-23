# IBL 烘焙：球谐函数（Spherical Harmonics, SH）深入理解指南

> 本文是 `IBL_Reference.md` 的专题展开篇。那篇回答的是"环境光为什么要 Split-Sum 拆成两半、漫反射怎么近似"；这篇只回答一个问题——**Irradiance 那一大坨数据，凭什么能用 27 个 float 代替，而且运行时还不采样任何纹理？**
> 对应知乎专栏《【Unity Graphics】PBR（Physically Based Rendering）间接漫反射》中的球谐部分。原文的主线是：先用黎曼和暴力算出 IrradianceMap（见 `IBL_Reference.md` 附录 C）→ 发现它低频得离谱 → 于是想到"能不能用球谐去**表达** IrradianceMap 的数据，这样就可以用**解析式**求值，而不是查表"。本文沿这条主线展开，并把原文一笔带过的数学骨架（投影 / 重构 / 卷积）与 Unity 落地细节补全。

在计算间接漫反射（Irradiance）时，如果我们用 Compute Shader 算出一张 6 面的 Cubemap 作为烘焙结果，会发现：这张图非常非常模糊，只有大色块过渡，几乎没有任何高频细节。
既然这样一张"全损画质"的 6 面图要在运行时被反复采样，显存带宽显得极其浪费。这时候，**球谐函数（SH）** 就是最佳替代品。

---

## 0. 先把主线串起来：从"暴力积分"到"解析式求值"

这是理解球谐最省力的一条路径，原文的思路也在这里：

| 阶段 | 做法 | 代价 |
|------|------|------|
| ① 暴力算 | 每个方向、每个烘焙点，用黎曼和把半球积分算一遍 | 离线几十分钟，但**正确** |
| ② 存成贴图 | 把结果写成 IrradianceMap（模糊 Cubemap），运行时 `texCUBE(N)` 查表 | 每个像素一次纹理采样 + 带宽 |
| ③ 换解析式 | 把 IrradianceMap 这份数据"投影"到 9 个球谐基函数上，存 27 个 float | 每个像素几十条 `mad` 指令，0 采样 |

关键转折点在 ②→③：**因为 Irradiance 本身低频（模糊），它几乎"不装细节"，所以用低频基函数去压缩它，损失小到看不出来。**
如果换成高频数据（比如未模糊的环境高光、锐利的阴影），球谐立刻就会崩——这是它的能力边界，第 8 节会专门讲。

---

## 1. 球谐函数到底是个什么东西？

不要被"球"和"谐"两个字吓到。
用一句话概括：**球谐函数就是在三维球面上的傅里叶变换。**

### 类比 2D：傅里叶
在平面的 2D 信号（比如一段声波）里，任何复杂的波形，都可以由无数个正弦波和余弦波（基函数）叠加而来。你只要存几个波的"振幅系数"，就能还原出大致的声音：

$$f(t) = \sum_{n} a_n \cos(nt) + b_n \sin(nt)$$

### 拓展到 3D 球面：球谐
环境光罩在物体外围，相当于一个定义在**球面上的 3D 信号**（每个方向对应一个颜色值）：

$$L = f(\theta, \phi) \quad \text{或者} \quad L = f(\mathbf{d}),\ \mathbf{d} \in S^2$$

数学家发明了一组像正弦波一样的、在球面上鼓包 / 凹陷的"基底波动形状"（这就是球谐基函数 $Y_l^m(\theta,\phi)$）：

$$L(\mathbf{d}) \approx \sum_{l=0}^{n}\sum_{m=-l}^{l} c_l^m\, Y_l^m(\mathbf{d})
\qquad
c_l^m = \int_{S^2} L(\mathbf{d})\, Y_l^m(\mathbf{d})\, d\omega$$

- $l$ 叫**阶（band / degree）**，$m$ 叫**次（order）**。
- $c_l^m$ 就是系数（"振幅"）。
- **任何在球面上的环境光照，都可以看作是这些基础鼓包、凹陷形状按照不同比例捏合出来的。**

### 一个更"数学"的出身
球谐并不是图形学发明的。它是**拉普拉斯方程在球坐标系下、角度部分的解**：

$$\nabla^2 f = 0 \;\Longrightarrow\; f(r,\theta,\phi) = \sum_l \left(A_l r^l + \frac{B_l}{r^{l+1}}\right) \sum_m Y_l^m(\theta,\phi)$$

（这也是为什么量子力学里电子轨道形状、以及 PBR 里的环境光，用的是同一套函数。）对你只有一个实际意义：**这组基函数是"天赐"的，公式固定、正交、完备，你不需要自己发明。**

### 复数 SH 与实数 SH
教科书写的是复数版：$Y_l^m = N_l^m P_l^{|m|}(\cos\theta) e^{im\phi}$。
实时渲染一律用**实数版（Real SH）**，做法是把 $\pm m$ 两根复数基函数线性组合成一对实函数（$\cos$ / $\sin$ 形式）。好处：

- 系数是实数，可以直接塞进 float；
- 颜色不再需要复平面，一个通道一个系数；
- 结果等价（只是基函数换了正交基），误差一样。

本文以下出现的 $Y_l^m$ 全部指**实数 SH，且已归一化**（正交归一：$\int Y_i Y_j\, d\omega = \delta_{ij}$）。

---

## 2. "怎么看"球谐的阶数（Bands）和形状？

通常你会在资料上看到一张金字塔形的图，从上往下分为一层一层（称为阶：Bands）。**第 $l$ 阶有 $2l+1$ 个基函数**，所以前 3 阶一共 $1+3+5=9$ 个：

- **第 0 阶（1 个参数，常数波，$l=0$）**：
  就像一个完美的圆球，没有任何凸起。它记录的是环境光的**平均整体颜色**（比如色调是蓝色的天光）。
- **第 1 阶（3 个参数，偶极波，$l=1$）**：
  长得像哑铃（上下、左右、前后），一边凸起一边凹陷。它负责记录环境光在 X, Y, Z 三个轴向上的**颜色渐变 / 对立关系**（比如天空是蓝的、地面是绿的，体现了 Y 轴上的明暗差）。
- **第 2 阶（5 个参数，四极波，$l=2$）**：
  长得像四叶草。它记录了环境光在三个基础轴之间倾斜交叉方向的**更细碎的亮暗对比**（比如太阳在斜 45 度角产生的晕轮）。

阶数越高，"波"的褶皱越频密，能重建的细节就越锐利。
每一组形状都在数学上是绝对固定的公式。你只需要给这 9 个公式分别乘上一个颜色系数（RGB），加起来，就能在三维空间中还原出环境光的平滑分布。

### 9 个基函数的显式公式（直角坐标版，抓虫必备）

把单位方向 $\mathbf{d}=(x,y,z)$ 代进去即可。注意这些"魔法常量"就是判定 SH 的铁证：

| 索引 $i$ | $(l,m)$ | 基函数 $Y_i(x,y,z)$ | 常用常量写法 | 形状 |
|---------|---------|---------------------|-------------|------|
| 0 | (0,0) | $\dfrac{1}{2\sqrt{\pi}}$ | `0.282095` | 球（常数） |
| 1 | (1,-1) | $\dfrac{\sqrt{3}}{2\sqrt{\pi}}\,y$ | `0.488603 * y` | 哑铃（Y 轴） |
| 2 | (1,0) | $\dfrac{\sqrt{3}}{2\sqrt{\pi}}\,z$ | `0.488603 * z` | 哑铃（Z 轴） |
| 3 | (1,1) | $\dfrac{\sqrt{3}}{2\sqrt{\pi}}\,x$ | `0.488603 * x` | 哑铃（X 轴） |
| 4 | (2,-2) | $\dfrac{\sqrt{15}}{2\sqrt{\pi}}\,xy$ | `1.092548 * x * y` | 四叶草 |
| 5 | (2,-1) | $\dfrac{\sqrt{15}}{2\sqrt{\pi}}\,yz$ | `1.092548 * y * z` | 四叶草 |
| 6 | (2,0) | $\dfrac{\sqrt{5}}{4\sqrt{\pi}}\,(3z^2-1)$ | `0.315392 * (3z*z - 1)` | 双叶（竖） |
| 7 | (2,1) | $\dfrac{\sqrt{15}}{2\sqrt{\pi}}\,xz$ | `1.092548 * x * z` | 四叶草 |
| 8 | (2,2) | $\dfrac{\sqrt{15}}{4\sqrt{\pi}}\,(x^2-y^2)$ | `0.546274 * (x*x - y*y)` | 四叶草 |

> **术语坑**：中文资料里的"三阶球谐"，指的是 $l=0,1,2$ 这一共 3 层（Band 1/2/3），不是 $l=3$。看到"3 阶 9 个系数"和"$l\le2$"说的是同一件事。

---

## 3. 数学骨架：投影与重构（为什么"只存系数"是合法的）

球谐之所以能当"压缩算法"用，靠的是正交归一性（附录 A 有白话解释）：

$$\int_{S^2} Y_i(\mathbf{d})\,Y_j(\mathbf{d})\, d\omega = \begin{cases}1 & i=j\\ 0 & i\neq j\end{cases}$$

**投影（编码）**：拿球面上的光照 $L$ 去和每个基函数"点乘"（内积）一次：

$$c_i = \int_{S^2} L(\mathbf{d})\,Y_i(\mathbf{d})\, d\omega$$

内积的物理含义就是"这份光照里，第 $i$ 个鼓包形状占了多少分量"。

**重构（解码）**：把系数乘回基函数再加起来：

$$L(\mathbf{d}) \approx \sum_{i=0}^{N-1} c_i\, Y_i(\mathbf{d})$$

用 9 个系数（$N=9$）就是"只用前 3 阶"，这也常写作 $\tilde{L}$。

### 代码怎么写？——还是黎曼和，只是被积函数换了
在计算机里 $d\omega$ 不存在，所以把积分降级成累加（同 `IBL_Reference.md` 附录 C 的黎曼和）。**注意每个 texel 要乘上它自己占的立体角权重**，否则两极附近会算重：

```csharp
// 离线烘焙：把一张天空盒 / Cubemap 投影成 9 个系数（每通道一套）
// 需要预先为每个 texel 算好：方向 d + 该 texel 占的立体角 dω
float[] coeffR = new float[9], coeffG = new float[9], coeffB = new float[9];

for (int face = 0; face < 6; face++)
for (int v = 0; v < N; v++)
for (int u = 0; u < N; u++)
{
    Vector3 d = TexelDir(face, u, v);            // 单位方向（texel 中心指向）
    Color   L = cubemap.GetPixel(face, u, v);    // 该方向来的辐射度（必须线性空间）
    float   w = TexelSolidAngle(face, u, v);     // 该 texel 的 dω（≈ 4π / 总texel数，边缘略小）

    float[] Y = SHBasis9(d);                     // 9 个基函数在该方向的值（见上表）

    for (int i = 0; i < 9; i++)
    {
        coeffR[i] += L.r * Y[i] * w;             // 就是  ∫ L·Y dω  的黎曼和
        coeffG[i] += L.g * Y[i] * w;
        coeffB[i] += L.b * Y[i] * w;
    }
}

float[] SHBasis9(Vector3 d)                  // 直接照抄第 2 节的表格
{
    float x = d.x, y = d.y, z = d.z;
    return new float[9] {
        0.282095f,
        0.488603f * y, 0.488603f * z, 0.488603f * x,
        1.092548f * x * y, 1.092548f * y * z,
        0.315392f * (3f * z * z - 1f),
        1.092548f * x * z, 0.546274f * (x * x - y * y),
    };
}
```

> 实务提醒：`GetPixel` 只能拿 LDR 颜色，HDR 天空盒要用 `GetPixelData<float>()` 或直接丢给 Compute Shader；另外 `w` 的精确值应该是该 texel 在球面上的投影立体角，近似用 $4\pi/N_{total}$ 也行（差异会体现为环境光整体亮度略微偏差）。

（Unity 里也可以完全不算这段循环，直接用 `SphericalHarmonicsL2.AddAmbientLight()` / `AddDirectionalLight()`，或者读天空盒烘焙好的 `RenderSettings.ambientProbe`。）

**重构**就是原文第 4 节点出的那种"把法线代进固定方程求和"：

```hlsl
// 运行时求值：给定宏观法线，直接得到该方向的环境漫反射颜色
float3 SHIrradiance(float3 n)
{
    // URP：一句搞定（内部 = SHEvalLinearL0L1 + SHEvalLinearL2 + 钳位）
    return SampleSH(n);
}
```

---

## 4. 从 Radiance 到 Irradiance：那个"模糊"其实是数学卷积

原文只说了"漫反射是低频的"，这里补上为什么。

Irradiance 的定义是**入射辐射度 $L$ 与 clamped cosine 核 $\max(0,\mathbf{n}\cdot\omega)$ 的卷积**：

$$E(\mathbf{n}) = \int_{\Omega} L(\omega)\, \max(0,\,\mathbf{n}\cdot\omega)\, d\omega$$

卷积定理告诉我们：**在球谐域里，卷积 = 逐系数相乘**（证明见附录 B）。于是：

$$E(\mathbf{n}) \approx \sum_{l,m} \bar{A}_l\, c_l^m\, Y_l^m(\mathbf{n})$$

其中 $\bar{A}_l$ 是 clamped cosine 核在第 $l$ 阶上的"缩放比例"，它是解析解（Ramamoorthi & Hanrahan, 2001）：

| 阶 $l$ | $\bar{A}_l$ | 数值 | 含义 |
|--------|-------------|------|------|
| 0 | $\pi$ | 3.141593 | 平均亮度，全额保留 |
| 1 | $\dfrac{2\pi}{3}$ | 2.094395 | 缩到约 67% |
| 2 | $\dfrac{\pi}{4}$ | 0.785398 | 缩到 25% |
| 3 | $0$ | 0 | 奇数阶全部归零 |
| 4 | $\pi\cdot\left(-\dfrac{1}{24}\right)$ | −0.130900 | 已经小到无关紧要，而且**是负数** |
| ≥5 | 奇数阶为 0，偶数阶以 $\dfrac{1}{l^2}$ 量级衰减 | ~0 | 完全忽略即可 |

> 第 4 行只是给你一个"衰减有多快"的直觉。不同文献对高阶 $\bar{A}_l$ 的归一化写法不完全一致，但前 3 行的 $\pi,\ \tfrac{2\pi}{3},\ \tfrac{\pi}{4}$ 是一致的。

这张表就是"3 阶够用"的**唯一原因**：

1. 核本身把每一阶的能量按 $\pi : \frac{2\pi}{3} : \frac{\pi}{4}$ 的速度往死里压；
2. **$\bar{A}_1$ 之后就急剧衰减，$l\ge3$ 的奇数阶直接为 0**（clamped cosine 关于 $\mathbf{n}$ 轴对称，只有偶次项），所以 $l=4$ 及以后即使算出来也只有千分之几的量级；
3. 论文实测：**只用 9 个系数（前 3 阶），就能还原 Photographic 级环境光照下约 99.2% 的能量**。

你继续算第 4 阶、第 5 阶，得出来的模型颜色不仅看不出肉眼区别，还白费计算量。
（顺带一提，$l=4$ 项是**负**的，这也解释了为什么硬算高阶会出现"过冲 / 负颜色"——那是截断后的振铃，见第 8 节。）

### 引擎源码里的旁证（本机实锤）
上面那张表不是"论文里才有的东西"，Unity 的源码里就躺着同一组数（`SRP Core/ShaderLibrary/SphericalHarmonics.hlsl`）：

```hlsl
// 基函数常量：与第 2 节表格里的 0.282095 / 0.488603 / 1.092548 / 0.315392 / 0.546274 完全一致
#define kSHBasis0  0.28209479177387814347f // {0, 0} : 1/2 * sqrt(1/Pi)
#define kSHBasis1  0.48860251190291992159f // {1, 0} : 1/2 * sqrt(3/Pi)
#define kSHBasis2  1.09254843059207907054f // {2,-2} : 1/2 * sqrt(15/Pi)
#define kSHBasis3  0.31539156525252000603f // {2, 0} : 1/4 * sqrt(5/Pi)
#define kSHBasis4  0.54627421529603953527f // {2, 2} : 1/4 * sqrt(15/Pi)

// Clamped cosine convolution coefs (pre-divided by PI)
#define kClampedCosine0 1.0f
#define kClampedCosine1 2.0f / 3.0f
#define kClampedCosine2 1.0f / 4.0f
```

看清楚 `pre-divided by PI` 这行注释：**Unity 的工作约定是"辐照度不含 $\frac{1}{\pi}$"，所以核系数是 $1,\ \frac{2}{3},\ \frac{1}{4}$ 而不是 $\pi,\ \frac{2\pi}{3},\ \frac{\pi}{4}$**——两者就差一个 $\pi$ 因子。这也正是 Seb Lagarde 那篇著名的 *Pi or not to Pi in Game Lighting Equation* 在吵的事。**看到别人的 SH 代码亮度差了 3.14 倍，八成是这里没对齐，而不是公式写错了。**

### ⚠️ 一个必须澄清的点：你存的究竟是哪一套系数？
- **Radiance SH**：$c_l^m$ 直接描述"环境光本身"，没乘 $\bar{A}_l$。想用它算漫反射，运行时还得乘核系数。
- **Irradiance SH（已卷积）**：存之前就把 $\bar{A}_l$（以及基函数的归一化常量）折进去了，运行时**只能做纯线性组合，不能再乘任何常数**。

Unity 的 `unity_SHAr / unity_SHAg / unity_SHAb / unity_SHBr / ...` 存的就是**第二套（已经按上面那套 `kClampedCosineCoefs` + `kSHBasis` 折算好的）**。所以你在 shader 里看到 `dot(unity_SHAr, normal)` 时，前面**没有** `0.886227`、`3.141593` 之类的额外乘法——常量已经被烘焙进系数里了。

---

## 5. Unity 落地：7 个 `real4` 变量 + `SampleSH9` 逐行拆解

Unity 用 L2（$l\le2$）球谐，RGB 每通道 9 个系数，一共 27 个 float。在 shader 里它被整理成 7 个 `real4`（28 个 float，多出来那个是 padding）：

```hlsl
// URP 14: Library/PackageCache/.../universal@14.0.12/ShaderLibrary/UnityInput.hlsl
real4 unity_SHAr;   // R 通道的 l=0 与 l=1 系数
real4 unity_SHAg;
real4 unity_SHAb;
real4 unity_SHBr;   // R 通道的 l=2 系数（前 4 个）
real4 unity_SHBg;
real4 unity_SHBb;
real4 unity_SHC;    // 三通道共用的最后一项 (x²-y²)
```

> 注意：URP / SRP Core 下是 `real4`（`half4` 或 `float4` 视平台精度而定）；老的内置管线 `UnityCG.cginc` 里是 `half4` / `float4`。变量名一致，布局一致。

```hlsl
// 真正的实现（就在你本机的包里，可以直接打开对着看）
// Library/PackageCache/com.unity.render-pipelines.core@14.0.12/ShaderLibrary/SphericalHarmonics.hlsl
// Ref: "Efficient Evaluation of Irradiance Environment Maps" from ShaderX 2

// L0 + L1：把常量项藏在 w 里，用 vA.w = 1 一并点乘掉
real3 SHEvalLinearL0L1(real3 N, real4 shAr, real4 shAg, real4 shAb)
{
    real4 vA = real4(N, 1.0);
    real3 x1;
    x1.r = dot(shAr, vA);
    x1.g = dot(shAg, vA);
    x1.b = dot(shAb, vA);
    return x1;
}

// L2：前 4 项一次点乘，最后 (x²-y²) 项单独乘
real3 SHEvalLinearL2(real3 N, real4 shBr, real4 shBg, real4 shBb, real4 shC)
{
    real3 x2;
    real4 vB = N.xyzz * N.yzzx;              // = (x*y, y*z, z*z, x*z)
    x2.r = dot(shBr, vB);
    x2.g = dot(shBg, vB);
    x2.b = dot(shBb, vB);

    real  vC = N.x * N.x - N.y * N.y;
    real3 x3 = shC.rgb * vC;
    return x2 + x3;
}
```

再往上一层（URP 的实际调用链），注意它做了两件"约定收尾"的事——**钳位**与**颜色空间**：

```hlsl
// URP: ShaderLibrary/GlobalIllumination.hlsl
half3 SampleSH(half3 normalWS)
{
    real4 SHCoefficients[7];        // 7 个 real4 就是那 27 个系数
    SHCoefficients[0] = unity_SHAr;
    SHCoefficients[1] = unity_SHAg;
    SHCoefficients[2] = unity_SHAb;
    SHCoefficients[3] = unity_SHBr;
    SHCoefficients[4] = unity_SHBg;
    SHCoefficients[5] = unity_SHBb;
    SHCoefficients[6] = unity_SHC;

    // 关键 1：max(0, ...) 钳位 —— 截断振铃会产生负颜色，必须掐掉
    return max(half3(0, 0, 0), SampleSH9(SHCoefficients, normalWS));
}

// SRP Core: SampleSH9 内部
half3 SampleSH9(half4 SHCoefficients[7], half3 N)
{
    half3 res = SHEvalLinearL0L1(N, ...);        // L0 + L1
    res += SHEvalLinearL2(N, ...);               // L2
#ifdef UNITY_COLORSPACE_GAMMA
    res = LinearToSRGB(res);                     // 关键 2：Gamma 工作空间时补一次转换
#endif
    return res;
}
```

这就是你以后在反编译 / 抄来的代码里会看到的东西。对外你只需要记住一个 API：

```hlsl
// 本仓库示例：Assets/球鞋/NormalVisualizer.shader:74
half3 ambient = SampleSH(normalWS);   // 无纹理采样，纯代数
```

> **移动端的两条捷径**：URP 用宏切换 SH 的求值位置——`EVALUATE_SH_VERTEX`（整体丢到顶点算，L0+L1+L2 全顶点）、`EVALUATE_SH_MIXED`（顶点只算 L2，L0+L1 留给像素，正好对应"高频项在顶点算会闪、低频项在顶点算没问题"）。抓虫时看到 `SampleSHVertex` / `SampleSHPixel` 这对函数，都是在干这件事。

### 数值映射对照（想自己实现时的坑）
因为 Unity 做了"常量折叠 + 重新排序"，**不要**照抄第 2 节的数学表去对上变量下标。真实对应关系是：

| 变量 | 里面装的是 | 点乘的向量 |
|------|-----------|-----------|
| `unity_SHAr` | R 通道的 $l=0$ + $l=1$（共 4 个系数） | `normal.xyzw`（w 取 1 带出常数项） |
| `unity_SHBr` | R 通道的 $l=2$ 前 4 个 | `(x*y, y*z, z*z, x*z)` |
| `unity_SHC` | RGB 三个通道 $l=2$ 的第 5 个 $(x^2-y^2)$ 项 | `(x*x - y*y)` |

> 记忆技巧：**L1 用 `normal` 点乘，L2 用 `normal` 的二次项点乘**——规律一致，只是幂次不同。

---

## 6. 为什么说它"以算力换带宽"？摆个账

**传统 Cubemap 烘焙 Irradiance（本项目 `Assets/PBR/MyPBR.shader:73` 的做法）：**
你要把一张 128x128 乘 6 面的环境图存进内存（$128^2\times6\times4\ \text{B} \approx 393\text{KB}$/张，还不算 Mip），片段着色器每渲染一个像素，都要去显存里 `texCUBE()` 采样 6 面中正确的像素插值。带宽开销大，且容易在极点或者纹理交界处出缝隙。

```hlsl
float3 irradianceColor = SAMPLE_TEXTURECUBE(_IrradianceMap, sampler_IrradianceMap, normalWS);
```

**球谐烘焙（Spherical Harmonics）：**

1. 烘焙时，直接把巨大的全景光照空间投影（点乘）到这 9 个基函数上，压缩成 **9 组小小的 RGB 浮点光照系数（共 27 个 float = 108 字节）**。
2. 渲染时，你不需要采样任何纹理。直接把表面的宏观法线 `normalWS` 的 x、y、z 分量，带入那 9 个数学基函数的固定方程计算式里，用常数算术求和，就能立刻得出这个方向环境照过来的总体颜色。

| 对比项 | IrradianceMap (Cubemap) | 球谐 SH (L2) |
|--------|------------------------|--------------|
| 数据量 | ~393 KB（128²×6×4B） | 108 B（27 float） |
| 运行时开销 | 1 次 cube 采样（+ LOD 计算、双线性插值） | ~20 条 `mad`/`dot`，无采样单元占用 |
| 接缝 | 面与面交界 / 极点可能出缝 | 天然连续（基函数是光滑解析式） |
| 细节 | 可保留（取决于分辨率） | 只保留到 $l=2$，**必然糊** |
| 插值 | 纹素插值 | 系数线性插值 = 重构后插值（线性算子可交换） |

**这就是图形学中极致的"以算力（简单的加乘代数）换显存带宽（省掉几 MB 的 Cubemap 贴图采样）"的艺术。**
在 Unity 引擎中，Light Probe（光照探针）也是靠存储各个位置的少量球谐系数，去照亮动态物体的。

### 顺带一提：SH 和 IrradianceMap 是同一件事的两种存法
两者都只是"Irradiance 这个低频函数"的载体，所以：

- 用 SH 求出的颜色，和采 IrradianceMap 得到的颜色，**在低频上是等价的**；
- 那些"常量系数 + `N.x*N.y` / `N.z*N.z` 交叉相乘"的代码，就是 SH 版的 `texCUBE(N)`。

---

## 7. Light Probe：把 SH 搬到空间的每一个点

只有 9 个系数时，你得到的是"整张天空盒的环境光"。但真实场景里，物体周围的环境光随位置变化——角落更暗、窗口更亮。Unity 用 **Light Probe（光照探针）** 解决：

1. **每个探针 = 一份 Irradiance SH（27 个 float）**。艺术家把探针撒成一圈 / 一网格。
2. 动态物体查询时，用 **Delaunay 四面体剖找到包裹物体的那 4 个探针，按其重心坐标混合 4 份 SH。
3. 混合的就是**系数**——因为重构是线性的，插值系数再重构 ≡ 重构后再插值，所以这一步在数学上完全合法。
4. 环境光（天空盒）本身则存在 `RenderSettings.ambientProbe` 里，作为全局的"第 0 号探针"。

```csharp
// 运行时拿一份插值好的探针（Unity C# 侧）
SphericalHarmonicsL2 probe = new SphericalHarmonicsL2();
LightProbes.GetInterpolatedProbe(worldPos, renderer, out probe);
```

常见配套技术：

| 技术 | 作用 | 与 SH 的关系 |
|------|------|-------------|
| Light Probe Group | 空间插值环境光 | 存 Irradiance SH |
| Light Probe Proxy Volume (LPPV) | 大物体内部也能插值 | 存 SH 到 3D 纹理，逐像素查 |
| 反射探针 (Reflection Probe) | 高光 | **不用 SH**，用预滤波 Cubemap（因为高频） |
| 减法探针 (Subtractive / Shadowmask) | 阴影 + 环境光混用 | 与 SH 拼接使用 |

---

## 8. SH 的能力边界与踩坑清单

球谐不是万能的，原文的"99%"有个重要的前提：**那是针对漫反射（低频）而言的**。

1. **只能表达低频**：$l\le2$ 只有 9 个自由度，不可能表达"半个天被墙挡住"这种锐利的明暗变化。所以：
   - 漫反射环境光 → SH 完美；
   - **环境高光（镜面反射）→ 必须用预滤波 Cubemap**，因为高光本身是高频的（粗糙度越低越锐利）。这就是 `IBL_Reference.md` 里 Part 1 / Part 2 的由来。
   - 硬阴影、自遮挡 → 必须靠 Lightmap / Shadowmask / AO，SH 无能为力。
2. **振铃（Ringing / 过冲）**：强行截断高阶（比如用 $l\le4$ 去逼近一个有锐利边界的光照）会出现负颜色和边缘振铃——和音频里"削掉高频后出现的 Gibbs 现象"是同一种病。L2 因为核系数衰减快，一般安全；Unity 还在 `SampleSH` 里加了 `max(0, ...)` 兜底（见第 5 节源码），你若自己实现别忘了这一句。
3. **旋转很麻烦**：
   - 对 $l=0$ 的旋转：无影响；
   - 对 $l=1$：等价于一次 3×3 矩阵乘法（可以理解成把"偶极子"转过去）；
   - 对 $l=2$：需要一个 5×5 矩阵；
   - 一般做法是分解成 $R_z(\alpha)R_y(\beta)R_z(\gamma)$ 或用 zonal harmonics 的绕单轴旋转公式。**这就是为什么引擎宁愿重新烘焙，也不愿在运行时旋转一套 SH。**
4. **颜色空间**：SH 系数必须存**线性空间**的颜色。如果误在 sRGB 空间烘焙，还原出来的环境光会偏暗 / 偏灰。Unity 会在 `UNITY_COLORSPACE_GAMMA` 下于 `SampleSH9` 里补一次 `LinearToSRGB`（第 5 节源码），所以你在 Gamma 工程里看到这段转换不要以为写错了。
5. **HDR 截断**：太阳这种超亮光源，投影到 L2 后会"摊平"——因为 L2 记不住"一个点极亮"，只会把它平均成大范围的亮度提升。想要太阳形状的光斑，请交给 specular IBL。
6. **别重复乘常量**（回顾第 4 节）：Unity 给的系数已经是"可直接用的 Irradiance SH"，手贱再乘个 $\pi$ 就会亮三倍多。
7. **探针插值对跨墙场景不适用**：四面体插值会把墙两侧的光"串味"，需要 LPPV 或手动加探针。

---

## 9. 抓虫指南（GLSL / HLSL 逆向痕迹）

- **看到常量 `0.282095` / `0.488603` / `1.092548` / `0.315392` / `0.546274`** → 100% 是实数球谐基函数（第 2 节表格），周围一定有 9 项求和。
- **看到 UE 风格的常量 `0.429043` / `0.511664` / `0.743125` / `0.886227` / `0.247708`** → 这是把 $\bar{A}_l$ 与基函数归一化折叠之后的"简化常量版" SH 求值（注意 `0.886227 = √π/2`，正是核的 0 阶系数）。
- **看到把法线 `N` 的 x, y, z 交叉相乘相加（`N.x*N.y`、`N.z*N.z`、`N.x*N.x - N.y*N.y`）** → 同上，那是 L2 的四个四叶草 + 双叶项。
- **看到 `SampleSH(normalWS)`（URP）/ `ShadeSH9(half4(N,1))`（内置管线）/ `SampleSH9(SHCoefficients, N)`（SRP Core）** → Unity 的环境光解析求值，**前面不需要任何纹理**。
- **看到采样 Cubemap 但坐标只用了法线 `N`、而且图非常糊** → 那是 SH 的"传统替代品" IrradianceMap，两者可以互相替换（本仓库 `MyPBR.shader` 用的是这一路）。
- **看到 7 个 `real4` 的 `unity_SHAr / unity_SHAg / unity_SHAb / unity_SHBr / unity_SHBg / unity_SHBb / unity_SHC`** → 不用猜了，Unity 的 Light Probe / 环境光 SH（27 个系数 + 1 个 padding）。
- **看到 `kSHBasis0..4` 与 `kClampedCosine0..2`** → Unity 自己的 SH 工具函数，前面那句 `pre-divided by PI` 注释就是"这套约定不含 $1/\pi$"的印章。
- **看到 `SampleSHVertex` / `SampleSHPixel` 成对出现** → 移动端的 SH 求值位置优化（顶点算 L2、像素算 L0+L1，或全部顶点算）。

### 一分钟自检
想验证一段 SH 代码对不对，只需求：
1. 传一个全白环境（所有系数只有 $c_0^0 = 1$，其余为 0）→ 输出的应当是**所有方向都一样**的均匀颜色；
2. 传一个"上白下黑"的偶极光（只有 $c_1^0$ 非零）→ 输出应当随 `normal.y` 从亮到暗单调过渡；
3. 把法线取反再求一遍 → 结果应当等于"环境光上下翻转"后的值（偶极项符号取反）。

---

# 附录：白话数学补充

## 附录 A：正交归一性——为什么"投影"就能把系数拆出来？

回到傅里叶：为什么 $\cos(3t)$ 的振幅可以直接从 $f(t)$ 里"抠"出来？因为 $\int_0^{2\pi} \cos(3t)\cos(5t)dt = 0$（不同频率互不干扰），而 $\int \cos(3t)\cos(3t)dt \neq 0$。
球谐同理：**不同的 $Y_l^m$ 在球面上互相"垂直"**（内积为 0），所以：

$$c_i = \int_{S^2} L(\mathbf{d})Y_i(\mathbf{d})d\omega
= \int \Big(\sum_j c_j Y_j\Big)Y_i\, d\omega
= \sum_j c_j \underbrace{\int Y_j Y_i\, d\omega}_{\delta_{ij}} = c_i$$

**翻译成程序员语言**：这套基函数构成一组正交基，投影就是"解方程"，每个系数可以独立求解，互不干扰。这也是为什么"存 9 个数"不会丢失别人信息，只是丢掉了"第 3 阶以后那些没被记录的分量"。

## 附录 B：卷积定理——为什么核只影响系数、不影响形状？

球面上的卷积定义为"把核 $\kappa$ 转到每个方向上去做加权平均"：

$$(L * \kappa)(\mathbf{n}) = \int_{S^2} L(\omega)\,\kappa(\mathbf{n}\cdot\omega)\, d\omega$$

把 $\kappa$ 按 zonal harmonics（仅依赖夹角的球谐）展开成 $\kappa = \sum_l \hat{\kappa}_l Y_l^0$，可以证明：

$$(L * \kappa)_l^m = \hat{\kappa}_l \sqrt{\frac{4\pi}{2l+1}}\; c_l^m
\qquad\Longrightarrow\qquad
E_l^m = \bar{A}_l \cdot c_l^m,\quad \bar{A}_l = \hat{\kappa}_l\sqrt{\tfrac{4\pi}{2l+1}}$$

也就是**在球谐域里，卷积退化成"每个系数乘一个只跟阶数 $l$ 有关的数字"**。

- 这就是为什么第 4 节里 $\bar{A}_l$ 与方向、与 $m$ 全都无关，只跟 $l$ 有关；
- 这也解释了 ray tracing 里"漫反射 = 极度模糊"的直觉：核把 $l=1$ 砍到 2/3、$l=2$ 砍到 1/4、$l\ge3$ 的奇数阶砍到 0，等于一台"低通滤波器"。

顺手验一下常量（$\kappa = \max(0,\cos\theta)$，这里 $\hat{\kappa}_0$ 是核的**展开系数**，不是平均值）：

$$\hat{\kappa}_0 = \int_{S^2}\kappa\,Y_0^0\,d\omega
= \frac{1}{\sqrt{4\pi}}\int_{S^2}\max(0,\cos\theta)\,d\omega
= \frac{1}{2\sqrt{\pi}}\cdot\pi = \frac{\sqrt{\pi}}{2} \approx 0.886227$$

$$\Longrightarrow\quad \bar{A}_0 = \hat{\kappa}_0\sqrt{\frac{4\pi}{1}} = \frac{\sqrt{\pi}}{2}\cdot 2\sqrt{\pi} = \pi \;\checkmark$$

两个数字都眼熟：**`0.886227` = 核的 0 阶展开系数（UE 里那个常量）**，**`π` = 上表里第 0 阶要乘的比例**。二者的差别只在于"归一化常量被折叠到哪一层"——这也再次说明，判定一段代码属于哪套约定，比死记常量重要得多。

## 附录 C：那三个"魔法常量"是怎么来的？

以 $Y_0^0$ 为例。球谐的一般形式是：

$$Y_l^m(\theta,\phi) = \underbrace{\sqrt{\frac{(2l+1)}{4\pi}\frac{(l-|m|)!}{(l+|m|)!}}}_{N_l^m}\; P_l^{|m|}(\cos\theta)\; e^{im\phi}$$

对 $l=0,m=0$：连带勒让德 $P_0^0 = 1$，$e^{i0}=1$，于是

$$Y_0^0 = \sqrt{\frac{1}{4\pi}} = \frac{1}{2\sqrt{\pi}} = 0.282095$$

对 $l=1$：$P_1^1 = -\sin\theta$、$P_1^0=\cos\theta$，配合实数化组合（且忽略 Condon–Shortley 相位）后，$x,y,z$ 前面的系数统一为

$$\sqrt{\frac{3}{4\pi}} = \frac{\sqrt{3}}{2\sqrt{\pi}} = 0.488603$$

> **归一化坑**：常量取决于基函数是否正交归一。
> - **正交归一版（本文表格、`SampleSH9` / `ShadeSH9` 语义）**：$x,y,z$ 前是 `0.488603`；
> - **未归一化版（部分论文 / 老 demo）**：基函数写成 $1,\ x,\ y,\ z,\ 3z^2-1,\ \dots$，此时常量全部搬到"系数"那一侧去乘 $\sqrt{4\pi/(2l+1)}$。
>
> 两套只要内部自洽，重构结果完全一致；但**混用两套常量就会出 bug**（典型症状：环境光整体偏亮或偏暗一个倍数）。看到陌生代码时，先确认它属于哪一套。

对 $l=2$：$N_2^m$ 算出来是 $\dfrac{\sqrt{15}}{2\sqrt{\pi}}=1.092548$（$xy,yz,xz$ 项）、$\dfrac{\sqrt{5}}{4\sqrt{\pi}}=0.315392$（$3z^2-1$ 项）、$\dfrac{\sqrt{15}}{4\sqrt{\pi}}=0.546274$（$x^2-y^2$ 项）。

**你不需要会推**——只要认识这几个数字，就能在任何 shader 里一眼认出球谐，这就是抓虫的意义。

## 附录 D：常见疑问速查（FAQ）

- **Q：到底是 9 个还是 27 个？**
  A：**9 个基函数 × 3 个颜色通道 = 27 个 float**。说"9 个"是基函数层数，说"27 个"是实际存储量。Unity 把这 27 个数装进 7 个 `real4`（`unity_SHAr ... unity_SHC`，第 7 个只用到 xyz，多出的分量是 padding）；内置管线的 `half4` / SRP Core 的 `real4` 是同一套布局，只是精度不同。
- **Q：为什么第 3、5 阶（奇数阶）是 0？**
  A：clamped cosine 核关于法线轴对称，展开成 zonal harmonics 时奇数阶积分恰好为 0（第 4 节表格）。这也是为什么"再多算一阶"常常什么都没买到。
- **Q：SH 能算高光吗？**
  A：理论上能（把 BRDF 的球谐系数和光照系数逐阶相乘），但现实里不行：粗糙度低时 BRDF 波瓣极窄，需要很高阶（几十阶）才能表达，性能还不如查预滤波 Cubemap。**这就是 IBL 把 Diffuse 给 SH、Specular 给 Cubemap 的分工逻辑。**
- **Q：为什么可以只存一份 SH 然后在 shader 里任意方向求值？**
  A：因为重构出来的是一整张"球面上的函数"，不是"某个方向的值"。你给它任何方向 $\mathbf{n}$，它立刻吐出该方向的环境漫反射颜色。
- **Q：移动端有什么不同？**
  A：不是精度变了，而是**求值位置**变了。URP 用 `EVALUATE_SH_VERTEX`（全丢顶点算）和 `EVALUATE_SH_MIXED`（顶点算 L2、像素算 L0+L1）来换性能，对应 `SampleSHVertex` / `SampleSHPixel` 两个函数（第 5 节）。所以在顶点着色器里看到 SH 求值不要惊讶。

## 参考资料

- Ramamoorthi & Hanrahan, *An Efficient Representation for Irradiance Environment Maps*（第 4 节 $\bar{A}_l$ 与 99% 结论的出处）
- Seb Lagarde, *Pi or not to Pi in Game Lighting Equation*（第 4 节 `pre-divided by PI` 那个 $\pi$ 因子的来历）
- Unity Manual, *Light Probes: Technical Information*（`unity_SH*` 的存储布局与 SH 求值约定）
- 本机源码：`Library/PackageCache/com.unity.render-pipelines.core@14.0.12/ShaderLibrary/SphericalHarmonics.hlsl`（基函数常量、clamped cosine 系数、`SampleSH9`）、`.../universal@14.0.12/ShaderLibrary/GlobalIllumination.hlsl`（`SampleSH` / `SampleSHVertex` / `SampleSHPixel`）、`.../universal@14.0.12/ShaderLibrary/UnityInput.hlsl`（`unity_SH*` 声明）
- 知乎专栏《【Unity Graphics】PBR（Physically Based Rendering）间接漫反射》：<https://zhuanlan.zhihu.com/p/6595985305>（本文的"主线"来源：用球谐**表达** IrradianceMap，以解析式替代查表）
- 本仓库配套文档：`IBL_Reference.md`（漫反射积分与 Split-Sum 偷懒法）、`IBL_Reference.md` 附录 C（黎曼和）
