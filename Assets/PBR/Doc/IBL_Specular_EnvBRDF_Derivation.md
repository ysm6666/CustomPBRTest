# 环境 BRDF（Term 2）：从积分到 $F_0A+B$ 再到代码

本文处理 split-sum 的**第二个因子**。第一个因子（预滤波环境贴图）见配套文档 `IBL_Specular_Prefilter_Derivation.md`。两份文档共用符号，本文引用该文档的第 3.4、5 节结论时不重复证明。

路线只有六步，前四步是纯等式变形，后两步是把它写成代码：

| 步骤 | 做什么 | 结果 | 出处 |
|---|---|---|---|
| 1 | $(n\cdot l)$ 相消，提出 $1/(4(n\cdot v))$ | $\text{Term}_2=\frac{1}{4(n\cdot v)}\int D F G\,d\omega_l$ | §1.2 |
| 2 | 用 Schlick 把 $F$ 写成 $F_0$ 的仿射函数 | $F=F_0(1-K)+K$，$K=(1-v\cdot h)^5$ | §1.3 |
| 3 | 代入 $F$，$F_0$ 提出积分号 | $\text{Term}_2=F_0A+B$ | §1.4 |
| 4 | 蒙特卡洛展开 | $A\approx\frac1N\sum(1-K)w$，$B\approx\frac1N\sum Kw$ | §2 |
| 5 | 写代码：定坐标、选 $G$、处理边界、存盘、合成 | 一张 256×256 的 LUT | §3 |
| 6 | 验证 | 三个不变量 | §4 |

---

## 1. 从积分到 $F_0A+B$

### 1.1 起点

$$\text{Term}_2=\int_\Omega f_r(l,v)\,(n\cdot l)\,d\omega_l,\qquad f_r(l,v)=\frac{D(h)\,F(v,h)\,G(l,v,h)}{4\,(n\cdot l)(n\cdot v)},\qquad h=\frac{l+v}{\|l+v\|}$$

### 1.2 第一步：$(n\cdot l)$ 相消

$$\begin{aligned}
\text{Term}_2
&=\int_\Omega \frac{D(h)F(v,h)G(l,v,h)}{4\,(n\cdot l)(n\cdot v)}\,(n\cdot l)\,d\omega_l\\[2pt]
&=\int_\Omega \frac{D(h)F(v,h)G(l,v,h)}{4\,(n\cdot v)}\,d\omega_l\\[2pt]
&=\frac{1}{4\,(n\cdot v)}\int_\Omega D(h)\,F(v,h)\,G(l,v,h)\,d\omega_l
\end{aligned}$$

$(n\cdot l)$ 精确相消；$1/(4(n\cdot v))$ 与积分变量 $l$ 无关，提到积分号外。被积函数里现在只剩 $D$、$F$、$G$。

### 1.3 第二步：把 $F$ 写成 $F_0$ 的仿射函数

Fresnel 用 Schlick 近似：

$$F(v,h)\approx F_0+(1-F_0)(1-v\cdot h)^5$$

记 $K\equiv(1-v\cdot h)^5$，按 $F_0$ 整理：

$$\begin{aligned}
F&=F_0+(1-F_0)K\\[2pt]
&=F_0+K-F_0K\\[2pt]
&=F_0(1-K)+K
\end{aligned}$$

$F$ 对 $F_0$ 是**仿射**的：一次项系数 $(1-K)$、常数项 $K$。

### 1.4 第三步：代入 $F$，$F_0$ 提出积分号

把 §1.3 的 $F=F_0(1-K)+K$ 代进 §1.2 的结果：

$$\begin{aligned}
\text{Term}_2
&=\frac{1}{4\,(n\cdot v)}\int_\Omega D\,G\,\big[F_0(1-K)+K\big]\,d\omega_l\\[2pt]
&=\frac{1}{4\,(n\cdot v)}\left(F_0\int_\Omega D\,G\,(1-K)\,d\omega_l+\int_\Omega D\,G\,K\,d\omega_l\right)\\[2pt]
&=F_0\cdot\frac{1}{4\,(n\cdot v)}\int_\Omega D\,G\,(1-K)\,d\omega_l+\frac{1}{4\,(n\cdot v)}\int_\Omega D\,G\,K\,d\omega_l
\end{aligned}$$

第二行到第三行把 $F_0$ 提出积分号，理由是 $F_0$ 是材质颜色、与积分变量 $l$ 无关。

