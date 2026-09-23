#ifndef PBR_MAIN_LIGHTING
#define PBR_MAIN_LIGHTING

#define PI 3.14159265358979323846
#define F0 float3(0.04, 0.04, 0.04)

float3 Diffuse(float3 albedo)
{
    return albedo/PI;
}

float GeometryFunc(float x,float roughness)
{
    float k=pow(roughness+1,2)/8;
    return x/lerp(k,1,x);
}

float3 Fresnel(float3 albedo,float metallic,float VdotH)
{
    float3 f0=lerp(F0,albedo,metallic);
    float3 F=f0+(1-f0)*pow(saturate(1 - VdotH),5);
    F=clamp(F,0,1);
    return F;
}

float3 Cook_Torrance(float roughness,float NdotH,float VdotH,float3 albedo,float metallic,float NdotV,float NdotL,out float3 F)
{
    roughness=max(roughness,0.2);
    float alpha = roughness * roughness;
    float d_denom = NdotH * NdotH * (alpha * alpha - 1) + 1;
    float D = (alpha * alpha) / max(PI * d_denom * d_denom,1e-6);
    
    F=Fresnel(albedo,metallic,VdotH);
    
    float G=GeometryFunc(NdotL,roughness)*GeometryFunc(NdotV,roughness);
    
    return D*F*G/(4*NdotV*NdotL+1e-5);
}

float3 BRDF_MainLighting(float3 albedo,float roughness,float metallic,float NdotL,float NdotV,float VdotH,float NdotH)
{
    float3 F;
    float3 specular=Cook_Torrance(roughness,NdotH,VdotH,albedo,metallic,NdotV,NdotL,F);
    float3 kd=(1-F)*(1-metallic);
    float3 diffuse=Diffuse(albedo);
    return kd*diffuse+specular;
}





#endif