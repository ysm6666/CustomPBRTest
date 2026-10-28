# SSS 次表面散射学习笔记

## BSSRDF 核心积分式

$$
L_o(p_o,\omega_o) = \int_A \int_{\xi_n(p)} S(p_o,\omega_o,p_i,\omega_i) L_i(p_i,\omega_i) |\cos\theta_i|\, d\omega_i\, dA
$$

**物理含义（表面累加）**：
*   $\int_{\xi_n(p)} \dots d\omega_i$：**方向积分**。单点 $p_i$ 接收半球各向入射光，含 $\cos\theta_i$ 投影衰减。
*   $\int_A \dots dA$：**面积嵌套积分**。接纳跨点散射，需积分整个模型表面积 $A$，将所有入口点 $p_i$ 射入并传达到出口点 $p_o$ 的能量全累加。计算开销极大。
*   $S(\dots)$：**衰减转换函数**。评估光子从 $p_i, \omega_i$ 入，经内部传导后，从 $p_o, \omega_o$ 出射的留存比例。

## BSSRDF 物理拆解式

$$
S(p_o,\omega_o,p_i,\omega_i) = \frac{1}{\pi} F_t(\eta_o,\omega_o) R_d(\|p_i - p_o\|) F_t(\eta_i,\omega_i)
$$

**物理含义（光子三步模型：进 $\rightarrow$ 传 $\rightarrow$ 出）**：将内部复杂弹射降维成三个独立事件。

1.  **进** $\rightarrow F_t(\eta_i,\omega_i)$：入射菲涅尔透射率。扣除表面镜面反射后，折射进入介质内部的光比例。
2.  **传** $\rightarrow R_d(\|p_i - p_o\|)$：内部漫反射分布（Diffuse Profile）。各向同性散射下，将能量残存量降维为仅依赖两点直线间距 $\|p_i - p_o\|$ 的衰减函数。
3.  **出** $\rightarrow F_t(\eta_o,\omega_o)$：出射菲涅尔透射率。光子穿透表面回到空气的比例。
4.  **归一化** $\rightarrow \frac{1}{\pi}$：极度均质漫反射状态，匹配半球积分能量守恒。

---

## Diffuse Profile 漫反射分布函数 $R_d(r)$ 的演进
$R_d(r)$ 决定了光晕分布。图形学界经历从理论推导到纯数学拟合的演变。

### 1. Jensen 旧版模型 (Standard Dipole，2001)
借助热扩散理论推导 $R_d(r)$。公式强依赖底层衰减系数 $\sigma_{tr}$ 和扩散系数 $D$。