### 1.5 结论

$$\boxed{\text{Term}_2=F_0\,A+B}$$

$$A=\frac{1}{4\,(n\cdot v)}\int_\Omega D(h)\,G(l,v,h)\,\big(1-K\big)\,d\omega_l,\qquad B=\frac{1}{4\,(n\cdot v)}\int_\Omega D(h)\,G(l,v,h)\,K\,d\omega_l,\qquad K=(1-v\cdot h)^5$$

$A$ 是 $F_0$ 的系数，$B$ 是与 $F_0$ 无关的常数项。三条附注：

1. **$F$ 自然被消掉。** 常见写法是先给 $f_r$ 除以 $F$、再乘回 Schlick 展开，全程保留 $\frac{f_r}{F}$。本文不需要那次"除一乘一"：§1.2 之后被积函数里只剩 $D$、$F$、$G$ 三个独立因子，把 Schlick 直接代入 $F$ 即可。
2. **$A$、$B$ 不带分母。** 这与第一个因子不同：那里 Karis 用 $D$ 替换了 $f_r$，破坏了拆分的恒等性，所以必须补一个归一化分母；这里 $f_r$ 原样保留，$\text{Term}_2$ 就是拆分式里那个不带分母的积分。
3. **$A+B=\frac{1}{4(n\cdot v)}\int_\Omega D\,G\,d\omega_l$**——两个积分里 $K$ 的系数相加恰为 1。§4.1 用它当能量自检。

---

## 2. 蒙特卡洛展开

$A$、$B$ 是两个球面积分，没有解析原函数，用蒙特卡洛。

### 2.1 估计器

$$\int_\Omega f\,d\omega=\int_\Omega\frac{f}{p}\,p\,d\omega\approx\frac1N\sum_{k=1}^N\frac{f(x_k)}{p(x_k)}$$

### 2.2 用什么 pdf

沿用预滤波文档的结论（证明见该文档 §3.4 与 §5）：按 NDF 重要性采样半角向量时

$$p_h(h)=D(h)\,(n\cdot h),\qquad p_l(l)=\frac{D(h)\,(n\cdot h)}{4\,(v\cdot h)}$$

### 2.3 代入 $A$

$$\begin{aligned}
A&\approx\frac1N\sum_k\frac{1}{4\,(n\cdot v)}\cdot\frac{D(h_k)\,G_k\,(1-K_k)}{p_l(l_k)}\\[2pt]
&=\frac1N\sum_k\frac{D(h_k)G_k}{4\,(n\cdot v)}\cdot\frac{4\,(v\cdot h_k)}{D(h_k)(n\cdot h_k)}\cdot(1-K_k)\\[2pt]
&=\frac1N\sum_k\frac{G_k\,(v\cdot h_k)}{(n\cdot v)(n\cdot h_k)}\,(1-K_k)
\end{aligned}$$

第一行代入 §1.5 的 $A$，第二行代入 $p_l$，$D$ 相消。把公共部分命名为单样本权重：

$$\boxed{w_k=\frac{G_k\,(v\cdot h_k)}{(n\cdot v)(n\cdot h_k)}}$$

$B$ 的推导完全相同，只有 $K$ 的位置不同，因此共用同一个 $w_k$：

$$\boxed{A\approx\frac1N\sum_{k=1}^N(1-K_k)\,w_k,\qquad B\approx\frac1N\sum_{k=1}^N K_k\,w_k,\qquad K_k=(1-v\cdot h_k)^5}$$

### 2.4 核心算式就是这四行

```glsl
float G_Vis = (G * VdotH) / (NdotH * NdotV);
float Fc = pow(1.0 - VdotH, 5.0);
A += (1.0 - Fc) * G_Vis;
B += Fc * G_Vis;
// 最后 A /= N; B /= N;
```

| 代码 | 本文符号 |
|---|---|
| `G` | $G_k=G(l_k,v,h_k)$ |
| `VdotH` | $v\cdot h_k$ |
| `NdotH` | $n\cdot h_k$ |
| `NdotV` | $n\cdot v$ |
| `G_Vis` | $w_k$ |
| `Fc` | $K_k$ |

**权重 $w_k$ 里不含 $D$、不含 $4$**：$D$ 被 $p_l$ 约掉，$4$ 与提出来的 $1/(4(n\cdot v))$ 合并。

