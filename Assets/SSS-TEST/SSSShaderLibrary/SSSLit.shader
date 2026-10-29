Shader "SSS/SSSLit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic("Metallic",Range(0,1))=0.5
        _ScatterColor("Scatter Color (RGB shape)",Color)=(1, 0.35, 0.2, 1)
        _ScatterDistance("Scatter Distance (mm)",Range(0,500))=250
        
        [Header(Transmission Setting)]
      
        _ThicknessMap("Thickness Map",2D)="white"{}
        _ThicknessScale("Thickness (mm)",Range(0,500))=50
        _TransmissionTint("Transmission Tint",Color)=(1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Smoothness;
            float _Metallic;
            float4 _ScatterColor;
            float _ScatterDistance;
            float _ThicknessScale;
            float4 _TransmissionTint;
            float _UseShadowThickness;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        
        TEXTURE2D(_FilterRT);
        SAMPLER(sampler_FilterRT);
        
        TEXTURE2D(_ThicknessMap);
        SAMPLER(sampler_ThicknessMap);
        ENDHLSL

        // 正常前向 Pass：只输出高光 + 环境反射，漫反射被抽走交给 SSS 流程
        Pass
        {
            Name "ForwardSpecularOnly"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                half3 n = normalize(i.normalWS);
                half3 v = normalize(GetWorldSpaceViewDir(i.positionWS));

                half alpha = 1.0;
                BRDFData brdfData;
                InitializeBRDFData(albedo, 0.0, half3(0, 0, 0), _Smoothness, alpha, brdfData);

                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half NdotL = saturate(dot(n, mainLight.direction));
                half3 radiance = mainLight.color * (mainLight.shadowAttenuation * mainLight.distanceAttenuation * NdotL);
                half3 specular = brdfData.specular * DirectBRDFSpecular(brdfData, n, mainLight.direction, v) * radiance;

                half3 reflectVector = reflect(-v, n);
                half fresnelTerm = Pow4(1.0 - saturate(dot(n, v)));
                half3 indirectSpecular = GlossyEnvironmentReflection(reflectVector, i.positionWS, brdfData.perceptualRoughness, 1.0h)
                                       * EnvironmentBRDFSpecular(brdfData, fresnelTerm);
                float2 screenUV=i.positionCS.xy/_ScreenParams.xy;
                float3 filterColor=SAMPLE_TEXTURE2D(_FilterRT,sampler_FilterRT,screenUV);
                return half4(sqrt(albedo)*filterColor+specular + indirectSpecular, 1);
            }
            ENDHLSL
        }

        // SSS 漫反射 Pass：由 SSSRenderFeature 通过 ShaderTagId("SSSDiffuse") 绘制到单独 RT
        Pass
        {
            Name "SSSDiffuse"
            Tags { "LightMode" = "SSSDiffuse" }
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/PBR/PBRLibrary/PBRLighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
            };

            struct fragOutput
            {
                float4 diffuseColor:SV_Target0;
                float4 sssParam:SV_Target1;
            };
            
            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            fragOutput frag(Varyings i) 
            {
                
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                half3 n = normalize(i.normalWS);
                float3 viewDirWS=normalize(GetCameraPositionWS()-i.positionWS);
                float ndotv=max(0,dot(n,viewDirWS));
                
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 h=normalize(viewDirWS+mainLight.direction);
                
                half NdotL = max(0,dot(n, mainLight.direction));
                float vdoth=max(0,dot(viewDirWS,h));
                half3 diffuse = mainLight.color * mainLight.shadowAttenuation*NdotL;
                
                float3 F_ambient=Fresnel(albedo,_Metallic,ndotv);
                float3 F_mainlight=Fresnel(albedo,_Metallic,vdoth);
                float3 kd_ambient=(1-F_ambient)*(1-_Metallic);
                float3 kd_mainlight=(1-F_mainlight)*(1-_Metallic);
                half3 ambient = SampleSH(n);
                
                //transmission
                float3 d=max(_ScatterColor.rgb*_ScatterDistance,1e-4);
                float t=SAMPLE_TEXTURE2D(_ThicknessMap,sampler_ThicknessMap,i.uv).r*_ThicknessScale;
               
                float3 T = 0.25*exp(-t/d)+0.75*exp(-t/(3*d));
                float backNdotL=saturate(dot(-n,mainLight.direction));
                float3 transmission = mainLight.color*mainLight.distanceAttenuation*backNdotL*T*_TransmissionTint;
                
                fragOutput output;
                output.diffuseColor = half4((diffuse*kd_mainlight+ambient*kd_ambient+transmission)*sqrt(albedo), 1);
                output.sssParam=float4(_ScatterColor.rgb*_ScatterDistance,1);
                return output;
            }
            ENDHLSL
        }
        
      

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half frag(Varyings i) : SV_Target
            {
                return i.positionCS.z;
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

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes v)
            {
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                Varyings o;
                o.positionCS = positionCS;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
