# 基于物理的渲染 (PBR) 核心指南与逆向手摸手教程

本文档旨在为初学者和技术美术（TA）提供一份思路清晰、用词统一的 PBR (Physically Based Rendering) 学习资料。特别针对“如何将理论公式与反编译出的 GLSL 代码对应”这一痛点进行了解析。

---

## 一、 统一术语（请死记硬背）

为了避免文档或不同教程中名词混乱，请严格认准以下术语：

### 1. 光源分类
*   **直接光 (Direct Lighting)**：指有明确位置和方向的光源（太阳光/平行光、点光源、聚光灯）。
*   **环境光 (IBL, Image-Based Lighting)**：来自四面八方的环境漫反射与环境高光（天空盒、反射探针）。

### 2. 向量（均需归一化）
*   **$\mathbf{N}$ (Normal)**：表面宏观法线。
*   **$\mathbf{V}$ (View)**：从表面指向**摄像机/眼睛**的观察向量。
*   **$\mathbf{L}$ (Light)**：从表面指向**光源**的光照向量。
*   **$\mathbf{H}$ (Half)**：$\mathbf{V}$ 和 $\mathbf{L}$ 的中间向量（半角向量），公式为 $H = \frac{L + V}{\|L + V\|}$。

### 3. 用到的点乘 (Dot Product)
*   **$\mathbf{N \cdot L}$ (NdotL)**：法线与光照方向的点乘。决定了表面受光背光（如果 $< 0$ 则是背光面）。
*   **$\mathbf{N \cdot V}$ (NdotV)**：法线与观察方向的点乘。决定了视线是否与表面平齐（掠射角）。
*   **$\mathbf{N \cdot H}$ (NdotH)**：用于判定有多少微表面的法线刚好和半角向量重合。
*   **$\mathbf{V \cdot H}$ (VdotH)** 或 **$\mathbf{L \cdot H}$ (LdotH)**：两者总是相等，常用于菲涅尔计算。

### 4. 材质参数 (Metallic-Roughness 工作流)
*   **Albedo (反照率)**：物体的基础颜色（非金属的漫反射颜色，或金属的表面反射率）。
*   **Roughness (粗糙度)**：0表示绝对光滑，1表示绝对粗糙。
*   **Metallic (金属度)**：0表示绝缘体（塑料、木头），1表示纯金属。
*   **$\mathbf{F_0}$ (基础反射率)**：材质当观察角度和法线垂直（正看）时的镜面反射率。

---

## 二、 PBR 的三大物理基石

理解PBR必须先接受三个基本假设：

1.  **微表面模型 (Microfacet Model)**：任何看似平滑的表面，在微观下都是由无数极其微小的“镜面（微表面）”组成的。越**粗糙**的表面，这些微小镜面的朝向就越**混乱**；越**光滑**，微表面朝向就越**一致**。
2.  **能量守恒 (Energy Conservation)**：一束光打到物体上，会被分成两部分：**折射**进入物体内部（随后发生散射，形成**漫反射 Diffuse**）和直接在表面**反射**弹走（形成**镜面高光 Specular**）。这两部分的能量之和不能超过入射光的能量（$Diffuse + Specular \le 1$）。**重要推论：纯金属没有漫反射，光要么被吸收要么被反射。**
3.  **双向反射分布函数 (BRDF, Bidirectional Reflectance Distribution Function)**：这就是那个最核心的渲染方程，用来计算在给定的视线 $\mathbf{V}$ 和给定的光线 $\mathbf{L}$ 下，到底有多少比例的光被弹到了眼睛里。

---

## 三、 直接光 BRDF 核心方程分解

对于一盏灯光，物体表面某点的最终出射光颜色 $L_{out}$ 计算如下：

$$L_{out} = (k_d \cdot f_{lambert} + k_s \cdot f_{cook-torrance}) \cdot L_{in} \cdot (\mathbf{N} \cdot \mathbf{L})$$

*   $L_{in}$ 是灯光的颜色和强度。
*   $\mathbf{N \cdot L}$ 是光的衰减（兰伯特定律，光越斜射光重越弱）。
*   $k_d$ 是能量分配给漫反射的比例，$k_s$ 是高光的比例。($k_d + k_s = 1$)
*   $f_{lambert}$ 是漫反射计算。
*   $f_{cook-torrance}$ 是高光计算。

下面我们逐一拆解如何计算这两个 $f$。

### 1. 漫反射项 (Diffuse) - Lambertian 函数

$$f_{lambert} = \frac{Albedo}{\pi}$$