推导到这里结束。接下来是把它补成一段能跑的代码——过程中会撞上几个必须做决定的地方，逐个处理。

---

## 3. 用代码落实

### 3.1 为什么要烘焙表，以及表上放什么

**为什么不能实时算。** 先把 §2.3 的式子完整摊开——$A$、$B$ 只是标签，它们实际代表的全部内容就是这四行：

$$A\approx\frac1N\sum_{k=1}^N(1-K_k)\,w_k,\qquad B\approx\frac1N\sum_{k=1}^N K_k\,w_k$$

$$K_k=(1-v\cdot h_k)^5,\qquad w_k=\frac{G_k\,(v\cdot h_k)}{(n\cdot v)(n\cdot h_k)},\qquad G_k=G_1(n\cdot l_k)\cdot G_1(n\cdot v)$$

而循环里的每一个 $k$，都要先造出 $h_k$ 和 $l_k$：

$$h_k=\text{GGX采样}(\xi_k,\ n,\ \alpha),\qquad l_k=2\,(v\cdot h_k)\,h_k-v$$

对某个 $k$，上面这些式子合起来需要做的运算，逐项对应如下：

| 算什么 | 在表达式里的位置 | 代价 |
|---|---|---|
| 造 $h_k$ | $K_k$、$w_k$ 里所有 $h_k$ | GGX 采样：1 开方 + 三角函数 |
| 造 $l_k$ | $G_k$ 里的 $l_k$ | 向量运算 |
| 两次 $G_1$ | $G_k$ 的两个因子 $G_1(n\cdot l_k)$、$G_1(n\cdot v)$ | 2 开方 |
| 三个点积 | $w_k$ 的分子 $v\cdot h_k$、分母 $n\cdot h_k$ 与 $n\cdot v$ | 点积 |
| 五次幂 | $K_k=(1-v\cdot h_k)^5$ | 1 次 `pow` |
| 累加 | 两个 $\sum_{k=1}^N$ | —— |

$N=1024$。整套放进着色器里每像素每帧跑，开销无法接受，所以 $A$、$B$ 必须**烘焙一次、运行时查表**。

**那要几维的表。** 表的维度等于结果独立输入的个数。看上面摊开的式子，$A$、$B$ 里出现的所有量是：$h_k$、$l_k$（由 $v$、$\alpha$ 经采样和镜像生成）、四个点积 $n\cdot h_k$、$v\cdot h_k$、$n\cdot v$、$n\cdot l_k$，以及一个幂运算。它们全部由 $v$ 和 $\alpha$ 决定；$F_0$ 已在 §1.4 提出去，不算在内。所以输入是：$v$（视线方向，单位向量，2 个自由度）+ $\alpha$（1 个）= 3 维。

三维表在分辨率和显存上都不划算，先看能不能降维。

**$v$ 的 2 个自由度里有一个是多余的。**

先明确这里说的是哪个积分。§1.5 的两个积分，以 $A$ 为例：

$$A=\frac{1}{4(n\cdot v)}\int_\Omega D(h)\,G(l,v,h)\,(1-K)\,d\omega_l$$

**积分变量**是入射方向 $l$；**积分域** $\Omega$ 是以 $n$ 为轴的上半球；**被积函数**是 $D(h)\,G(l,v,h)\,(1-K)$，其中 $h=\mathrm{normalize}(l+v)$。

**为什么可以让 $n$ 沿 $z$ 轴。** 被积函数只通过点积依赖方向——$n\cdot v$、$n\cdot h$、$n\cdot l$、$v\cdot h$，就是上面摊开的式子里出现的那几个。有两点：

1. 点积在坐标系整体旋转下不变；
2. 积分域 $\Omega$ 是以 $n$ 为轴的半球，也随同旋转不变。

所以把 $n$、$v$ 一起绕 $n$ 轴转任意角度，$A$ 的值不变。既然 $v$ 的方位角取什么值结果都一样，就取最省事的那个：$n$ 沿 $z$ 轴、$v$ 落在 $xz$ 平面内，方位角记作 0。

（$n$ 是表面法线本身，它的世界朝向不是我们选的；这里选的只是**用来写数学的坐标系**。）

**验证。** 把 $v$ 放在 $xz$ 平面内、$h$ 写球坐标：

