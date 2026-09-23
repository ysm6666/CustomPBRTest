#ifndef PBR_INDIRECT_LIGHTING
#define PBR_INDIRECT_LIGHTING
#include "PBRMainLighting.hlsl"

float4 _SHCoefficients[9]; 
float _SH9ConstantPart[9];
TEXTURECUBE(_IrradianceMap);
SAMPLER(sampler_IrradianceMap);

TEXTURECUBE(_PrefilterSpecMap);
SAMPLER(sampler_PrefilterSpecMap);

TEXTURE2D(_BRDFLUT);
SAMPLER(sampler_BRDFLUT);

float3 SampleBakeSH(float3 normalWS)
{
    float x=normalWS.x;
    float y=normalWS.y;
    float z=normalWS.z;
    float3 res=float3(0,0,0);
    res+=_SHCoefficients[0]*_SH9ConstantPart[0];
    res+=_SHCoefficients[1]*_SH9ConstantPart[1]*y;
    res+=_SHCoefficients[2]*_SH9ConstantPart[2]*z;
    res+=_SHCoefficients[3]*_SH9ConstantPart[3]*x;
    
    res+=_SHCoefficients[4]*_SH9ConstantPart[4]*x*y;
    res+=_SHCoefficients[5]*_SH9ConstantPart[5]*y*z;
    res+=_SHCoefficients[6]*_SH9ConstantPart[6]*(2*z*z-x*x-y*y);
    res+=_SHCoefficients[7]*_SH9ConstantPart[7]*x*z;
    res+=_SHCoefficients[8]*_SH9ConstantPart[8]*(x*x-y*y);
    
    return res;
}

float3 BRDF_IndirectLighting(float3 albedo,float3 normalWS,float3 viewDirWS,float metallic,float roughness,float NdotV,bool useSH=true)
{
    normalWS=normalize(normalWS);
    float3 ambient_diffuse=float3(0,0,0);
    if (useSH)
    {
        ambient_diffuse+=SampleBakeSH(normalWS);
    }
    else
    {
        ambient_diffuse=SAMPLE_TEXTURECUBE(_IrradianceMap,sampler_IrradianceMap,normalWS);
    }
    float3 F=Fresnel(albedo,metallic,NdotV);
    float3 _F0=lerp(float3(0.04, 0.04, 0.04),albedo,metallic);
    float3 kd=(1-F)*(1-metallic);
    float3 diffuse=albedo*kd*ambient_diffuse;
    
    float3 R=reflect(-viewDirWS,normalWS);
    float mipLevel=roughness*7;
    float3 filterSpecColor=SAMPLE_TEXTURE2D_LOD(_PrefilterSpecMap,sampler_PrefilterSpecMap,R,mipLevel).rgb;
    float2 brdfLut_uv=float2(NdotV,roughness);
    float2 brdflut=SAMPLE_TEXTURE2D(_BRDFLUT,sampler_BRDFLUT,brdfLut_uv).rg;
    float3 specular=filterSpecColor*(_F0*brdflut.x+brdflut.y);
    
    
     return diffuse + specular;
     //return filterSpecColor;
}


#endif