**解释**：最简单的漫反射模型。光进入材质内部均匀分散。除以 $\pi$ 是为了在半球积分领域保证能量守恒。
**GLSL 找代码技巧**：
你常在逆向时看到材质颜色乘以 `0.318309` (这就是 $1/\pi$)。

### 2. 高光项 (Specular) - Cook-Torrance BRDF模型

这个是 PBR 的灵魂公式（重点掌握）：

$$f_{cook-torrance} = \frac{D \cdot F \cdot G}{4 \cdot (\mathbf{N \cdot V}) \cdot (\mathbf{N \cdot L})}$$

它由三个著名的函数首字母组成：$D$ (法线分布), $F$ (菲涅尔), $G$ (几何遮蔽)。
分母是一个归一化因子（用来校正微表面模型的投影面积），**在图形学代码中，通常会把分母的 4xNxL 和 G 项合并计算，称之为 Visibility 项 (V项)**。

#### (1) D项：法线分布函数 (Normal Distribution Function)
**常用算法**：Trowbridge-Reitz GGX
**物理意义**：当前微观下，有多少个微小镜面的法线刚好是 $\mathbf{H}$？（只有微表面法线朝向 $\mathbf{H}$，光线 $\mathbf{L}$ 才能恰好反射到你的眼睛 $\mathbf{V}$ 里）。$D$ 值直接决定了高光的**形状和大小**。

$$D = \frac{\alpha^2}{\pi \cdot \left((\mathbf{N} \cdot \mathbf{H})^2 \cdot (\alpha^2 - 1) + 1\right)^2}$$

*   Epic/迪士尼工作流中，定义 $\alpha = Roughness^2$。所以在代码里最开始，你总会看到 `alpha = roughness * roughness`。
*   **GLSL 逆向痕迹**：找 `NdotH` 的平方操作，找分母被平方的操作。

#### (2) F项：菲涅尔方程 (Fresnel Equation)
**常用近似算法**：Fresnel-Schlick 近似
**物理意义**：当你看物体的角度越平齐（掠射角），物体的反射能力越强（哪怕是木头也很反光）。
$F_0$ 代表材质基础反射率。非金属的 $F_0$ 很低且不带颜色（业界标准一般定死为 `0.04` (即4%)）。纯金属的 $F_0$ 很高且带有金属本身的颜色（使用 Albedo 作为 $F_0$）。

在代码层面往往会根据 Metallic 进行插值混合：
`F0 = mix(vec3(0.04), Albedo, Metallic);`

核心公式：

$$F = F_0 + (1 - F_0) \cdot (1 - \mathbf{V \cdot H})^5$$

*   **GLSL 逆向痕迹**：如果在反编译里看到一个常数 **`0.04`** 或者看到一个数字/点乘结果被算了 **5次方**（经常优化为两步平方再乘一遍，或者是 `exp2` 函数加魔法数字 `-5.55473`，如 `exp2((-5.55473 * VdotH - 6.98316) * VdotH)` ），这里绝对是在算 F 项。

#### (3) G项：几何函数 (Geometry Function)
**常用算法**：Schlick-GGX
**物理意义**：微表面是凹凸不平的，在某些角度光线会被凸起的部分挡住进不来（Shadowing），或者反射出去后被挡住出不去（Masking）。G项就是为了压暗掠射角的高光，防止高光亮得超标。

$$G = G_{1}(\mathbf{N \cdot V}) \cdot G_{1}(\mathbf{N \cdot L})$$
$$G_{1}(x) = \frac{x}{x \cdot (1 - k) + k}$$

这个 $k$ 是针对直接光的映射方式：$k = \frac{(Roughness + 1)^2}{8}$
*   **GLSL 逆向痕迹**：找除以 8 的常数（乘以 **`0.125`**）。见到了就是在算针对直接光的 $k$。

---

## 四、 工业界 GLSL 中真正的组合大招：Visibility 项 (V)

由于完整的 $f_{cook-torrance}$ 公式分母包含 $4 \cdot (\mathbf{N \cdot V}) \cdot (\mathbf{N \cdot L})$。这存在两个问题：
1. 可能碰到 $\mathbf{N \cdot V} = 0$ 导致除零崩溃。
2. 算 G 还要算除法，浪费性能。

主流引擎（包括 UE, Unity SRP, Google Filament）普遍利用数学推导，把 $G$ 的分子和分母进行了化简，合并出一个叫 **Visibility 项 ($V$)**：

$$V = \frac{G}{4 \cdot (\mathbf{N \cdot V}) \cdot (\mathbf{N \cdot L})}$$

最终的高光光照强度代码长这样：
**`Specular = D * V * F;`**