$$v=(\sin\theta_v,0,\cos\theta_v),\qquad h=(\sin\theta_h\cos\varphi_h,\ \sin\theta_h\sin\varphi_h,\ \cos\theta_h)$$

于是

$$v\cdot h=\sin\theta_v\sin\theta_h\cos\varphi_h+\cos\theta_v\cos\theta_h,\qquad n\cdot l=2\,(v\cdot h)(n\cdot h)-(n\cdot v)$$

$\varphi_h$ 是采样点绕 $n$ 的方位角，它只通过 $v\cdot h$ 里那一个 $\cos\varphi_h$ 项进入被积量。把 $l$ 在整个半球上积分时，$\varphi_h$ 随之扫遍 $0\to2\pi$，**这一步积分把 $\varphi_h$ 消掉，结果里不再含它**——"被积掉"就是这个意思，不是"因为对称所以等于 0"。

剩下 $\theta_v$ 只以 $\cos\theta_v=n\cdot v$ 与 $\sin\theta_v=\sqrt{1-\cos^2\theta_v}$ 出现，两者都由 $n\cdot v$ 唯一确定。所以只剩一个标量自变量：

$$\boxed{A=A(n\cdot v,\ \alpha),\qquad B=B(n\cdot v,\ \alpha)}$$

**表的坐标定下来了**，二维：

| 表轴 | 代码 |
|---|---|
| 横轴 $n\cdot v$ | `NdotV` |
| 纵轴 | `roughness`（表里存 perceptual roughness，采样器内部再算 $\alpha=roughness^2$） |
| R 通道 | $A$（scale，乘 $F_0$） |
| G 通道 | $B$（bias，直接加） |

**烘焙每个格子时挑一个代表位形。** 既然积分只依赖 $n\cdot v$，那对表上任意一个格子，都可以选最方便的那个位形去算：取 $n=z$ 轴、$v$ 落在 $xz$ 平面内。这正是 §3.3 kernel 里 `N = float3(0,0,1)`、`V = (sinθv, 0, cosθv)` 的来历——不是随手写的，是这条对称性允许的。

最后一点便利：$A$、$B$ 与环境贴图无关、与 $n$ 在世界空间的朝向也无关，所以这张表**所有场景共用一张，只烘焙一次**。

### 3.2 采样循环要算 $G$，先定它用哪一套

$w_k$ 里那个 $G_k$ 还没定下具体形式。$G$ 按 Smith 模型分解：

$$G(l,v,h)=G_1(n\cdot l)\cdot G_1(n\cdot v)$$

$G_1$ 有两个候选。

**（a）精确 Smith-GGX**，无自由参数：

$$G_1(x)=\frac{2x}{x+\sqrt{\alpha^2+(1-\alpha^2)x^2}}$$

自检：$\alpha\to0$ 时 $G_1=\frac{2x}{x+x}=1$（完美镜面无遮蔽）；$\alpha=1$ 时 $G_1=\frac{2x}{x+1}$。

**（b）Schlick-GGX 近似**：

$$G_1(x)=\frac{x}{x(1-k)+k},\qquad k_{\text{direct}}=\frac{(roughness+1)^2}{8},\qquad k_{\text{IBL}}=\frac{roughness^2}{2}=\frac{\alpha}{2}$$

两套 $k$ 不是"哪个对"：$k$ 是针对入射光方向分布拟合出来的——直接光假设单一方向、IBL 假设整个半球，拟合结果自然不同。要求是烘焙与运行时用同一套；烘焙用 $k_{\text{IBL}}$、运行时用 $k_{\text{direct}}$，掠射角会出现亮度断层。

**这里选 (a)**：没有参数，少一个出错点。

（记号坑：LearnOpenGL 正文写 $k_{\text{IBL}}=\frac{\alpha^2}{2}$，那里的 $\alpha$ 指 perceptual roughness。按本文约定 $\alpha\equiv roughness^2$，应写作 $k_{\text{IBL}}=\frac{\alpha}{2}$。）

### 3.3 kernel，以及必须处理的四个边界

把 §3.2 选定的 $G$ 与 §2.3 那四行算式拼起来，就是完整的烘焙 kernel：