**① 物理域运算**
$$ \phi(r) = \frac{1}{4\pi D} \frac{e^{-\sqrt{\frac{\sigma_a}{D}}\, r}}{r} $$
*注：$\phi(r)$ 是指单个内部点光源的漫射辐射分布预测。Dipole 模型通过在表面内外摆放一正一负两个这种虚光源，利用边界条件计算两者 $\phi(r)$ 的梯度变化，最终折算出完整的表面出射率 $R_d(r)$。*
*   $r$：两点直线距离。
*   $\sigma_a$：**吸收系数**。光子被介质转化为热能的概率。
*   $D$：**扩散系数**。近似定义为 $D \approx 1 / (3\sigma_t')$。它是统筹吸收项 $\sigma_a$ 和约化散射项 $\sigma_s'$ 从而决定扩散宽度的核心。

**② 美术参数映射 (LUT 查表硬伤)**
美术无法直调物理系数，只能暴露 `DiffuseAlbedo` (基底色) 和 $l_d$ (透光深度)。但旧版中两者绑定关系为隐式方程，为了让输入的颜色最终能驱动底层运算公式中的 $D$ 和 $\sigma_a$，渲染引擎必须在幕后通过预计算二维查找贴图 (LUT) 或牛顿求解器执行高耗能的**参数逆解倒推**：
*   **颜色逆解 ($DiffuseAlbedo \rightarrow \alpha'$)**：
    $$ DiffuseAlbedo = \frac{\alpha'}{2}\left(1 + e^{-\frac{4}{3}A_{dipole}\sqrt{3(1-\alpha')}}\right) e^{-\sqrt{3(1-\alpha')}} $$
    *因 $\alpha'$ 嵌套在多阶指数中无法代数移项，故必须用已知美术要求的 $DiffuseAlbedo$ 和基于折射率决定的常量 $A_{dipole}$ 去反向暴力查出未知的约化反照率 $\alpha'$ (即光子内部存活率)。*
*   **深度逆解 ($l_d \rightarrow \sigma_t'$)**：
    $$ l_d \approx \frac{1}{\sigma_{tr}} $$
    *由透光深度 $l_d$ 反向倒推物理等效传输衰减率 $\sigma_{tr}$。*

### 2. Pixar 归一化双指数拟合模型 (Normalized Diffusion，2015)
彻底抛弃复杂物理倒推。利用光追参考线，使用极简双指数函数直接拟合能量分布。

**① 公式表达**
$$ R_d(r) = \frac{e^{-r/d} + e^{-r/3d}}{8\pi d r} $$
*   $d$：衰减形状控制系数。

**② 工业优势 (自归一化)**
公式全域面积极分为 1。内部无需嵌入 $\sigma_a, \sigma_s$。计算时只需在公式外部乘 `DiffuseAlbedo` (RGB)，天然保障能量守恒，并将色彩与形态解耦。

**③ 代数映射 (废除 LUT)**
$d$ 可通过美术参数直接代数求解：
$$ d = \frac{l_d}{3.5 + 100(DiffuseAlbedo - 0.33)^4} $$
免除逆解开销。

---

## 工业引擎对 $R_d(r)$ 的落地取舍
Pixar 代数映射在物理上强行令“颜色”影响“透射深度 (d)”，业界落地时产生两种取舍。

### 1. Unity HDRP：全解耦派
为防颜色改变干扰通透范围，直接**斩断了颜色 $A$ 与形状 $d$ 的代数关联**。
*   **重构**：规定 $d = \frac{1}{\text{ScatteringDistance}}$。散射范围独立决定分布曲线形状。
*   **执行**：独立计算纯标量灰度衰减，在混合阶段与 `Diffuse Albedo` 色彩贴图粗暴相乘染色。算法轻量，直觉性强。

### 2. UE (Unreal)：带补正重构派
认为极简代数在边缘拟合不够精准，对原始 ND 追加系数补正。
*   **改良公式**：
    $$ R_d(r) = SurfaceAlbedo \times s \frac{e^{-s \cdot r/\ell} + e^{-s \cdot r / (3\ell)}}{8\pi \ell r} $$
*   **执行**：保留材质的色偏干涉。底层 Shader 利用超级计算机提前穷举得出的高阶多项式（如 $s \approx 1.85 - A + 7|A - 0.8|^3$）算出补正形变标量 $s$，代入修改版公式积分。计算相对重，但物理反馈更忠实。

---

## 实时渲染大妥协：屏幕空间次表面散射 (SSSS)
实机无法每帧求解双重几何面积积分。现代 SSSS 管线是将 3D $R_d(r)$ 降维成 2D Render Texture 高阶高斯滤波。

### 1. 积分拆解 (违背物理的近似)
为了在管线中剥离出独立的“漫射受光贴图 (Diffuse RT)”，SSSS 执行了粗暴的变量隔离：强行忽略微观角度的菲涅尔动态穿透干涉，**假设无论光怎样入射和出射，内外边界的穿透率 $F_t$ 和 $1/\pi$ 都可以折中为平均常数并入材质基底色**。
这样，原本包含方向项的复杂内核 $S$，被强行退化成了纯几何距离权重 $R_d$：
$$ L_o(p_o) \approx \int_A \underbrace{R_d(\|p_i - p_o\|)}_{\text{空间均分权核}} \left( \underbrace{ \int_{\xi_n(p)} L_i |\cos\theta_i| d\omega_i }_{\text{屏幕原位坍缩 Diffuse RT}} \right) dA $$
*注：内层括号代表无论直接光 (Directional) 还是间接光 (IBL 球谐积分、GI 漫射)，都在此步被提前统合算死为一个常量色彩存入 RT，外层面积分布积分仅负责对其进行事后强行晕染。*

### 2. SSSS 落地流
*   **Pass 1 (获取基底)**：将遮挡与常规法线漫反射算好，存入 Diffuse RT 和 Depth RT。
*   **Pass 2 (伪造空间卷积)**：在后处理/Compute Shader 阶段提取该像素周围一圈 Diffuse。利用 Depth RT 换算 3D 两点落差配合 $R_d(r)$ 为其赋权累加，伪造周围底光的透射蔓延。深度断崖则开启 Stencil/遮罩阈值强制抛弃（抗越界漏光）。
*   **Pass 3 (高光补偿)**：常规 Specular 不参算皮下散射，于次表面滤波完成后单独盖于表层。

### 3. SSSS 对贴图颜色的干预策略
全屏模糊极易使面部高频痣点、毛孔软化。图形学采用开方割裂技巧防范：
即通过开方 (`sqrt`) 平分色彩贴图。先令 `sqrt(Diffuse)` 落入 Pass 1 被模糊运算晕开以保证血色透移，待 Pass 2 管线末端乘回剩余的 `sqrt(Diffuse)`，从而保全毛孔细节的锐利度。