using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public class SSSRenderFeature : ScriptableRendererFeature
{
    [Serializable]
    public class Settings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        public Shader SSSFilterShader;
        [Range(1, 128)]
        public int MaxFilterSampleNum = 16;
        
        [Range(2,8)]
        public int pixelNumPerSample = 4;
        
        public bool AutoThick=false;
    }

  

    class SSSRenderPass : ScriptableRenderPass
    {
       static readonly int SSSParamsId = Shader.PropertyToID("_SSSParams");
       Material sssFilterMaterial;
       private RTHandle diffuseRT;
       private RTHandle filterRT;
       private RTHandle paramRT;
       private RTHandle depthRT;
       private RTHandle depthLS_RT;
       
       private ShaderTagId sss_diffuse_shaderTagId = new ShaderTagId("SSSDiffuse");
       private int maxFilterSampleNum;
       private int pixelNumPerSample;
       
       private bool AutoThick;
       private readonly Settings settings;
       private readonly List<Renderer> thicknessRenderers = new List<Renderer>();
       private const string AutoThickKeyword = "_SSS_AUTO_THICK";
       private const int LightDepthSize = 1024;
        public SSSRenderPass(Settings settings,Material sssFilterMaterial)
        {
          this.settings = settings;
          this.sssFilterMaterial = sssFilterMaterial;
          this.renderPassEvent = settings.renderPassEvent;
          maxFilterSampleNum = settings.MaxFilterSampleNum;
         pixelNumPerSample = settings.pixelNumPerSample;
         AutoThick = settings.AutoThick;
          ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
           AutoThick = settings.AutoThick;
           var descriptor = renderingData.cameraData.cameraTargetDescriptor;
           descriptor.msaaSamples = 1;
           descriptor.colorFormat = RenderTextureFormat.ARGBHalf;
           descriptor.depthBufferBits = 0;
           RenderingUtils.ReAllocateIfNeeded(ref diffuseRT, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SSSDiffuseRT");
           RenderingUtils.ReAllocateIfNeeded(ref filterRT, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_FilterRT");
           RenderingUtils.ReAllocateIfNeeded(ref paramRT, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_ParamRT");
           descriptor.depthBufferBits = 32;
           RenderingUtils.ReAllocateIfNeeded(ref depthRT, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_DepthRT");
           if (AutoThick && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
           {
               var lightDescriptor = new RenderTextureDescriptor(LightDepthSize, LightDepthSize, RenderTextureFormat.RFloat, 32);
               lightDescriptor.msaaSamples = 1;
               lightDescriptor.sRGB = false;
               // RTHandle allocation with depth bits creates a depth-only texture in this URP version.
               // Allocate the combined color/depth RenderTexture explicitly so RFloat survives.
               if (depthLS_RT == null)
               {
                   var lightTexture = new RenderTexture(lightDescriptor)
                   {
                       name = "_DepthLSRT", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
                   };
                   lightTexture.Create();
                   depthLS_RT = RTHandles.Alloc(lightTexture);
               }
           }
           else
           {
               depthLS_RT?.Release();
               depthLS_RT = null;
           }
        }

        private bool RenderLightDepth(CommandBuffer cmd, ref RenderingData renderingData)
        {
            int lightIndex = renderingData.lightData.mainLightIndex;
            if (!AutoThick || depthLS_RT == null || lightIndex < 0 || renderingData.cameraData.camera.stereoEnabled)
                return false;
            var visibleLight = renderingData.lightData.visibleLights[lightIndex];
            if (visibleLight.lightType != LightType.Directional || visibleLight.light == null)
                return false;

            var light = visibleLight.light;
            var camera = renderingData.cameraData.camera;
            thicknessRenderers.Clear();
            Bounds bounds = new Bounds();
            // 不使用相机 cullResults：入口面和屏幕外的同一物体也必须参与。
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                int layer = 1 << renderer.gameObject.layer;
                if ((camera.cullingMask & light.cullingMask & layer) == 0) continue;
                bool eligible = false;
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && material.renderQueue <= 2500 && material.FindPass("SSSLightDepth") >= 0)
                        eligible = true;
                if (!eligible) continue;
                if (thicknessRenderers.Count == 0) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                thicknessRenderers.Add(renderer);
            }
            if (thicknessRenderers.Count == 0) return false;

            float padding = Mathf.Max(0.01f, bounds.extents.magnitude * 0.01f);
            Vector3 eye = bounds.center - light.transform.forward * (bounds.extents.magnitude + padding * 2f);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1)) *
                             Matrix4x4.TRS(eye, light.transform.rotation, Vector3.one).inverse;
            Vector3 min = Vector3.one * float.PositiveInfinity;
            Vector3 max = Vector3.one * float.NegativeInfinity;
            for (int i = 0; i < 8; ++i)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = view.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float near = Mathf.Max(0.001f, -max.z - padding);
            float far = Mathf.Max(near + 0.01f, -min.z + padding);
            Matrix4x4 projection = Matrix4x4.Ortho(min.x - padding, max.x + padding,
                min.y - padding, max.y + padding, near, far);
            cmd.SetGlobalMatrix("_SSSLightView", view);
            RenderingUtils.SetViewAndProjectionMatrices(cmd, view, GL.GetGPUProjectionMatrix(projection, true), false);
            cmd.SetGlobalMatrix("_SSSLightVP", GL.GetGPUProjectionMatrix(projection, true) * view);
            // One light-map texel tolerance avoids falling back on tiny negative entry differences.
            float depthTolerance = Mathf.Max(max.x - min.x + 2 * padding,
                max.y - min.y + 2 * padding) / LightDepthSize;
            cmd.SetGlobalVector("_SSSLightDepthRange", new Vector4(near, far, depthTolerance, 0));
            cmd.SetRenderTarget(depthLS_RT);
            cmd.SetViewport(new Rect(0, 0, LightDepthSize, LightDepthSize));
            cmd.ClearRenderTarget(true, true, new Color(-1, 0, 0, 0), 1.0f);
            foreach (var renderer in thicknessRenderers)
            {
                var materials = renderer.sharedMaterials;
                for (int submesh = 0; submesh < materials.Length; ++submesh)
                {
                    var material = materials[submesh];
                    if (material == null || material.renderQueue > 2500) continue;
                    int pass = material.FindPass("SSSLightDepth");
                    if (pass >= 0) cmd.DrawRenderer(renderer, material, submesh, pass);
                }
            }
            cmd.SetGlobalTexture("_DepthLSRT", depthLS_RT);
            return true;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
          CommandBuffer cmd =CommandBufferPool.Get("SSS");
          cmd.DisableShaderKeyword(AutoThickKeyword);
          cmd.SetGlobalTexture("_DepthLSRT", Texture2D.blackTexture);
          //调用SSSLit渲染光源空间深度的pass，先生成当前帧深度，再开启diffuse Pass自动测厚关键字。
          if (RenderLightDepth(cmd, ref renderingData)) cmd.EnableShaderKeyword(AutoThickKeyword);
          context.ExecuteCommandBuffer(cmd);
          cmd.Clear();
          // 恢复相机矩阵及 diffuse RT 的 viewport。
          RenderingUtils.SetViewAndProjectionMatrices(cmd, renderingData.cameraData.GetViewMatrix(),
              renderingData.cameraData.GetGPUProjectionMatrix(), false);
          cmd.SetRenderTarget(new RenderTargetIdentifier[] { diffuseRT, paramRT },depthRT);
          cmd.SetViewport(new Rect(0, 0, renderingData.cameraData.cameraTargetDescriptor.width,
              renderingData.cameraData.cameraTargetDescriptor.height));
          cmd.ClearRenderTarget(true, true, Color.clear);
          context.ExecuteCommandBuffer(cmd);
          cmd.Clear();
          
          var drawing = CreateDrawingSettings(sss_diffuse_shaderTagId, ref renderingData, SortingCriteria.CommonOpaque);
          var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
          context.DrawRenderers(renderingData.cullResults,ref drawing,ref filteringSettings);

          cmd.DisableShaderKeyword(AutoThickKeyword);
          cmd.SetGlobalTexture("_DepthLSRT", Texture2D.blackTexture);
          
          cmd.SetGlobalTexture("_SSSDiffuseRT",diffuseRT);
          cmd.SetGlobalTexture("_ParamRT",paramRT);
          var desc=renderingData.cameraData.cameraTargetDescriptor;
          var proj=renderingData.cameraData.GetProjectionMatrix();
          cmd.SetGlobalVector(SSSParamsId,new Vector4(2f/(proj.m00*desc.width),maxFilterSampleNum,desc.width,desc.height));
          cmd.SetGlobalFloat("_PixelNumPerSample",pixelNumPerSample);
          Blitter.BlitCameraTexture(cmd,diffuseRT,filterRT,sssFilterMaterial,0);
          cmd.SetGlobalTexture("_FilterRT",filterRT);
          context.ExecuteCommandBuffer(cmd);
          cmd.Clear();
          
          cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle);
          context.ExecuteCommandBuffer(cmd);
          cmd.Clear();
          CommandBufferPool.Release(cmd);
        }

        public void Dispose()
        {
            diffuseRT?.Release();
            filterRT?.Release();
            paramRT?.Release();
            depthRT?.Release();
            depthLS_RT?.Release();
        }
    }

    public Settings settings = new Settings();

    SSSRenderPass sssRenderPass;
    Material sssMaterial;

    public override void Create()
    {
        if (sssMaterial == null && settings.SSSFilterShader != null)
        {
            sssMaterial = new Material(settings.SSSFilterShader);
        }

        if (sssMaterial!=null)
        {
            sssRenderPass = new SSSRenderPass(settings,sssMaterial);   
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var cameraType = renderingData.cameraData.cameraType;
        if (sssMaterial == null || (cameraType != CameraType.Game && cameraType !=CameraType.SceneView))
            return;
        if (sssRenderPass != null) renderer.EnqueuePass(sssRenderPass);
    }

    protected override void Dispose(bool disposing)
    {
        sssRenderPass?.Dispose();
        CoreUtils.Destroy(sssMaterial);
        sssMaterial = null;
    }
}
