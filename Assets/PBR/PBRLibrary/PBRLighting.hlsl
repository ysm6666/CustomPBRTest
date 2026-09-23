#ifndef PBR_LIGHTING
#define PBR_LIGHTING

#include "PBRMainLighting.hlsl"
#include "PBRIndirectLighting.hlsl"

struct CustomSurfaceData
{
    float3 albedo;
    float metallic;
    float roughness;
    float3 normalWS;
};

float3 CustomPBR(CustomSurfaceData surfaceData,Light mainLight,float3 viewDirWS)
{
    mainLight.color*=PI;
    float3 normalWS=normalize(surfaceData.normalWS);
    float3 halfDirWS=normalize(viewDirWS+mainLight.direction);
    float NdotV=max(dot(normalWS,viewDirWS),0);
    float NdotL=max(dot(normalWS,mainLight.direction),0);
    float VdotH=max(dot(viewDirWS,halfDirWS),0);
    float NdotH=max(dot(normalWS,halfDirWS),0);
    float roughness=surfaceData.roughness;
    float3 brdf_mainlighting=BRDF_MainLighting(surfaceData.albedo,roughness,surfaceData.metallic,NdotL,NdotV,VdotH,NdotH);
    float3 brdf_indirectlighting=BRDF_IndirectLighting(surfaceData.albedo,normalWS,viewDirWS,surfaceData.metallic,roughness,NdotV);
    float3 finalColor=brdf_mainlighting*mainLight.color*NdotL+brdf_indirectlighting;
    return finalColor;
}

#endif