```hlsl
#pragma kernel CSMainBRDFLUT

#define PI 3.14159265358979323846
#define SAMPLE_COUNT 1024u

RWTexture2D<float2> _BRDFLUT;

// ---- 与 IBLBakerCS.compute 中的同名函数一致，可直接复用 ----
float RadicalInverse_VdC(uint bits) { /* 32 位位反转，见预滤波文档 §8.1 */ }
float2 Hammersley(uint i, uint N)   { return float2(float(i) / float(N), RadicalInverse_VdC(i)); }

float3 ImportanceSampleGGX(float2 xi, float3 n, float roughness)
{
    float a = roughness * roughness;      // a = α
    float phi = 2 * PI * xi.x;
    float cosTheta = sqrt((1 - xi.y) / (1 + (a * a - 1) * xi.y));
    float sinTheta = sqrt(1 - cosTheta * cosTheta);
    float3 H = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
    float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, n));
    float3 bitangent = cross(n, tangent);
    return normalize(tangent * H.x + bitangent * H.y + n * H.z);
}

// ---- 精确 Smith-GGX 单方向遮蔽（§3.2 选 (a)）----
float GeometrySmithG1(float NdotX, float alpha)
{
    float a2 = alpha * alpha;                                   // a2 = α²
    return (2.0 * NdotX) / max(NdotX + sqrt(a2 + (1.0 - a2) * NdotX * NdotX), 1e-6);
}

[numthreads(8, 8, 1)]
void CSMainBRDFLUT(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    _BRDFLUT.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    // 纹素中心取样（§3.1 的两个轴）
    const float NdotV     = (id.x + 0.5) / (float)w;
    const float roughness = (id.y + 0.5) / (float)h;
    const float alpha     = max(roughness * roughness, 1e-4);

    // 标准位形：n 为 z 轴，v 放在 xz 平面内（§3.1）
    const float3 N = float3(0, 0, 1);
    const float3 V = float3(sqrt(max(1.0 - NdotV * NdotV, 0.0)), 0.0, NdotV);

    float A = 0.0, B = 0.0;

    for (uint i = 0; i < SAMPLE_COUNT; ++i)
    {
        float2 xi = Hammersley(i, SAMPLE_COUNT);
        float3 H  = ImportanceSampleGGX(xi, N, roughness);   // 按 p_h = D(n·h) 采样
        float3 L  = normalize(2.0 * dot(V, H) * H - V);

        float NoL = L.z;                    // 因为 N = (0,0,1)
        float NoH = H.z;
        float VoH = saturate(dot(V, H));

        if (NoL > 0.0)                      // 域外样本被积函数为 0
        {
            float G = GeometrySmithG1(NoL, alpha) * GeometrySmithG1(NdotV, alpha);
            float wk = G * VoH / max(NoH * NdotV, 1e-6);   // §2.3 的 w_k
            float K  = pow(1.0 - VoH, 5.0);                // §1.3 的 K_k

            A += (1.0 - K) * wk;
            B +=       K  * wk;
        }
    }

    _BRDFLUT[id.xy] = float2(A, B) / (float)SAMPLE_COUNT;
}
```

四个边界情况，不加会出错：

| 情况 | 出问题的位置 | 现象 | 处理 |
|---|---|---|---|
| `NdotV` 取到 0 | $w_k$ 分母里的 $(n\cdot v)$ | 除零 → NaN、黑斑 | 纹素中心取样使 $n\cdot v>0$，再补 `max(·,1e-6)` |
| $n\cdot l_k<0$ | $G_k=G_1(n\cdot l_k)\cdot G_1(n\cdot v)$ 的**第一个因子** | $G_1(x)$ 在 $x<0$ 时为负，$w_k$ 变负 → 亮斑或暗斑 | `if (NoL > 0)` 丢弃；域外被积函数本应为 0，丢弃等价 |
| `NoH` 极小 | $w_k$ 分母里的 $(n\cdot h_k)$ | 除零 | `max(NoH * NdotV, 1e-6)` |
| `alpha` = 0 | $h_k$ 的生成（$K_k$、$w_k$ 里所有 $h_k$ 都受影响） | $\cos\theta_h=\sqrt{(1-\xi)/\big(1+\xi(\alpha^2-1)\big)}$ 退化 | `max(roughness*roughness, 1e-4)` |

采样数取 1024 个 Hammersley 样本对 256×256 的表绰绰有余——这个积分的方差远小于预滤波卷积，降到 256 通常看不出差异；表只烘焙一次，不必为样本数省。

### 3.4 C# 侧：烘焙与存盘

