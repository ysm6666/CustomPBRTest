# Unity Compute Shader 核心机制与 IBL 实践指南

Compute Shader (CS) 是利用 GPU 进行高度并行泛用计算的着色器，独立于常规渲染管线。本文参考底层硬件执行逻辑，去除非严谨比喻，直接剖析 CS 的并发模型、线程语义及核心语法。

---

## 一、 线程坐标系统与并行模型

GPU 在执行 Compute Shader 时，将任务划分为一个**三维网格 (Grid)**。这个网格由多个**线程组 (Thread Group)** 组成，而每个线程组内部又包含多个**线程 (Thread)**。

### 1. 核心定义
*   **`[numthreads(X, Y, Z)]`**：在 HLSL 代码中定义，规定了**单个线程组 (Thread Group)** 内部的三维线程数量尺寸。硬件层面，在 NVIDIA GPU 上，一组通常对应一个（或多个）Warp。
*   **`Dispatch(Kernel, GX, GY, GZ)`**：在 C# CPU 端调用，规定了本次调用将在 GPU 上分配多少个**线程组 (Thread Group)**。

**总线程数分配公式**：
*   全局总线程数 X 轴 = `Dispatch 的 GX` $\times$ `numthreads 的 X`
*   全局总线程数 Y 轴 = `Dispatch 的 GY` $\times$ `numthreads 的 Y`
*   全局总线程数 Z 轴 = `Dispatch 的 GZ` $\times$ `numthreads 的 Z`

### 2. HLSL 语义标识 (System Value Semantics)
Compute Shader 中，GPU 会将当前线程的空间坐标作为参数自动塞给入口函数。必须严格记忆以下四个坐标语义：

*   **`SV_GroupID (uint3)`**：
    当前线程组在整个 Dispatch 定义的三维网格中的三维索引。
*   **`SV_GroupThreadID (uint3)`**：
    当前线程在它所属的那个线程组（由 `numthreads` 限制的大小的空间）内的局部三维索引。
*   **`SV_DispatchThreadID (uint3)`**：
    (极高频使用) 线程在所有派发出去的任务当中的**全局唯一三维索引**。常被直接用作纹理的像素坐标 (XY) 或三维数组的存取坐标 (XYZ)。
    *数学关系: `SV_DispatchThreadID = SV_GroupID * numthreads + SV_GroupThreadID`*
*   **`SV_GroupIndex (uint)`**：
    把 `SV_GroupThreadID` 按内存连续性展平后的局部一维索引。

---

## 二、 2D 坐标到 1D 线性内存的映射本质

在很多 IBL Baker 库中（如你提取的代码），你会看到如下运算：
`uint index = id.y * _Resolution + id.x;`

**为什么这样做？**
1. **显存的连续性**：无论是贴图还是 Buffer，在 GPU 硬件显存物理层面全是一维连续存储的内存块。
2. **多维转一维公式**：为了将一个分辨率为 $W \times H$ 的二维图像数据存入一个一维数组 (`RWStructuredBuffer<T>`)，必须做展平操作。
   *   图像可以看作是逐行存储的。
   *   `id.y` 代表当前扫描到了第几行。
   *   每行有 `_Resolution` 个像素数据（即 $W$）。
   *   `id.y * _Resolution` 表示计算并跳过前面的 `id.y` 完整行，得到了当前行在显存中的首地址。
   *   加上 `id.x` 代表当前像素在本行内部的位置偏移（列坐标）。
因此，这是标准的将 2D 坐标（`SV_DispatchThreadID.xy`）映射到 1D 数据阵列的显存偏移指针算法。

---

## 三、 C# 发送端交互控制规范

Compute Shader 在独立显存空间运行，需要 CPU 将指令、数据引用下发。

```csharp
using UnityEngine;

public class CS_IBLBaker : MonoBehaviour
{
    public ComputeShader bakerCS;
    
    void DispatchKernel()
    {
        // 1. 获取入口函数 (Kernel) 索引
        int kernelId = bakerCS.FindKernel("CSMain"); 

        // 2. 分配资源 (RenderTexture必须开启 RandomWrite)
        RenderTexture rt = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGB64);
        rt.enableRandomWrite = true;
        rt.Create();

        // 3. 关联资源数据到 CS 中的变量指针 (Texture / Buffer / 常量)
        bakerCS.SetTexture(kernelId, "Result", rt);
        bakerCS.SetInt("_Resolution", 512);

        // 4. 调用分配
        // 为确保所有像素(512)都被完全覆盖，Dispatch组数需等于: 图像宽 / 单个组宽(numthreads)
        // 使用 Mathf.CeilToInt 防止分辨率无法被组数整除导致的边缘像素丢弃
        int groupX = Mathf.CeilToInt(512.0f / 8.0f);
        int groupY = Mathf.CeilToInt(512.0f / 8.0f);
        bakerCS.Dispatch(kernelId, groupX, groupY, 1);
    }
}
```

---

## 四、 针对 IBL 烘焙的 CS 专项处理机制

### 1. Cubemap 天空盒的读取 
*   **声明方式**：利用 `TextureCube<float4> _SkyboxMap;` 和 `SamplerState sampler_SkyboxMap;`。注意读取 Cubemap 不需要且不支持 `RW` 标识。
*   **采样方式**：不再使用 `(U, V)` 获取，必须使用三维方向向量：
    `_SkyboxMap.SampleLevel(sampler_SkyboxMap, directionV3, mipLevel);`
    *IBL 烘焙的一半工作量就是在依靠 SV_DispatchThreadID 不断反算此对应球面的方向向量 directionV3。*

### 2. 写入 Cubemap (Texture2DArray 替代法)
由于 Compute Shader 无法直接写入底层带有六个面的原生 `RenderTextureCube` 对象，在管线开发中通常用 `Texture2DArray` 曲线救国。

*   **HLSL 声明**：`RWTexture2DArray<float4> _OutputCubemapArray;` (三维数据)
*   **维度降解**：Z轴索引（`SV_DispatchThreadID.z`）直接定义为 Cubemap 的六个平面枚举 `[0, 5]`。
*   **C# 派发**：`Dispatch(kernel, res/8, res/8, 6);` —— 高效发起一次派发，利用硬件并发，让 Z 轴分发六层直接同步写完整个天空盒六个面。

### 3. IBL 高光预滤波 (Prefilter Map)
制作多层 Mip 的预滤波贴图时，往往需要随着粗糙度的提升，按 `Pow2` 缩小分辨率并写入。
*   HLSL 中，只需关注由 CPU 传来的当前 `_Roughness` 变量，生成粗糙对应的镜面分布方向，按上述 1D 或 2D 逻辑采样并输出即可。
*   在 C# 端，须维护一个对应 `Mip` 数量的外部循环：遍历每一层 Mip 级别生成对应尺寸的 RenderTexture，分别执行 `SetTexture`，并伴随按比例递减的 `groupsX/Y` 调用 `Dispatch`。