Shader "Hidden/SSS/SSSFilter"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        
        #define MAX_FILTER_RADIUS_MUTI_NUM 16.56
        #define GOLDEN_ANGLE 2.39996323

        int _FilterSampleCount;
        
        // x: 2/(P00*width)，乘以线性深度 = 该深度下 1 像素的世界尺寸
        // y: 最大滤波采样次数
        // zw: 1/width, 1/height
        float4 _SSSParams;
        
        float _PixelNumPerSample;

        TEXTURE2D(_BlitTexture);
        SAMPLER(sampler_BlitTexture);
        TEXTURE2D(_ParamRT);
        SAMPLER(sampler_ParamRT);
       
        
        struct a2v
        {
            uint id : SV_VertexID;
        };

        struct v2f
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(a2v input)
        {
            v2f output;
            output.positionCS = GetFullScreenTriangleVertexPosition(input.id);
            output.uv = GetFullScreenTriangleTexCoord(input.id);
            return output;
        }

        float SampleLinearDepth(float2 uv)
        {
            float raw = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_CameraDepthTexture, uv, 0).r;
            return LinearEyeDepth(raw, _ZBufferParams);
        }

        //rcpPdf——1/q----------1/pdf
        void resolveCDF(float cdf_val,float d,out float r,out float rcpPdf)
        {
            float v=1-cdf_val;
            float g = 1.0 + 4.0 * v * (2.0 * v + sqrt(1.0 + 4.0 * v * v));
            float n = pow(g, -1.0 / 3.0);   // g^(-1/3) = a^(-2)
            float p = g * n * n;            // g^(+1/3) = a^2
            float c = 1.0 + p + n;
            float y = 4.0 * v / c;          // y = exp(-r / 3d)
            r = -3.0 * log(y) * d;
            rcpPdf = 8.0 * PI * d / (y * y * y + y);
        }
        
        float getRandomNum_by_float2(float2 param)
        {
            return frac(52.9829189 * frac(dot(param, float2(0.06711056, 0.00583715))));
        }
        
        float3 getBurley_R(float r,float3 d)
        {
            return (exp(-r / d) + exp(-r / (3.0 * d))) / (8.0 * PI * d);
        }
        
        ENDHLSL
    
        Pass
        {
            HLSLPROGRAM
            
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f input):SV_Target
            {
                float2 screenUV=input.positionCS.xy/_ScreenParams.xy;
                float4 scatter_d=SAMPLE_TEXTURE2D(_ParamRT,sampler_ParamRT,screenUV);
                scatter_d.rgb=max(scatter_d.rgb,1e-4);
                float d_max=max(scatter_d.r,max(scatter_d.g,scatter_d.b));
                if (scatter_d.a<=0)
                {
                    return 0;
                }
                float eyeDepth=SampleLinearDepth(screenUV);
                float meterPerPixel=eyeDepth*_SSSParams.x;
                float pixelPerMm=1/(meterPerPixel*1000);
                float maxFilterRadius_Mm=MAX_FILTER_RADIUS_MUTI_NUM*d_max;
                float maxFilterRadius_Pixel=maxFilterRadius_Mm*pixelPerMm;
                float filterAreaSize=PI*maxFilterRadius_Pixel*maxFilterRadius_Pixel;
                int sampleCount=filterAreaSize/_PixelNumPerSample;
                sampleCount=min(sampleCount,_SSSParams.y);
                float3 filterColor=0;
                float3 totalWeight=0;
                float rand=getRandomNum_by_float2(screenUV*_ScreenParams.xy);
                float phase=rand*2*PI;
                float2 texelSize=1/_ScreenParams.xy;
               
                for (int i = 0; i < sampleCount; ++i)
                {
                    float r,rcpPdf;
                    float cdf_val=(i+0.5)/sampleCount;
                    resolveCDF(cdf_val,d_max,r,rcpPdf);
                    float theta=phase+i*GOLDEN_ANGLE;
                    float2 offset=r*pixelPerMm*float2(cos(theta),sin(theta))*texelSize;
                    float2 sampleUV=clamp(screenUV+offset,0,1);
                    if (SAMPLE_TEXTURE2D_X_LOD(_ParamRT,sampler_ParamRT,sampleUV,0).a<=0)
                    {
                        continue;
                    }
                    float3 diffuseColor=SAMPLE_TEXTURE2D_X_LOD(_BlitTexture,sampler_BlitTexture,sampleUV,0);
                    float distance=pow((SampleLinearDepth(sampleUV)-eyeDepth)*1000,2)+r*r;
                    distance=sqrt(max(0,distance));//长度单位转换到mm
                    float3 w=getBurley_R(distance,scatter_d.rgb);
                    w=w*rcpPdf;
                    filterColor+=w*diffuseColor;
                    totalWeight+=w;
                }
                float3 finalColor=totalWeight>0?filterColor/totalWeight:SAMPLE_TEXTURE2D(_BlitTexture,sampler_BlitTexture,screenUV);
                return float4(finalColor,1);
            }
            
            ENDHLSL
        }
      
    }
}
