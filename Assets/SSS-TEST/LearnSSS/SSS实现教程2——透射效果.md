# SSS 实现教程 2：透射（Transmission）——从渲染方程到 URP 代码

逆光看耳朵时，正面没有直接朝向光源，却可能呈现暖色的透亮。要解释这个现象，需要同时回答两件事：背面收到了多少光，光穿过这处组织后还能给正面多少贡献。只混合可见面的漫反射纹理，无法读取被物体挡住的背面光照。

本文从这两个需求开始，建立局部厚板近似，再计算厚度衰减 $T(t)$，最后对应到 URP 的材质代码。需要了解法线、`N·L`、纹理与 Render Texture（RT）；BSSRDF、面积积分和换元会在正文解释，可以独立阅读。《SSS实现教程.md》另有同侧扩散与采样的详细说明。

本文区分两层实现：**现有 `SSSLit`** 使用贴图厚度、背面余弦与 Burley 衰减；**第 16 节的可选扩展示例**加入 shadow map 测厚、wrap 和背面环境光。后者是供接入的文档代码，不代表工程已经启用这些功能。教程 1 的 `SSSBurley` 命名属于另一组实现，不能与 `SSSLit` 的现状互换。

外部实现片段按末尾链接讨论其公式与设计；接入时还需核对具体包版本。第 16 节以主光普通/级联阴影为接入条件，其他阴影变体需另行适配。

---

## 目录