kernel 写好了，接下来在 C# 里建输出纹理、派发、把结果读回成资产。沿用 `IBLBaker.cs` 的写法：

```csharp
public Texture2D BakeBRDFLUT(int size = 256)
{
    var rt = new RenderTexture(size, size, 0, RenderTextureFormat.RGHalf)
    {
        enableRandomWrite = true,                 // RWTexture2D 必需
        wrapMode   = TextureWrapMode.Clamp,       // 防止边界采样溢出
        filterMode = FilterMode.Bilinear
    };
    rt.Create();

    int kernel = iblBakerCS.FindKernel("CSMainBRDFLUT");
    iblBakerCS.SetTexture(kernel, "_BRDFLUT", rt);
    iblBakerCS.Dispatch(kernel, size / 8, size / 8, 1);   // 对应 numthreads(8,8,1)

    var tex = new Texture2D(size, size, TextureFormat.RGHalf, false);
    RenderTexture.active = rt;
    tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
    tex.Apply();
    RenderTexture.active = null;

    rt.Release();
    return tex;                                   // 再 AssetDatabase.CreateAsset 存盘
}
```

- **分辨率**：256×256 足够（UE 用 128×128，LearnOpenGL 用 512×512）。
- **精度**：Epic 用 RG16F（每通道半精度），`RenderTextureFormat.RGHalf` 对应它；平台不支持时退回 `ARGBHalf` 或 `RGFloat`。`RG16` 只有 8 位/通道，偏低。
- **v 方向自洽**：kernel 里 `id.y` 从 0 开始对应 roughness 从 0 开始，`SAMPLE_TEXTURE2D` 的 $v$ 坐标同样下到上，两边天然一致——只要不对读回的 `Texture2D` 做纵向翻转。

### 3.5 运行时合成

表备好之后，运行时每个像素只需要查一次。查表坐标就是 §3.1 定下的那两个轴：

```hlsl
float3 F0 = lerp(0.04, albedo, metallic);
float2 envBRDF = SAMPLE_TEXTURE2D(_BRDFLUT, sampler_LinearClamp,
                                  float2(NdotV, roughness)).rg;

// Term 1 × Term 2
float3 ambientSpecular = prefiltered * (F0 * envBRDF.x + envBRDF.y);
```

`prefiltered` 是 Term 1 按粗糙度选 mip 采到的值（见预滤波文档 §8.2）；采样坐标就是 §3.1 那两个轴。推导给出的正是 $F_0\cdot A+B$——逐通道乘 $A$ 再加 $B$。

---

## 4. 怎么验证写对了

烘焙完先别看效果，把 LUT 的 R、G 通道分别显示出来，跑下面三条。任何一条不过，都不用往下查。

### 4.1 三个不变量

**（1）$A+B\le1$**

由 §1.5 附注 3，$A+B=\frac{1}{4(n\cdot v)}\int_\Omega D\,G\,d\omega_l$。这正是"令 $F\equiv1$"时的方向-半球反射率，对能量守恒的表面应 $\le1$。

自检：R、G 通道相加取最大值，应 $\le1$。若 $>1$，几乎一定是权重写错（多了 $D$、或漏了 $n\cdot v$）。

高粗糙度下 $A+B$ 会**明显小于 1**——那是单次散射的能量损失（§6.2），不是 bug。

**（2）$n\cdot v\to1$、低粗糙度 ⇒ $A\to1,\ B\to0$**

此时 $l_k\approx h_k\approx n$，于是 $v\cdot h_k\to1$、$n\cdot h_k\to1$；代回 §3.1 的式子得 $K_k=(1-v\cdot h_k)^5\to0$、$w_k\to1$，而 $A$ 里乘的正是 $(1-K_k)$，所以 $A\to1$、$B\to0$，合成结果 $\to F_0$。物理上正确：完美镜面法线入射时反射率就是 $F_0$。对应表的**右上角**。

**（3）$n\cdot v\to0$、低粗糙度 ⇒ $B\to1,\ A\to0$**

掠射时菲涅尔趋 1，结果应趋 1，即由 bias 主导。此时 $h_k$ 仍接近 $n$ 而 $v$ 近乎垂直于 $n$，故 $v\cdot h_k\approx0$；代回 $K_k=(1-v\cdot h_k)^5$ 得 $K_k\approx1$，于是 $A$ 里乘的 $(1-K_k)\approx0$、$B\approx1$。对应表的**右下角**。

