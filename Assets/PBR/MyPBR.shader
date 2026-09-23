Shader "MyPBR/PBR"
{
    Properties
    {
        _BaseColor("Base Color",Color)=(1,1,1,1)
        _Smoothness("Smoothness",Range(0,1))=0
        _Metallic("Metallic",Range(0,1))=0
    }

    SubShader
    {
        Tags{
            "RenderType"="Opaque"
            "Queue"="Geometry"
            "RenderPipeline"="UniversalPipeline"
        }
        Pass
        {
            Tags{"LightMode"="UniversalForward"}
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "PBRLibrary/PBRLighting.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            struct a2v
            {
                float4 postionOS:POSITION;
                float3 normalOS:NORMAL;
            };

            struct v2f
            {
                float4 postionCS:SV_POSITION;
                float3 normalWS:NORMAL;
                float3 positionWS:TEXCOORD0;
            };
            
            CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float _Smoothness;
            float _Metallic;
            CBUFFER_END
            
           

            v2f vert(a2v input)
            {
                v2f output;
                output.postionCS=TransformObjectToHClip(input.postionOS.xyz);
                output.normalWS=TransformObjectToWorldNormal(input.normalOS);
                output.positionWS=TransformObjectToWorld(input.postionOS);
                return output;
            }

            float4 frag(v2f input):SV_Target
            {
                Light mainLight=GetMainLight();
                mainLight.color*=PI;
                float3 viewDirWS=normalize(GetCameraPositionWS()-input.positionWS);
                float3 normalWS=normalize(input.normalWS);
                float3 halfDirWS=normalize(viewDirWS+mainLight.direction);
                float NdotV=max(dot(normalWS,viewDirWS),0);
                float NdotL=max(dot(normalWS,mainLight.direction),0);
                float VdotH=max(dot(viewDirWS,halfDirWS),0);
                float NdotH=max(dot(normalWS,halfDirWS),0);
                float roughness=1-_Smoothness;
                float3 brdf_mainlighting=BRDF_MainLighting(_BaseColor.rgb,roughness,_Metallic,NdotL,NdotV,VdotH,NdotH);
                
               
                float3 brdf_indirectlighting=BRDF_IndirectLighting(_BaseColor,normalWS,viewDirWS,_Metallic,roughness,NdotV);
                
                float3 finalColor=brdf_mainlighting*mainLight.color*NdotL+brdf_indirectlighting;
                
                return float4(finalColor,1);
            }

            ENDHLSL
        }
        Pass
        {
            Tags {"LightMode"="DepthNormals"}
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma vertex vert
            #pragma fragment frag

            struct a2f
            {
                float4 positionOS:POSITION;
            };

            struct v2f
            {
                float4 positionCS:SV_POSITION;
            };
            
            v2f vert(a2f input)
            {
                v2f output;
                output.positionCS=TransformObjectToHClip(input.positionOS);
                return output;
            }
            
            float4 frag(v2f input):SV_Target
            {
                return 0;
            }
            
            ENDHLSL
        }
    }
}