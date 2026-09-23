using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;

public class SHBaker 
{
    public Cubemap irradianceMap;
    private Vector3[] shCoefficients;
    private float[] sh9_constantPart=new float[9];
    
    public SHBaker(Cubemap irradianceMap)
    {
        this.irradianceMap = irradianceMap;
    }

    private void init_sh9_constantPart()
    {
        float sh0_0=1.0f/math.sqrt(4*math.PI);
        float sh1_0=0.5f*math.sqrt(3.0f/math.PI);
        float sh1_1=sh1_0;
        float sh1_n1=sh1_0;
        float sh2_n2=0.5f*math.sqrt(15.0f/math.PI);
        float sh2_n1=sh2_n2;
        float sh2_0=0.25f*math.sqrt(5.0f/math.PI);
        float sh2_1=sh2_n1;
        float sh2_2 = sh2_0;
        sh9_constantPart[0]=sh0_0;
        sh9_constantPart[1]=sh1_n1;
        sh9_constantPart[2]=sh1_0;
        sh9_constantPart[3]=sh1_1;
        sh9_constantPart[4]=sh2_n2;
        sh9_constantPart[5]=sh2_n1;
        sh9_constantPart[6]=sh2_0;
        sh9_constantPart[7]=sh2_1;
        sh9_constantPart[8]=sh2_2;
    }
    
    public void BakeSHCoefficients()
    {
        init_sh9_constantPart();
        shCoefficients=BakeSH();
        if (shCoefficients != null)
        {
            Vector4[] shCoefficients4=new Vector4[9];
            for (int i = 0; i < 9; i++)
            {
                shCoefficients4[i]=new Vector4(shCoefficients[i].x,shCoefficients[i].y,shCoefficients[i].z);
            }
            Shader.SetGlobalVectorArray("_SHCoefficients", shCoefficients4);
            Shader.SetGlobalFloatArray("_SH9ConstantPart", sh9_constantPart);
        }
    }

    private Vector3[] BakeSH()
    {
        if (irradianceMap == null)
        {
            return null;
        }
        Vector3[] coefficients = new Vector3[9];
        for (int face = 0; face < 6; face++)
        {
            var colors=irradianceMap.GetPixels((CubemapFace)face);
            for (int texel = 0; texel < irradianceMap.width*irradianceMap.width; texel++)
            {
                float u = (texel % irradianceMap.width) / (float)irradianceMap.width+0.5f*(1.0f/irradianceMap.width);
                float v = ((int)(texel / irradianceMap.width)) / (float)irradianceMap.width+0.5f*(1.0f/irradianceMap.width);
                Vector3 dir=GetDirectionFromUV(u,v,face);
                Color irradiance=colors[texel];
                float deltaSolidAngle=GetDeltaSolidAngle(u,v,irradianceMap.width);
                float[] sh9Func=HarmonicsBaasFunc(dir);
                for (int i = 0; i < 9; i++)
                {
                    coefficients[i].x+=irradiance.r*sh9Func[i]*deltaSolidAngle;
                    coefficients[i].y+=irradiance.g*sh9Func[i]*deltaSolidAngle;
                    coefficients[i].z+=irradiance.b*sh9Func[i]*deltaSolidAngle;
                }
            }
        }
        return coefficients;
    }

    private float[] HarmonicsBaasFunc(Vector3 input)
    {
        float x=input.x;
        float y=input.y;
        float z=input.z;
        float[] results = new float[9];
        results[0] = sh9_constantPart[0];
        results[1] = sh9_constantPart[1]*y;
        results[2] = sh9_constantPart[2]*z;
        results[3] = sh9_constantPart[3]*x;
        results[4] = sh9_constantPart[4]*x*y;
        results[5] = sh9_constantPart[5]*y*z;
        results[6] = sh9_constantPart[6]*(2*z*z-x*x-y*y);
        results[7] = sh9_constantPart[7]*x*z;
        results[8] = sh9_constantPart[8]*(x*x-y*y);
        return results;
    }
    
    private Vector3 GetDirectionFromUV(float u, float v, int face)
    {
        Vector3 direction = Vector3.zero;
        switch (face)
        {
            case 0://+x
                direction.x = 1;
                direction.y=v*-2f+1;
                direction.z=u*-2f+1;
                break;
            case 1://-x
                direction.x = -1;
                direction.y=v*-2f+1;
                direction.z = u * 2f - 1;
                break;
            case 2://+y
                direction.y = 1;
                direction.x=u*2f-1;
                direction.z=v*2f-1;
                break;
            case 3://-y
                direction.y = -1;
                direction.x=u*2f-1;
                direction.z=v*-2f+1;
                break;
            case 4://+z
                direction.z = 1;
                direction.x=u*2f-1;
                direction.y=v*-2f+1;
                break;
            case 5://-z
                direction.z = -1;
                direction.x=u*-2f+1;
                direction.y=v*-2f+1;
                break;
        }

        return direction.normalized;
    }

    private float GetDeltaSolidAngle(float u, float v, float resolution)
    {
        float delta = 1f / resolution;
        u = u * 2f - 1;
        v = v * 2f - 1;
        float x0 = u - delta;
        float x1 = u + delta;
        float y0 = v - delta;
        float y1 = v + delta;
        return AreaElement(x1,y1)-AreaElement(x0,y1)-AreaElement(x1,y0)+AreaElement(x0,y0);
    }

    private float AreaElement(float x, float y)
    {
        return Mathf.Atan2(x * y, Mathf.Sqrt(x * x + y * y + 1));
    }
}