这三条决定 LUT 的外观：**红色（$A$）在绝大部分区域接近 1，绿色（$B$）只在"低粗糙度 + 掠射"那个角落隆起。** 绿色若出现在别处，就是坐标轴或 $K_k=(1-v\cdot h_k)^5$ 的写法有问题。

---

## 5. 常见错误对照

| 错误 | 症状 | 原因 |
|---|---|---|
| 权重里带 $D$ | 高光整体过亮，粗糙度越大越亮 | $D$ 已被 $p_l$ 约掉（§2.3） |
| 忘记除 `SAMPLE_COUNT` | 数值放大 1024 倍，全白 | $A,B$ 未取平均 |
| $k$ 用了直接光那套 | 掠射角亮度断层 | §3.2：$k_{\text{IBL}}\neq k_{\text{direct}}$ |
| LUT 两轴写反 | 高光形状完全错乱 | 必须横轴 $n\cdot v$、纵轴 roughness，与 §3.5 采样坐标一致 |
| 把 $\alpha$ 当 roughness 传进 LUT | 粗糙度响应被压缩到极窄区间 | 轴上的量与采样器内部 `a = x*x` 必须成套（§3.1） |
| `NdotV` 传 0 或负 | NaN、黑斑 | 纹素中心取样 + `max(·,1e-6)`（§3.3） |
| 漏掉 `NoL > 0` 判断 | 暗斑或亮斑 | $G_1(x)$ 在 $x<0$ 时为负（§3.3） |
| 读回纹理时纵向翻转 | 粗糙度方向反了 | 烘焙与采样要用同一套 v 方向约定（§3.4） |

---

## 6. 已知偏差与可选改进

### 6.1 用 roughness-aware Fresnel 代替 $F_0$（常见偏离）

LearnOpenGL 运行时写的是

```glsl
vec3 F = FresnelSchlickRoughness(max(dot(N, V), 0.0), F0, roughness);
vec3 specular = prefilteredColor * (F * envBRDF.x + envBRDF.y);
```

即把 $A$ 前面那个位置从**常数 $F_0$** 换成了**依赖 $n\cdot v$ 与 roughness 的 $F$**（Lazarov 的粗糙度修正版 Schlick）。

推导给出的是 $F_0A+B$，$F_0$ 与方向无关；换成 $F$ 会把 $n\cdot v$ 的依赖算两遍——一遍已在 $A$ 里。**它不是上述公式的解，而是一个启发式修正**，实践中往往观感更好（部分补偿了 §6.2 的能量损失）。两者属于不同算法，分别测过再选。

### 6.2 单次散射的能量损失

GGX + Smith 的单次散射模型没有建模微表面之间的多次弹射，高粗糙度下丢失能量，表现为 $A+B$ 明显小于 1——粗糙表面的镜面高光偏暗偏灰。

这是模型缺陷，不是实现错误。补偿方案是在结果上再乘一个多散射因子 $F_{ms}$（Kulla-Conty；实时版本见 Fdez-Agüera 2019）。落地需额外烘焙一张表或做解析拟合，本文不展开。

### 6.3 改用 VNDF 采样（掠射角）

§2.2 用的是 NDF 采样 $p_h=D(n\cdot h)$，它不区分微表面是否被遮挡。当 $n\cdot v$ 很小（掠射）时，大量样本落到看不见的微表面上（$v\cdot h\le0$），被 `if (NoL > 0)` 丢弃，等效样本数骤降——而掠射区恰好是 bias 最重要的地方，也是 LUT 噪声最大的地方。

改用 Heitz 的 VNDF 采样：$p_h(h)=D_v(h)=\dfrac{G_1(v)\,(v\cdot h)\,D(h)}{n\cdot v}$，对应 $p_l(l)=\dfrac{G_1(v)\,D(h)}{4(n\cdot v)}$。代入 §2.3 的第一步，权重随之**简化**：

$$w_k=\frac{1}{4(n\cdot v)}\cdot\frac{D(h_k)\,G\,(1-K)}{p_l(l_k)}=\frac{G\,(1-K)}{G_1(v)}=G_1(n\cdot l_k)\,(1-K_k)$$

（末式用了 Smith 的 $G=G_1(n\cdot l)G_1(n\cdot v)$。）于是

$$A\approx\frac1N\sum_k G_1(n\cdot l_k)(1-K_k),\qquad B\approx\frac1N\sum_k G_1(n\cdot l_k)\,K_k$$

