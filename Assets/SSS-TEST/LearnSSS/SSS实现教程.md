# 屏幕空间次表面散射（SSS）实现教程：从 BSSRDF 积分到 URP 代码

> 本教程配套代码（已在本工程 Unity 2022.3 / URP 14 下编译通过）：
> - `Assets/SSS-TEST/SSSBurley/SSSBurleyLit.shader`：材质 Shader
> - `Assets/SSS-TEST/SSSBurley/SSSBurleyFilter.shader`：散射滤波 Shader
> - `Assets/SSS-TEST/SSSBurley/SSSBurleyFeature.cs`：Renderer Feature
>
> 实现思路参考 Unity HDRP 的 `SubsurfaceScattering.compute`（Burley 剖面 + 圆盘重要性采样），在 URP 下用最少的代码复刻。

---

## 目录

- [0. 读前准备：符号表](#0-读前准备符号表)
- [1. 总览：SSS 到底在做什么](#1-总览sss-到底在做什么)
- [第一部分：理论——从积分公式到工程公式](#第一部分理论从积分公式到工程公式)
  - [2. 起点：BSSRDF 渲染积分](#2-起点bssrdf-渲染积分)
  - [3. 拆开 BSSRDF：进、传、出](#3-拆开-bssrdf进传出)
  - [4. 扩散剖面 R(r)：Burley Normalized Diffusion](#4-扩散剖面-rrburley-normalized-diffusion)
  - [5. 近似一：把方向积分"提前算好"](#5-近似一把方向积分提前算好)
  - [6. Albedo 放在模糊前还是模糊后](#6-albedo-放在模糊前还是模糊后)
  - [7. 近似二：局部平面 + 极坐标](#7-近似二局部平面--极坐标)
  - [8. 用蒙特卡洛估算积分](#8-用蒙特卡洛估算积分)
  - [9. 重要性采样：按 R(r) 的形状撒点](#9-重要性采样按-rr-的形状撒点)
  - [10. 怎么生成服从 Burley 分布的半径 r](#10-怎么生成服从-burley-分布的半径-r)
  - [11. 角度、截断半径与采样数](#11-角度截断半径与采样数)
  - [12. 屏幕空间的补丁：单位换算与双边权重](#12-屏幕空间的补丁单位换算与双边权重)
  - [13. 最终工程公式](#13-最终工程公式)
  - [14. Unity HDRP 实际是怎么做的](#14-unity-hdrp-实际是怎么做的)
- [第二部分：实践——在 URP 里写出来](#第二部分实践在-urp-里写出来)
  - [15. 整体数据流](#15-整体数据流)
  - [16. 步骤一：材质 Shader 输出漫反射与参数](#16-步骤一材质-shader-输出漫反射与参数)
  - [17. 步骤二：Renderer Feature 组织流程](#17-步骤二renderer-feature-组织流程)
  - [18. 步骤三：散射滤波 Shader](#18-步骤三散射滤波-shader)
  - [19. 步骤四：前向 Pass 合成](#19-步骤四前向-pass-合成)
  - [20. 使用、调参与调试](#20-使用调参与调试)
  - [21. 进阶方向](#21-进阶方向)

---

## 0. 读前准备：符号表

全文统一使用下面的符号。后文出现的每一个符号都在这里，含义不会中途改变。

| 符号 | 含义 | 单位/取值 |
|---|---|---|
| $p_o$ | **出射点**：我们要算颜色的那个表面点。屏幕上就是"当前像素" | 位置 |
| $p_i$ | **入射点**：光线进入皮肤的点。屏幕上就是"邻居像素" | 位置 |
| $\omega_o$ | 出射方向，从 $p_o$ 指向相机 | 单位向量 |
| $\omega_i$ | 入射方向，从 $p_i$ 指向光源 | 单位向量 |
| $\mathbf{n}$ | 表面法线 | 单位向量 |
| $\theta_i$ | $\omega_i$ 与 $\mathbf{n}$ 的夹角，$\cos\theta_i = \mathbf{n}\cdot\omega_i$ | 角度 |
| $\Omega$ | 法线所在一侧的半球（所有可能的入射方向） | — |
| $\mathcal{A}$ | 物体的整个表面（所有可能的入射点） | — |
| $dA$ | 表面上一小块面积 | 面积 |
| $L_i(p_i,\omega_i)$ | 从方向 $\omega_i$ 照到 $p_i$ 的光（radiance） | — |
| $L_o(p_o,\omega_o)$ | 从 $p_o$ 沿 $\omega_o$ 射出的光，也就是像素最终颜色 | — |
| $S(\cdot)$ | BSSRDF，描述"从 $p_i$ 进、从 $p_o$ 出"的比例 | — |
| $F(\omega)$ | 菲涅尔**反射**率：光打到表面时被直接反射掉的比例 | 0~1 |
| $F_t(\omega)$ | 菲涅尔**透射**率，$F_t = 1 - F$，即进入（或离开）物体的比例 | 0~1 |
| $r$ | 两点距离 $r = \lVert p_i - p_o \rVert$ | 毫米 |
| $\rho$ | 漫反射颜色（albedo）= 贴图颜色 × BaseColor，RGB 三通道 | 0~1 |
| $R(r)$ | **归一化扩散剖面**，不含颜色，只描述"光扩散的形状" | 1/面积 |
| $d$ | Burley 剖面的**散射距离**（形状参数），RGB 各一个：$d_R, d_G, d_B$ | 毫米 |
| $d_{max}$ | $\max(d_R, d_G, d_B)$ | 毫米 |
| $s$ | HDRP 里的 ShapeParam，$s = 1/d$ | 1/毫米 |
| $E(p)$ | 点 $p$ 接收到并进入物体的光（辐照度） | — |
| $D(p)$ | $E(p)/\pi$，"一个白色漫反射表面在 $p$ 点会显示的亮度" | — |
| $m$ | albedo 中放在"模糊前"的比例，$0 \le m \le 1$ | — |
| $I(p)$ | 被模糊的量 $I(p) = \rho(p)^m D(p)$，也就是 Diffuse RT 里存的值 | — |
| $(r, \theta)$ | 以 $p_o$ 为圆心的极坐标：距离 $r$、角度 $\theta$ | 毫米、弧度 |
| $N$ | 采样个数 | 整数 |
| $k$ | 第几个样本，$k = 0, 1, \dots, N-1$ | 整数 |
| $u$ | $[0,1)$ 上的均匀数 | — |
| $q(\cdot)$ | 概率密度函数（pdf） | — |
| $C(r)$ | 半径 $r$ 的累积分布函数（CDF） | 0~1 |
| $w_k$ | 第 $k$ 个样本的权重，RGB 三通道 | — |
| $\Delta z$ | 邻居像素与中心像素的深度差 | 毫米 |
| $\tau$ | 无量纲距离 $\tau = r/d$ | — |

> **关于你学习笔记里的一处说法**：笔记里写"HDRP 规定 $d = 1/\text{ScatteringDistance}$"。准确说法是：HDRP 面板上的 Scattering Distance **就是** $d$；代码里用的 ShapeParam 是它的倒数 $s = 1/d$。知乎文章里"`d (Shape) = 1 / ScatteringDistance`"中的 d 指的是 $s$，两个字母撞了，容易混。本教程只用 $d$ 表示散射距离，用 $s$ 表示它的倒数。

---

## 1. 总览：SSS 到底在做什么

**一句话**：普通漫反射认为"光从哪进就从哪出"；次表面散射认为"光进入皮肤后会横向跑一段再出来"。所以一个像素的漫反射颜色，应该是**周围一圈像素漫反射光照的加权平均**，权重由"光在皮肤里走多远还能剩多少"决定。

在实时渲染里，这件事被拆成四步：

```
 ┌─────────────────┐    ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
 │ 步骤一           │    │ 步骤二           │    │ 步骤三           │    │ 步骤四           │
 │ 画漫反射光照     │ →  │ 屏幕空间加权模糊 │ →  │ 存成 SSS 结果 RT │ →  │ 前向 Pass：      │
 │ 到 Diffuse RT    │    │ （Burley 权重）  │    │                  │    │ 结果 + 高光      │
 │ + 参数 + 深度    │    │                  │    │                  │    │                  │
 └─────────────────┘    └──────────────────┘    └──────────────────┘    └──────────────────┘
```

| 步骤 | 目的 | 对应理论 |
|---|---|---|
| 一：画 Diffuse RT | 先把"每个点接收了多少光"存下来，模糊时要读邻居的值 | 第 5、6 节 |
| 二：加权模糊 | 用 Burley 剖面做权重，计算"周围的光扩散过来多少" | 第 7~12 节 |
| 三：存结果 | 后面的前向 Pass 要读它 | — |
| 四：合成 | 高光发生在皮肤表面，不参与散射，最后单独加上 | 第 3 节 |

下面先把理论讲通，再落到代码。

---

# 第一部分：理论——从积分公式到工程公式

## 2. 起点：BSSRDF 渲染积分

$$
L_o(p_o,\omega_o) = \int_{\mathcal{A}} \int_{\Omega} S(p_o,\omega_o,p_i,\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i \, d\omega_i \, dA
$$

从里往外读：

1. **内层 $\int_\Omega \dots d\omega_i$**：站在某个入射点 $p_i$ 上，把从半球各个方向照过来的光都加起来。$\cos\theta_i$ 是因为斜着照的光会摊到更大的面积上，单位面积接收的能量变少（和普通 Lambert 漫反射里的 $N\cdot L$ 是同一个东西）。
2. **外层 $\int_\mathcal{A} \dots dA$**：把物体表面每一个入射点 $p_i$ 的贡献都加起来。这就是 SSS 和普通 BRDF 的区别：BRDF 只看 $p_o$ 自己，BSSRDF 要看所有点。
3. **$S(\dots)$**：光从 $p_i$ 沿 $\omega_i$ 进去，从 $p_o$ 沿 $\omega_o$ 出来的比例。

这个积分直接算是不可能的（每个像素都要对整个模型表面积分），后面所有步骤都是在想办法把它变成"每个像素采几十个邻居"。

## 3. 拆开 BSSRDF：进、传、出

$$
S(p_o,\omega_o,p_i,\omega_i) = \frac{1}{\pi}\, F_t(\omega_o)\; \big[\rho\, R(r)\big]\; F_t(\omega_i)
$$

| 项 | 物理含义 |
|---|---|
| $F_t(\omega_i)$ | **进**：光打到皮肤表面，一部分被反射掉（这部分就是高光），剩下 $F_t = 1 - F$ 进入皮肤 |
| $\rho\, R(r)$ | **传**：在皮肤里乱弹很多次之后，从距离 $r$ 处冒出来的比例。$\rho$ 管颜色（被吸收了多少），$R(r)$ 管形状（扩散多远） |
| $F_t(\omega_o)$ | **出**：从皮肤内部射出时，又有一部分被内表面反射回去 |
| $\frac{1}{\pi}$ | 假设出射光在各个方向上均匀分布（和 Lambert 的 $\frac{\rho}{\pi}$ 同理）时需要的归一化系数 |

> 原论文里写的是 $R_d(r)$，它等于这里的 $\rho\, R(r)$。我们把颜色 $\rho$ 单独拿出来，是因为后面要决定颜色在"模糊前"还是"模糊后"乘（第 6 节）。

这一步本身已经是一个近似（假设：物体是无限大、无限厚、内部均匀的平板），但它把"方向"和"距离"分开了，后面才能继续简化。

**为什么高光不参与散射**：高光就是 $F$ 那部分，在表面就被反射走了，根本没进皮肤。所以工程上高光照常算，只有漫反射部分走 SSS 流程。这就是步骤四"结果 + 高光"的来源。

## 4. 扩散剖面 R(r)：Burley Normalized Diffusion

### 4.1 公式

$$
R(r) = \frac{e^{-r/d} + e^{-r/(3d)}}{8\pi\, d\, r}
$$

- $r$：入射点和出射点的距离。
- $d$：散射距离，控制曲线有多"胖"。$d$ 越大，光扩散得越远。
- RGB 三个通道各用自己的 $d$。皮肤里红光走得最远，所以 $d_R > d_G > d_B$，这就是阴影边缘发红的原因。

这个式子是 Pixar 拿蒙特卡洛模拟的参考曲线**拟合**出来的经验公式，不是从物理方程推出来的。它好用的原因是：形状准、只有两个指数、并且可以解析地积分（下面就要用到）。

### 4.2 前提知识：极坐标下的面积微元

$R(r)$ 只和距离有关，所以以 $p_o$ 为圆心用极坐标 $(r,\theta)$ 描述平面最方便。

在极坐标里，半径从 $r$ 到 $r+dr$、角度从 $\theta$ 到 $\theta + d\theta$ 围成的小块，近似是一个长 $r\,d\theta$（弧长 = 半径 × 角度）、宽 $dr$ 的小矩形，所以：

$$
dA = r\, dr\, d\theta
$$

这个多出来的 $r$ 很重要：离圆心越远的圆环，周长越长，面积越大。

### 4.3 为什么叫"归一化"：积分等于 1

在无限大平面上对 $R(r)$ 积分：

$$
\int_{\mathcal{A}} R\, dA = \int_0^{2\pi}\!\!\int_0^\infty R(r)\, r\, dr\, d\theta
$$

1. $R(r)$ 和 $\theta$ 无关，对 $\theta$ 积分直接得到 $2\pi$。
2. $R(r)\cdot r$ 把分母里的 $r$ 消掉了：
   $$
   R(r)\, r = \frac{e^{-r/d} + e^{-r/(3d)}}{8\pi d}
   $$
3. 用到一个基本积分：$\int_0^\infty e^{-r/a}\, dr = a$（指数衰减曲线下的面积等于它的衰减长度）。所以
   $$
   \int_0^\infty \left(e^{-r/d} + e^{-r/(3d)}\right) dr = d + 3d = 4d
   $$
4. 合起来：
   $$
   2\pi \cdot \frac{4d}{8\pi d} = 1
   $$

**结论**：不管 $d$ 是多少，$R$ 在整个平面上的积分都是 1。

**这条性质的意义**：如果皮肤上每个点接收的光都一样多，散射之后每个点出来的光也一样多，SSS 不会让画面整体变亮或变暗。它只是把光从亮处**搬到**暗处，总量不变。这也是后面检验公式对不对的标准。

## 5. 近似一：把方向积分"提前算好"

这一节要做的事：把第 3 节拆好的 $S$ 代回第 2 节的积分，然后整理成"内层只管方向、外层只管位置"的形式。整理过程只用到两条前提知识。

### 5.1 前提知识

**前提知识 1：与积分变量无关的因子可以提到积分号外面**

积分 $\int g(x)\,dx$ 里只有积分变量 $x$ 在变。不含 $x$ 的因子 $c$，对这个积分来说就是常数：

$$
\int c\cdot g(x)\,dx = c\int g(x)\,dx \qquad (c \text{ 与 } x \text{ 无关})
$$

原因：积分本质上是"切成很多小块，每块的值乘以宽度再求和"。每一块都乘了同一个 $c$，提公因式即可。

**前提知识 2：嵌套积分从里往外算**

$\displaystyle\int_{\mathcal{A}}\left[\int_\Omega \cdots\, d\omega_i\right] dA$ 的计算顺序是：

1. 先**固定**一个入射点 $p_i$，在内层对所有入射方向 $\omega_i$ 积分，得到一个只和 $p_i$ 有关的值；
2. 再让 $p_i$ 走遍整个表面 $\mathcal{A}$，在外层把这些值积分起来。

由此得到三条规则：

- **内层积分**里 $p_i$ 是固定的，只有 $\omega_i$ 在变。只依赖 $p_i$、不依赖 $\omega_i$ 的因子，对内层是常数，可以从内层提到外层。
- **外层积分**里只有 $p_i$ 在变。不依赖 $p_i$ 的因子，可以提到最外面。
- $p_o$ 和 $\omega_o$ 是"当前要算的像素"和"当前视线方向"，整个计算过程中都不变，**它们不是积分变量**。

### 5.2 逐步代入

**第 1 步：写出原式（第 2 节）**

$$
L_o(p_o,\omega_o) = \int_{\mathcal{A}} \int_{\Omega} S(p_o,\omega_o,p_i,\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i \, d\omega_i \, dA
$$

**第 2 步：把 $S$ 原样替换成第 3 节的拆分式**

$$
S(p_o,\omega_o,p_i,\omega_i) = \frac{1}{\pi}\, F_t(\omega_o)\, \rho\, R(r)\, F_t(\omega_i)
$$

直接放进去，此时什么都不调整：

$$
L_o(p_o,\omega_o) = \int_{\mathcal{A}} \int_{\Omega} \frac{1}{\pi}\, F_t(\omega_o)\, \rho\, R(r)\, F_t(\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i \, d\omega_i \, dA
$$

**第 3 步：给每个因子标出"它随谁变化"**

| 因子 | 随 $\omega_i$ 变？ | 随 $p_i$ 变？ | 原因 |
|---|---|---|---|
| $\frac{1}{\pi}$ | 否 | 否 | 纯常数 |
| $F_t(\omega_o)$ | 否 | 否 | 只和出射方向 $\omega_o$ 有关，而 $\omega_o$ 是固定的 |
| $\rho$ | 否 | 暂视为否 | 先假设物体颜色处处相同。颜色随位置变化的情况留到第 6 节 |
| $R(r)$ | 否 | **是** | $r = \lVert p_i - p_o\rVert$，$p_o$ 固定，$p_i$ 一变 $r$ 就变 |
| $F_t(\omega_i)$ | **是** | 否 | 入射角不同，进入皮肤的比例不同 |
| $L_i(p_i,\omega_i)$ | **是** | **是** | 不同位置、不同方向照来的光都可能不同 |
| $\cos\theta_i$ | **是** | **是** | $\theta_i$ 是 $\omega_i$ 和 $p_i$ 处法线的夹角，两者都影响它 |

按依赖关系分成三组：

- **A 组**（两者都不依赖）：$\frac{1}{\pi}$、$F_t(\omega_o)$、$\rho$
- **B 组**（只依赖 $p_i$）：$R(r)$
- **C 组**（依赖 $\omega_i$）：$F_t(\omega_i)$、$L_i(p_i,\omega_i)$、$\cos\theta_i$

**第 4 步：把 A、B 组提出内层积分**

内层只对 $\omega_i$ 积分。A、B 两组都不含 $\omega_i$，按前提知识 1，从内层积分号里提出来（但仍在外层积分里面）。用中括号把内层积分圈起来：

$$
L_o(p_o,\omega_o) = \int_{\mathcal{A}} \frac{1}{\pi}\, F_t(\omega_o)\, \rho\, R(r) \left[ \int_{\Omega} F_t(\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i \, d\omega_i \right] dA
$$

现在中括号里只剩 C 组。

**第 5 步：把 A 组中的 $\frac{1}{\pi}$ 和 $F_t(\omega_o)$ 提出外层积分**

外层只对 $p_i$ 积分。$\frac{1}{\pi}$ 和 $F_t(\omega_o)$ 不含 $p_i$，再提一次，放到最外面：

$$
L_o(p_o,\omega_o) = \frac{1}{\pi}\, F_t(\omega_o) \int_{\mathcal{A}} \rho\, R(r) \left[ \int_\Omega F_t(\omega_i)\, L_i(p_i,\omega_i) \cos\theta_i\, d\omega_i \right] dA
$$

$\rho$ 现在是常数，本来也可以提出去。这里故意把它留在外层积分里面，因为第 6 节它会变成随位置变化的 $\rho(p_i)$，那时就不能提出去了。

**这一串操作的意义**：原来"位置"和"方向"纠缠在同一个二重积分里。整理之后，方向积分被完整地关进了中括号，而中括号**只依赖 $p_i$**。所以可以对每个点单独把它算好、存起来；外层积分只需要读取这些存好的值，再按 $R(r)$ 加权求和。

这就是"先画 Diffuse RT，再做模糊"这个工程流程在数学上成立的原因：Diffuse RT 存的就是每个点的中括号，模糊就是外层积分。

### 5.3 中括号就是普通漫反射光照

给中括号起个名字：

$$
E(p_i) = \int_\Omega F_t(\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i\, d\omega_i
$$

$E(p_i)$ 就是"$p_i$ 点接收到、并且真正进入皮肤的光"。逐项看：

- $L_i(p_i,\omega_i)$：从方向 $\omega_i$ 照到 $p_i$ 的光，对应 shader 里的 `lightColor × shadow`；
- $\cos\theta_i$：对应 `NdotL`；
- $F_t(\omega_i) = 1 - F$：扣掉被表面反射走的部分（那部分是高光）；
- $\int_\Omega$：把所有方向的光加起来。实时渲染里就是"主光源 + 附加光源 + 环境光"逐个累加。

这正是普通漫反射 shader 每个像素都在算的东西。代入后：

$$
L_o(p_o,\omega_o) = \frac{1}{\pi}\, F_t(\omega_o) \int_{\mathcal{A}} \rho\, R(r)\, E(p_i)\, dA
$$

### 5.4 近似一：去掉出射菲涅尔

最外面还剩一个 $F_t(\omega_o)$。它表示光从皮肤内部射出时，被内表面反射回去之后剩下的比例，只在视线非常斜（掠射角）时才明显小于 1。

**近似一**：工程上忽略它随视角的变化，取 $F_t(\omega_o) \approx 1$。这一步之后，式子里不再有任何东西依赖 $\omega_o$，所以 $L_o$ 的参数里可以去掉 $\omega_o$。这和 Lambert 漫反射一样：从哪个方向看，亮度都相同。

再把 $\frac{1}{\pi}$ 并进 $E$，定义 $D(p_i) = \dfrac{E(p_i)}{\pi}$。$\frac{1}{\pi}$ 是常数，所以从积分外面挪到积分里面不影响结果（前提知识 1 反过来用）。于是：

$$
L_o(p_o) \approx \int_{\mathcal{A}} \rho\, R(r)\, D(p_i)\, dA, \qquad D(p_i) = \frac{E(p_i)}{\pi}
$$

**这一节的完整变形链**：

$$
\begin{aligned}
L_o(p_o,\omega_o)
&= \int_{\mathcal{A}} \int_{\Omega} S\, L_i \cos\theta_i \, d\omega_i \, dA && \text{原式}\\
&= \int_{\mathcal{A}} \int_{\Omega} \tfrac{1}{\pi} F_t(\omega_o)\, \rho\, R(r)\, F_t(\omega_i)\, L_i \cos\theta_i \, d\omega_i \, dA && \text{代入 } S\\
&= \tfrac{1}{\pi} F_t(\omega_o) \int_{\mathcal{A}} \rho\, R(r) \left[\int_{\Omega} F_t(\omega_i)\, L_i \cos\theta_i \, d\omega_i\right] dA && \text{提出常数因子}\\
&= \tfrac{1}{\pi} F_t(\omega_o) \int_{\mathcal{A}} \rho\, R(r)\, E(p_i)\, dA && \text{定义 } E\\
&\approx \int_{\mathcal{A}} \rho\, R(r)\, D(p_i)\, dA && F_t(\omega_o)\approx 1,\ D = E/\pi
\end{aligned}
$$

只有最后一步是近似，前面四步都是严格相等的代数整理。

### 5.5 在 Unity 里 D 怎么算

**关于 $\pi$**：Unity 的灯光强度定义里已经把 $\pi$ 吃掉了（这就是为什么 URP 的 Lambert 写成 `albedo * lightColor * NdotL`，没有除以 $\pi$）；`SampleSH` 返回的环境光也一样。所以在 Unity 里：

$$
D(p) = \underbrace{(1 - F)\cdot \text{lightColor}\cdot\text{shadow}\cdot (\mathbf{n}\cdot\omega_i)}_{\text{主光源}} + \underbrace{(1-F)\cdot \text{SampleSH}(\mathbf{n})}_{\text{环境光}}
$$

可以理解为"一个白色（$\rho=1$）Lambert 表面在这里会显示的颜色"。

### 5.6 退化检验

**退化检验**：如果所有 $D(p_i)$ 都等于同一个值 $D_0$，用第 4.3 节的结论：

$$
L_o = \rho\, D_0 \int_\mathcal{A} R\, dA = \rho\, D_0
$$

和普通 Lambert 漫反射结果完全一样。说明近似之后能量依然守恒，SSS 只在光照有变化（明暗交界、阴影边缘）的地方起作用。**这也解释了你之前白球在平滑光照下"看不出效果"**：光照几乎没有变化的区域，模糊前后本来就差不多。

## 6. Albedo 放在模糊前还是模糊后

上面的式子里 $\rho$ 在积分里面，严格说应该是 $\rho(p_i)$（入射点的颜色）还是 $\rho(p_o)$（出射点的颜色）？物理上光在进、出时各被表皮吸收一次，所以工程上把 $\rho$ 拆成两半：

$$
\rho = \rho^{m}\cdot \rho^{1-m}
$$

$$
L_o(p_o) \approx \rho(p_o)^{1-m} \int_{\mathcal{A}} R(r)\, \underbrace{\rho(p_i)^{m}\, D(p_i)}_{I(p_i)}\, dA
$$

| $m$ | 名称 | 效果 |
|---|---|---|
| 1 | Pre-Scatter | 颜色全部参与模糊：颜色渗透最强，但毛孔、雀斑等贴图细节会被糊掉 |
| 0 | Post-Scatter | 只模糊光照，颜色最后乘：细节最清晰，但没有颜色渗透 |
| 0.5 | Pre & Post（HDRP 默认） | 模糊前乘 $\sqrt{\rho}$，模糊后再乘 $\sqrt{\rho}$：两者折中 |

本教程用 $m = 0.5$：Diffuse RT 里存 $I = \sqrt{\rho}\cdot D$，模糊完再乘 $\sqrt{\rho(p_o)}$。

> 验证：当 $\rho$ 处处相同时，$\sqrt{\rho}\cdot\sqrt{\rho} = \rho$，结果和 $m=1$、$m=0$ 一样。三种模式只在贴图颜色有变化的地方有区别。

## 7. 近似二：局部平面 + 极坐标

第 5 节的积分仍然是对整个模型表面积分。$R(r)$ 衰减很快，远处的点贡献几乎为 0，所以只需要考虑 $p_o$ 附近一小片。

**近似二**：把 $p_o$ 附近的表面当成一个平面，用以 $p_o$ 为圆心的极坐标 $(r,\theta)$ 描述上面的点，用 $dA = r\,dr\,d\theta$（第 4.2 节）：

$$
L_o(p_o) \approx \rho(p_o)^{1-m} \int_0^{2\pi}\!\!\int_0^{\infty} R(r)\, I(r,\theta)\, r\, dr\, d\theta
$$

$I(r,\theta)$ 表示"距离 $p_o$ 为 $r$、方向为 $\theta$ 的那个点的 $I$ 值"。

**为什么能在屏幕上做**：这个平面用屏幕平面代替。平面上距离 $r$、方向 $\theta$ 的点，对应屏幕上偏移 $r \times \text{每毫米多少像素}$ 的像素。$I$ 已经存在 Diffuse RT 里，直接按偏移读邻居像素就行。代价是：表面斜对着相机时，屏幕上的距离比真实表面距离短（第 12 节用深度差部分补偿）。

## 8. 用蒙特卡洛估算积分

蒙特卡洛方法的核心思想：**积分很难直接算，但"随机抽一些点，求个平均"很容易算。只要抽点的方式设计得当，这个平均值就会接近积分的值。**

要讲清楚"设计得当"是什么意思，需要先补一些概率知识。下面从最基础的概念讲起，每一条后面都会用到。

### 8.1 前提知识：从离散概率到概率密度

#### 8.1.1 随机变量

**随机变量**是"取值由随机过程决定的量"，一般用大写字母表示，如 $X$；它某一次实际取到的值用小写字母表示，如 $x$。

- **离散随机变量**：只能取有限个（或可数个）值。例如掷骰子的点数，只能是 1~6。
- **连续随机变量**：可以取某个区间里的任意实数。例如"在 $[0,1)$ 里随机取一个数"，可能是 0.3、0.31、0.3141……

本教程里的采样半径 $r$、采样角度 $\theta$ 都是连续随机变量。

#### 8.1.2 离散情况：概率与期望

离散随机变量用**概率**描述：每个取值 $x_j$ 有一个概率 $P_j$，所有概率加起来等于 1：

$$
\sum_j P_j = 1
$$

**期望** $\mathbb{E}[X]$ 是"按概率加权的平均值"，也就是大量重复实验后结果的平均：

$$
\mathbb{E}[X] = \sum_j x_j\, P_j
$$

例：普通骰子每面概率都是 $\frac16$，期望为

$$
\mathbb{E}[X] = 1\cdot\tfrac16 + 2\cdot\tfrac16 + \dots + 6\cdot\tfrac16 = 3.5
$$

如果骰子灌了铅，6 点的概率是 $\frac12$，其余五面各 $\frac1{10}$，期望变成 $(1+2+3+4+5)\cdot\frac{1}{10} + 6\cdot\frac12 = 4.5$。概率大的取值对期望的影响大，这就是"加权"的含义。

#### 8.1.3 连续情况：为什么需要"概率密度"

连续随机变量的取值有无穷多个，**取到某一个精确值的概率是 0**。比如在 $[0,1)$ 里随机取数，恰好取到 $0.5000\ldots$ 的概率为 0。所以不能像离散情况那样给每个值标一个概率。

能谈的是"落在某一**段**里的概率"。于是引入**概率密度函数**（probability density function，pdf），记作 $q(x)$：

$$
X \text{ 落在 } [x,\ x+dx] \text{ 这一小段里的概率} \approx q(x)\,dx
$$

落在区间 $[a,b]$ 里的概率，就是把这些小段的概率加起来，即积分：

$$
P(a \le X \le b) = \int_a^b q(x)\,dx
$$

**几何意义**：$q(x)$ 曲线下、$a$ 到 $b$ 之间的面积，就是落在这段里的概率。$q(x)$ 越高的地方，样本越容易落在那里。

pdf 必须满足两个条件：

1. **非负**：$q(x) \ge 0$。概率不能为负。
2. **归一化**：$\displaystyle\int q(x)\,dx = 1$。随机变量总得取某个值，所有可能性加起来是 100%。

**注意：$q(x)$ 本身不是概率，可以大于 1。** 它是"单位长度上的概率"，乘以长度 $dx$ 才是概率。例如在 $[0, 0.5]$ 上均匀取数，pdf 是 $q(x) = 2$，因为 $\int_0^{0.5} 2\,dx = 1$。

**例：均匀分布**。在 $[a,b]$ 上均匀取数，每处可能性相同，所以 $q(x)$ 是常数。由归一化 $\int_a^b c\,dx = c\,(b-a) = 1$，得到

$$
q(x) = \frac{1}{b-a}
$$

在 $[0,1)$ 上均匀分布时 $q(x) = 1$。代码里的 $u_k$、`PixelNoise` 生成的数，都可以看作这种分布。

#### 8.1.4 连续情况的期望

从离散公式出发：把 $x$ 轴切成宽度为 $\Delta x$ 的很多小段，第 $j$ 段的位置是 $x_j$，落在这段的概率约为 $q(x_j)\,\Delta x$。套用离散期望：

$$
\mathbb{E}[X] \approx \sum_j x_j\, q(x_j)\,\Delta x
$$

$\Delta x \to 0$ 时，求和变成积分：

$$
\mathbb{E}[X] = \int x\, q(x)\,dx
$$

更一般地，如果关心的不是 $X$ 本身，而是它的某个函数 $g(X)$（比如"采样点的颜色"），期望是：

$$
\boxed{\ \mathbb{E}[g(X)] = \int g(x)\, q(x)\,dx\ }
$$

含义不变：**$g$ 的取值按"$X$ 落在那里的可能性"加权平均**。这个式子是蒙特卡洛方法的基础。

#### 8.1.5 期望的线性性

期望有一个非常好用的性质：

$$
\mathbb{E}[a\,Y + b\,Z] = a\,\mathbb{E}[Y] + b\,\mathbb{E}[Z]
$$

$a, b$ 是常数，$Y, Z$ 是任意随机变量。推论：$N$ 个随机变量之和的期望，等于各自期望之和：

$$
\mathbb{E}\Big[\sum_{k} Y_k\Big] = \sum_{k} \mathbb{E}[Y_k]
$$

理由：期望本质上是积分（或求和），而积分和求和都满足"和的积分 = 积分的和""常数可以提出来"。

#### 8.1.6 独立同分布采样

**独立**：抽第 $k$ 个样本时，不受其他样本取值的影响。

**同分布**：每个样本都按同一个 pdf $q$ 抽取。

"独立同分布"（简称 i.i.d.）的 $N$ 个样本 $X_0, X_1, \dots, X_{N-1}$，就是按同一规则、互不影响地抽 $N$ 次。

#### 8.1.7 样本平均：无偏性

抽 $N$ 个样本，算 $g$ 的平均值：

$$
\bar{G}_N = \frac{1}{N}\sum_{k=0}^{N-1} g(X_k)
$$

$\bar{G}_N$ 本身也是随机的，每次重新抽样结果都不一样。它的期望是多少？用 8.1.5 的线性性：

$$
\mathbb{E}[\bar{G}_N] = \frac{1}{N}\sum_{k} \mathbb{E}[g(X_k)] = \frac{1}{N}\cdot N\cdot \mathbb{E}[g(X)] = \mathbb{E}[g(X)]
$$

中间一步用了"同分布"：每个 $X_k$ 的分布都一样，所以每个 $\mathbb{E}[g(X_k)]$ 都等于 $\mathbb{E}[g(X)]$。

**结论**：样本平均的期望**正好等于**真实期望，不管 $N$ 是多少。这叫**无偏**。意思是这个估计方法没有系统性的偏高或偏低，误差只来自随机波动。

#### 8.1.8 大数定律

无偏只说明"平均来看是对的"，单次估计仍然可能偏差很大（比如 $N=1$ 时）。**大数定律**说的是：

$$
N \to \infty \text{ 时}, \quad \bar{G}_N \to \mathbb{E}[g(X)]
$$

样本越多，样本平均越接近真实期望。掷骰子 6 次，平均值可能是 2.8；掷 10000 次，平均值会非常接近 3.5。

#### 8.1.9 方差：误差有多大、怎么减小

**方差**衡量随机变量偏离期望的程度：

$$
\mathrm{Var}[Y] = \mathbb{E}\big[(Y - \mathbb{E}[Y])^2\big]
$$

$(Y - \mathbb{E}[Y])^2$ 是"偏离期望的距离的平方"，方差就是它的平均值。方差的平方根叫**标准差**，可以粗略理解为"一次取值通常偏离期望多远"。

对 $N$ 个独立样本求平均，方差会缩小为原来的 $\frac{1}{N}$（独立随机变量之和的方差等于方差之和，再除以 $N^2$）：

$$
\mathrm{Var}[\bar{G}_N] = \frac{\mathrm{Var}[g(X)]}{N}, \qquad \text{标准差} = \frac{\sqrt{\mathrm{Var}[g(X)]}}{\sqrt{N}}
$$

这个式子在渲染里非常重要：

- 误差（标准差）按 $\frac{1}{\sqrt{N}}$ 缩小。**样本数翻 4 倍，噪点才减半**，靠堆样本数降噪代价很大。
- 另一条路是减小分子 $\mathrm{Var}[g(X)]$。这就是第 9 节**重要性采样**要做的：通过改变 $q$，让 $g$ 的取值更"平"，波动更小。
- 实时渲染里样本数只有几十个，剩下的噪点交给 TAA（时间抗锯齿）：每帧换一套随机采样，把多帧的结果混合，相当于在时间上增加样本数。

#### 8.1.10 多维随机变量

本教程的采样点有两个坐标 $(r,\theta)$，需要二维的 pdf $q(r,\theta)$：

$$
(r,\theta) \text{ 落在 } [r,\ r+dr]\times[\theta,\ \theta+d\theta] \text{ 这个小矩形里的概率} \approx q(r,\theta)\,dr\,d\theta
$$

归一化与期望都改成二重积分：

$$
\iint q(r,\theta)\,dr\,d\theta = 1, \qquad \mathbb{E}[g] = \iint g(r,\theta)\,q(r,\theta)\,dr\,d\theta
$$

**独立的两个变量**：如果 $r$ 和 $\theta$ 互不影响，联合 pdf 等于各自 pdf 的乘积：

$$
q(r,\theta) = q_r(r)\cdot q_\theta(\theta)
$$

这时可以**分别**抽 $r$ 和 $\theta$，再组合起来。第 10 节就是这样做的：$\theta$ 均匀抽，$r$ 按 Burley 分布抽。

**注意 pdf 是"对谁的密度"**：这里的 $q(r,\theta)$ 是对 $dr\,d\theta$ 的密度。而面积微元是 $dA = r\,dr\,d\theta$，多出来的 $r$ 不在 pdf 里，要放进被积函数（第 8.3 节的 $f$ 里有一个 $r$，就是它）。如果把 pdf 定义成对面积 $dA$ 的密度，公式会不一样。两种写法都对，但必须前后一致，不能混用。

### 8.2 蒙特卡洛估计式

#### 8.2.1 推导

目标：计算积分 $\displaystyle\int f(x)\,dx$。

**关键技巧**：选一个自己方便抽样的 pdf $q(x)$，构造新函数 $g(x) = \dfrac{f(x)}{q(x)}$。按 8.1.4 计算 $g(X)$ 的期望：

$$
\mathbb{E}\!\left[\frac{f(X)}{q(X)}\right] = \int \frac{f(x)}{q(x)}\, q(x)\, dx = \int f(x)\, dx
$$

$q$ 被约掉了，**$g(X)$ 的期望正好等于要求的积分**。积分问题就转化成了期望问题，而期望可以用样本平均来估计（8.1.7、8.1.8）：

$$
\boxed{\ \int f(x)\,dx \approx \frac{1}{N}\sum_{k=0}^{N-1} \frac{f(X_k)}{q(X_k)}, \qquad X_k \text{ 按 } q \text{ 独立抽取}\ }
$$

由 8.1.7，这个估计是无偏的；由 8.1.9，误差按 $\frac{1}{\sqrt N}$ 缩小。

**使用条件**：在 $f(x) \ne 0$ 的地方，必须有 $q(x) > 0$。否则那一段永远抽不到，它对积分的贡献就丢了，估计会偏小。

#### 8.2.2 直觉：除以 q 是在"纠正抽样的偏心"

- $q$ 大的地方样本多。如果直接平均 $f$，这些区域会被重复计算、权重偏大。所以每个样本要**除以一个大的 $q$**，降低它的"话语权"。
- $q$ 小的地方样本少，每个样本**除以一个小的 $q$**，话语权变大，代表了它周围那一大片没被抽到的区域。

最终每个区域对结果的贡献都与它在积分里的真实分量相符。

#### 8.2.3 数值例子

计算 $\displaystyle\int_0^1 x^2\,dx$，精确答案是 $\frac13 \approx 0.333$。每种方法都只用 3 个样本。

**方法 A：均匀抽样**，$q(x) = 1$，$\frac{f}{q} = x^2$。

取样本 $x = 0.2,\ 0.5,\ 0.9$：

$$
\frac{0.04 + 0.25 + 0.81}{3} \approx 0.367
$$

**方法 B：按 $q(x) = 2x$ 抽样**。$q$ 在 $x$ 大的地方更大，和 $f = x^2$ 的形状更接近。$\frac{f}{q} = \frac{x^2}{2x} = \frac{x}{2}$。

（怎么生成服从 $2x$ 分布的样本见第 10 节，这里直接给出 $x = 0.408,\ 0.707,\ 0.913$。）

$$
\frac{0.204 + 0.354 + 0.456}{3} \approx 0.338
$$

**方法 C：按 $q(x) = 3x^2$ 抽样**。$q$ 和 $f$ 形状完全一样（只差一个常数倍）。$\frac{f}{q} = \frac{1}{3}$，**不管抽到哪里都是 $\frac13$**，一个样本就得到精确答案。

用 8.1.9 的方差比较 $g = f/q$ 的波动：

| 方法 | $q(x)$ | $g(x) = f/q$ | $\mathrm{Var}[g]$ |
|---|---|---|---|
| A | $1$ | $x^2$ | $\frac15 - \frac19 \approx 0.089$ |
| B | $2x$ | $\frac{x}{2}$ | $\frac18 - \frac19 \approx 0.014$ |
| C | $3x^2$ | $\frac13$ | $0$ |

（方差用 $\mathrm{Var}[g] = \mathbb{E}[g^2] - (\mathbb{E}[g])^2$ 计算，这是方差定义展开后的等价形式。例如方法 A：$\mathbb{E}[g^2] = \int_0^1 x^4\,dx = \frac15$，$\mathbb{E}[g] = \frac13$。）

**规律**：$q$ 的形状越接近 $f$，$f/q$ 就越接近常数，方差越小，同样样本数下噪点越少。这就是第 9 节**重要性采样**的出发点。我们的 Burley 剖面恰好能做到方法 C 那样"$q$ 与 $f$ 中的 $R(r)\,r$ 形状完全相同"。

### 8.3 套到我们的积分上

我们的被积函数（二维，变量是 $r$ 和 $\theta$）：

$$
f(r,\theta) = R(r)\, r\, I(r,\theta)
$$

在 $(r,\theta)$ 上按某个 pdf $q(r,\theta)$ 撒 $N$ 个点 $(r_k, \theta_k)$，则：

$$
L_o(p_o) \approx \rho(p_o)^{1-m}\cdot \frac{1}{N}\sum_{k} \frac{R(r_k)\, r_k}{q(r_k,\theta_k)}\, I_k
$$

$I_k$ 就是第 $k$ 个样本位置的 Diffuse RT 像素值。剩下的问题是 $q$ 怎么选。

## 9. 重要性采样：按 R(r) 的形状撒点

### 9.1 为什么不均匀撒点

$R(r)\,r$ 在 $r$ 小的地方大，$r$ 大的地方几乎为 0。均匀撒点的话，大部分样本落在几乎没贡献的远处，结果噪点很大。（你之前的可分离实现就是均匀撒点。）

**重要性采样**：让 $q$ 和被积函数的形状尽量一样，贡献大的地方多撒点。

### 9.2 最理想的 q

第 4.3 节已经算过 $\int_0^{2\pi}\int_0^\infty R(r)\,r\,dr\,d\theta = 1$，也就是说 $R(r)\,r$ 本身就满足 pdf 的条件（非负、积分为 1）。直接取：

$$
q(r,\theta) = R(r)\, r = \frac{e^{-r/d} + e^{-r/(3d)}}{8\pi d}
$$

代入第 8.3 节：

$$
\frac{R(r_k)\, r_k}{q(r_k,\theta_k)} = 1 \quad\Longrightarrow\quad L_o \approx \rho^{1-m}\cdot\frac{1}{N}\sum_k I_k
$$

**每个样本权重都是 1，结果就是样本的简单平均。** 复杂的权重全部体现在"样本撒在哪里"上了。

### 9.3 RGB 三个 d 不一样怎么办

一次只能按一个 $d$ 撒点。HDRP 的做法：**按最宽的 $d_{max}$ 撒点**（保证最宽的通道也被覆盖），然后每个通道 $c \in \{R,G,B\}$ 单独算权重：

$$
w_{k,c} = \frac{R_c(r_k)\, r_k}{q_{d_{max}}(r_k,\theta_k)}
$$

- $R_c$ 用通道 $c$ 自己的 $d_c$；$q_{d_{max}}$ 用 $d_{max}$。
- 对 $d_c = d_{max}$ 的那个通道（通常是红色），$w = 1$。
- 对 $d_c$ 更小的通道（绿、蓝），远处样本的 $w$ 很小，近处样本的 $w$ 偏大。所以远处的光几乎只贡献红色，这就是颜色偏移的来源。

### 9.4 为什么除以 Σw 而不是 N

HDRP 最后用的是：

$$
L_o \approx \rho^{1-m}\cdot\frac{\sum_k w_k\, I_k}{\sum_k w_k}
$$

这叫**自归一化估计**。原因：

1. 理论上 $\mathbb{E}[w] = \int \frac{R_c\, r}{q}\, q = \int R_c\, r = 1$，所以 $\sum_k w_k \approx N$，两种写法在理想情况下相等。
2. 实际中有些样本要丢掉：落到了非 SSS 物体上（背景、头发），或者深度差太大（第 12 节）。此时 $\sum w < N$。如果还除以 $N$，边缘会变暗；除以 $\sum w$ 相当于"用剩下的有效样本重新分配权重"，亮度保持不变。
3. 有限样本时，三个通道的 $\sum w_c$ 各自不严格等于 $N$，自归一化保证每个通道都能量守恒，不会偏色。

## 10. 怎么生成服从 Burley 分布的半径 r

这一节是 HDRP 里那段看起来很吓人的 `SampleBurleyDiffusionProfile` 的来历。

### 10.1 拆分 r 和 θ

$q(r,\theta) = R(r)\,r$ 和 $\theta$ 无关，所以可以拆成两个独立的部分：

- $\theta$：在 $[0, 2\pi)$ 上均匀分布，密度 $\frac{1}{2\pi}$。
- $r$：密度 $q_r(r) = 2\pi\, R(r)\, r = \frac{e^{-r/d} + e^{-r/(3d)}}{4d}$。

两者相乘正好等于 $q(r,\theta)$。角度好办（第 11 节），难的是 $r$。

### 10.2 前提知识：逆变换采样

**累积分布函数（CDF）**：$C(r) = \int_0^r q_r(t)\, dt$，表示"半径小于 $r$ 的概率"。它从 0 单调增到 1。

**逆变换采样**：取 $[0,1)$ 上的均匀数 $u$，解方程 $C(r) = u$ 得到 $r = C^{-1}(u)$，这样得到的 $r$ 就服从 $q_r$。

为什么成立：

$$
P(r \le x) = P\big(C^{-1}(u) \le x\big) = P\big(u \le C(x)\big) = C(x)
$$

最后一步是因为 $u$ 均匀分布，$u \le$ 某个值的概率就等于那个值。$r$ 的 CDF 正是 $C$，所以分布对了。

直观理解：$C(r)$ 陡的地方（pdf 大的地方），$u$ 的一大段区间都会映射到很小的一段 $r$ 上，样本自然就密集了。

### 10.3 求 CDF

$$
C(r) = \int_0^r \frac{e^{-t/d} + e^{-t/(3d)}}{4d}\, dt
= \frac{1}{4d}\Big[d\,(1-e^{-r/d}) + 3d\,(1 - e^{-r/(3d)})\Big]
$$

$$
C(r) = 1 - \frac{1}{4}e^{-r/d} - \frac{3}{4}e^{-r/(3d)}
$$

### 10.4 解方程 C(r) = u

令 $\tau = r/d$，$y = e^{-\tau/3}$（所以 $e^{-\tau} = y^3$），再令 $v = 1 - u$：

$$
1 - \tfrac14 y^3 - \tfrac34 y = u \quad\Longrightarrow\quad y^3 + 3y = 4v
$$

这是一个三次方程。不用记卡尔达诺公式，用"猜形式 + 验证"就能解：

**猜** $y = a - \frac{1}{a}$。展开立方：

$$
y^3 = a^3 - 3a + \frac{3}{a} - \frac{1}{a^3} = \left(a^3 - \frac{1}{a^3}\right) - 3\left(a - \frac{1}{a}\right) = \left(a^3 - \frac{1}{a^3}\right) - 3y
$$

所以 $y^3 + 3y = a^3 - \frac{1}{a^3}$。令 $A = a^3$，方程变成：

$$
A - \frac{1}{A} = 4v \quad\Longrightarrow\quad A^2 - 4vA - 1 = 0 \quad\Longrightarrow\quad A = 2v + \sqrt{4v^2 + 1}
$$

（取正根，因为 $a > 0$。）于是：

$$
y = A^{1/3} - A^{-1/3}, \qquad r = -3d\,\ln y
$$

### 10.5 HDRP 的等价改写（数值更稳定）

当 $v$ 很小时，$A^{1/3}$ 和 $A^{-1/3}$ 都接近 1，相减会损失精度。HDRP 改写成没有减法的形式。

利用 $(a - \frac1a)(1 + a^2 + \frac{1}{a^2}) = a^3 - \frac{1}{a^3} = 4v$（展开即可验证），得到：

$$
y = \frac{4v}{1 + a^2 + a^{-2}}
$$

再令 $g = A^2$，则 $a^2 = g^{1/3}$、$a^{-2} = g^{-1/3}$，并且：

$$
g = (2v + \sqrt{4v^2+1})^2 = 1 + 4v\left(2v + \sqrt{1 + 4v^2}\right)
$$

这就是代码：

```hlsl
float v = 1.0 - u;
float g = 1.0 + 4.0 * v * (2.0 * v + sqrt(1.0 + 4.0 * v * v));
float n = pow(g, -1.0 / 3.0);   // g^(-1/3) = a^(-2)
float p = g * n * n;            // g^(+1/3) = a^2
float c = 1.0 + p + n;
float y = 4.0 * v / c;          // y = exp(-r / 3d)
r = -3.0 * log(y) * d;
```

### 10.6 顺便算出 1/q

第 9.3 节的权重要除以 $q$。$q$ 也能用 $y$ 表示：

$$
q(r,\theta) = \frac{e^{-\tau} + e^{-\tau/3}}{8\pi d} = \frac{y^3 + y}{8\pi d}
\quad\Longrightarrow\quad
\frac{1}{q} = \frac{8\pi d}{y^3 + y}
$$

```hlsl
rcpPdf = 8.0 * PI * d / (y * y * y + y);
```

## 11. 角度、截断半径与采样数

### 11.1 u 不用随机数：分层采样

HDRP 不用真随机数，而是取 $u_k = \frac{k + 0.5}{N}$，即把 $[0,1)$ 等分成 $N$ 段，每段中点取一个。这叫**分层采样**：保证 CDF 的每一段都恰好有一个样本，不会扎堆，噪点比纯随机小得多。

### 11.2 角度：黄金角 + 每像素随机旋转

- **黄金角**：第 $k$ 个样本的角度取 $\theta_k = k \times 2.39996$ 弧度（约 137.5°，$= 2\pi(1 - 1/\varphi)$，$\varphi$ 是黄金比例）。用这个角度每次转一下，无论取多少个点，它们在圆周上都分布得很均匀，不会排成几条直线。
- **每像素随机旋转**：如果所有像素用同一套采样图案，画面上会出现规律的条纹。所以给每个像素加一个随机初始角 $\theta_k = \text{phase} + k\times 2.39996$，`phase` 由像素坐标生成。条纹变成细碎噪点，噪点可以被 TAA 抹掉。

### 11.3 截断半径：CDF = 0.997

理论上 $r$ 可以到无穷远，实际只需要覆盖 99.7% 的能量。解 $C(r) = 0.997$，即 $v = 0.003$，代入第 10.5 节：

- $g = 1 + 0.012 \times (0.006 + 1.000018) \approx 1.01207$
- $g^{1/3} \approx 1.00401$，$g^{-1/3} \approx 0.99601$，$c \approx 3.00002$
- $y = 0.012 / 3.00002 \approx 0.004$
- $\tau = -3\ln 0.004 \approx 16.56$

所以**滤波半径** $= 16.56\, d_{max}$（毫米）。这个数只和 0.997 有关，可以写成常量。HDRP 在 C# 里预算这个值（`SampleBurleyDiffusionProfile(0.997f, maxScatteringDistance)`）。

### 11.4 采样数：按屏幕面积决定

同样的散射半径，物体离相机近时在屏幕上占很多像素，离得远时可能不到一个像素。HDRP 的规则：

$$
N = \min\left(\frac{\pi \cdot (\text{屏幕上的滤波半径像素数})^2}{4},\ \text{采样预算}\right)
$$

- 分子是圆盘在屏幕上覆盖的像素个数，除以 4 表示"大约每 4 个像素采一个"。
- 采样预算（budget）是上限，控制性能。
- 如果 $N < 1$，说明散射范围不到一个像素，直接输出中心像素，不用模糊。

## 12. 屏幕空间的补丁：单位换算与双边权重

### 12.1 毫米 ↔ 像素

$r$ 的单位是毫米，采样要换成像素偏移。用之前推导过的结论：在线性深度 depth（米）处，1 个像素对应的世界宽度为

$$
\text{metersPerPixel} = \text{depth}\cdot\frac{2}{P_{00}\cdot\text{width}}
$$

（$P_{00}$ 是投影矩阵第 0 行第 0 列，width 是 RT 宽度。$\frac{2}{P_{00}\cdot\text{width}}$ 在 C# 里每帧算一次传给 shader。）所以：

$$
\text{pixelsPerMm} = \frac{1}{\text{metersPerPixel} \times 1000}
$$

样本的像素偏移 $= r_k \cdot \text{pixelsPerMm}\cdot(\cos\theta_k, \sin\theta_k)$。

> 这里假设 1 个 Unity 单位 = 1 米。HDRP 的 Diffusion Profile 里有 World Scale 参数，就是处理"1 单位不等于 1 米"的情况。

### 12.2 双边权重：用深度差修正距离

屏幕上相邻的两个像素，在 3D 空间里可能隔得很远（比如鼻尖和后面的脸颊、耳朵和后面的脖子）。只用屏幕距离 $r$ 算权重，光会"穿越"到不相连的表面上，出现漏光。

修正方法：把深度差 $\Delta z$ 也算进距离（勾股定理）：

$$
r' = \sqrt{r^2 + \Delta z^2}
$$

权重改成用 $r'$ 计算 $R$：

$$
w_{k,c} = \frac{R_c(r'_k)\, r'_k}{q(r_k,\theta_k)}
$$

- 分子用 3D 距离 $r'$：深度断崖处 $r'$ 很大，$R$ 衰减到接近 0，自动丢掉，不需要额外的阈值。
- 分母仍然是按 $r_k$ 撒点时的 pdf，因为样本就是按 $r_k$ 撒的。
- 当 $\Delta z = 0$ 时，$r' = r$，退化回第 9.3 节。

这就是"双边滤波"：权重既看空间距离，也看深度差。

### 12.3 遮罩

邻居像素如果不是 SSS 物体（参数 RT 的 alpha 为 0），直接丢弃这个样本。配合第 9.4 节的自归一化，边缘不会变暗，也不会把背景色模糊进皮肤。

## 13. 最终工程公式

把第 5~12 节的近似全部合起来，每个 SSS 像素 $p_o$ 的最终颜色为：

$$
\boxed{\;
L_{final}(p_o) = \sqrt{\rho(p_o)}\cdot\frac{\displaystyle\sum_{k\in\text{有效}} w_k\, I_k}{\displaystyle\sum_{k\in\text{有效}} w_k} \;+\; L_{spec}(p_o)
\;}
$$

其中：

| 量 | 计算方式 | 来自 |
|---|---|---|
| $I_k$ | Diffuse RT 在样本像素的值 $= \sqrt{\rho}\cdot D$ | 第 5、6 节 |
| $u_k$ | $\frac{k+0.5}{N}$ | 第 11.1 节 |
| $r_k$ | 用 $u_k$、$d_{max}$ 解逆 CDF | 第 10 节 |
| $\theta_k$ | $\text{phase} + k \times 2.39996$ | 第 11.2 节 |
| 样本像素 | 中心像素 $+ r_k\cdot\text{pixelsPerMm}\cdot(\cos\theta_k,\sin\theta_k)$ | 第 12.1 节 |
| $r'_k$ | $\sqrt{r_k^2 + \Delta z_k^2}$ | 第 12.2 节 |
| $w_{k,c}$ | $\dfrac{e^{-r'_k/d_c} + e^{-r'_k/(3d_c)}}{8\pi d_c}\cdot\dfrac{8\pi d_{max}}{y_k^3 + y_k}$ | 第 9.3、10.6、12.2 节 |
| 有效 | 样本在 SSS 物体上（遮罩 > 0） | 第 12.3 节 |
| $N$ | $\min(\pi R_{px}^2/4,\ \text{budget})$，$R_{px} = 16.56\, d_{max}\cdot\text{pixelsPerMm}$ | 第 11.3、11.4 节 |
| $L_{spec}$ | 普通的 PBR 高光 + 环境反射 | 第 3 节 |

**每一步用了什么近似，汇总如下：**

| 近似 | 丢掉了什么 | 后果 |
|---|---|---|
| BSSRDF 分离 + Burley 拟合 | 物体有限厚度、内部不均匀 | 薄的部位（耳朵）透光不对，需要单独做透射（第 21 节） |
| 忽略出射 $F_t(\omega_o)$ 的角度变化 | 掠射角时的亮度变化 | 影响很小 |
| 局部平面 + 屏幕平面 | 曲面、斜面的真实距离 | 斜面上散射偏窄，双边权重部分补偿 |
| 屏幕空间 | 屏幕外、被遮挡部分的光 | 屏幕边缘、遮挡边缘略有误差 |
| 少量样本 | 精确积分 | 有噪点，靠每像素随机旋转 + TAA 解决 |

## 14. Unity HDRP 实际是怎么做的

### 14.1 HDRP 的流程

1. **GBuffer / Forward 阶段**：SSS 材质除了正常输出，还把"SSS 数据"（albedo、subsurface mask、diffusion profile 编号）写进专门的 SSS buffer，并在 stencil 上打标记。
2. **光照阶段（split lighting）**：SSS 像素的光照拆成两份：漫反射写到单独的缓冲（乘的是按 Texturing Mode 处理过的 albedo，对应第 6 节），高光直接写到相机颜色。
3. **`SubsurfaceScattering.compute`**：
   - 每 16×16 像素一个线程组，先用粗粒度 stencil 跳过不含 SSS 的整块区域。
   - 每个像素按 profile 编号取出 $s = 1/d$、$d_{max}$、滤波半径、World Scale。
   - 算 pixelsPerMm、样本数 $N = \min(\text{面积}/4, \text{Sample Budget})$。
   - 用第 10 节的逆 CDF + 黄金角 + 每像素随机旋转撒点，用第 12.2 节的双边权重累加。
   - 结果 $= \text{albedo 修正} \times \sum w I / \sum w$，加回相机颜色。
4. **TAA**：把每像素随机旋转产生的噪点在时间上平均掉。
5. **透射（Transmission）**：薄的部位（耳朵、鼻翼）背光时透出的光，在光照阶段用 `ComputeTransmittanceDisney` 单独计算，不走屏幕空间模糊。

HDRP 早期版本还提供过 Jimenez 的可分离（横 + 竖两次 1D）方案，后来只保留了 Burley 圆盘采样。

### 14.2 HDRP Diffusion Profile 的参数

| 参数 | 含义 | 对应本教程 |
|---|---|---|
| Scattering Distance（颜色 × 乘数） | RGB 三个通道的 $d$，单位毫米 | 材质的 `_ScatterColor × _ScatterDistance` |
| World Scale | 1 个 Unity 单位等于多少米 | 本教程固定为 1 |
| Texturing Mode | Pre & Post Scatter / Post Scatter | 本教程固定为 Pre & Post（$m = 0.5$） |
| Transmission Mode / Thickness Remap / IOR | 透射相关 | 本教程未实现 |

### 14.3 本教程与 HDRP 的差异

| 方面 | HDRP | 本教程 |
|---|---|---|
| 参数存储 | Diffusion Profile 资源，最多十几个，像素里只存编号 | 每像素直接存 $d$（MRT 第二张图） |
| 漫反射来源 | 完整光照系统的 split lighting | 单独一个 Pass，只算主光源 + 环境光 |
| 计算方式 | Compute Shader + 共享内存缓存 | 全屏 Pixel Shader |
| 跳过非 SSS 区域 | Stencil | Alpha 遮罩 |
| 降噪 | TAA | 无（可在 URP 相机上开 TAA） |
| 公式与采样 | 第 13 节 | 相同 |

---

# 第二部分：实践——在 URP 里写出来

## 15. 整体数据流

### 15.1 用到的 RT

| RT | 内容 | 格式 | 写入者 | 读取者 |
|---|---|---|---|---|
| `_SSSDiffuseRT` | $I = \sqrt{\rho}\cdot D$ | ARGBHalf | 步骤一 | 步骤三 |
| `_SSSParamRT` | rgb = $d$（毫米），a = 遮罩 | ARGBHalf | 步骤一 | 步骤三 |
| `_SSSDepthRT` | SSS 物体自己的深度 | Depth 32 | 步骤一 | 步骤三 |
| `_SSSResultRT` | $\sum wI / \sum w$ | ARGBHalf | 步骤三 | 步骤四 |

> **为什么 Diffuse 用 Half 格式**：光照值会超过 1（强光、HDR），8 位格式会截断并产生色带。

> **为什么要有自己的深度 RT**：
> 1. 步骤一画 SSS 物体时需要深度测试，否则物体背面会盖住正面。
> 2. 步骤三需要深度算 $\Delta z$。
> 3. 这个 Pass 在不透明物体之前执行，那时还拿不到完整的相机深度；而且相机开了 MSAA 时，它的深度缓冲和我们的单采样 RT 不能混用。

### 15.2 执行顺序

```
URP 一帧:
  阴影 → 深度预渲染 → [SSSBurleyPass: 步骤一 + 步骤三] → 不透明物体（含步骤四） → 天空盒 → ...
                        ↑ RenderPassEvent.AfterRenderingPrePasses
```

SSS 必须在不透明物体**之前**算好，因为步骤四（SSS 材质的前向 Pass）要读结果。

## 16. 步骤一：材质 Shader 输出漫反射与参数

文件：`Assets/SSS-TEST/SSSBurley/SSSBurleyLit.shader`，Pass `SSSBurleyDiffuse`。

### 16.1 目的

对应理论第 5、6 节：每个 SSS 像素算出 $I = \sqrt{\rho}\cdot D$ 存进 Diffuse RT，同时存下散射参数 $d$ 和遮罩，并写深度。

### 16.2 自定义 LightMode：让这个 Pass 只被我们的 Feature 画

```hlsl
Pass
{
    Name "SSSBurleyDiffuse"
    Tags { "LightMode" = "SSSBurleyDiffuse" }
    ZWrite On
    ZTest LEqual
    Cull Back
    ...
}
```

- URP 只会自动画 `LightMode` 为 `UniversalForward`、`ShadowCaster`、`DepthOnly` 等内置名字的 Pass。
- 写一个自定义名字 `SSSBurleyDiffuse`，URP 就不会自己画它。我们在 C# 里用 `new ShaderTagId("SSSBurleyDiffuse")` 告诉 `DrawRenderers`"只画带这个标签的 Pass"，就能把所有 SSS 物体画到我们自己的 RT 上。
- 没有这个 Pass 的物体（地面、墙）不会被画，Diffuse RT 上对应位置保持清屏的 0，遮罩也是 0。

### 16.3 MRT：一次输出两张图

```hlsl
struct FragOutput
{
    half4 diffuse : SV_Target0; // rgb = D * sqrt(albedo)
    half4 param   : SV_Target1; // rgb = 每通道散射距离 d（毫米），a = SSS 遮罩
};
```

MRT（Multiple Render Targets）：片元着色器同时往多张 RT 写数据。`SV_Target0` 写到 C# 里绑定的第 0 张，`SV_Target1` 写到第 1 张。这样只画一遍物体就得到两张图，比画两遍省。

### 16.4 计算 D

```hlsl
// 皮肤折射率约 1.4 → F0 = ((1.4-1)/(1.4+1))^2 ≈ 0.028
#define SKIN_F0 0.028

float FresnelSchlick(float f0, float cosTheta)
{
    return f0 + (1.0 - f0) * pow(1.0 - saturate(cosTheta), 5.0);
}

FragOutput frag(Varyings i)
{
    half3 albedo = SampleAlbedo(i.uv);
    float3 n = normalize(i.normalWS);
    float3 v = normalize(GetCameraPositionWS() - i.positionWS);

    Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
    float3 h = normalize(v + light.direction);
    float NdotL = saturate(dot(n, light.direction));

    // 进入皮肤的比例 = 1 - 表面反射掉的比例
    float kdDirect  = 1.0 - FresnelSchlick(SKIN_F0, dot(v, h));
    float kdAmbient = 1.0 - FresnelSchlick(SKIN_F0, dot(n, v));

    // D：一个"白色"漫反射表面在这里接收到的光（不含 albedo）
    float3 D = kdDirect * light.color * (light.shadowAttenuation * light.distanceAttenuation * NdotL)
             + kdAmbient * SampleSH(n);

    FragOutput o;
    o.diffuse = half4(D * sqrt(albedo), 1);
    o.param = half4(_ScatterColor.rgb * _ScatterDistance, 1);
    return o;
}
```

逐行对应理论：

| 代码 | 理论 |
|---|---|
| `FresnelSchlick` | 菲涅尔反射率 $F$ 的 Schlick 近似：$F = F_0 + (1-F_0)(1-\cos)^5$。$F_0$ 是垂直入射时的反射率，由折射率 $\eta$ 决定：$F_0 = \left(\frac{\eta-1}{\eta+1}\right)^2$ |
| `kdDirect = 1 - F` | 第 3 节的 $F_t(\omega_i)$：进入皮肤的比例。直接光用 $V\cdot H$（与高光项保持一致），环境光没有确定方向，用 $N\cdot V$ 近似 |
| `light.color * shadow * NdotL` | 第 5 节内层积分的主光源部分。阴影一定要乘在这里，SSS 最明显的效果就是阴影边缘发红 |
| `SampleSH(n)` | 环境光（球谐） |
| `D * sqrt(albedo)` | 第 6 节，$m = 0.5$ 的模糊前那一半 |
| `_ScatterColor.rgb * _ScatterDistance` | $d_R, d_G, d_B$（毫米）。颜色决定三通道的比例，距离决定整体大小 |
| `param.a = 1` | 遮罩：这里是 SSS 物体 |

**参数设计**：材质上的 `_ScatterColor` 默认 `(1, 0.35, 0.2)`，`_ScatterDistance` 默认 5 mm，所以 $d = (5, 1.75, 1)$ mm。真实皮肤的红色通道 $d$ 约 1 mm 量级（HDRP 默认皮肤 profile 是 `(0.76, 0.32, 0.2)` mm 左右），这里故意调大，方便在测试场景里看出效果。

## 17. 步骤二：Renderer Feature 组织流程

文件：`Assets/SSS-TEST/SSSBurley/SSSBurleyFeature.cs`

### 17.1 前提知识：URP 的 Renderer Feature

- **`ScriptableRendererFeature`**：挂在 Renderer 资源（如 `URP-HighFidelity-Renderer`）上的插件。`Create()` 在资源加载时调用一次，`AddRenderPasses()` 每个相机每帧调用一次，用来把自定义 Pass 塞进渲染队列。
- **`ScriptableRenderPass`**：真正干活的类。`renderPassEvent` 决定它插在哪个位置；`OnCameraSetup()` 在执行前调用，适合分配 RT；`Execute()` 里录制渲染命令。
- **`CommandBuffer`**：命令列表。往里添加"设置 RT""画东西"等命令，`context.ExecuteCommandBuffer(cmd)` 时才提交。
- **`RTHandle`**：URP 14 的 RT 包装类，配合 `RenderingUtils.ReAllocateIfNeeded` 使用：尺寸或格式变了才重新分配，否则复用。

### 17.2 选择执行时机

```csharp
renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
```

- 必须在**阴影之后**：步骤一要采样阴影图。
- 必须在**不透明物体之前**：步骤四要读结果。
- 为什么不用知乎文章里的 `AfterRenderingShadows + 1`：URP 在阴影之后、预渲染之前才设置相机的 View / Projection 矩阵。在那之前画物体，矩阵是错的，所以文章里不得不手动调 `SetViewProjectionMatrices`。`AfterRenderingPrePasses` 时相机矩阵已经设好，可以省掉这一步。

### 17.3 分配 RT

```csharp
public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
{
    var desc = renderingData.cameraData.cameraTargetDescriptor;
    desc.msaaSamples = 1;
    desc.depthBufferBits = 0;
    desc.colorFormat = RenderTextureFormat.ARGBHalf;
    RenderingUtils.ReAllocateIfNeeded(ref m_DiffuseRT, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SSSDiffuseRT");
    RenderingUtils.ReAllocateIfNeeded(ref m_ParamRT, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SSSParamRT");
    RenderingUtils.ReAllocateIfNeeded(ref m_ResultRT, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SSSResultRT");

    var depthDesc = desc;
    depthDesc.graphicsFormat = GraphicsFormat.None;
    depthDesc.depthStencilFormat = GraphicsFormat.D32_SFloat_S8_UInt;
    RenderingUtils.ReAllocateIfNeeded(ref m_DepthRT, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SSSDepthRT");
}
```

- 以相机目标的描述符为模板，尺寸自动和相机一致（包括 Render Scale）。
- `msaaSamples = 1`：MSAA 的 RT 不能直接在 shader 里按像素读取。
- 颜色 RT 不需要自带深度（`depthBufferBits = 0`），深度单独一张。
- 深度 RT：`graphicsFormat = None` 表示没有颜色部分，`depthStencilFormat` 指定深度格式。这种纯深度 RT 也可以在 shader 里当纹理读（读出来的 `.r` 就是深度值）。
- `FilterMode.Point`：滤波 shader 用 `LOAD_TEXTURE2D` 按整数像素读取，不需要插值。

### 17.4 Execute 第 1 段：画 SSS 物体

```csharp
cmd.SetRenderTarget(new RenderTargetIdentifier[] { m_DiffuseRT, m_ParamRT }, m_DepthRT);
cmd.ClearRenderTarget(true, true, Color.clear);
context.ExecuteCommandBuffer(cmd);
cmd.Clear();

var drawing = CreateDrawingSettings(k_DiffuseTag, ref renderingData, SortingCriteria.CommonOpaque);
context.DrawRenderers(renderingData.cullResults, ref drawing, ref m_Filtering);
```

- `SetRenderTarget(颜色数组, 深度)`：绑定 MRT。数组第 0 个对应 shader 里的 `SV_Target0`，第 1 个对应 `SV_Target1`。
- `ClearRenderTarget(true, true, Color.clear)`：深度清成最远，颜色清成 0。清成 0 很关键，遮罩 alpha = 0 表示"不是 SSS 物体"。
- **必须先 `ExecuteCommandBuffer` 再 `DrawRenderers`**：`DrawRenderers` 是直接提交给 context 的，不在 cmd 里。如果不先提交 cmd，设置 RT 的命令会排在画物体之后执行，物体就画错地方了。
- `CreateDrawingSettings(k_DiffuseTag, ...)`：只画带 `LightMode = SSSBurleyDiffuse` 的 Pass。它会继承相机的灯光、阴影等每物体数据。
- `m_Filtering = new FilteringSettings(RenderQueueRange.opaque)`：只考虑不透明队列的物体。
- `renderingData.cullResults`：URP 已经做好的视锥剔除结果，复用即可。

### 17.5 Execute 第 2 段：滤波

```csharp
var desc = renderingData.cameraData.cameraTargetDescriptor;
var proj = renderingData.cameraData.GetProjectionMatrix();
cmd.SetGlobalTexture(k_ParamRT, m_ParamRT);
cmd.SetGlobalTexture(k_DepthRT, m_DepthRT);
cmd.SetGlobalVector(k_Params, new Vector4(
    2f / (proj.m00 * desc.width),
    m_Settings.sampleBudget,
    desc.width,
    desc.height));
Blitter.BlitCameraTexture(cmd, m_DiffuseRT, m_ResultRT, m_Material, 0);
```

- `SetGlobalTexture / SetGlobalVector`：设成全局变量，所有 shader 里同名变量都能读到。
- `_SSSParams.x = 2/(P00·width)`：第 12.1 节的系数，乘以深度就是每像素对应的米数。
- `_SSSParams.zw`：RT 尺寸，用来防止采样越界。
- `Blitter.BlitCameraTexture(cmd, 源, 目标, 材质, pass)`：画一个铺满目标的全屏三角形，用指定材质的指定 Pass，并把"源"自动绑定到 shader 的 `_BlitTexture`。这就是步骤三的入口。

### 17.6 Execute 第 3、4 段：传出结果、还原渲染目标

```csharp
cmd.SetGlobalTexture(k_ResultRT, m_ResultRT);

var renderer = renderingData.cameraData.renderer;
CoreUtils.SetRenderTarget(cmd, renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);

context.ExecuteCommandBuffer(cmd);
CommandBufferPool.Release(cmd);
```

- `_SSSResultRT` 设成全局纹理，步骤四的前向 Pass 才能读到。
- **还原渲染目标**：URP 会记住"当前绑定的是哪个 RT"，如果下一个 Pass 的目标和它记住的一样，就不会重新绑定。我们在 cmd 里偷偷换了 RT，URP 并不知道，下一个 Pass（画不透明物体）可能就画到我们的 RT 上去了。所以结束前手动绑回相机的颜色和深度。

### 17.7 Feature 主体

```csharp
public override void Create()
{
    if (settings.filterShader == null) return;
    m_Material = CoreUtils.CreateEngineMaterial(settings.filterShader);
    m_Pass = new SSSBurleyPass(settings, m_Material);
}

public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
{
    var cameraType = renderingData.cameraData.cameraType;
    if (m_Pass == null || (cameraType != CameraType.Game && cameraType != CameraType.SceneView))
        return;
    renderer.EnqueuePass(m_Pass);
}
```

- `CreateEngineMaterial`：用 shader 创建一个不保存到磁盘的材质（隐藏标记），`Dispose` 时用 `CoreUtils.Destroy` 销毁。
- 只对 Game 和 Scene 视图相机执行。材质预览、反射探针这类相机跳过，省性能。

## 18. 步骤三：散射滤波 Shader

文件：`Assets/SSS-TEST/SSSBurley/SSSBurleyFilter.shader`

### 18.1 目的

对每个 SSS 像素执行第 13 节的公式，输出 $\sum wI/\sum w$。

### 18.2 全屏三角形顶点着色器

```hlsl
struct Attributes { uint vertexID : SV_VertexID; };
struct Varyings   { float4 positionCS : SV_POSITION; };

Varyings vert(Attributes v)
{
    Varyings o;
    o.positionCS = GetFullScreenTriangleVertexPosition(v.vertexID);
    return o;
}
```

`Blitter` 画的是一个只有 3 个顶点、没有顶点缓冲的大三角形，它盖住整个屏幕。`SV_VertexID` 是顶点编号 0、1、2，`GetFullScreenTriangleVertexPosition` 根据编号算出三角形顶点位置。这样屏幕上每个像素都会执行一次片元着色器。

### 18.3 用 LOAD 按像素读取

```hlsl
int2 pixel = int2(i.positionCS.xy);
float3 centerIrradiance = LOAD_TEXTURE2D(_BlitTexture, pixel).rgb;
```

- 片元着色器里的 `SV_POSITION.xy` 是当前像素的屏幕坐标（像素中心，如 `(100.5, 200.5)`），转成 `int2` 就是像素编号。
- `LOAD_TEXTURE2D(tex, int2)` 直接按整数像素坐标读取，不做插值、不需要采样器。所有 RT 尺寸相同，同一个像素编号在各张 RT 上对应同一个位置。
- 用像素坐标而不是 UV，可以避免在不同平台上的 UV 上下翻转问题。

### 18.4 第 1 段：非 SSS 像素直接返回

```hlsl
float4 param = LOAD_TEXTURE2D(_SSSParamRT, pixel);
if (param.a <= 0) return float4(centerIrradiance, 0);
```

遮罩为 0 说明这里不是 SSS 物体，不用算。

### 18.5 第 2 段：单位换算与采样数

```hlsl
float3 d = max(param.rgb, 1e-4);
float dMax = max(d.r, max(d.g, d.b));

float centerZ = LoadLinearDepth(pixel);
float metersPerPixel = centerZ * _SSSParams.x;
float pixelsPerMm = 1.0 / (metersPerPixel * 1000.0);

float filterRadius = BURLEY_X_AT_CDF_0997 * dMax;
float filterRadiusPx = filterRadius * pixelsPerMm;
float filterArea = PI * filterRadiusPx * filterRadiusPx;
uint n = min((uint)(filterArea / SSS_PIXELS_PER_SAMPLE), (uint)_SSSParams.y);
if (n < 1) return float4(centerIrradiance, 1);
```

| 代码 | 理论 |
|---|---|
| `max(param.rgb, 1e-4)` | 防止后面 `r / d` 除以 0 |
| `LoadLinearDepth` | 深度缓冲存的是非线性值，用 `LinearEyeDepth` 还原成到相机的距离（米） |
| `metersPerPixel`、`pixelsPerMm` | 第 12.1 节 |
| `BURLEY_X_AT_CDF_0997 * dMax` | 第 11.3 节，$16.56\, d_{max}$ |
| `n = min(面积 / 4, 预算)` | 第 11.4 节 |

**数值例子**：$d_{max} = 5$ mm，物体在 3 m 处，fovY = 60°，1920×1080：

- $\frac{2}{P_{00}\cdot\text{width}} \approx 0.00107$，metersPerPixel $\approx 0.0032$，pixelsPerMm $\approx 0.31$
- 滤波半径 $= 82.8$ mm $\approx 26$ 像素，面积 $\approx 2100$ 像素，$/4 \approx 525$，被预算 32 截断，$N = 32$

### 18.6 第 3 段：采样循环

```hlsl
int2 maxPixel = int2(_SSSParams.zw) - 1;
float phase = TWO_PI * PixelNoise(i.positionCS.xy);

float3 totalIrradiance = 0;
float3 totalWeight = 0;
for (uint k = 0; k < n; k++)
{
    float r, rcpPdf;
    SampleBurley((k + 0.5) / n, dMax, r, rcpPdf);

    float theta = phase + k * GOLDEN_ANGLE;
    float2 offset = r * pixelsPerMm * float2(cos(theta), sin(theta));
    int2 samplePixel = clamp(pixel + int2(round(offset)), int2(0, 0), maxPixel);

    if (LOAD_TEXTURE2D(_SSSParamRT, samplePixel).a <= 0) continue;

    float3 irradiance = LOAD_TEXTURE2D(_BlitTexture, samplePixel).rgb;
    float dzMm = (LoadLinearDepth(samplePixel) - centerZ) * 1000.0;
    float r3D = sqrt(r * r + dzMm * dzMm);

    float3 w = BurleyTimesR(r3D, d) * rcpPdf;
    totalIrradiance += w * irradiance;
    totalWeight += w;
}
```

| 代码 | 理论 |
|---|---|
| `PixelNoise` | Interleaved Gradient Noise，一个用像素坐标算伪随机数的公式，相邻像素的值差异大且分布均匀 |
| `(k + 0.5) / n` | 第 11.1 节分层采样的 $u_k$ |
| `SampleBurley(u, dMax, r, rcpPdf)` | 第 10 节：用 $d_{max}$ 解逆 CDF 得到 $r_k$ 和 $1/q$ |
| `phase + k * GOLDEN_ANGLE` | 第 11.2 节 |
| `r * pixelsPerMm * (cos, sin)` | 毫米 → 像素偏移 |
| `clamp(...)` | 防止读到 RT 外面 |
| `.a <= 0 → continue` | 第 12.3 节遮罩：丢弃非 SSS 样本 |
| `r3D = sqrt(r² + dz²)` | 第 12.2 节双边距离（深度差从米换成毫米） |
| `BurleyTimesR(r3D, d) * rcpPdf` | 第 9.3 节权重 $w_{k,c}$，三个通道用各自的 $d_c$ |

`BurleyTimesR` 就是 $R(r)\cdot r$：

```hlsl
float3 BurleyTimesR(float r, float3 d)
{
    return (exp(-r / d) + exp(-r / (3.0 * d))) / (8.0 * PI * d);
}
```

`d` 是 `float3`，所以 `exp`、除法都是三个通道分别算，一次得到 RGB 三个权重。

### 18.7 第 4 段：归一化输出

```hlsl
float3 result = totalWeight > 1e-5 ? totalIrradiance / totalWeight : centerIrradiance;
return float4(result, 1);
```

- 第 9.4 节的自归一化。
- 某个通道所有样本都被丢弃（`totalWeight` 接近 0）时，退回中心像素，避免 0/0。
- `totalWeight > 1e-5` 对 `float3` 是逐通道比较，三元运算符也逐通道选择。

## 19. 步骤四：前向 Pass 合成

文件：`Assets/SSS-TEST/SSSBurley/SSSBurleyLit.shader`，Pass `ForwardComposite`。

### 19.1 目的

对应第 13 节公式的最后一步：$\sqrt{\rho(p_o)}\times$ 散射结果 $+$ 高光。

### 19.2 实现

```hlsl
Pass
{
    Name "ForwardComposite"
    Tags { "LightMode" = "UniversalForward" }
    ...
    TEXTURE2D(_SSSResultRT);

    half4 frag(Varyings i) : SV_Target
    {
        half3 albedo = SampleAlbedo(i.uv);
        ...
        half3 specular = brdfData.specular * DirectBRDFSpecular(brdfData, n, light.direction, v) * radiance;
        ...
        half3 indirectSpecular = GlossyEnvironmentReflection(...) * EnvironmentBRDFSpecular(brdfData, fresnelTerm);

        half3 scattered = LOAD_TEXTURE2D(_SSSResultRT, int2(i.positionCS.xy)).rgb;
        return half4(scattered * sqrt(albedo) + specular + indirectSpecular, 1);
    }
}
```

- `LightMode = UniversalForward`：URP 画不透明物体时会自动画这个 Pass。
- 高光部分直接用 URP 的 `BRDFData` / `DirectBRDFSpecular` / `GlossyEnvironmentReflection`，和普通 Lit 一致。
- `scattered * sqrt(albedo)`：第 6 节模糊后的那一半 albedo。
- 用 `int2(i.positionCS.xy)` 读结果：步骤三的 RT 和相机目标尺寸一致，同一像素坐标对应同一位置。

### 19.3 为什么在前向 Pass 里合成，而不是最后全屏叠加

你原来的实现是在不透明物体画完之后，全屏 `Blend One One` 把结果加到相机颜色上。这样做有两个问题：

1. 如果 SSS 物体被别的物体挡住，全屏叠加仍然会把它的漫反射加到挡在前面的物体上，必须额外做深度比较。
2. 后乘的那一半 $\sqrt{\rho}$ 需要每个像素的 albedo，全屏 Pass 里拿不到，还得多存一张图。

在前向 Pass 里读结果，相机的深度测试自动处理遮挡，albedo 也是现成的。知乎文章的 Pass3 也是这样做的。

## 20. 使用、调参与调试

### 20.1 接入步骤

1. 打开 `Assets/Settings/URP-HighFidelity-Renderer.asset`（或你当前使用的 Renderer）。
2. 关掉原来的 `SSSRenderFeature`（避免两套流程互相干扰）。
3. Add Renderer Feature → `SSS Burley Feature`，`Filter Shader` 选 `Hidden/SSS/SSSBurleyFilter`。
4. 新建材质，Shader 选 `SSS/SSSBurleyLit`，赋给测试模型。

### 20.2 推荐的测试场景

SSS 的效果只在光照有变化的地方出现（第 5 节退化检验），所以：

- 在模型前放一个物体投下阴影，观察**阴影边缘**是否有一圈红色过渡。
- 用侧光，观察**明暗交界线**是否变柔和、偏红。
- 有法线贴图的话，细节凹凸会被柔化（皮肤"蜡质感"的来源）。
- 用白光、白色 albedo 测试，避免颜色干扰判断。

### 20.3 参数含义

| 参数 | 位置 | 作用 | 建议 |
|---|---|---|---|
| Scatter Color | 材质 | 三通道 $d$ 的比例，决定颜色偏移 | 皮肤 `(1, 0.35, 0.2)` 左右；玉石偏绿 |
| Scatter Distance | 材质 | $d$ 的整体大小（mm） | 真实人头尺度约 1~2 mm；测试时可调到 10~30 mm |
| Sample Budget | Feature | 每像素最多采样数 | 16~32；太低会有噪点 |

### 20.4 用 Frame Debugger 检查

Window → Analysis → Frame Debugger，找到 `SSS Burley`：

| 检查项 | 期望 | 不对时的原因 |
|---|---|---|
| DrawRenderers 后的 `_SSSDiffuseRT` | SSS 物体的漫反射光照，其他地方黑色 | 全黑：材质没用 `SSSBurleyLit`，或 Pass 时机不对导致没有灯光数据 |
| `_SSSParamRT` | SSS 物体处为 Scatter 颜色 | 全黑：同上 |
| Blit 后的 `_SSSResultRT` | 和 Diffuse 相比，阴影边缘变软、偏红 | 和 Diffuse 一样：$d$ 太小或物体太远，$N < 1$ 被跳过；调大 Scatter Distance |
| 最终画面 | 物体正常显示 | 物体全黑：`_SSSResultRT` 没传过来（Feature 未启用，或当前相机被跳过） |

### 20.5 常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| 有细碎噪点 | 样本少 + 每像素随机旋转 | 提高 Budget，或在相机上开 TAA |
| 边缘出现一圈亮/暗边 | 遮罩、深度不一致 | 检查深度 RT 是否正确写入 |
| 远处物体没效果 | 散射范围小于一个像素 | 这是正确的：远处本来就看不出 SSS |
| 近处特别卡 | 屏幕上滤波半径太大 | Budget 已经限制了样本数；性能瓶颈通常是大半径带来的缓存不命中 |

## 21. 进阶方向

1. **更多光源**：步骤一里遍历 `GetAdditionalLightsCount()`，把附加光源的漫反射也累加进 $D$。
2. **透射**：耳朵、鼻翼背光时的红色透光。屏幕空间模糊处理不了（光是从背面来的），需要在步骤一里额外算：用 $-\mathbf{n}\cdot\omega_i$ 或阴影图估计厚度 $t$，按 Burley 剖面的透射公式 $T(t) = \frac14\left(e^{-t/d} + 3e^{-t/(3d)}\right)$ 衰减后加进 $D$。知乎文章中 `ComputeTransmittanceDisney` 就是这个公式。
3. **Diffusion Profile 化**：像 HDRP 一样把参数做成资源，像素里只存编号，节省一张 RT。
4. **Stencil 优化**：步骤一写 stencil，步骤三用 stencil 测试跳过非 SSS 像素，比在 shader 里读遮罩判断更省。
5. **TAA 配合**：在 `phase` 里加入帧号，让每帧的采样图案不同，TAA 累积后等效于更多样本。
6. **切平面采样**：HDRP 有一个 `SSS_USE_TANGENT_PLANE` 选项，在表面的切平面而不是屏幕平面上撒点，斜面上更准确，代价是需要法线。

---

## 附：和你之前可分离实现的对比

| 方面 | 之前（`SSSFilter.shader`） | 本教程（`SSSBurleyFilter.shader`） |
|---|---|---|
| 采样形状 | 横一遍、竖一遍（两次 1D） | 一次 2D 圆盘 |
| 样本分布 | 均匀 | 按 Burley 剖面重要性采样 |
| 权重 | 经验去掉 $1/r$ 后的 1D 近似 | $R(r)\,r / q$，严格对应第 13 节的积分估计 |
| 采样数 | 固定 | 随屏幕尺寸自适应，有上限 |
| Albedo | 全部模糊前乘（Pre-Scatter） | $\sqrt{\rho}$ 前后各一半 |
| 合成 | 不透明之后全屏叠加 | 前向 Pass 内读取 |
| 噪点 | 无（规则采样） | 有少量噪点，需要 TAA |

可分离方案性能好、没有噪点，是 Jimenez 2015 的经典做法；圆盘重要性采样更贴近积分公式本身，是 HDRP 当前的做法。两种都是可用的工程方案。
