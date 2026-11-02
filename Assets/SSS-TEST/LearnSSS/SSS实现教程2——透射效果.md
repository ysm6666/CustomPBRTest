# SSS 实现教程 2：透射（Transmission）——从渲染方程到 URP 代码

> 前置：本文默认你已读过《SSS实现教程.md》（下文简称"教程 1"），符号、单位、近似的编号都沿用教程 1。
>
> 本文要回答三个问题：
> 1. 教程 1 的 SSS 为什么是"简化版"，在公式上丢掉了哪一项；
> 2. 丢掉的那一项怎么一步步推导成可以在 shader 里算的公式；
> 3. 公式里每个符号在代码里取什么值，业界（HDRP、Jimenez、Barré-Brisebois）各自怎么取。
>
> **关于出处**：文中所有"业界做法"都标了来源。标"源码"的，代码取自对应仓库（HDRP 2022.3 / Jimenez `SeparableSSS.h`），有省略时会注明"节选"；**代码块里的英文注释是原文，中文注释是本文加的**。标"经验公式"的，表示它不是从 BSSRDF 推出来的，而是为了好看、好调设计的。第 16 节的参考实现已在本工程（Unity 2022.3 / URP 14.0.12）下编译通过（无阴影、单张阴影、级联阴影三个变体）。

---

## 目录