**两个好处**：权重里不再出现 $1/(n\cdot v)$，掠射角的数值爆炸消失；$v\cdot h>0$ 恒成立，不再丢弃样本。这是严格改进（无偏、方差更低），代价只是把采样器换成 VNDF 版本。

一致性自检：$n\cdot v\to1$ 时 VNDF 与 NDF 重合，两个权重都退化为 $G_1(n\cdot l)$——与预滤波文档 §9.1 的结论吻合。

---

## 附录 A：符号表

| 符号 | 含义 | 代码 |
|---|---|---|
| $\alpha$ | GGX 粗糙度参数，$\alpha=roughness^2$ | `a` |
| $n\cdot v$ | 视线与法线夹角余弦（LUT 横轴） | `NdotV` |
| $v\cdot h$ | 视线与半角向量夹角余弦 | `VdotH` |
| $n\cdot h$ | 半角向量与法线夹角余弦 | `NdotH` |
| $n\cdot l$ | 入射方向与法线夹角余弦 | `NoL` |
| $K$ | $(1-v\cdot h)^5$ | `Fc` |
| $F_0$ | 垂直入射反射率 | `F0` |
| $A,\ B$ | LUT 的 scale / bias | `envBRDF.x`、`envBRDF.y` |
| $w_k$ | 单样本权重 $=\frac{G(v\cdot h)}{(n\cdot v)(n\cdot h)}$ | `G_Vis` |
| $G_1$ | Smith 单方向遮蔽 | `GeometrySmithG1` |
| $k$ | Schlick-GGX 的拟合参数 | `k` |

## 附录 B：推导链依赖图

```
Term₂ = ∫ f_r (n·l) dω_l                              [1.1]
 ├─ (n·l) 与 BRDF 分母相消 ⇒ 1/(4(n·v)) ∫DFG dω_l      [1.2]  恒等变形
 ├─ 代入 F = F₀(1-K) + K ⇒ F₀A + B                    [1.3–1.5]
 ├─ p_l = D(n·h) / (4(v·h))                            [2.2]
 ├─ D 相消 ⇒ w = G(v·h) / ((n·v)(n·h))                 [2.3]
 └─ A = mean((1-K)w)，B = mean(Kw)                     [2.3]
      ├─ 定表轴 ⇒ A = A(n·v, roughness)                [3.1]
      ├─ 选 G ⇒ 精确 Smith-GGX                         [3.2]
      ├─ kernel + 四个边界                             [3.3–3.4]
      └─ 合成 ⇒ Prefiltered × (F₀·A + B)               [3.5]
```

## 附录 C：出处

1. **Brian Karis, "Real Shading in Unreal Engine 4", SIGGRAPH 2013 Course.**
   环境 BRDF 的原始出处；用 Schlick 把 $F_0$ 拆成 scale/bias、以及 $k_{\text{IBL}}$ 的取值均来自此文。
   https://blog.selfshadow.com/publications/s2013-shading-course/

2. **LearnOpenGL — Specular IBL.**
   §2.4 的实现逐项对标该文；$k_{\text{IBL}}=\frac{\alpha}{2}$ 与 §6.1 的偏离写法也来自此文。
   https://learnopengl.com/PBR/IBL/Specular-IBL

3. **Eric Heitz, "Sampling the GGX Distribution of Visible Normals", JCGT 2018**（及 2017 技术报告 "A Simpler and Exact Sampling Routine for the GGX Distribution of Visible Normals"）。
   §6.3 VNDF 采样及其权重的来源。
   https://jcgt.org/published/0007/04/01/

4. **Aleksandar Lazarov, "Getting More Physical in Call of Duty: Black Ops II".**
   §6.1 中 `FresnelSchlickRoughness` 的来源。

5. **Alejandro Conty Estevez & Christopher Kulla, "A Multiple-Scattering Microfacet Model for Real-Time Image Based Lighting", JCGT 2019.**
   §6.2 多散射补偿的来源。

6. **Zero Radiance, "Alternative Take on the Split Sum Approximation for Cubemap Pre-filtering".**
   讨论 split-sum 的归一化形式与权重函数差异（$W=FG$ 与 $W\approx\langle n,l\rangle$），经 Karis 本人审阅。
   https://zero-radiance.github.io/post/split-sum/