- [0. 新增符号](#0-新增符号)
- [1. 总览：透射是什么，加在流程哪里](#1-总览透射是什么加在流程哪里)
- [第一部分：理论——从背面受光建立透射近似](#第一部分理论从背面受光建立透射近似)
  - [2. 从全表面传输到背面贡献](#2-从全表面传输到背面贡献)
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

这张表用于查阅，不要求预先记住。正文会逐步引入这些量；本文所有长度统一用毫米，世界空间转换时另行注明。

| 基础符号 | 含义 | 单位 |
|---|---|---|
| $p_i,p_o$ | 光进入、离开物体的位置 | 位置 |
| $\omega_i,\omega_o$ | 入口指向光源、出口指向观察者的方向 | 单位向量 |
| $S$ | BSSRDF，入口到出口的传递分布 | 1/(面积·立体角) |
| $\rho$ | 漫反射反照率，RGB | 无量纲 |
| $R(r)$ | 归一化同侧空间剖面 | mm⁻² |
| $d,s$ | RGB 散射长度及倒数 $s=1/d$ | mm、mm⁻¹ |
| $F_t$ | 近似边界透射因子 | 无量纲 |

| 符号 | 含义 | 单位/取值 |
|---|---|---|
| $\mathcal{A}_F$ | 与 $p_o$ **同一侧**的表面（光从这里进、横向扩散到 $p_o$ 出来） | — |
| $\mathcal{A}_B$ | 与 $p_o$ **相对一侧**的表面，也就是物体的"背面"（光从这里进、穿过物体到 $p_o$ 出来） | — |
| $L_R$ | 反射项：$\mathcal{A}_F$ 的同侧贡献 | — |
| $L_T$ | 透射项：$\mathcal{A}_B$ 的贡献，本文要算的东西 | — |
| $\Pi_F,\ \Pi_B$ | 厚板模型中的正面平面、背面平面（第 3 节） | — |
| $t$ | 厚度：$\Pi_F$ 与 $\Pi_B$ 之间的**垂直**距离 | 毫米 |
| $p_o'$ | $p_o$ 在背面 $\Pi_B$ 上的垂足 | 位置 |
| $\ell$ | 三维距离 $\ell = \lVert p_i - p_o\rVert$（背面入射点到出射点） | 毫米 |
| $\mathbf{l}$ | 指向光源的单位向量（平行光对所有点相同），即 URP 的 `mainLight.direction` | 单位向量 |
| $E_B,\ D_B$ | 背面入射点接收并进入物体的光；$D_B = E_B/\pi$ | — |
| $T(t)$ | 本文跨面模型对均匀背面输入的厚度衰减系数，简称透射率；RGB 各一个 | 0~1 |
| $a(\cdot)$ | 角度项：背面接收光照的余弦项（及其各种变体） | 0~1 |
| $V_B$ | 背面入射点的可见性（是否被别的东西挡住光） | 0~1 |
| $c_{tint}$ | 透射颜色乘数（美术参数，不来自推导） | 0~1 |
| $t_l$ | 沿光线方向量得的穿透长度（shadow map 能给出的量），区别于垂直厚度 $t$ | 毫米 |
| $\kappa$ | 环绕光照（wrap lighting）参数 | — |
| $\alpha_j,\ \sigma_j^2$ | 高斯和剖面中第 $j$ 个高斯的权重与方差 | —、毫米² |

> **注意符号冲突**：教程 1 用 $s = 1/d$ 表示 HDRP 的 ShapeParam，所以本文三维距离用 $\ell$，不用 $s$。

---

## 1. 总览：透射是什么，加在流程哪里

同侧扩散处理光从一侧进入、从附近同侧位置出来；透射处理光从背面进入、从正面出来。这里的正面指当前观察的出口一侧，不一定是朝向光源的一侧。逆光耳朵的暖色亮区，需要后者的贡献。

在已有“漫反射 RT → 滤波 → 合成”的管线里，贴图厚度透射可以直接加入绘制 Diffuse RT 的 Pass，无须另画背面。令 $D$ 为正面白色漫反射响应，$D_B$ 为背面响应，$\rho$ 为材质反照率，流程为：

```
 步骤一（SSSDiffuse Pass）                      步骤二~四：完全不变
 ┌────────────────────────────────────────┐
 │ Diffuse RT = √ρ · ( D_正面  +  T(t)·D_B )│ → 模糊 → ×√ρ → +高光
 │                    └正面项┘ └─透射项─┘ │
 └────────────────────────────────────────┘
```

| 新增的量 | 目的 | 对应理论 | 对应代码 |
|---|---|---|---|
| 厚度 $t$ | 光在物体里要穿过多远 | 第 3 节 | 第 9 节 |
| 透射率 $T(t)$ | 穿过 $t$ 之后还剩多少 | 第 4.4 节 | 第 10 节 |
| 背面光照 $D_B$ | 背面接收了多少光 | 第 4.2 节 | 第 11~13 节 |

---

# 第一部分：理论——从背面受光建立透射近似

## 2. 从全表面传输到背面贡献

### 2.1 起点：完整的 BSSRDF 渲染方程

先固定要观察的出口 $p_o$ 与指向观察者的方向 $\omega_o$。光可以从另一处入口 $p_i$ 的不同方向 $\omega_i$ 进入。BSSRDF $S$ 描述每份入射光如何贡献到指定出口，它是单位面积、单位立体角的传递分布。先累加一个入口收到的方向，再累加所有入口，写成：

$$
L_o(p_o,\omega_o) = \int_{\mathcal{A}} \int_{\Omega} S(p_o,\omega_o,p_i,\omega_i)\, L_i(p_i,\omega_i)\, \cos\theta_i \, d\omega_i \, dA
$$

这里 $L_i$、$L_o$ 分别是入射与出射辐亮度，描述指定方向上的光；$\theta_i$ 是入射方向与入口外法线的夹角。$L_i\cos\theta_i\,d\omega_i$ 是入口从一小片方向收到的辐照度贡献，乘 $S$ 和入口小块面积 $dA$，才得到它对指定出口的贡献。$\Omega$ 是入口外侧半球，$\mathcal A$ 是整个物体表面；积分就是把这些方向、这些小块依次加起来。这里计算的出射只含次表面部分，表面高光另算。

这个式子本身没有区分反射和透射：光从表面上任何一点进去、从 $p_o$ 出来，都可以计入。

### 2.2 为什么同侧滤波不足以表现逆光透射

先看一个具体像素。设正面外法线为 $\mathbf n$，指向光源的单位向量为 $\mathbf l$。光源在正后方时，$\mathbf n\cdot\mathbf l=-1$，正面漫反射的 $\max(0,\mathbf n\cdot\mathbf l)$ 为零；背面的法线约为 $-\mathbf n$，其受光余弦却为 1。两处需要的光照信息不同。

同侧扩散剖面描述光从一个表面进入、从附近位置出来的分布。这里常采用均匀、局部平面、相对扩散范围足够厚的介质近似，尚未建立另一面的厚度与边界。屏幕空间实现又只能读取相机可见的入口，因此把可见面滤得更宽，也无法自动得到背面这份受光。

要补上这份贡献，就需要一套背面模型：用几何近似确定入口与出口的距离，用厚度衰减估计跨面传递，再用背面法线估计受光。下面先把“同侧入口”与“另一侧入口”的贡献分开，随后分别补齐这些量。

### 2.3 把积分域拆成两块

积分是“切成小块求和”。将入口小块分为两组，分别求和后再相加，总结果不变。对于接下来采用的局部厚板，把与出口同侧的表面记为 $\mathcal A_F$，相对一侧记为 $\mathcal A_B$，忽略远处侧边的影响：

$$
\int_{\mathcal A_F\cup\mathcal A_B}f\,dA
=\int_{\mathcal A_F}f\,dA+\int_{\mathcal A_B}f\,dA.
$$

用到渲染方程上，被积函数 $S L_i\cos\theta_i$ 简记为 $\cdots$：

$$
L_o \approx \underbrace{\int_{\mathcal A_F}\int_\Omega\cdots\,d\omega_i\,dA}_{L_R:\ \text{同侧入口贡献}}
+\underbrace{\int_{\mathcal A_B}\int_\Omega\cdots\,d\omega_i\,dA}_{L_T:\ \text{背面入口贡献}}.
$$

$L_R$ 是同侧反射部分，可以近似成漫反射光照纹理的空间滤波；$L_T$ 是本篇要建立的透射部分。对只有一盏方向光的平板，顺光时背面直接光为零，逆光时背面直接光则成为主要输入。实际还有环境光与其他光源时，两部分可以同时存在。接下来要解决的是：背面的哪些位置能够对当前出口贡献多少光？

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

- 正面 $\Pi_F$ 是出口附近的局部切平面，$\mathcal{A}_F \to \Pi_F$；
- 背面 $\Pi_B$ 与它平行、相距 $t$，$\mathcal{A}_B \to \Pi_B$；
- 背面的外法线朝外、也就是朝 $-\mathbf{n}$ 方向。

### 3.2 背面入射点到出射点的距离

以垂足 $p_o'$ 为圆心，在 $\Pi_B$ 上用极坐标 $(r,\theta)$ 描述入射点 $p_i$。$p_o \to p_o'$ 垂直于平面，长 $t$；$p_o' \to p_i$ 在平面内，长 $r$；两段互相垂直，由勾股定理：

$$
\ell = \lVert p_i - p_o \rVert = \sqrt{r^2 + t^2}
$$

### 3.3 假设 T2：用同侧剖面估计跨面传递

几何只给出了距离，还需要一条“距离怎样影响贡献”的曲线。先引入同侧 Burley 归一化扩散剖面：

$$
R(x)=\frac{e^{-x/d}+e^{-x/(3d)}}{8\pi d x},\qquad d>0.
$$

$x$ 是求值距离，$d$ 是控制扩散宽度的长度，均用毫米。$R$ 的单位是每平方毫米，表示单位面积的空间权重。第一项下降快，第二项下降慢，因此兼有中心集中部分和长尾；RGB 分别使用 $d_R,d_G,d_B$。这个同侧剖面的总面积权重为 1：

$$
\int_0^\infty R(x)2\pi x\,dx
=\frac{1}{4d}\int_0^\infty(e^{-x/d}+e^{-x/(3d)})\,dx
=\frac{d+3d}{4d}=1.
$$

$2\pi x\,dx$ 是半径 $x$ 的薄圆环面积。归一化使 $d$ 控制分布范围，反照率 $\rho$ 另行控制 RGB 总量。

现在增加跨面假设：**把背面小块对出口的空间权重，也估计为 $R(\ell)$，其中 $\ell=\sqrt{r^2+t^2}$。** 例如厚度为 3 mm、横向偏移为 4 mm 的入口，就按两点欧氏距离 5 mm 求值。这里没有追踪光在内部的曲折路径，而是以距离拟合许多路径的总体影响。

将进入、传播、离开三部分组合，得到：

$$
S(p_o,\omega_o,p_i,\omega_i) \approx \frac{1}{\pi}\,F_t(\omega_o)\;\rho R(\ell)\;F_t(\omega_i),\qquad p_i\in\Pi_B.
$$

$F_t=1-F$ 是扣除菲涅尔表面反射后的近似透射因子；$\rho$ 是漫反射反照率。$1/\pi$ 对应 Lambert 型出射：方向不变的辐亮度按半球余弦积分得到 $\pi$，除以它便完成这部分角度归一化。

这一步是对有限厚度传输的**启发式建模**：原来的同侧反射剖面没有描述第二个表面的边界。后续可以准确计算这个假设模型的积分，但精确积分不会让假设变成严格的厚板光传输解。第 6 节会说明反射与透射共同分配能量时还缺少什么。

参考资料中的 HDRP `ComputeTransmittanceDisney` 片段采用同样的平面积分形式：

> *"Evaluate Int{0, inf}{2 \* Pi \* r \* R(sqrt(r^2 + d^2))}, where R is the diffusion profile."*

这条注释里的 `d` 是厚度，对应本文的 $t$；本文用 $d$ 专门表示 Burley 长度尺度。

## 4. 推导透射项 L_T

### 4.1 先算每个入口的受光，再累加入口

内层积分固定入口位置，只改变入射方向；外层再遍历入口位置。不随当前积分变量变化的因子可以提出积分号：例如每个小块都乘同一个 0.6，先相加再乘 0.6，结果相同。下面依照这个规则整理，把可先算好的受光量分出来。

**第 1 步：写出 $L_T$**

$$
L_T(p_o,\omega_o) = \int_{\mathcal{A}_B}\int_{\Omega} S(p_o,\omega_o,p_i,\omega_i)\, L_i(p_i,\omega_i)\cos\theta_i\, d\omega_i\, dA
$$

注意 $\theta_i$ 是入射方向与**入射点 $p_i$ 处法线**的夹角。$p_i$ 在背面，所以这里的法线是背面法线（厚板模型里是 $-\mathbf{n}$）。

**第 2 步：代入假设 T2 的 $S$**

$$
L_T = \int_{\mathcal{A}_B}\int_{\Omega} \frac{1}{\pi}\, F_t(\omega_o)\, \rho\, R(\ell)\, F_t(\omega_i)\, L_i \cos\theta_i\, d\omega_i\, dA
$$

**第 3 步：按“随谁变化”分组**。出口与观察方向始终固定；局部材质先取均匀。

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

这里 $E_B$ 是背面入口收到并进入内部的辐照度。方向积分已经算完，但不同入口的受光仍可以不同，所以它保留位置参数 $p_i$。

**第 7 步：忽略出射边界因子的方向变化**，取 $F_t(\omega_o)\approx1$。定义 $D_B=E_B/\pi$，它表示白色 Lambert 表面对这份背面输入的响应，于是：

$$
L_T(p_o) \approx \int_{\mathcal{A}_B} \rho\, R(\ell)\, D_B(p_i)\, dA
$$

和教程 1 的 $L_R \approx \int_{\mathcal{A}_F} \rho\, R(r)\, D(p_i)\, dA$ 对比：形式完全一样，区别只有两点——积分域换成背面，距离换成三维距离 $\ell$。

### 4.2 背面光照 D_B 在 Unity 里是什么

在本工程的光照约定中，直接用灯光颜色乘受光余弦计算白色漫反射响应，背面只需将法线改为 $-\mathbf n$。以下是包含主光与背面环境光的目标表达式；`lightColor` 在此包含距离衰减，`SampleSH` 用球谐环境光近似背面半球输入，不再额外除以 $\pi$：

$$
D_B = \underbrace{(1-F)\cdot \text{lightColor}\cdot V_B \cdot \max(0,\ -\mathbf{n}\cdot\mathbf{l})}_{\text{主光源}} \;+\; \underbrace{(1-F)\cdot \text{SampleSH}(-\mathbf{n})}_{\text{环境光}}
$$

- $\max(0,-\mathbf{n}\cdot\mathbf{l})$：背面法线与光方向的余弦。逆光时 $\mathbf{n}\cdot\mathbf{l}<0$，这一项反而大于 0。
- $V_B$：**背面入射点**有没有被挡住。注意是背面那个点，不是当前像素 $p_o$。第 12 节会专门讲它为什么不能直接用 `mainLight.shadowAttenuation`。
- $(1-F)$：背面入射时的菲涅尔透射，可由背面入射角和折射率估计。本教程的实时近似将它取为 1（假设 T4）。当前 `SSSLit` 还取 $V_B=1$，且没有背面环境光；第 16 节才加入后两项的扩展处理。

### 4.3 假设 T3：背面光照在局部是均匀的

$R(\ell)$ 衰减很快，只有 $p_o'$ 附近一小片背面的贡献不可忽略。假设这一小片上 $D_B$ 近似相同，等于 $D_B(p_o')$，就可以把它当常数提到积分外面：各小块都乘同一份背面受光，求和后再乘即可。同时 $\rho$ 也是常数：

$$
L_T(p_o) \approx \rho\, D_B \int_{\Pi_B} R(\ell)\, dA
$$

于是问题只剩下一个**空间权重积分**：

$$
T(t) \;\equiv\; \int_{\Pi_B} R\!\left(\sqrt{r^2+t^2}\right) dA
$$

在此模型中，$T(t)$ 是均匀背面输入的衰减系数。它只和厚度 $t$、RGB 散射长度 $d$ 有关，与光强无关。

先理解积分在加什么。把背面分成小块，一块面积 $\Delta A$ 对出口贡献约为 $\rho D_B R(\ell)\Delta A$。例如该块的 $R(\ell)=0.02\,\text{mm}^{-2}$、面积为 $3\,\text{mm}^2$，则给共同输入 $\rho D_B$ 提供约 0.06 的权重。这里 $R$ 是每单位面积的权重，乘面积才得到这一块的份额。

同一背面圆环上的点到出口距离都相同，为 $\sqrt{r^2+t^2}$。半径 $r$、宽度 $dr$ 的圆环面积约为 $2\pi r\,dr$，所以一圈权重为 $R(\sqrt{r^2+t^2})2\pi r\,dr$。收集全部圆环，便得到 $T(t)$。厚度增加时，每个背面位置离出口更远，权重下降，总贡献随之降低。下一节把这次累加算成可直接用于 shader 的表达式。

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

**第 1 步：写成极坐标**。将圆环按角度再切成小片，弧长为 $r\,d\theta$，宽度为 $dr$，所以小片面积 $dA=r\,dr\,d\theta$。角度累加一圈、半径从中心加到无穷：

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
| $t = 0$ | $T = \frac14 + \frac34 = 1$ | 跨面核退化成归一化同侧核；这是模型极限，仍需另乘反照率与边界因子 |
| $t \to \infty$ | $T \to 0$ | 无限厚，光穿不过去 |
| $t$ 增大 | $T$ 单调减小 | 越厚越暗 |

**几何解释**：定义同侧剖面的半径累计份额 $C(x)=2\pi\int_0^x R(r)r\,dr$。代入 Burley 剖面得到 $C(x)=1-\frac14e^{-x/d}-\frac34e^{-x/(3d)}$，因此本模型有

$$
T(t)=1-C(t).
$$

换元后的积分与“同侧平面上半径大于 $t$ 的圆外份额”相同。这给计算提供了方便的交叉检查；这个等式依赖第 3.3 节把同侧剖面用于跨面距离的假设，并非真实有限厚介质必须遵守的透射规律。

**数值表**（$\tau = t/d$，无量纲）：

| $\tau = t/d$ | 0 | 0.5 | 1 | 2 | 4 | 8 | 16.56 |
|---|---|---|---|---|---|---|---|
| $T$ | 1.000 | 0.786 | 0.629 | 0.419 | 0.202 | 0.052 | 0.003 |

例如 $d=1$ mm、$t=2$ mm 时，$T\approx0.419$；若这个通道的 $\rho=0.6$、背面白色漫反射响应 $D_B=2$，则局部透射贡献约为 $0.6\times0.419\times2=0.503$。厚度与光强分别进入不同因子，因而可以独立检查。

最后一列为 $T(16.56d)\approx0.003004$，对应同侧剖面半径累计份额约 0.996996。

当 `_ScatterColor = (1,0,0)` 时，G、B 通道的 $d$ 被代码钳到 $10^{-4}$ mm。厚度为 0 时三个通道都得到 $T=1$；增加到 1 mm 时，G、B 的 $t/d=10^4$，指数已接近零，而红通道仍可能有贡献，因此颜色会快速变化。这是该衰减模型在极小尺度下的行为；真实介质是否透光还取决于吸收与散射，不能仅凭“散射很少”判断。

### 4.6 小结

$$
L_T(p_o) \approx \rho \cdot T(t) \cdot D_B
$$

假设清单：T1 局部厚板；T2 背面沿用同一剖面、按三维距离求值；T3 背面光照局部均匀；T4 背面入射菲涅尔 $\approx 1$；以及忽略出射边界方向变化 $F_t(\omega_o)\approx1$。

## 5. 合进现有 SSS 流程

### 5.1 颜色的拆分

颜色贴图放在滤波前，会让颜色细节一起扩散；全部放在滤波后，细节清晰，但入口颜色不再向邻域传播。这里采用折中：输入先乘 $\sqrt\rho$，滤波后再乘出口处的 $\sqrt\rho$。常量颜色为 0.25 时，两次各乘 0.5，总乘数仍为 0.25。这个拆分控制纹理细节，不是将真实吸收事件机械分成两次。透射项也按同一输入约定组织：

$$
L_T(p_o) \approx \sqrt{\rho(p_o)}\cdot \underbrace{\sqrt{\rho(p_o)}\cdot T(t)\cdot D_B}_{I_T(p_o)}
$$

### 5.2 透射项也放进 Diffuse RT 一起模糊

把正面白色漫反射响应记为 $D$，原来的输入为 $I_R=\sqrt\rho D$。现在把 $I_T$ 加进同一张 RT：

$$
I(p) = I_R(p) + I_T(p) = \sqrt{\rho(p)}\,\Big[D(p) + c_{tint}\, T\big(t(p)\big)\, D_B(p)\Big]
$$

注意 $I_T$ 是在**正面像素 $p$ 自己**身上算出来的：背面平面上的积分已经在 4.4 节被解析地算掉了，结果只依赖 $p$ 的厚度和背面光照 $D_B$。而在厚板模型里背面法线就是 $-\mathbf{n}$，所以 $D_B$ 可以直接用 $p$ 自己的法线取反来算（可见性 $V_B$ 例外，见第 12 节）。因此每个屏幕像素都能算出自己的 $I_T$，不需要背面像素。$c_{tint}$ 是美术乘数，推导里不存在，保持上述模型时取 1（第 13.4 节）。

然后按现有管线滤波漫反射输入。**这一步是工程选择，不是推导结果**，理由有两个：

1. **HDRP 就是这么做的**。`Lit.hlsl` 里把透射加进 diffuse 时的注释原文：*"We use diffuse lighting for accumulation since it is going to be blurred during the SSS pass."*
2. **它近似补回了假设 T3 丢掉的东西**。T3 假设背面光照均匀。如果不均匀（比如背面有阴影边界），在恒定厚度、平行平面模型内，积分结果是 $D_B$ 与核 $R(\sqrt{r^2+t^2})$ 的卷积，透射光应该是"糊的"。先乘 $T$ 再用 $R(r)$ 模糊，也会把它糊开。两者并不相等，但都让透射光有柔和的边缘，而不是生硬的分界线。

### 5.3 最终工程公式

令 $k$ 为滤波样本编号，$w_k$ 为对应 RGB 权重，$L_{spec}$ 为单独计算的表面高光与环境镜面反射，最终合成式为：

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

### 6.1 反射与透射需要共同分配输入能量

第 3.3 节给出 $\int R\,dA=1$。在忽略边界因子的均匀平面模型中，同侧反射剖面 $\rho R$ 的总量因此为 $\rho$。它已经使用了半无限介质的反射总量。

若从同一份入射光出发，同时保留这份反射，又额外添加跨面的 $\rho T(t)$，两面总量就是 $\rho[1+T(t)]$。例如 $\rho=0.8$、$T=0.5$ 时得到 1.2，超过单位输入；即使总量尚未超过 1，也没有完成有限厚介质中反射、透射、吸收三者的联合分配。真实薄板让更多光从另一面离开时，同侧反射也会改变，不能总保留半无限反射值。

只看物体一侧时，这种收支不一致可能不明显，但增加透射仍可能使材质整体过亮。因此应结合反射强度控制，而不能把单独归一化的滤波当作全系统守恒证明。需要联合处理有限厚度边界时，可以进一步研究 multipole 等扩散模型（Donner & Jensen 2005）。

$t = 0$ 时 $T = 1$ 也属于这个问题：零厚度的板应该是"没有介质，光直接穿过"，而不是"扩散后全部穿过"。

### 6.2 厚度应该沿哪个方向量

推导里的 $t$ 是**垂直于表面**的厚度（图 3.1）。但第 9.2 节的 shadow map 方法量到的是**沿光线方向**的穿透长度 $t_l$。厚板几何里两者的关系是 $t = t_l\cdot(-\mathbf{n}\cdot\mathbf{l})$。

HDRP 知道这个差别，但选择不乘这一项。`SurfaceShading.hlsl` 注释原文：

> *"Our subsurface scattering models use the semi-infinite planar slab assumption. Therefore, we need to find the thickness along the normal. Note: based on the artist's input, dependence on the NdotL has been disabled."*

Jimenez 的实现同样直接用 $t_l$。所以业界的实际做法是：**用光线方向的穿透长度代替垂直厚度**。这在厚板模型里不严格；但真实物体是弯的（比如球），"垂直厚度"本身就没有明确定义（Alan Zucconi 的教程里有一张图专门说明这一点），沿光线方向的长度能表达随光向变化的几何穿透距离。不过散射路径会转弯，$t_l$ 仍不等于真实光路总长度。

### 6.3 本文的 Lambert 出射近似与视角

本文将出射近似为 Lambert 型并取 $F_t(\omega_o)\approx1$，因此同一出口的透射辐亮度不随观察方向变化。若保留出射边界的角度因子，这种视角不变性也会相应改变。

迎着光源看时，玉石、蜡烛那种"中心发亮"的通透感，来自**前向散射**：光倾向于沿原方向继续走，再加上低阶（少数几次）散射。这些不在扩散模型里。第 14 节的 Barré-Brisebois 公式就是用一个与视角相关的经验项去模拟它。

> 容易混淆的一点：本文推出的透射项**属于多次散射**（扩散）。被扩散模型漏掉的是单次 / 低阶散射以及各向异性的前向散射，不是多次散射。

---

# 第二部分：从符号到代码——每个量从哪来

## 7. 符号—代码总表

| 符号 | 含义 | 当前 SSSLit | 第 16 节可选扩展 |
|---|---|---|---|
| $d$ | RGB 散射长度 | `_ScatterColor.rgb * _ScatterDistance`，透射计算钳至正数 | 相同 |
| $t$ | 厚度 | 贴图 R × `_ThicknessScale`，mm | 增加主光 shadow map 测厚 |
| $T(t)$ | 衰减 | Burley 双指数 | 相同 |
| $a$ | 背面角度项 | `saturate(dot(-n, mainLight.direction))` | 可调 wrap |
| $V_B$ | 背面可见性 | 取 1，透射不乘阴影 | 贴图路径用 Thin 阴影近似；成功测厚时取 1 |
| 主光颜色 | 入射光强与颜色 | `mainLight.color * mainLight.distanceAttenuation` | 相同 |
| 背面环境光 | 背面半球受光 | 未计算 | `SampleSH(-n)` × 贴图对应的 $T$ |
| $F_t$ | 入射边界因子 | 透射项取 1 | 相同 |
| $c_{tint}$ | 美术颜色乘数 | `_TransmissionTint` | 相同 |
| 颜色与滤波 | 透射写入位置 | 与正面漫反射一起乘 $\sqrt\rho$ 写进 Diffuse RT | 相同 |

当前实现的对应位置为 `Assets/SSS-TEST/SSSShaderLibrary/SSSLit.shader` 的 `SSSDiffuse` Pass。CBUFFER 虽声明了 `_UseShadowThickness`，透射代码并未读取它；是否具备某功能，要看执行路径，而非仅看参数名。

## 8. 散射距离 d

第 3.3 节假设透射沿用同侧剖面，因此两者使用同一组 RGB 长度 $d$。若给透射另设长度，就得到独立的美术控制，也同时改变了这个共用剖面的假设。

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

**局限**：厚度和光的方向无关。同一个点不管光从哪边来都一样厚，所以闭合的厚物体（比如球）上效果不对。用常量白图时，所有点使用同一衰减，无法产生由中心厚、边缘薄造成的亮度变化；球面法线不同，背面余弦仍会使各点亮度不同。

### 9.2 方法 B：从 shadow map 测厚度（Jimenez / GPU Gems 1）

**思路**：shadow map 记录的是"从光源看过去，每个方向上**最先被照到的表面**的深度"，也就是光**进入**物体的位置。当前像素是光**离开**物体的位置。当最近深度确实是同一物体的入口、入口到出口之间是一段连续实体时，将深度差换算成世界长度，就得到几何穿透距离 $t_l$。若中间包含空气间隙或其他物体，单层深度无法辨认这些分段。

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

**优点**：在光线只穿过同一物体的一段连续实体、最近深度确实属于其入口时，厚度会随光方向和形状变化。球体逆光时，中心穿透长、边缘穿透短，边缘透光就出来了。

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

> 所以第 16 节扩展的“平行光 + shadow map 测厚度”，对应的是方法 B（Jimenez 思路），不是 HDRP 的原样做法。

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

逐项对照：$S = 1/d$；`exp_13` $= e^{-t/(3d)}$；`exp_13³` $= e^{-t/d}$；`volumeAlbedo` 是 transmission tint。注释要求它预乘 0.25，而 `FillMaterialTransmission` 传入的 `_TransmissionTintsAndFresnel0[...].rgb` 在 shader 里没有再乘，调用者必须保证输入已经满足这个预乘约定；仅凭函数片段不能确定具体由哪一层完成。整理后就是 $c_{tint}\cdot\big(\frac14 e^{-t/d} + \frac34 e^{-t/(3d)}\big)$，和第 4.4 节一致。

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

代码中可先把各分母记为 $v_j$，把经过 `scale` 缩放的厚度记为 $x$，这样表达式明确是 $\sum_j\alpha_j e^{-x^2/v_j}$。RGB 系数是权重 $\alpha_j$，每通道之和均为 1，因此 $x=0$ 时结果为 1。分母必须与 $x^2$ 使用一致单位；在没有规定 `sssWidth`、世界尺度与 `scale` 换算之前，不能直接把这些数称为毫米平方。

理解它与高斯剖面的关系，先从标准方差定义推导。若用与第 3.3 节相同的跨面距离假设，把空间剖面换成二维归一化高斯和，则有：

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

对照代码的 $e^{-x^2/v_j}$，若令 $x=t$，对应的标准方差必须是 $\sigma_j^2=v_j/2$。若希望把 $v_j$ 解释成另一份资料中的方差，则需要明确采用 $t=\sqrt2 x$ 的尺度映射。这是两种参数约定的代数换算；经验 `scale` 可以调出相近曲线，但不能据此断言某个实现已经自动完成了这项物理单位换算。

**和 Burley 的区别**：单个高斯随 $t$ 按 $e^{-t^2}$ 衰减，比 Burley 的 $e^{-t}$ 型衰减更快，所以要靠多个不同方差的高斯叠加才能拟合出长尾；而且这组系数专门为皮肤拟合，不能像 Burley 那样只调一个 $d$ 就换成别的材质。Jimenez 的注释还提到，这个剖面可以预计算成纹理（*"It can be precomputed into a texture, for maximum performance"*）。

### 10.3 不用剖面（Barré-Brisebois）

直接把"局部厚度贴图"的值（越白越透）当成线性乘数，见第 14 节。

## 11. 角度项 a：背面接收了多少光

### 11.1 推导式

$$
a = \max(0,\ -\mathbf{n}\cdot\mathbf{l})
$$

光正对背面（$\mathbf{l} = -\mathbf{n}$）时为 1；光与表面平行时为 0；受光面为 0。

### 11.2 HDRP：归一化的环绕光照

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
- 分母 $(1+\kappa)^2$ 补偿角度支持范围的扩大。令 $u=-\mathbf n\cdot\mathbf l$，在 $0\le\kappa\le1$ 时，对整个方向球面的角度响应积分为 $2\pi\int_{-\kappa}^{1}(u+\kappa)/(1+\kappa)^2\,du=\pi$，与截断余弦相同。这只归一化了角度响应；再乘方向相关厚度、阴影后，仍不能据此证明整个材质守恒。

**为什么要环绕**：厚板模型假设表面是平的；真实物体是弯的，明暗交界线附近受光面一侧进入的光，也能扩散穿过去。环绕是对这一点的经验补偿。

**Thin / Thick 是否都用环绕**：`FillMaterialTransmission` 的注释写的是 *"the thin mode uses wrapped diffuse lighting for the NdotL"*，看起来只有 Thin 模式用。但 `EvaluateBSDF` 里 `flippedNdotL` 对两种模式都按环绕计算，Thick 模式的 shadow map 厚度分支又只在 `NdotL < 0` 时进入（`ShouldEvaluateThickObjectTransmission`）。也就是说，注释与代码不完全一致，本文以代码为准。

本工程 URP 14.0.12 的 ShaderLibrary 里没有 include `CommonLighting.hlsl`，为避免额外依赖，第 16 节直接把这个公式写成内联代码。

### 11.3 Jimenez：常数偏移

**源码**：`return profile * SSSSSaturate(0.3 + dot(light, -worldNormal));`

即 $a = \mathrm{saturate}(0.3 - \mathbf{n}\cdot\mathbf{l})$（按单位向量理解）。思路和 HDRP 相同，但没有做能量归一化：轮廓处为 0.3，$-\mathbf{n}\cdot\mathbf{l} \ge 0.7$ 时已经饱和到 1。

### 11.4 HDRP 还乘了一个漫反射项

HDRP 的 `EvaluateBSDF` 里，透射项是 `cbsdf.diffT = diffTerm * flippedNdotL`，其中 `diffTerm = DisneyDiffuse(clampedNdotV, abs(NdotL), LdotV, perceptualRoughness)`。它是额外的漫反射角度模型，不能直接等同于单个界面的菲涅尔透射率，见第 13.3 节。

## 12. 背面可见性 V_B：要不要乘阴影

**推导要求**：$V_B$ 是**背面入射点**是否被挡住，不是当前像素 $p_o$ 是否被挡住。

**最常见的错误**：直接乘 `mainLight.shadowAttenuation`。逆光且物体参与投影时，当前出口可能被自身遮挡，直接沿用这个阴影值就会压掉透射。是否完全为零还受阴影距离、偏移、滤波和投影设置影响。

业界的几种处理：

| 方案 | $V_B$ 的取法 | 理由 / 出处 |
|---|---|---|
| HDRP Thin | 乘正面阴影 | 假设物体足够薄，正面和背面的阴影可以共用。源码注释：*"in the thin object mode, we assume that the geometry is thin enough for us to safely share the shadowing information between the front and the back faces"* |
| HDRP Thick（点光 / 聚光） | 不乘阴影 | 厚度来自 shadow map，遮挡已经体现在厚度里（被挡 → 厚度大 → $T$ 小） |
| HDRP Thick（平行光） | 不乘阴影 | 用烘焙透射率，见 9.3 节引文 |
| Jimenez | 不乘阴影 | 头文件注释给出的用法是 `float3 t = albedo.rgb * lights[i].color * attenuation * spot; color.rgb += t * SSSSTransmittance(...)`，只乘了衰减和聚光项，没有阴影项；厚度来自 shadow map |

**第 16 节扩展示例的规则**（当前 `SSSLit` 始终取 $V_B=1$）：
- 用 shadow map 测厚度时，$V_B = 1$；
- 用厚度贴图时，按 HDRP Thin 乘正面阴影。所以**对球这种厚物体用贴图模式，透射会被自阴影吃掉**。这是 Thin 模式本来的适用范围，不是 bug。

## 13. 其余各项：环境光、菲涅尔、颜色、是否参与模糊

### 13.1 主光颜色

`mainLight.color * mainLight.distanceAttenuation`。平行光的 `distanceAttenuation` 为 1。附加光源可以按同样方式循环累加；但第 16 节扩展的 shadow map 测厚度只针对主光源，附加光源只能用贴图厚度。

### 13.2 背面环境光

推导里 $E_B$ 是对半球所有方向积分，环境光也包括在内。用背面法线采样即可：`SampleSH(-n)`。

HDRP 的对应做法（源码）：
- `LitBuiltinData.hlsl`：*"For back lighting we use the oposite vertex normal"*，即用 `-input.tangentToWorld[2]` 采样一份 `backBakeDiffuseLighting`；
- `Lit.hlsl`：`builtinData.bakeDiffuseLighting += builtinData.backBakeDiffuseLighting * bsdfData.transmittance;`

其中 `bsdfData.transmittance` 由**烘焙厚度**计算。HDRP 注释说明，这个值 *"is always used for indirect lighting"*，即使在 Thick 模式下也一样。第 16 节扩展沿用这个选择：环境光部分始终使用贴图厚度对应的 $T$；当前 `SSSLit` 只采样正面 SH。

### 13.3 背面入射菲涅尔与漫反射角度项

- 当前 `SSSLit`：透射项取 1（假设 T4），不复用正面高光的 `vdoth`。实际入射边界透射应由背面入射角与折射率计算；正面反射的半角向量不能直接代表这个角度。
- 引用的 HDRP 路径：乘 `DisneyDiffuse(clampedNdotV, abs(NdotL), LdotV, roughness)`（第 11.4 节）。这是包含粗糙度与方向依赖的漫反射角度模型，不能当成 $1-F$ 的逐项实现；它也使该路径不再具有本文简化式的严格视角不变性。

### 13.4 透射颜色 c_tint

推导里没有这一项。HDRP 在 Diffusion Profile 上提供了 Transmission Tint（即 10.1 节的 `volumeAlbedo`），本工程对应 `_TransmissionTint`。保持上述模型时取白色 $(1,1,1)$；想让透射偏某种颜色、又不想改 $d$（改 $d$ 会同时影响反射项的模糊）时再调它。

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

**和本文推导的关系**：两者描述的是不同的东西。推导式 $T(t)\,D_B$ 描述"有多少光穿过来"（与视角无关）；Barré-Brisebois 主要描述"迎光看时的前向散射叶片"。两者可以按美术需求组合，但前者也只是厚度传输近似，后者又额外增加亮度。叠加时应控制共同的强度预算，避免把同一份透射贡献算两遍。

## 15. 各方案对比

| | 第 16 节可选扩展 | HDRP Thin | HDRP Thick | Jimenez | Barré-Brisebois |
|---|---|---|---|---|---|
| 厚度 $t$ | 贴图 / shadow map | 贴图 | 贴图 + 点光聚光的 shadow map 修正 | shadow map | 局部厚度贴图（语义相反） |
| $T(t)$ | Burley | Burley | Burley × $e^{-\Delta t/(3d)}$ | 6 高斯（皮肤） | 线性 |
| 角度项 | HDRP 环绕（可调 $\kappa$） | 环绕 15° | 环绕 15° | $0.3 - \mathbf{n}\cdot\mathbf{l}$ | 视角相关叶片 |
| 阴影 $V_B$ | 贴图：乘；shadow map：不乘 | 乘正面阴影 | 不乘 | 不乘 | 由使用者决定 |
| 背面环境光 | `SampleSH(-n)` × 贴图 $T$ | 背面 GI × 烘焙 $T$ | 同左 | 无 | 无（`_Ambient` 是常驻项，不是环境光采样） |
| 视角相关 | 否（本示例） | 漫反射角度项可相关 | 漫反射角度项可相关 | 所示透射项无关 | 是 |
| 模型依据 | 跨面核假设后解析积分 | Burley 透射近似 | Burley + 厚度修正近似 | 高斯跨面核近似 | 视角相关经验项 |
| 适合 | 学习、通用 | 薄物体 | 厚物体 + 点光聚光 | 皮肤 | 风格化玉石 / 蜡 / 叶子 |

---

# 第三部分：实现

## 16. 参考实现（基于 SSSLit 的 SSSDiffuse Pass）

方案：**Burley $T(t)$ + 厚度二选一（贴图 / shadow map）+ HDRP 环绕 + 背面环境光**。

> 下列代码是基于 `Assets/SSS-TEST/SSSShaderLibrary/SSSLit.shader` 变量约定的扩展示例，不是当前文件的逐字摘录。当前基线的透射可简写为：
>
> `transmission = mainLight.color * mainLight.distanceAttenuation * saturate(dot(-n, mainLight.direction)) * T * _TransmissionTint.rgb;`
>
> 示例只展示透射接入，不替代完整的阴影系统。级联图集边界、阴影偏移与屏幕空间阴影须结合目标平台测试。接入扩展时保留已有纹理声明，更新已有属性和 CBUFFER 字段，新增缺少的字段，避免重复声明 `_ThicknessScale`、`_TransmissionTint`、`_UseShadowThickness`。示例假设 1 Unity 世界单位等于 1 米，只处理主光的普通/级联 shadow map；屏幕空间阴影变体与其他平台纹理约定需要单独适配。

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
| `_TransmissionTint` | $c_{tint}$ | 保持上述模型时取白色 |
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
    if (BEYOND_SHADOW_FAR(sc) || any(sc.xy < 0.0) || any(sc.xy >= 1.0)) return -1.0;
    float entryZ    = LOAD_TEXTURE2D(_MainLightShadowmapTexture, sc.xy * _MainLightShadowmapSize.zw).r; // 步骤 4
    float zPerMeter = length(_MainLightWorldToShadow[cascadeIndex][2].xyz);     // 步骤 5
    if (zPerMeter <= 1e-6) return -1.0;
    #if UNITY_REVERSED_Z
        if (entryZ <= 0.0) return -1.0; // 阴影图清空值，没有有效入口
    #else
        if (entryZ >= 1.0) return -1.0;
    #endif
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
        VBack = 1.0;                                                                              // V_B：以深度差衰减近似遮挡，不再重复乘出口阴影
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

**接入说明**：
- 变量名用 `NdotLRaw` 而不是 `NdotL`：基线 `frag` 里已经有一个 `half NdotL = max(0, ...)`，那个被截断过，不能用于背面。
- shadow map 模式拿不到厚度时（超出阴影距离、没开阴影），自动回退到贴图模式和 Thin 规则。
- 环境光部分固定用 `TBaked`，与 HDRP 一致（第 13.2 节）。
- 前向 Pass（`ForwardSpecularOnly`）、Renderer Feature、模糊 Shader 都不需要改。

## 17. 调试与调参

以下场景和现象针对第 16 节扩展接入后；当前 `SSSLit` 的贴图透射不乘阴影，也没有 wrap 或 shadow map 测厚。验证基线时应先使用第 7 节的功能对应表。

### 17.1 测试场景

1. URP Asset 与 Directional Light 都打开阴影；
2. 光从球的正后方朝相机方向照（逆光）；
3. 先把 frag 输出临时改成 `half4(transmission, 1)`，单独确认透射项是否正确。

### 17.2 预期现象与对应原因

| 现象 | 原因 | 对应章节 |
|---|---|---|
| 贴图模式 + 球：看不到透射 | Thin 规则乘了正面阴影，被自阴影吃掉 | 第 12 节 |
| 贴图模式 + 常量白图：暗面只有受光角度造成的变化，缺少由厚度变化产生的透光边缘 | 衰减处处相同，法线与余弦仍随位置变化 | 第 9.1 节 |
| shadow map 模式 + 球：轮廓附近有一圈亮环，中心暗 | 中心穿透长、$T$ 小；轮廓处穿透短，再加上环绕项 $a \approx 0.163$ | 第 9.2、11.2 节 |
| `_TransmissionWrap = 0` 时，亮环从轮廓往里收 | 轮廓处 $a = 0$，峰值移到 $a$ 和 $T$ 乘积最大的位置 | 第 11.1 节 |
| 厚度从 0 调到 1 mm，颜色突变 | `_ScatterColor` 有通道为 0，该通道 $d \approx 0$ | 第 4.5 节 |
| 光源不在正后方时，轮廓附近受光的一侧也稍微变亮 | 环绕项让受光面 15° 内也有透射，且那里 $t_l \approx 0$、$T \approx 1$ | 第 11.2 节 |
| 透射整体偏强 | 阴影投射的 Depth / Normal Bias 把入射面往远离光的方向推，$t_l$ 偏小；可以减小灯光的 Bias | 第 9.2 节 |
| 明暗交界附近出现噪点或条纹 | 受光面一侧 $t_l \approx 0$，深度精度不够；可以调大 `_ThicknessShrink`。注意它在背光侧会让 $t_l$ 略微变小（缩进的方向是朝光源的） | 第 9.2 节 |

### 17.3 参数怎么定

用第 4.5 节的数值表反推。默认球直径 1000 mm，穿过中心的 $t_l \approx 1000$ mm：

- 希望中心几乎不透（$T < 0.05$）：解得 $\tau=t/d\gtrsim8.13$，即 $d\lesssim123$ mm；
- 希望轮廓附近（$t_l \approx 100$ mm）明显透光（$T > 0.6$）：解得 $\tau\lesssim1.11$，即 $d\gtrsim90$ mm；取 $d>100$ mm 可留出余量。

所以这个球上 $d_R \approx 100\sim123$ mm 比较容易看到"边缘亮、中心暗"。G、B 通道取更小的 $d$（例如 `_ScatterColor = (1, 0.35, 0.2)`），透射会从边缘的暖白过渡到内侧的红。

真实皮肤的 $d$ 只有毫米量级；这里数值大，是因为测试球比人的耳朵大得多。对衰减公式而言，只要 $t/d$ 不变，$T$ 就不变；屏幕投影、阴影精度与采样数仍会影响最终画面。

**同一个 $d$ 也决定反射项的分布宽度**。若采用教程 1 的 Burley 圆盘采样和约 99.7% 累计份额截断，其半径约为 $16.56\,d_{max}$；这不是任意 `SSSLit` 滤波器都具有的固定半径。$d_R = 125$ mm 时半径约 2 m，在 1 m 的球上模糊会很重。这是"反射与透射共用剖面"的代价。如果只想改透射的强弱而不动模糊，应该缩放**厚度**而不是 $d$：
- 贴图模式：调 `_ThicknessScale`。HDRP 的厚度 remap 起的是同样的作用；
- shadow map 模式：给 `tShadow` 乘一个系数。Jimenez 的 `scale` 就是这个作用（第 8 节）。本文的参考实现没有加这个参数，需要时自行添加。

## 18. 参考资料

**源码参考（对应本文片段；不同版本的调用路径需按链接核对）**
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
