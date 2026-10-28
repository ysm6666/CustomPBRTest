using System;
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
    }

  

    class SSSRenderPass : ScriptableRenderPass
    {
       static readonly int SSSParamsId = Shader.PropertyToID("_SSSParams");
       Material sssFilterMaterial;
       private RTHandle diffuseRT;
       private RTHandle filterRT;
       private RTHandle paramRT;
       private RTHandle depthRT;
       
       private ShaderTagId sss_diffuse_shaderTagId = new ShaderTagId("SSSDiffuse");
       private int maxFilterSampleNum;
       private int pixelNumPerSample;
        public SSSRenderPass(Settings settings,Material sssFilterMaterial)
        {
          this.sssFilterMaterial = sssFilterMaterial;
          this.renderPassEvent = settings.renderPassEvent;
          maxFilterSampleNum = settings.MaxFilterSampleNum;
         pixelNumPerSample = settings.pixelNumPerSample;
          ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
           var descriptor = renderingData.cameraData.cameraTargetDescriptor;
           descriptor.msaaSamples = 1;
           descriptor.colorFormat = RenderTextureFormat.ARGBHalf;
           descriptor.depthBufferBits = 0;
           RenderingUtils.ReAllocateIfNeeded(ref diffuseRT, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SSSDiffuseRT");
           RenderingUtils.ReAllocateIfNeeded(ref filterRT, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_FilterRT");
           RenderingUtils.ReAllocateIfNeeded(ref paramRT, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_ParamRT");
           descriptor.depthBufferBits = 32;
           RenderingUtils.ReAllocateIfNeeded(ref depthRT, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_DepthRT");
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
          CommandBuffer cmd =CommandBufferPool.Get("SSS");
          cmd.SetRenderTarget(new RenderTargetIdentifier[] { diffuseRT, paramRT },depthRT);
          cmd.ClearRenderTarget(true, true, Color.clear);
          context.ExecuteCommandBuffer(cmd);
          cmd.Clear();
          
          var drawing = CreateDrawingSettings(sss_diffuse_shaderTagId, ref renderingData, SortingCriteria.CommonOpaque);
          var filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
          context.DrawRenderers(renderingData.cullResults,ref drawing,ref filteringSettings);
          
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