- [0. 新增符号](#0-新增符号)
- [1. 总览：透射是什么，加在流程哪里](#1-总览透射是什么加在流程哪里)
- [第一部分：理论——从渲染方程推出透射公式](#第一部分理论从渲染方程推出透射公式)
  - [2. 教程 1 的公式丢掉了什么](#2-教程-1-的公式丢掉了什么)
  - [3. 局部厚板模型](#3-局部厚板模型)
  - [4. 推导透射项 L_T](#4-推导透射项-l_t)
  - [5. 合进现有 SSS 流程](#5-合进现有-sss-流程)
  - [6. 这个模型的局限](#6-这个模型的局限)
- [第二部分：从符号到代码——每个量从哪来](#第二部分从符号到代码每个量从哪来)
  - [7. 符号—代码总表](#7-符号代码总表)
  - [8. 散射距离 d](#8-散射距离-d)
  - [9. 厚度 t：三种来源](#9-厚度-t三种来源)
  - [10. 透射率 T(t)：三种剖面](#10-透射率-tt三种剖面)
  - [11. 角度项 a：背面接收了多少光](#11-角度项-a背面接收了多少光)
  - [12. 背面可见性 V_B：要不要乘阴影](#12-背面可见性-v_b要不要乘阴影)
  - [13. 其余各项：环境光、菲涅尔、颜色、是否参与模糊](#13-其余各项环境光菲涅尔颜色是否参与模糊)
  - [14. 另一条路：Barré-Brisebois 经验公式](#14-另一条路barré-brisebois-经验公式)
  - [15. 各方案对比](#15-各方案对比)
- [第三部分：实现](#第三部分实现)
  - [16. 参考实现（基于 SSSLit 的 SSSDiffuse Pass）](#16-参考实现基于-ssslit-的-sssdiffuse-pass)
  - [17. 调试与调参](#17-调试与调参)
  - [18. 参考资料](#18-参考资料)

---

## 0. 新增符号

教程 1 第 0 节的符号全部沿用。本文新增：

| 符号 | 含义 | 单位/取值 |
|---|---|---|
| $\mathcal{A}_F$ | 与 $p_o$ **同一侧**的表面（光从这里进、横向扩散到 $p_o$ 出来）。教程 1 只积分了这一部分 | — |
| $\mathcal{A}_B$ | 与 $p_o$ **相对一侧**的表面，也就是物体的"背面"（光从这里进、穿过物体到 $p_o$ 出来） | — |
| $L_R$ | 反射项：$\mathcal{A}_F$ 的贡献，即教程 1 算的东西 | — |
| $L_T$ | 透射项：$\mathcal{A}_B$ 的贡献，本文要算的东西 | — |
| $\Pi_F,\ \Pi_B$ | 厚板模型中的正面平面、背面平面（第 3 节） | — |
| $t$ | 厚度：$\Pi_F$ 与 $\Pi_B$ 之间的**垂直**距离 | 毫米 |
| $p_o'$ | $p_o$ 在背面 $\Pi_B$ 上的垂足 | 位置 |
| $\ell$ | 三维距离 $\ell = \lVert p_i - p_o\rVert$（背面入射点到出射点） | 毫米 |
| $\mathbf{l}$ | 指向光源的单位向量（平行光对所有点相同），即 URP 的 `mainLight.direction` | 单位向量 |
| $E_B,\ D_B$ | 背面入射点接收并进入物体的光；$D_B = E_B/\pi$ | — |
| $T(t)$ | **透射率**：厚度为 $t$ 时，背面进入的光有多少比例从正面出来。RGB 各一个 | 0~1 |
| $a(\cdot)$ | 角度项：背面接收光照的余弦项（及其各种变体） | 0~1 |
| $V_B$ | 背面入射点的可见性（是否被别的东西挡住光） | 0~1 |
| $c_{tint}$ | 透射颜色乘数（美术参数，不来自推导） | 0~1 |
| $t_l$ | 沿光线方向量得的穿透长度（shadow map 能给出的量），区别于垂直厚度 $t$ | 毫米 |
| $\kappa$ | 环绕光照（wrap lighting）参数 | — |
| $\alpha_j,\ \sigma_j^2$ | 高斯和剖面中第 $j$ 个高斯的权重与方差 | —、毫米² |

> **注意符号冲突**：教程 1 用 $s = 1/d$ 表示 HDRP 的 ShapeParam，所以本文三维距离用 $\ell$，不用 $s$。

---

## 1. 总览：透射是什么，加在流程哪里

**一句话**：教程 1 的 SSS 算的是"光从**同一面**进、横向跑一段、再从同一面出来"；透射算的是"光从**背面**进、**穿过**物体、从正面出来"。逆光看耳朵、叶子、玉石边缘发亮，就是后者。

透射不需要新的 Pass。它只是在教程 1 步骤一（画 Diffuse RT）里多加一项：

```
 步骤一（SSSDiffuse Pass）                      步骤二~四：完全不变
 ┌────────────────────────────────────────┐
 │ Diffuse RT = √ρ · ( D_正面  +  T(t)·D_B )│ → 模糊 → ×√ρ → +高光
 │                    └教程1┘  └─本文新增─┘ │
 └────────────────────────────────────────┘
```

| 新增的量 | 目的 | 对应理论 | 对应代码 |
|---|---|---|---|
| 厚度 $t$ | 光在物体里要穿过多远 | 第 3 节 | 第 9 节 |
| 透射率 $T(t)$ | 穿过 $t$ 之后还剩多少 | 第 4.4 节 | 第 10 节 |
| 背面光照 $D_B$ | 背面接收了多少光 | 第 4.2 节 | 第 11~13 节 |

---

# 第一部分：理论——从渲染方程推出透射公式

## 2. 教程 1 的公式丢掉了什么

### 2.1 起点：完整的 BSSRDF 渲染方程

和教程 1 第 2 节完全相同：

$$
L_o(p_o,\omega_o) = \int_{\mathcal{A}} \int_{\Omega} S(p_o,\omega_o,p_i,\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i \, d\omega_i \, dA
$$

这里的 $\mathcal{A}$ 是**物体的整个表面**，包括背光的那一半。这个式子本身没有区分"反射"和"透射"：光从表面上任何一点进去、从 $p_o$ 出来，都算在里面。

### 2.2 教程 1 的三处处理，各自砍掉了背面

回看教程 1 从这个积分走到工程公式的过程，有三个地方让背面的贡献变成了 0：

**① BSSRDF 分离时用的剖面 $R(r)$ 假设介质"无限厚"（教程 1 第 3 节）**

教程 1 第 3 节写过：拆分 $S$ 时"假设物体是无限大、无限厚、内部均匀的平板"。Burley 剖面就是在这个假设下拟合出来的。HDRP 源码里也写明了同一个前提（`SurfaceShading.hlsl`）：

> *"Our subsurface scattering models use the semi-infinite planar slab assumption."*

"半无限"的意思是：只有一个表面，下面是无限深的介质。**这个模型里根本不存在"背面"**，光进去以后只能从同一个面出来。所以 $R(r)$ 描述的只有反射。

**② 局部平面近似（教程 1 第 7 节，近似二）**

教程 1 把积分域 $\mathcal{A}$ 换成了"$p_o$ 附近的切平面"。切平面只覆盖 $p_o$ 这一侧，物体另一侧的点不在这个平面上。

**③ 屏幕空间 + 正面光照**

- 模糊只能读屏幕上看得见的像素，背面没有像素；
- 每个像素存的 $D(p)$ 中，主光部分正比于 $\max(0,\ \mathbf{n}\cdot\mathbf{l})$。逆光时，正面像素 $\mathbf{n}\cdot\mathbf{l} < 0$，主光部分为 0，从背后来的光在 Diffuse RT 里一点都没有。

三者叠加的结果是：**教程 1 实际只算了 $\mathcal{A}$ 中与 $p_o$ 同侧的那一部分**。

### 2.3 把积分域拆成两块

**前提知识：积分区域可以拆开**。如果区域 $\mathcal{A}$ 由互不重叠的两块 $\mathcal{A}_F$、$\mathcal{A}_B$ 组成，那么

$$
\int_{\mathcal{A}} f\, dA = \int_{\mathcal{A}_F} f\, dA + \int_{\mathcal{A}_B} f\, dA
$$

原因：积分是"切成小块求和"，把小块分成两堆分别求和，再加起来，总和不变。

用到渲染方程上（被积函数 $S\,L_i\cos\theta_i$ 简记为"$\cdots$"）：

$$
L_o(p_o,\omega_o) = \underbrace{\int_{\mathcal{A}_F}\int_\Omega \cdots\, d\omega_i\, dA}_{L_R:\ \text{教程 1 算的}} \;+\; \underbrace{\int_{\mathcal{A}_B}\int_\Omega \cdots\, d\omega_i\, dA}_{L_T:\ \text{被丢掉的}}
$$

- $L_R$（反射项）：教程 1 已经把它变成了"Diffuse RT + 圆盘模糊"。
- $L_T$（透射项）：本文的目标。

**结论**：教程 1 不是"错了"，而是只算了 $L_R$。顺光时 $L_T$ 几乎为 0（背面没光），所以看不出差别；逆光时 $L_T$ 是主要贡献，缺了它就没有透光。教程 1 第 13 节的近似汇总表里，第一行"BSSRDF 分离 + Burley 拟合 → 薄的部位透光不对，需要单独做透射"说的就是这件事。

## 3. 局部厚板模型

要算 $L_T$，必须知道"背面"长什么样。真实物体形状任意，没法一般地积分，所以要再做一个几何近似。

### 3.1 假设 T1：局部是一块平行厚板

把 $p_o$ 附近的物体看成一块**两面平行、厚度为 $t$ 的平板**：

```
                  相机
                   ↑
  ─────────────────●─────────────────  Π_F：正面，包含 p_o，外法线 n
                   │ p_o
                   │
                   │ t（垂直厚度）
                   │
  ─────────────────○───────────●─────  Π_B：背面，外法线 −n
                  p_o'  ←─ r ─→ p_i
                                ↑
                         光从背面射入
```

- 正面 $\Pi_F$ 就是教程 1 的切平面，$\mathcal{A}_F \to \Pi_F$；
- 背面 $\Pi_B$ 与它平行、相距 $t$，$\mathcal{A}_B \to \Pi_B$；
- 背面的外法线朝外、也就是朝 $-\mathbf{n}$ 方向。

### 3.2 背面入射点到出射点的距离

以垂足 $p_o'$ 为圆心，在 $\Pi_B$ 上用极坐标 $(r,\theta)$ 描述入射点 $p_i$。$p_o \to p_o'$ 垂直于平面，长 $t$；$p_o' \to p_i$ 在平面内，长 $r$；两段互相垂直，由勾股定理：

$$
\ell = \lVert p_i - p_o \rVert = \sqrt{r^2 + t^2}
$$

### 3.3 假设 T2：背面也用同一个剖面，按三维距离求值

光从 $p_i$ 进、从 $p_o$ 出，它的 BSSRDF 我们沿用教程 1 第 3 节的拆分形式，只把剖面的自变量换成三维距离 $\ell$：

$$
S(p_o,\omega_o,p_i,\omega_i) \approx \frac{1}{\pi}\, F_t(\omega_o)\; \rho\, R(\ell)\; F_t(\omega_i), \qquad p_i \in \Pi_B
$$

**这是一个启发式假设，不是严格推论**。$R$ 是为"半无限介质里，同一表面上两点"拟合的；把它当作"三维空间里两点之间的扩散核"直接拿来用，是 HDRP 的做法。HDRP 的 `ComputeTransmittanceDisney` 注释原文是：

> *"Evaluate Int{0, inf}{2 \* Pi \* r \* R(sqrt(r^2 + d^2))}, where R is the diffusion profile."*

即"对 $R(\sqrt{r^2+d^2})$ 在平面上积分"，这里 HDRP 的 `d` 就是本文的厚度 $t$。第 6.1 节会讲这个假设带来的问题。

## 4. 推导透射项 L_T

### 4.1 代入并整理（与教程 1 第 5 节完全同一套操作）

**第 1 步：写出 $L_T$**

$$
L_T(p_o,\omega_o) = \int_{\mathcal{A}_B}\int_{\Omega} S(p_o,\omega_o,p_i,\omega_i)\, L_i(p_i,\omega_i)\cos\theta_i\, d\omega_i\, dA
$$

注意 $\theta_i$ 是入射方向与**入射点 $p_i$ 处法线**的夹角。$p_i$ 在背面，所以这里的法线是背面法线（厚板模型里是 $-\mathbf{n}$）。

**第 2 步：代入假设 T2 的 $S$**

$$
L_T = \int_{\mathcal{A}_B}\int_{\Omega} \frac{1}{\pi}\, F_t(\omega_o)\, \rho\, R(\ell)\, F_t(\omega_i)\, L_i \cos\theta_i\, d\omega_i\, dA
$$

**第 3 步：按"随谁变化"分组**（规则见教程 1 第 5.1 节）

- 两者都不依赖：$\frac{1}{\pi}$、$F_t(\omega_o)$、$\rho$（先视为常数）
- 只依赖 $p_i$：$R(\ell)$（$\ell$ 随 $p_i$ 变）
- 依赖 $\omega_i$：$F_t(\omega_i)$、$L_i$、$\cos\theta_i$

**第 4、5 步：把不含 $\omega_i$ 的提出内层，把不含 $p_i$ 的提到最外面**

$$
L_T = \frac{1}{\pi}\, F_t(\omega_o) \int_{\mathcal{A}_B} \rho\, R(\ell) \left[\int_{\Omega} F_t(\omega_i)\, L_i \cos\theta_i\, d\omega_i\right] dA
$$

**第 6 步：给中括号命名**

$$
E_B(p_i) = \int_{\Omega} F_t(\omega_i)\, L_i(p_i,\omega_i) \cos\theta_i\, d\omega_i
$$

它和教程 1 的 $E$ 是同一个东西，只不过是在**背面**的点上算：背面那一点接收到、并进入物体的光。

**第 7 步：沿用教程 1 的近似一**，$F_t(\omega_o) \approx 1$，并定义 $D_B = E_B / \pi$：

$$
L_T(p_o) \approx \int_{\mathcal{A}_B} \rho\, R(\ell)\, D_B(p_i)\, dA
$$

和教程 1 的 $L_R \approx \int_{\mathcal{A}_F} \rho\, R(r)\, D(p_i)\, dA$ 对比：形式完全一样，区别只有两点——积分域换成背面，距离换成三维距离 $\ell$。

### 4.2 背面光照 D_B 在 Unity 里是什么

和教程 1 第 5.5 节同理（Unity 的灯光强度已经吃掉了 $\pi$），只是法线换成背面法线 $-\mathbf{n}$：

$$
D_B = \underbrace{(1-F)\cdot \text{lightColor}\cdot V_B \cdot \max(0,\ -\mathbf{n}\cdot\mathbf{l})}_{\text{主光源}} \;+\; \underbrace{(1-F)\cdot \text{SampleSH}(-\mathbf{n})}_{\text{环境光}}
$$

- $\max(0,-\mathbf{n}\cdot\mathbf{l})$：背面法线与光方向的余弦。逆光时 $\mathbf{n}\cdot\mathbf{l}<0$，这一项反而大于 0。
- $V_B$：**背面入射点**有没有被挡住。注意是背面那个点，不是当前像素 $p_o$。第 12 节会专门讲它为什么不能直接用 `mainLight.shadowAttenuation`。
- $(1-F)$：背面入射时的菲涅尔透射。逆光时没有合适的半角向量可用，工程上通常取 $\approx 1$（假设 T4，第 13.3 节）。

### 4.3 假设 T3：背面光照在局部是均匀的

$R(\ell)$ 衰减很快，只有 $p_o'$ 附近一小片背面的贡献不可忽略。假设这一小片上 $D_B$ 近似相同，等于 $D_B(p_o')$，就可以把它当常数提到积分外面（教程 1 第 5.1 节的前提知识 1：与积分变量无关的因子可以提出）。同时 $\rho$ 也是常数：

$$
L_T(p_o) \approx \rho\, D_B \int_{\Pi_B} R(\ell)\, dA
$$

于是问题只剩下一个**纯几何积分**：

$$
T(t) \;\equiv\; \int_{\Pi_B} R\!\left(\sqrt{r^2+t^2}\right) dA
$$

它只和厚度 $t$、散射距离 $d$ 有关，和光照无关。

### 4.4 计算 T(t)

**前提知识 1：换元积分**

如果新变量 $\ell$ 是旧变量 $r$ 的函数 $\ell = g(r)$，那么

$$
\int_{a}^{b} f\big(g(r)\big)\, g'(r)\, dr = \int_{g(a)}^{g(b)} f(\ell)\, d\ell
$$

直观理解：$g'(r)\,dr$ 就是 $r$ 变化 $dr$ 时 $\ell$ 的变化量 $d\ell$。把"小段 $dr$"换成"小段 $d\ell$"，上下限也跟着换成 $\ell$ 的取值。

**前提知识 2：指数函数从 $t$ 到无穷的积分**

$$
\int_t^\infty e^{-\ell/a}\, d\ell = a\, e^{-t/a}
$$

推导：$e^{-\ell/a}$ 的原函数是 $-a\,e^{-\ell/a}$（求导验证：$\frac{d}{d\ell}(-a\,e^{-\ell/a}) = e^{-\ell/a}$）。代入上下限：$\ell\to\infty$ 时为 0，$\ell = t$ 时为 $-a\,e^{-t/a}$，相减得 $a\,e^{-t/a}$。当 $t=0$ 时就是教程 1 第 4.3 节用过的 $\int_0^\infty e^{-r/a}dr = a$。

**第 1 步：写成极坐标**（$dA = r\,dr\,d\theta$，教程 1 第 4.2 节）

$$
T(t) = \int_0^{2\pi}\!\!\int_0^\infty R\!\left(\sqrt{r^2+t^2}\right) r\, dr\, d\theta
$$

**第 2 步：对 $\theta$ 积分**。被积函数与 $\theta$ 无关，得到 $2\pi$：

$$
T(t) = 2\pi\int_0^\infty R\!\left(\sqrt{r^2+t^2}\right) r\, dr
$$

**第 3 步：换元 $\ell = \sqrt{r^2+t^2}$**

- 求导：$\dfrac{d\ell}{dr} = \dfrac{r}{\sqrt{r^2+t^2}} = \dfrac{r}{\ell}$，所以 $r\,dr = \ell\,d\ell$；
- 下限：$r = 0 \Rightarrow \ell = t$；
- 上限：$r \to \infty \Rightarrow \ell \to \infty$。

$$
T(t) = 2\pi\int_t^\infty R(\ell)\, \ell\, d\ell
$$

**第 4 步：代入 Burley 剖面**。$R(\ell) = \dfrac{e^{-\ell/d} + e^{-\ell/(3d)}}{8\pi d\,\ell}$，乘以 $\ell$ 正好消掉分母里的 $\ell$：

$$
T(t) = 2\pi\int_t^\infty \frac{e^{-\ell/d} + e^{-\ell/(3d)}}{8\pi d}\, d\ell = \frac{1}{4d}\int_t^\infty \left(e^{-\ell/d} + e^{-\ell/(3d)}\right) d\ell
$$

**第 5 步：用前提知识 2**（两项分别取 $a = d$ 和 $a = 3d$）

$$
T(t) = \frac{1}{4d}\left(d\, e^{-t/d} + 3d\, e^{-t/(3d)}\right)
$$

**第 6 步：化简**

$$
\boxed{\;T(t) = \frac{1}{4}\,e^{-t/d} + \frac{3}{4}\,e^{-t/(3d)}\;}
$$

RGB 三个通道各用自己的 $d_c$ 算一遍，得到一个 RGB 透射率。

### 4.5 检验与直观理解

**三条检验**：

| 检验 | 结果 | 含义 |
|---|---|---|
| $t = 0$ | $T = \frac14 + \frac34 = 1$ | 没有厚度，背面进来的光全部到达正面（模型极限情况，见第 6.1 节） |
| $t \to \infty$ | $T \to 0$ | 无限厚，光穿不过去 |
| $t$ 增大 | $T$ 单调减小 | 越厚越暗 |

**和教程 1 的 CDF 的关系**：教程 1 第 10 节的 $C(r) = 1 - \frac14 e^{-r/d} - \frac34 e^{-r/(3d)}$，所以

$$
T(t) = 1 - C(t)
$$

几何意义：第 3 步之后的 $2\pi\int_t^\infty R(\ell)\,\ell\,d\ell$，和"在正面平面上，把 $R(r)$ 在半径大于 $t$ 的区域（圆外）积分"是同一个式子。也就是说，**穿过厚度 $t$ 的能量比例 = 横向扩散超过距离 $t$ 的能量比例**。

**数值表**（$\tau = t/d$，无量纲）：

| $\tau = t/d$ | 0 | 0.5 | 1 | 2 | 4 | 8 | 16.56 |
|---|---|---|---|---|---|---|---|
| $T$ | 1.000 | 0.787 | 0.629 | 0.419 | 0.202 | 0.052 | 0.003 |

最后一列和教程 1 的截断半径对应：$\tau = 16.56$ 时 $C = 0.997$，$T = 0.003$。

**这张表也解释了一个常见现象："厚度从 0 调到 1 mm，颜色突变"**：当 `_ScatterColor = (1,0,0)` 时，G、B 通道的 $d$ 被钳到 $10^{-4}$ mm，厚度只要 1 mm，$\tau = 10^4$，$T_{G,B}$ 立刻变成 0。这符合公式：$d=0$ 就是"完全不散射"，自然也透不过光。

### 4.6 小结

$$
L_T(p_o) \approx \rho \cdot T(t) \cdot D_B
$$

假设清单：T1 局部厚板；T2 背面沿用同一剖面、按三维距离求值；T3 背面光照局部均匀；T4 背面入射菲涅尔 $\approx 1$；以及教程 1 的近似一 $F_t(\omega_o)\approx1$。

## 5. 合进现有 SSS 流程

### 5.1 颜色的拆分

教程 1 第 6 节把 $\rho$ 拆成 $\rho^m \cdot \rho^{1-m}$（本工程 $m = 0.5$）。透射项照做：

$$
L_T(p_o) \approx \sqrt{\rho(p_o)}\cdot \underbrace{\sqrt{\rho(p_o)}\cdot T(t)\cdot D_B}_{I_T(p_o)}
$$

### 5.2 透射项也放进 Diffuse RT 一起模糊

教程 1 的 Diffuse RT 里存的是 $I_R = \sqrt{\rho}\,D$。现在把 $I_T$ 加进同一张 RT：

$$
I(p) = I_R(p) + I_T(p) = \sqrt{\rho(p)}\,\Big[D(p) + c_{tint}\, T\big(t(p)\big)\, D_B(p)\Big]
$$

注意 $I_T$ 是在**正面像素 $p$ 自己**身上算出来的：背面平面上的积分已经在 4.4 节被解析地算掉了，结果只依赖 $p$ 的厚度和背面光照 $D_B$。而在厚板模型里背面法线就是 $-\mathbf{n}$，所以 $D_B$ 可以直接用 $p$ 自己的法线取反来算（可见性 $V_B$ 例外，见第 12 节）。因此每个屏幕像素都能算出自己的 $I_T$，不需要背面像素。$c_{tint}$ 是美术乘数，推导里不存在，物理上取 1（第 13.4 节）。

然后照常做教程 1 的模糊。**这一步是工程选择，不是推导结果**，理由有两个：

1. **HDRP 就是这么做的**。`Lit.hlsl` 里把透射加进 diffuse 时的注释原文：*"We use diffuse lighting for accumulation since it is going to be blurred during the SSS pass."*
2. **它近似补回了假设 T3 丢掉的东西**。T3 假设背面光照均匀。如果不均匀（比如背面有阴影边界），严格结果是 $D_B$ 与核 $R(\sqrt{r^2+t^2})$ 的卷积，透射光应该是"糊的"。先乘 $T$ 再用 $R(r)$ 模糊，也会把它糊开。两者并不相等，但都让透射光有柔和的边缘，而不是生硬的分界线。

### 5.3 最终工程公式

在教程 1 第 13 节公式的基础上，只改 $I_k$ 的定义：

$$
\boxed{\;
L_{final}(p_o) = \sqrt{\rho(p_o)}\cdot\frac{\displaystyle\sum_{k} w_k\, I_k}{\displaystyle\sum_{k} w_k} + L_{spec}(p_o),
\qquad
I_k = \sqrt{\rho}\,\Big[D + c_{tint}\, T(t)\, D_B\Big]_{\text{样本像素 }k}
\;}
$$

$$
T(t) = \tfrac14 e^{-t/d} + \tfrac34 e^{-t/(3d)},\qquad
D_B = \text{lightColor}\cdot V_B\cdot a(\mathbf{n},\mathbf{l}) + \text{SampleSH}(-\mathbf{n})
$$

其中推导给出的角度项是 $a = \max(0, -\mathbf{n}\cdot\mathbf{l})$。第二部分会逐个说明 $t$、$d$、$a$、$V_B$ 在代码里怎么取，以及业界的不同取法。

## 6. 这个模型的局限

把局限写在前面，是为了让你在第二部分看到各种"修补手段"时，知道它们在修补什么。

### 6.1 假设 T2 导致能量不守恒

教程 1 第 4.3 节证明了 $\int R\, dA = 1$：在半无限模型里，从某一面进去的光，**全部**（乘以 $\rho$）会从同一面出来。

现在又让背面进来的光有 $\rho\, T(t)$ 从正面出去。对背面那份入射光来说，总出射是 $\rho\,(1 + T) > \rho$，多出来的就是不守恒的部分。原因是：真正的有限厚板里，透射走的能量要从反射里扣掉，薄板的反射应该比半无限介质更暗。

工程上能接受，原因是同一视角下你通常只看到其中一面。物理上更准确的有限厚板扩散模型是 multipole（Donner & Jensen 2005），实时渲染里很少用。

$t = 0$ 时 $T = 1$ 也属于这个问题：零厚度的板应该是"没有介质，光直接穿过"，而不是"扩散后全部穿过"。

### 6.2 厚度应该沿哪个方向量

推导里的 $t$ 是**垂直于表面**的厚度（图 3.1）。但第 9.2 节的 shadow map 方法量到的是**沿光线方向**的穿透长度 $t_l$。厚板几何里两者的关系是 $t = t_l\cdot(-\mathbf{n}\cdot\mathbf{l})$。

HDRP 知道这个差别，但选择不乘这一项。`SurfaceShading.hlsl` 注释原文：

> *"Our subsurface scattering models use the semi-infinite planar slab assumption. Therefore, we need to find the thickness along the normal. Note: based on the artist's input, dependence on the NdotL has been disabled."*

Jimenez 的实现同样直接用 $t_l$。所以业界的实际做法是：**用光线方向的穿透长度代替垂直厚度**。这在厚板模型里不严格；但真实物体是弯的（比如球），"垂直厚度"本身就没有明确定义（Alan Zucconi 的教程里有一张图专门说明这一点），沿光线方向的长度反而更接近"光实际走了多远"。

### 6.3 扩散模型和视角无关

扩散模型输出的光在各方向均匀分布（教程 1 第 3 节的 $\frac1\pi$），所以 $L_T$ 从哪个方向看都一样亮。

迎着光源看时，玉石、蜡烛那种"中心发亮"的通透感，来自**前向散射**：光倾向于沿原方向继续走，再加上低阶（少数几次）散射。这些不在扩散模型里。第 14 节的 Barré-Brisebois 公式就是用一个与视角相关的经验项去模拟它。

> 容易混淆的一点：本文推出的透射项**属于多次散射**（扩散）。被扩散模型漏掉的是单次 / 低阶散射以及各向异性的前向散射，不是多次散射。

---

# 第二部分：从符号到代码——每个量从哪来

## 7. 符号—代码总表

| 符号 | 含义 | 本工程取值（第 16 节） | 业界其他取法 |
|---|---|---|---|
| $d$ | 散射距离 | `_ScatterColor.rgb * _ScatterDistance`（mm） | 第 8 节 |
| $t$ | 厚度 | 厚度贴图，或 shadow map 测量 | 第 9 节，三种 |
| $T(t)$ | 透射率 | Burley 解析式 | 第 10 节，三种 |
| $a$ | 角度项 | HDRP 环绕光照，$\kappa = 0$ 时退化为推导式 | 第 11 节，三种 |
| $V_B$ | 背面可见性 | 贴图模式乘正面阴影；shadow map 模式取 1 | 第 12 节 |
| lightColor | 主光颜色 | `mainLight.color * mainLight.distanceAttenuation` | 第 13.1 节 |
| SampleSH$(-\mathbf{n})$ | 背面环境光 | `SampleSH(-n)` 乘贴图厚度对应的 $T$ | 第 13.2 节 |
| $F_t(\omega_i)$ | 背面入射菲涅尔 | 取 1 | 第 13.3 节 |
| $c_{tint}$ | 透射颜色 | `_TransmissionTint` | 第 13.4 节 |
| $\sqrt\rho$ + 模糊 | 颜色拆分、参与模糊 | 写进 Diffuse RT | 第 13.5 节 |

## 8. 散射距离 d

**推导要求**：与反射项使用**同一个** $d$。透射和反射是同一个剖面的两种积分，$d$ 不一致就没有物理联系了。

| 方案 | 取法 | 出处 |
|---|---|---|
| 本工程 | `_ScatterColor.rgb * _ScatterDistance`，单位 mm，和写进 `sssParam` 的值相同 | — |
| HDRP | Diffusion Profile 里的 Scattering Distance；shader 中用的是倒数 $S = 1/d$（`_ShapeParamsAndMaxScatterDists.rgb`） | 源码 |
| Jimenez | 没有 $d$。用一个经验系数缩放厚度：`scale = 8.25 * (1.0 - translucency) / sssWidth` | 源码 |
| Barré-Brisebois | 没有 $d$，全部由美术参数控制 | 经验公式 |

## 9. 厚度 t：三种来源

厚度是透射里**最难拿到**的量：shader 只知道当前像素，不知道物体另一面在哪。业界有三条路。

### 9.1 方法 A：厚度贴图（HDRP Thin 模式）

**思路**：厚度是物体的固有属性，离线烘焙进贴图，运行时直接读。

**HDRP 源码**（`SubsurfaceScattering.hlsl`，`FillMaterialTransmission`）：

```hlsl
float2 remap = _WorldScalesAndFilterRadiiAndThicknessRemaps[diffusionProfileIndex].zw;
bsdfData.thickness = remap.x + remap.y * thickness;   // thickness 是贴图值
```

贴图值经 Diffusion Profile 里的厚度重映射（remap）变成毫米。按这个公式，**贴图越白越厚**。本工程的 `_ThicknessMap.r * _ThicknessScale` 是同一种语义（只有缩放没有偏移）。

**贴图怎么来**：Barré-Brisebois 提出的烘焙流程（Alan Zucconi 的教程转述）是"翻转模型法线 → 烘 AO → 反相"。注意它得到的是"越白**越透**"（越薄），语义和 HDRP 相反。拿这种贴图给本工程用，需要先反相，或者用 `(1 - tex) * scale`。

**适用**：耳朵、叶子、薄布这类"厚度主要由形状决定、与光方向关系不大"的物体。

**局限**：厚度和光的方向无关。同一个点不管光从哪边来都一样厚，所以闭合的厚物体（比如球）上效果不对。**用一张常量白图时，所有点厚度相同，得不到边缘透光**，只会让整个背光面均匀变亮或变暗。

### 9.2 方法 B：从 shadow map 测厚度（Jimenez / GPU Gems 1）

**思路**：shadow map 记录的是"从光源看过去，每个方向上**最先被照到的表面**的深度"，也就是光**进入**物体的位置。当前像素是光**离开**物体的位置。两者在光空间的深度差，就是光在物体内走过的长度 $t_l$。

```
 光源 ──→ [入射点] ══════ t_l ══════ [当前像素 p_o] ──→ 相机
           ↑                              ↑
     shadow map 存的深度          p_o 在光空间的深度
```

GPU Gems 1 第 16 章（Simon Green，用深度图模拟吸收）中已有这一思路，Jimenez 等人把它用在皮肤透射上。

**Jimenez 源码**（`SeparableSSS.h`，`SSSSTransmittance`；节选，原注释改写为中文）：

```hlsl
// 沿法线把位置往物体里缩一点，避免伪影
float4 shrinkedPos = float4(worldPosition - 0.005 * worldNormal, 1.0);
// 从光源视角计算厚度
float4 shadowPosition = SSSSMul(shrinkedPos, lightViewProjection);
float d1 = SSSSSample(shadowMap, shadowPosition.xy / shadowPosition.w).r; // 0..1
float d2 = shadowPosition.z;                                              // 0..lightFarPlane
d1 *= lightFarPlane;
float d = scale * abs(d1 - d2);
```

**在 URP 主光源（平行光）上怎么实现**（第 16 节代码的 `ShadowMapThicknessMM`）：

| 步骤 | 代码 | 说明 |
|---|---|---|
| 1. 往里缩 | `p = positionWS - n * _ThicknessShrink` | 同 Jimenez。受光面的点 $t_l \approx 0$，缩一点可以避开自身深度的精度问题 |
| 2. 选级联 | `ComputeCascadeIndex(p)` | 只在开了级联阴影时需要。返回值等于 `MAX_SHADOW_CASCADES` 表示超出所有级联，URP 给这一档放的是空矩阵，必须跳过 |
| 3. 变换到光空间 | `mul(_MainLightWorldToShadow[c], float4(p,1))` | 结果的 xy 是 shadow map 上的纹理坐标，z 是 $p$ 在光空间的深度 |
| 4. 读入射点深度 | `LOAD_TEXTURE2D(_MainLightShadowmapTexture, xy * _MainLightShadowmapSize.zw).r` | 用 `LOAD` 读原始深度。不能用 URP 的阴影采样函数，那个返回的是 0/1 比较结果 |
| 5. 深度差换成米 | `abs(z - entryZ) / length(_MainLightWorldToShadow[c][2].xyz)` | 平行光是正交投影，深度与距离成线性关系：$z = \mathbf{m}_2\cdot p + c$，沿光方向移动 1 米，$z$ 变化 $\lVert\mathbf{m}_2\rVert$（$\mathbf{m}_2$ 是矩阵第三行的前三个分量） |
| 6. 换成毫米 | `* 1000` | 与 $d$ 的单位一致 |

反向 Z（DX11）下，shadow map 里的深度和 `shadowCoord.z` 都是反向的，二者相减后取绝对值即可，不影响结果。

**优点**：厚度随光方向变化，对任意闭合形状都成立。球体逆光时，中心穿透长、边缘穿透短，边缘透光就出来了。

**局限**：
- 测的是 $t_l$ 不是 $t$（第 6.2 节）；
- 阴影投射时的 depth bias / normal bias 会把入射面往远离光的方向推，$t_l$ 偏小；
- 光路中间如果有别的物体，测到的是"到那个物体的距离"，$t_l$ 偏大、$T$ 偏小。HDRP 注释认为这可以接受：*"If the distance is large, it may indicate that the closest occluder is not the back face of the current object. That's not a problem, since large thickness will result in low intensity."*
- 精度受 shadow map 分辨率和级联范围限制；超出阴影距离就测不到，需要回退到方法 A。

### 9.3 方法 C：烘焙厚度 + shadow map 修正（HDRP Thick 模式）

**HDRP 源码**（`SurfaceShading.hlsl`，`EvaluateTransmittance_Punctual`，只用于**点光源 / 聚光灯**；节选：函数参数用 `...` 省略，`thicknessInMeters`、`thicknessInMillimeters` 两行合并成一行）：

```hlsl
float distBackFaceToLight = GetPunctualShadowClosestDistance(...);   // shadow map 中最近遮挡物到光源的距离
float distFrontFaceToLight   = distances.x;                          // 当前点到光源的距离
float thicknessInUnits       = (distFrontFaceToLight - distBackFaceToLight) /* * -NdotL */;
float thicknessInMillimeters = thicknessInUnits * metersPerUnit * MILLIMETERS_PER_METER;

// We need to make sure it's not less than the baked thickness to minimize light leaking.
float dt = max(0, thicknessInMillimeters - bsdfData.thickness);
float3 exp_13 = exp2(((LOG2_E * (-1.0/3.0)) * dt) * S);              // Exp[-S * dt / 3]
// Approximate the decrease of transmittance by e^(-1/3 * dt * S).
return bsdfData.transmittance * exp_13;
```

**思路**：以烘焙厚度 $t_b$ 算出的 $T(t_b)$ 为基础；shadow map 测得的厚度只在**大于** $t_b$ 时起作用，用多出来的 $\Delta t$ 进一步衰减。这样烘焙厚度相当于一个下限，可以防止 shadow map 精度不足时漏光。

**为什么乘 $e^{-\Delta t/(3d)}$ 是合理的近似**：把 $T$ 改写成

$$
T(t) = \tfrac34 e^{-t/(3d)}\Big(1 + \tfrac13 e^{-2t/(3d)}\Big)
$$

当 $t$ 比 $d$ 大一些时，括号里的第二项很快趋于 0（$t = 2d$ 时括号约为 1.088），$T(t) \approx \frac34 e^{-t/(3d)}$，于是

$$
\frac{T(t_b + \Delta t)}{T(t_b)} \approx e^{-\Delta t/(3d)}
$$

即"多穿过 $\Delta t$，就再乘一个慢衰减的指数"。

**对平行光的处理**：HDRP Thick 模式**不对平行光测厚度**。`ShadeSurface_Directional` 注释原文：*"Transmission through thick objects does not support shadowing from directional lights. It will use the 'baked' transmittance value."* 此时用烘焙厚度算的 $T$，不乘阴影，光强额外乘一个全局系数 `_DirectionalTransmissionMultiplier`。

> 所以本工程"平行光 + shadow map 测厚度"的做法，对应的是方法 B（Jimenez 思路），不是 HDRP 的原样做法。

## 10. 透射率 T(t)：三种剖面

### 10.1 Burley 解析式（本工程 / HDRP）

第 4.4 节推导的结果。HDRP 源码（`DiffusionProfile.hlsl`）：

```hlsl
// Note: 'volumeAlbedo' should be premultiplied by 0.25.
float3 ComputeTransmittanceDisney(float3 S, float3 volumeAlbedo, float thickness)
{
    float3 exp_13 = exp2(((LOG2_E * (-1.0/3.0)) * thickness) * S); // Exp[-S * t / 3]
    // Premultiply & optimize: T = (1/4 * A) * (e^(-S * t) + 3 * e^(-S * t / 3))
    return volumeAlbedo * (exp_13 * (exp_13 * exp_13 + 3));
}
```

逐项对照：$S = 1/d$；`exp_13` $= e^{-t/(3d)}$；`exp_13³` $= e^{-t/d}$；`volumeAlbedo` 是 transmission tint。注释要求它预乘 0.25，而 `FillMaterialTransmission` 传入的 `_TransmissionTintsAndFresnel0[...].rgb` 在 shader 里没有再乘，可见预乘是在 CPU 端做的。整理后就是 $c_{tint}\cdot\big(\frac14 e^{-t/d} + \frac34 e^{-t/(3d)}\big)$，和第 4.4 节一致。

### 10.2 高斯和剖面（Jimenez）

**源码**（`SeparableSSS.h`）：

```hlsl
float dd = -d * d;
float3 profile = float3(0.233, 0.455, 0.649) * exp(dd / 0.0064) +
                 float3(0.1,   0.336, 0.344) * exp(dd / 0.0484) +
                 float3(0.118, 0.198, 0.0)   * exp(dd / 0.187)  +
                 float3(0.113, 0.007, 0.007) * exp(dd / 0.567)  +
                 float3(0.358, 0.004, 0.0)   * exp(dd / 1.99)   +
                 float3(0.078, 0.0,   0.0)   * exp(dd / 7.41);
```

Jimenez 的代码里没有注明系数出处；这些数值与 d'Eon & Luebke（GPU Gems 3 第 14 章）用 6 个高斯拟合人类皮肤剖面时给出的表格一致：分母是各高斯的方差 $\sigma_j^2$（单位 mm²），RGB 系数是权重 $\alpha_j$。每个通道的权重之和都等于 1（例如红：0.233+0.1+0.118+0.113+0.358+0.078 = 1.000），所以 $t=0$ 时透射率为 1，和 Burley 版本的检验结果一致。

**它和第 4 节是同一个推导**。把剖面换成高斯和，再走一遍第 4.4 节的积分即可得到这种形式：

- 二维归一化高斯：$G(\sigma^2, r) = \dfrac{1}{2\pi\sigma^2} e^{-r^2/(2\sigma^2)}$，平面积分为 1；
- 皮肤剖面：$R(r) = \sum_j \alpha_j\, G(\sigma_j^2, r)$；
- 对一个高斯做第 4.4 节第 1~3 步：

$$
2\pi\int_0^\infty G\!\left(\sigma^2, \sqrt{r^2+t^2}\right) r\,dr
= \frac{e^{-t^2/(2\sigma^2)}}{\sigma^2}\int_0^\infty e^{-r^2/(2\sigma^2)}\, r\,dr
= e^{-t^2/(2\sigma^2)}
$$

  这里先用 $e^{-(r^2+t^2)/(2\sigma^2)} = e^{-t^2/(2\sigma^2)}\,e^{-r^2/(2\sigma^2)}$ 把与 $r$ 无关的部分提出来；再用换元 $u = r^2/(2\sigma^2)$、$du = r\,dr/\sigma^2$，得到 $\int_0^\infty e^{-r^2/(2\sigma^2)} r\,dr = \sigma^2$。

- 所以 $T(t) = \sum_j \alpha_j\, e^{-t^2/(2\sigma_j^2)}$。

Jimenez 代码写的是 $e^{-d^2/\sigma_j^2}$。由于 $e^{-d^2/\sigma^2} = e^{-(\sqrt2 d)^2/(2\sigma^2)}$，差别只是厚度乘了一个 $\sqrt2$，被前面的经验系数 `scale` 一并吸收了。

**和 Burley 的区别**：单个高斯随 $t$ 按 $e^{-t^2}$ 衰减，比 Burley 的 $e^{-t}$ 型衰减更快，所以要靠多个不同方差的高斯叠加才能拟合出长尾；而且这组系数专门为皮肤拟合，不能像 Burley 那样只调一个 $d$ 就换成别的材质。Jimenez 的注释还提到，这个剖面可以预计算成纹理（*"It can be precomputed into a texture, for maximum performance"*）。

### 10.3 不用剖面（Barré-Brisebois）

直接把"局部厚度贴图"的值（越白越透）当成线性乘数，见第 14 节。

## 11. 角度项 a：背面接收了多少光

### 11.1 推导式

$$
a = \max(0,\ -\mathbf{n}\cdot\mathbf{l})
$$

光正对背面（$\mathbf{l} = -\mathbf{n}$）时为 1；光与表面平行时为 0；受光面为 0。

### 11.2 HDRP：能量守恒的环绕光照

**源码**：`Lit.hlsl` 中 `flippedNdotL = ComputeWrappedDiffuseLighting(-NdotL, TRANSMISSION_WRAP_LIGHT)`；`SubsurfaceScattering.hlsl` 中

```hlsl
#define TRANSMISSION_WRAP_ANGLE (PI/12)                               // 15 度
#define TRANSMISSION_WRAP_LIGHT cos(PI/2 - TRANSMISSION_WRAP_ANGLE)
```

`ComputeWrappedDiffuseLighting` 在 core 包的 `CommonLighting.hlsl` 里，注释标明出自 Steve McAuley 的 Energy-Conserving Wrapped Diffuse：

```hlsl
real ComputeWrappedDiffuseLighting(real NdotL, real w)
{
    return saturate((NdotL + w) / ((1.0 + w) * (1.0 + w)));
}
```

写成公式：

$$
a = \mathrm{saturate}\!\left(\frac{-\mathbf{n}\cdot\mathbf{l} + \kappa}{(1+\kappa)^2}\right),\qquad \kappa = \cos(75^\circ) = \sin(15^\circ) \approx 0.259
$$

- $\kappa = 0$ 时退化为推导式 11.1；
- $\kappa > 0$ 时，透射一直延伸到受光面一侧 15° 的范围（$\mathbf{n}\cdot\mathbf{l} < \kappa$ 都有值）。在轮廓处（$\mathbf{n}\cdot\mathbf{l} = 0$），$a = \kappa/(1+\kappa)^2 \approx 0.163$；
- 分母 $(1+\kappa)^2$ 用来补偿"范围变大"，保持总能量大致不变。

**为什么要环绕**：厚板模型假设表面是平的；真实物体是弯的，明暗交界线附近受光面一侧进入的光，也能扩散穿过去。环绕是对这一点的经验补偿。

**Thin / Thick 是否都用环绕**：`FillMaterialTransmission` 的注释写的是 *"the thin mode uses wrapped diffuse lighting for the NdotL"*，看起来只有 Thin 模式用。但 `EvaluateBSDF` 里 `flippedNdotL` 对两种模式都按环绕计算，Thick 模式的 shadow map 厚度分支又只在 `NdotL < 0` 时进入（`ShouldEvaluateThickObjectTransmission`）。也就是说，注释与代码不完全一致，本文以代码为准。

本工程 URP 14.0.12 的 ShaderLibrary 里没有 include `CommonLighting.hlsl`，为避免额外依赖，第 16 节直接把这个公式写成内联代码。

### 11.3 Jimenez：常数偏移

**源码**：`return profile * SSSSSaturate(0.3 + dot(light, -worldNormal));`

即 $a = \mathrm{saturate}(0.3 - \mathbf{n}\cdot\mathbf{l})$（按单位向量理解）。思路和 HDRP 相同，但没有做能量归一化：轮廓处为 0.3，$-\mathbf{n}\cdot\mathbf{l} \ge 0.7$ 时已经饱和到 1。

### 11.4 HDRP 还乘了一个漫反射项

HDRP 的 `EvaluateBSDF` 里，透射项是 `cbsdf.diffT = diffTerm * flippedNdotL`，其中 `diffTerm = DisneyDiffuse(clampedNdotV, abs(NdotL), LdotV, perceptualRoughness)`。这一项在作用上对应 $F_t$ 的角度变化，归到第 13.3 节。

## 12. 背面可见性 V_B：要不要乘阴影

**推导要求**：$V_B$ 是**背面入射点**是否被挡住，不是当前像素 $p_o$ 是否被挡住。

**最常见的错误**：直接乘 `mainLight.shadowAttenuation`。逆光时，正面的 $p_o$ 被物体**自己**挡住，阴影值为 0，透射被整个乘掉。对闭合的厚物体（球、头部）一定会出这个问题。

业界的几种处理：

| 方案 | $V_B$ 的取法 | 理由 / 出处 |
|---|---|---|
| HDRP Thin | 乘正面阴影 | 假设物体足够薄，正面和背面的阴影可以共用。源码注释：*"in the thin object mode, we assume that the geometry is thin enough for us to safely share the shadowing information between the front and the back faces"* |
| HDRP Thick（点光 / 聚光） | 不乘阴影 | 厚度来自 shadow map，遮挡已经体现在厚度里（被挡 → 厚度大 → $T$ 小） |
| HDRP Thick（平行光） | 不乘阴影 | 用烘焙透射率，见 9.3 节引文 |
| Jimenez | 不乘阴影 | 头文件注释给出的用法是 `float3 t = albedo.rgb * lights[i].color * attenuation * spot; color.rgb += t * SSSSTransmittance(...)`，只乘了衰减和聚光项，没有阴影项；厚度来自 shadow map |

**本工程的规则**（第 16 节）：
- 用 shadow map 测厚度时，$V_B = 1$；
- 用厚度贴图时，按 HDRP Thin 乘正面阴影。所以**对球这种厚物体用贴图模式，透射会被自阴影吃掉**。这是 Thin 模式本来的适用范围，不是 bug。

## 13. 其余各项：环境光、菲涅尔、颜色、是否参与模糊

### 13.1 主光颜色

`mainLight.color * mainLight.distanceAttenuation`。平行光的 `distanceAttenuation` 为 1。附加光源可以按同样方式循环累加；但本工程的 shadow map 测厚度只针对主光源，附加光源只能用贴图厚度。

### 13.2 背面环境光

推导里 $E_B$ 是对半球所有方向积分，环境光也包括在内。用背面法线采样即可：`SampleSH(-n)`。

HDRP 的对应做法（源码）：
- `LitBuiltinData.hlsl`：*"For back lighting we use the oposite vertex normal"*，即用 `-input.tangentToWorld[2]` 采样一份 `backBakeDiffuseLighting`；
- `Lit.hlsl`：`builtinData.bakeDiffuseLighting += builtinData.backBakeDiffuseLighting * bsdfData.transmittance;`

其中 `bsdfData.transmittance` 由**烘焙厚度**计算。HDRP 注释说明，这个值 *"is always used for indirect lighting"*，即使在 Thick 模式下也一样。本工程照做：环境光部分始终使用贴图厚度对应的 $T$。

### 13.3 背面入射菲涅尔 F_t(ω_i)

- 本工程：取 1（假设 T4）。逆光时 $\mathbf{l}$ 与 $\mathbf{v}$ 接近相反，正面那套 `vdoth` 的半角向量没有意义。
- HDRP：乘 `DisneyDiffuse(clampedNdotV, abs(NdotL), LdotV, roughness)`（第 11.4 节），反射项和透射项用的是同一个漫反射 BRDF。

### 13.4 透射颜色 c_tint

推导里没有这一项。HDRP 在 Diffusion Profile 上提供了 Transmission Tint（即 10.1 节的 `volumeAlbedo`），本工程对应 `_TransmissionTint`。物理一致时取白色 $(1,1,1)$；想让透射偏某种颜色、又不想改 $d$（改 $d$ 会同时影响反射项的模糊）时再调它。

### 13.5 颜色拆分与是否参与模糊

| 方案 | 透射加在哪 | 是否被模糊 |
|---|---|---|
| 本工程 | SSSDiffuse Pass，与正面漫反射相加后乘 $\sqrt\rho$，写进 Diffuse RT | 是 |
| HDRP | 加进 diffuse lighting，进入 SSS pass；albedo 按 texturing mode 处理 | 是（见 5.2 节引文） |
| Jimenez | 主光照 Pass：`color.rgb += t * SSSSTransmittance(...)`，`t` 中已含 albedo | 是：它的模糊以最终颜色缓冲作为输入（头文件注释） |
| Barré-Brisebois | 直接加到最终颜色（Zucconi 复现版：`pbr.rgb = pbr.rgb + gi.light.color * I`） | 否，它没有屏幕空间模糊 |

## 14. 另一条路：Barré-Brisebois 经验公式

GDC 2011，Colin Barré-Brisebois & Marc Bouchard（EA），用于 Frostbite 2。它**不从 BSSRDF 推导**，而是直接构造一个看起来像透光的项。游戏里的玉石、蜡、树叶大量使用这种做法。

**公式**（Alan Zucconi 教程中的复现版本，变量名与原演讲对应）：

```hlsl
float3 H     = normalize(L + N * _Distortion);                 // 演讲中：vLTLight = vLight + vNormal * fLTDistortion
float  VdotH = pow(saturate(dot(V, -H)), _Power) * _Scale;     // fLTDot
float3 I     = _Attenuation * (VdotH + _Ambient) * thickness;  // fLT
// Zucconi 版本的最终叠加：pbr.rgb = pbr.rgb + gi.light.color * I;
```

| 项 | 作用 |
|---|---|
| `dot(V, -L)` | 光沿 $-\mathbf{l}$ 方向穿过物体；视线 $\mathbf{v}$ 越接近这个方向（即迎着光看），值越大。这是一个**与视角相关**的前向散射叶片，扩散模型里没有（第 6.3 节） |
| `_Distortion` | 把穿透方向朝法线方向偏一点，使透光形状随表面起伏变化，而不是一个均匀的光斑 |
| `_Power`, `_Scale` | 叶片的集中程度和强度，作用类似 Blinn-Phong 的指数和系数 |
| `_Ambient` | 不随视角变化的常驻透光 |
| `thickness` | 局部厚度贴图（翻转法线烘 AO 后反相），**越白越透**；直接线性相乘，没有 $T(t)$ 那样的指数衰减 |

**和本文推导的关系**：两者描述的是不同的东西。推导式 $T(t)\,D_B$ 描述"有多少光穿过来"（与视角无关）；Barré-Brisebois 主要描述"迎光看时的前向散射叶片"。实践中可以叠加：推导式负责物理上的透光量，经验项负责"通透感"。

## 15. 各方案对比

| | 本工程（第 16 节） | HDRP Thin | HDRP Thick | Jimenez | Barré-Brisebois |
|---|---|---|---|---|---|
| 厚度 $t$ | 贴图 / shadow map | 贴图 | 贴图 + 点光聚光的 shadow map 修正 | shadow map | 局部厚度贴图（语义相反） |
| $T(t)$ | Burley | Burley | Burley × $e^{-\Delta t/(3d)}$ | 6 高斯（皮肤） | 线性 |
| 角度项 | HDRP 环绕（可调 $\kappa$） | 环绕 15° | 环绕 15° | $0.3 - \mathbf{n}\cdot\mathbf{l}$ | 视角相关叶片 |
| 阴影 $V_B$ | 贴图：乘；shadow map：不乘 | 乘正面阴影 | 不乘 | 不乘 | 由使用者决定 |
| 背面环境光 | `SampleSH(-n)` × 贴图 $T$ | 背面 GI × 烘焙 $T$ | 同左 | 无 | 无（`_Ambient` 是常驻项，不是环境光采样） |
| 视角相关 | 否 | 否 | 否 | 否 | 是 |
| 能否从 BSSRDF 推导 | 是（T1~T4） | 是 | 是 | 是（高斯剖面） | 否 |
| 适合 | 学习、通用 | 薄物体 | 厚物体 + 点光聚光 | 皮肤 | 风格化玉石 / 蜡 / 叶子 |

---

# 第三部分：实现

## 16. 参考实现（基于 SSSLit 的 SSSDiffuse Pass）

方案：**Burley $T(t)$ + 厚度二选一（贴图 / shadow map）+ HDRP 环绕 + 背面环境光**。

> 下面的代码只涉及透射部分，按你 `Assets/SSS-TEST/SSSShaderLibrary/SSSLit.shader` 当前的变量名书写（`n`、`mainLight`、`albedo`、`diffuse`、`kd_mainlight`、`ambient`、`kd_ambient`、`i.positionWS`、`i.uv`）。已在本工程用同样的函数和代码，分别在无阴影、`_MAIN_LIGHT_SHADOWS`、`_MAIN_LIGHT_SHADOWS_CASCADE` 三个变体下编译通过。

### 16.1 属性

```hlsl
[Header(Transmission Setting)]
[Toggle] _UseShadowThickness("Thickness From Shadow Map", Float) = 1
_ThicknessMap("Thickness Map (R, white = thick)", 2D) = "white" {}
_ThicknessScale("Thickness (mm)", Range(0, 500)) = 50
_TransmissionTint("Transmission Tint", Color) = (1, 1, 1, 1)
_TransmissionWrap("Transmission Wrap", Range(0, 1)) = 0.2588
_ThicknessShrink("Shadow Lookup Shrink (m)", Range(0, 0.05)) = 0.005
```

CBUFFER 中对应补上：

```hlsl
float _ThicknessScale;
float4 _TransmissionTint;
float _UseShadowThickness;
float _TransmissionWrap;
float _ThicknessShrink;
```

| 属性 | 对应符号 | 说明 |
|---|---|---|
| `_UseShadowThickness` | $t$ 的来源 | 勾选：方法 B（shadow map）；不勾选：方法 A（贴图） |
| `_ThicknessMap` × `_ThicknessScale` | $t$（方法 A） | HDRP 语义：越白越厚，单位 mm |
| `_TransmissionTint` | $c_{tint}$ | 物理一致时取白色 |
| `_TransmissionWrap` | $\kappa$ | 0 = 推导式；0.2588 = HDRP 的 15° |
| `_ThicknessShrink` | 方法 B 第 1 步 | 世界单位（米），Jimenez 用 0.005 |

### 16.2 两个函数（放在 SSSDiffuse Pass 的 `#include` 之后、`frag` 之前）

```hlsl
// 符号 T(t)：第 4.4 节，T = 1/4 e^{-t/d} + 3/4 e^{-t/(3d)}，t、d 单位 mm
float3 BurleyTransmittance(float t, float3 d)
{
    return 0.25 * exp(-t / d) + 0.75 * exp(-t / (3.0 * d));
}

// 符号 t（方法 B）：第 9.2 节，沿主光方向的穿透长度，单位 mm；拿不到时返回 -1
float ShadowMapThicknessMM(float3 positionWS, float3 normalWS)
{
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    float3 p = positionWS - normalWS * _ThicknessShrink;                        // 步骤 1
    #ifdef _MAIN_LIGHT_SHADOWS_CASCADE
        half cascadeIndex = ComputeCascadeIndex(p);                             // 步骤 2
        if (cascadeIndex >= MAX_SHADOW_CASCADES) return -1.0;
    #else
        half cascadeIndex = half(0.0);
    #endif
    float4 sc = mul(_MainLightWorldToShadow[cascadeIndex], float4(p, 1.0));     // 步骤 3
    if (BEYOND_SHADOW_FAR(sc)) return -1.0;
    float entryZ    = LOAD_TEXTURE2D(_MainLightShadowmapTexture, sc.xy * _MainLightShadowmapSize.zw).r; // 步骤 4
    float zPerMeter = length(_MainLightWorldToShadow[cascadeIndex][2].xyz);     // 步骤 5
    return abs(sc.z - entryZ) / zPerMeter * 1000.0;                             // 步骤 5、6
#else
    return -1.0;
#endif
}
```

### 16.3 frag 中的透射代码（替换原来的 `//transmission` 段）

```hlsl
//transmission
float3 d      = max(_ScatterColor.rgb * _ScatterDistance, 1e-4);                                  // d，mm，第 8 节
float  tBaked = SAMPLE_TEXTURE2D(_ThicknessMap, sampler_ThicknessMap, i.uv).r * _ThicknessScale; // t（方法 A），mm
float3 TBaked = BurleyTransmittance(tBaked, d);                                                   // T(t)（方法 A）

float NdotLRaw = dot(n, mainLight.direction);                                                     // 未截断的 n·l
float w        = _TransmissionWrap;
float aBack    = saturate((-NdotLRaw + w) / ((1.0 + w) * (1.0 + w)));                             // a，第 11.2 节

float3 TMain = TBaked;
float  VBack = mainLight.shadowAttenuation;                                                       // V_B：Thin 规则，第 12 节
if (_UseShadowThickness > 0.5)
{
    float tShadow = ShadowMapThicknessMM(i.positionWS, n);                                        // t（方法 B）
    if (tShadow >= 0.0)
    {
        TMain = BurleyTransmittance(tShadow, d);
        VBack = 1.0;                                                                              // V_B：遮挡已体现在厚度里
    }
}

float3 DBackMain    = mainLight.color * mainLight.distanceAttenuation * VBack * aBack;             // D_B 主光部分
float3 DBackAmbient = SampleSH(-n);                                                                // D_B 环境光部分，第 13.2 节
float3 transmission = _TransmissionTint.rgb * (TMain * DBackMain + TBaked * DBackAmbient);         // c_tint · T · D_B
```

输出那一行保持不变：

```hlsl
output.diffuseColor = half4((diffuse*kd_mainlight + ambient*kd_ambient + transmission) * sqrt(albedo), 1);
```

它就是第 5.2 节的 $I = \sqrt\rho\,[D + c_{tint}\,T\,D_B]$。

**几点说明**：
- 变量名用 `NdotLRaw` 而不是 `NdotL`：你的 frag 里已经有一个 `half NdotL = max(0, ...)`，那个被截断过，不能用于背面。
- shadow map 模式拿不到厚度时（超出阴影距离、没开阴影），自动回退到贴图模式和 Thin 规则。
- 环境光部分固定用 `TBaked`，与 HDRP 一致（第 13.2 节）。
- 前向 Pass（`ForwardSpecularOnly`）、Renderer Feature、模糊 Shader 都不需要改。

## 17. 调试与调参

### 17.1 测试场景

1. URP Asset 与 Directional Light 都打开阴影；
2. 光从球的正后方朝相机方向照（逆光）；
3. 先把 frag 输出临时改成 `half4(transmission, 1)`，单独确认透射项是否正确。

### 17.2 预期现象与对应原因

| 现象 | 原因 | 对应章节 |
|---|---|---|
| 贴图模式 + 球：看不到透射 | Thin 规则乘了正面阴影，被自阴影吃掉 | 第 12 节 |
| 贴图模式 + 常量白图：整个暗面均匀变亮，没有边缘透光 | 厚度处处相同 | 第 9.1 节 |
| shadow map 模式 + 球：轮廓附近有一圈亮环，中心暗 | 中心穿透长、$T$ 小；轮廓处穿透短，再加上环绕项 $a \approx 0.163$ | 第 9.2、11.2 节 |
| `_TransmissionWrap = 0` 时，亮环从轮廓往里收 | 轮廓处 $a = 0$，峰值移到 $a$ 和 $T$ 乘积最大的位置 | 第 11.1 节 |
| 厚度从 0 调到 1 mm，颜色突变 | `_ScatterColor` 有通道为 0，该通道 $d \approx 0$ | 第 4.5 节 |
| 光源不在正后方时，轮廓附近受光的一侧也稍微变亮 | 环绕项让受光面 15° 内也有透射，且那里 $t_l \approx 0$、$T \approx 1$ | 第 11.2 节 |
| 透射整体偏强 | 阴影投射的 Depth / Normal Bias 把入射面往远离光的方向推，$t_l$ 偏小；可以减小灯光的 Bias | 第 9.2 节 |
| 明暗交界附近出现噪点或条纹 | 受光面一侧 $t_l \approx 0$，深度精度不够；可以调大 `_ThicknessShrink`。注意它在背光侧会让 $t_l$ 略微变小（缩进的方向是朝光源的） | 第 9.2 节 |

### 17.3 参数怎么定

用第 4.5 节的数值表反推。默认球直径 1000 mm，穿过中心的 $t_l \approx 1000$ mm：

- 希望中心几乎不透（$T < 0.05$）：需要 $\tau = t/d > 8$，即 $d < 125$ mm；
- 希望轮廓附近（$t_l \approx 100$ mm）明显透光（$T > 0.6$）：需要 $\tau < 1$，即 $d > 100$ mm。

所以这个球上 $d_R \approx 100\sim125$ mm 比较容易看到"边缘亮、中心暗"。G、B 通道取更小的 $d$（例如 `_ScatterColor = (1, 0.35, 0.2)`），透射会从边缘的暖白过渡到内侧的红。

真实皮肤的 $d$ 只有毫米量级；这里数值大，是因为测试球比人的耳朵大得多。只要 $d$ 与物体尺寸的比例合适，效果就是一致的。

**注意同一个 $d$ 也决定反射项的模糊半径**（教程 1 第 11 节：$16.56\,d_{max}$）。$d_R = 125$ mm 时半径约 2 m，在 1 m 的球上模糊会很重。这是"反射与透射共用剖面"的代价。如果只想改透射的强弱而不动模糊，应该缩放**厚度**而不是 $d$：
- 贴图模式：调 `_ThicknessScale`。HDRP 的厚度 remap 起的是同样的作用；
- shadow map 模式：给 `tShadow` 乘一个系数。Jimenez 的 `scale` 就是这个作用（第 8 节）。本文的参考实现没有加这个参数，需要时自行添加。

## 18. 参考资料

**源码（本文引用的代码片段均摘自这里）**
- Unity HDRP 2022.3 `DiffusionProfile.hlsl`（`ComputeTransmittanceDisney`）：https://github.com/Unity-Technologies/Graphics/blob/2022.3/staging/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/DiffusionProfile/DiffusionProfile.hlsl
- Unity HDRP 2022.3 `SubsurfaceScattering.hlsl`（`FillMaterialTransmission`、Thin/Thick 说明、`TRANSMISSION_WRAP_*`）：https://github.com/Unity-Technologies/Graphics/blob/2022.3/staging/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/SubsurfaceScattering/SubsurfaceScattering.hlsl
- Unity HDRP 2022.3 `SurfaceShading.hlsl`（`EvaluateTransmittance_Punctual`、平行光 Thick 处理）：https://github.com/Unity-Technologies/Graphics/blob/2022.3/staging/Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/SurfaceShading.hlsl
- Unity HDRP 2022.3 `Lit.hlsl`（`EvaluateBSDF` 中的 `diffT`、`ModifyBakedDiffuseLighting`）：https://github.com/Unity-Technologies/Graphics/blob/2022.3/staging/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl
- Unity HDRP 2022.3 `LitBuiltinData.hlsl`（背面 GI 使用反向顶点法线）：https://github.com/Unity-Technologies/Graphics/blob/2022.3/staging/Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitBuiltinData.hlsl
- Unity core 14.0.12 `CommonLighting.hlsl`（`ComputeWrappedDiffuseLighting`），本工程路径：`Library/PackageCache/com.unity.render-pipelines.core@14.0.12/ShaderLibrary/CommonLighting.hlsl`
- Jorge Jimenez, *Separable SSS*（`SSSSTransmittance`）：https://github.com/iryoku/separable-sss/blob/master/SeparableSSS.h

**论文 / 文章**
- J. Jimenez, D. Whelan, V. Sundstedt, D. Gutierrez, *Real-Time Realistic Skin Translucency*, IEEE CG&A 2010：http://www.iryoku.com/translucency/
- E. d'Eon, D. Luebke, *Advanced Techniques for Realistic Real-Time Skin Rendering*, GPU Gems 3 第 14 章（6 高斯皮肤剖面）
- S. Green, *Real-Time Approximations to Subsurface Scattering*, GPU Gems 1 第 16 章（深度图模拟吸收）
- P. Christensen, B. Burley, *Approximate Reflectance Profiles for Efficient Subsurface Scattering*, Pixar 2015（Burley 剖面）
- C. Donner, H. W. Jensen, *Light Diffusion in Multi-Layered Translucent Materials*, SIGGRAPH 2005（有限厚板的 multipole 模型）
- C. Barré-Brisebois, M. Bouchard, *Approximating Translucency for a Fast, Cheap and Convincing Subsurface Scattering Look*, GDC 2011：https://www.gdcvault.com/play/1014536/Approximating-Translucency-for-a-Fast
- A. Zucconi, *Fast Subsurface Scattering in Unity (Part 2)*（上述 GDC 方法的复现与厚度贴图烘焙流程）：https://www.alanzucconi.com/2017/08/30/fast-subsurface-scattering-2/
