using System;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

public class IBLBaker:EditorWindow
{
    public Cubemap skybox;
    public Cubemap irradianceMap;
    public ComputeShader iblBakerCS;
    public string savePath="Assets/PBR/Textures";
    private int irradianceBakerKernel = 0;
    private SHBaker shBaker;
    
    private int prefilterSpecularBakeKernel=0;
    private Cubemap specularMap;
    
    private int BakeSpecularLUTKernel=0;
    private Texture2D brdfLut;
    
    [MenuItem("Tools/IBLBaker")]
    public static void showWindow()
    {
        GetWindow<IBLBaker>("IBLBaker");
    }
    
    private void OnGUI()
    {
        GUILayout.Label("Settings",EditorStyles.boldLabel);
        skybox=(Cubemap)EditorGUILayout.ObjectField(skybox,typeof(Cubemap),false);
        iblBakerCS=(ComputeShader)EditorGUILayout.ObjectField(iblBakerCS,typeof(ComputeShader),false);
        savePath=EditorGUILayout.TextField("Save Path",savePath);
        
        EditorGUILayout.Space();

        if (GUILayout.Button("Bake CubeMap To Irradiance"))
        {
            if (skybox is null || iblBakerCS is null ||  savePath is null || savePath == ""  )
            {
                Debug.LogError("Please fill in all the settings");
                return;
            }
            BakeCubeMapToIrradiance();
        }

        if (GUILayout.Button("Bake Irradiance To SH"))
        {
            if (irradianceMap is null)
            {
                Debug.LogError("Please bake irradiance map first");
                return;
            }
            shBaker = new SHBaker(irradianceMap);
            shBaker.BakeSHCoefficients();
            Debug.Log("Bake Irradiance To SH Done");
        }

        if (GUILayout.Button("Bake Prefilter Specular Map"))
        {
            if (skybox is null || iblBakerCS is null ||  savePath is null || savePath == ""  )
            {
                Debug.LogError("Please fill in all the settings");
                return;
            }
            BakePrefilterSpecular();
            Debug.Log("Bake Prefilter Specular Map Done");
        }

        if (GUILayout.Button("Bake Specular LUT"))
        {
            if (iblBakerCS is null || savePath is null || savePath == "")
            {
                Debug.LogError("Please fill in all the settings");
                return;
            }
            BakeSpecularLUT();
            Debug.Log("Bake Specular LUT Done");
        }
    }

    public void BakeCubeMapToIrradiance()
    {
        irradianceBakerKernel = iblBakerCS.FindKernel("CSMainDiffuse");
        PrefillterDiffuseCubemap(skybox,out irradianceMap);
        AssetDatabase.CreateAsset(irradianceMap,$"{savePath}/IrradianceMap.asset");
        AssetDatabase.Refresh();
        Debug.Log("Bake CubeMap To Irradiance Done");
        Shader.SetGlobalTexture("_IrradianceMap",irradianceMap);
    }

    private void PrefillterDiffuseCubemap(Cubemap envCubemap,out Cubemap outputCubemap)
    {
        int size = 128;
        outputCubemap=new Cubemap(size,TextureFormat.RGBAFloat,false);
        ComputeBuffer resultBuffer = new ComputeBuffer(size*size,sizeof(float)*4);
        Color[] tempColors =new Color[size*size];
        for (int face = 0; face < 6; face++)
        {
            iblBakerCS.SetInt("_Face",face);
            iblBakerCS.SetTexture(irradianceBakerKernel,"_Skybox",envCubemap);
            iblBakerCS.SetInt("_Resolution",size);
            iblBakerCS.SetBuffer(irradianceBakerKernel,"_Result",resultBuffer);
            iblBakerCS.SetInt("_SkyboxResolution",Math.Max(skybox.width,skybox.height));
            iblBakerCS.Dispatch(irradianceBakerKernel,size/8,size/8,1);
            resultBuffer.GetData(tempColors);
            outputCubemap.SetPixels(tempColors,(CubemapFace)face);
        }
        resultBuffer.Release();
        outputCubemap.Apply();
    }

    private void BakePrefilterSpecular()
    {
        PrefilterSpecular(skybox,out specularMap);
        AssetDatabase.CreateAsset(specularMap,$"{savePath}/SpecularMap.asset");
        AssetDatabase.Refresh();
        Shader.SetGlobalTexture("_PrefilterSpecMap",specularMap);
    }

    private void PrefilterSpecular(Cubemap envCubemap,out Cubemap outputCubemap)
    {
        prefilterSpecularBakeKernel = iblBakerCS.FindKernel("CSMainSpecular_Prefilter");
        int bakeSize = 128;
        outputCubemap=new Cubemap(bakeSize,TextureFormat.RGBAFloat,true);
        int maxMip = outputCubemap.mipmapCount;
        int sampleCubemapSize = envCubemap.width;
        outputCubemap.filterMode = FilterMode.Trilinear;
        for (int mip = 0; mip < maxMip; mip++)
        {
            int size = bakeSize;
            size= size>>mip;
            int size2=size*size;
            Color[] tempCOlors = new Color[size2];
            float roughness = (float)mip/(maxMip-1);
            ComputeBuffer resultBuffer = new ComputeBuffer(size2,sizeof(float)*4);
            for (int face = 0; face < 6; face++)
            {
                 iblBakerCS.SetInt("_Face",face);
                 iblBakerCS.SetTexture(prefilterSpecularBakeKernel,"_Skybox",envCubemap);
                 iblBakerCS.SetInt("_Resolution",size);
                 iblBakerCS.SetFloat("_FilterMapRoughness",roughness);
                 iblBakerCS.SetBuffer(prefilterSpecularBakeKernel,"_Result",resultBuffer);
                 iblBakerCS.Dispatch(prefilterSpecularBakeKernel,size,size,1);
                 resultBuffer.GetData(tempCOlors);
                 outputCubemap.SetPixels(tempCOlors,(CubemapFace)face,mip);
            }
            resultBuffer.Release();
        }
        outputCubemap.Apply(false);
    }

    private void BakeSpecularLUT()
    {
        BakeSpecularLUTKernel = iblBakerCS.FindKernel("CSMainSpecular_BakeBRDF");
        int resolution = 512;
        brdfLut = new Texture2D(resolution,resolution,TextureFormat.RGBAFloat,false);
        brdfLut.wrapMode = TextureWrapMode.Clamp;
        brdfLut.filterMode = FilterMode.Bilinear;
        Color[] tempColors = new Color[resolution*resolution];
        ComputeBuffer resultBuffer = new ComputeBuffer(resolution*resolution,sizeof(float)*4);
        iblBakerCS.SetInt("_Resolution",resolution);
        iblBakerCS.SetBuffer(BakeSpecularLUTKernel,"_Result",resultBuffer);
        iblBakerCS.Dispatch(BakeSpecularLUTKernel,resolution/8,resolution/8,1);
        resultBuffer.GetData(tempColors);
        brdfLut.SetPixels(tempColors);
        brdfLut.Apply();
        AssetDatabase.CreateAsset(brdfLut,$"{savePath}/SpecularLut.asset");
        AssetDatabase.Refresh();
        Shader.SetGlobalTexture("_BRDFLUT",brdfLut);
    }
    
}