**常见的 Smith-GGX 联合近似（Appoximation）化简后：**
$V = \frac{0.5}{\mathbf{N \cdot L} \cdot \left(\mathbf{N \cdot V} \cdot (1 - a) + a\right) + \mathbf{N \cdot V} \cdot \left(\mathbf{N \cdot L} \cdot (1 - a) + a\right)}$
*（注：$a = \alpha = roughness^2$。你会发现极其优雅，除零风险小很多，而且没有了原本公式里那一大串分母）*

### 整理能量守恒：
当分别算出了 `Diffuse` 和 `Specular`，引擎是如何防止能量溢出的？
前面算出了 $F$（菲涅尔项），$F$ 实际上代表了被直接反射的光的比例（也就是抢走了多少光头去高光）。
如果 $F$ 是被反射走的，那么剩下的 $(1 - F)$ 才是穿透进金属去进行漫反射的。且金属度越高，能发生的漫反射越少。

```glsl
// GLSL 代码结构骨架 (直接光 PBR 过程)
vec3 calculateDirectLight(Light light, vec3 N, vec3 V, vec3 albedo, float roughness, float metallic) {
    vec3 L = normalize(light.direction);
    vec3 H = normalize(V + L);
    
    // 预计算点乘
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    // 1. 估算本材质的基础反射率 F0
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedo, metallic);

    // 2. 算 D, V, F
    float D = D_GGX(NdotH, roughness);
    float Vis = V_SmithGGX(NdotL, NdotV, roughness); // G 项和分母合并的产物
    vec3 F = F_Schlick(VdotH, F0);
    
    // 3. 计算高光能量 
    vec3 specular = D * Vis * F; 

    // 4. 计算漫反射保留的能量 (能量守恒: KD + KS = 1)
    vec3 kS = F;                 // 高光占走的能量就是 F
    vec3 kD = vec3(1.0) - kS;    // 剩下的给漫反射 (1 - F)
    kD *= 1.0 - metallic;        // 金属不产生漫反射，乘(1 - metallic)压死它

    // 5. 漫反射公式
    vec3 diffuse = kD * (albedo / 3.1415926);

    // 6. 最终合并 (别忘了乘以环境光的颜色和光照强度衰减)
    return (diffuse + specular) * light.color * NdotL;
}
```

---

## 五、 IBL 环境光 (Image-Based Lighting) 中的差异

直接光只有一个光源向量 $\mathbf{L}$，但环境光来自四面八方。引擎不可能从所有方向连 $\mathbf{L}$ 来循环跑高光公式，所以用了一套基于**预计算**的近似解法（Split Sum Approximation）。对于 TA 来说，看竞品的 Shader 时要知道：

1. **环境光漫反射**：不用 Lambert 算一遍，而是直接去采样一张做过模糊的 CubeMap（Irradiance Map / 球谐光照 SH）。
2. **环境光高光**：被拆成了两张贴图相乘。
   第一部分：去采样带有特定粗糙度的环境反射 CubeMap（Prefiltered Map，粗糙度越高采样的 MIP 层级越高）。
   第二部分：查一张叫 BRDF LUT（Look-up Texture / 查找贴图）的 2D 贴图。贴图横纵坐标常常是 `(NdotV, roughness)`，返回值对应菲涅尔效果。

**环境光 GLSL 魔法常数：**
当你在 IBL 中看到算 G 的地方，如果发现 $k$ 的计算里没有除以8，而是除以了2，不要惊讶：
IBL 计算中 $k = \frac{Roughness^2}{2}$ ，所以看到常数为 `0.5` 往往暗示当前在算环境光。

## 六、 总结怎么看反编译出的恶心代码
1. 找出最关键的**四个主输入量**流向：$N, V, L, H$。只要看到向量加法跟 `normalize()` 就猜是不是在算向量 $H$。
2. 看到连续的乘法里有个 `0.318`，毫不犹豫当成是除以 $\pi$。这就是漫反射或者算 $D$ 项。
3. 看到包含 `0.04`，就是非金属的基础高光反射率，这是在求 $F_0$ 也就是在算菲涅尔相关的路线。
4. 竞品可能用了他们自己的改版模型，比如把 `roughness` 和 `metallic` 和 `AO` 压在了同一张贴图（所谓的 RMA 贴图）。追踪你的采样器，看看 `texture(x).r/g/b` 是被送去乘以哪一部分的，借以反推出通道含义。
5. 某些移动端项目为了省性能，会省去 $G$ 也就是直接把 Visibility 返回 $1.0$，重点看其有没有省掉不该省的运算。

掌握以上要素，在没有源码的情况下看 RenderDoc 的 GLSL 会从“看天书”变成“抓虫子”。加油打工人！