using UnityEngine;
using UnityEditor;

public class CubemapCaptureWindow : EditorWindow
{
    public Transform capturePoint;
    public int resolution = 512;
    public string savePath = "Assets/PBR/Textures/CapturedCubemap.asset";

    [MenuItem("Tools/Capture Cubemap")]
    public static void ShowWindow()
    {
        GetWindow<CubemapCaptureWindow>("Capture Cubemap");
    }

    private void OnGUI()
    {
        capturePoint = (Transform)EditorGUILayout.ObjectField("Capture Point", capturePoint, typeof(Transform), true);
        resolution = EditorGUILayout.IntField("Resolution (Per Face)", resolution);
        savePath = EditorGUILayout.TextField("Save Path", savePath);

        if (GUILayout.Button("Capture"))
        {
            if (capturePoint == null)
            {
                Debug.LogError("Error: Capture Point missing.");
                return;
            }
            ExecuteCapture();
        }
    }

    private void ExecuteCapture()
    {
        GameObject tempObj = new GameObject("TempCubemapCamera");
        tempObj.transform.position = capturePoint.position;
        Camera cam = tempObj.AddComponent<Camera>();
        
        // PBR 要求 HDR 数据。TextureFormat.RGBAHalf 满足。
        Cubemap cubemap = new Cubemap(resolution, TextureFormat.RGBAHalf, false);
        
        // C++ 原生方法：自动向 6 轴 (±X, ±Y, ±Z) 渲染并填充 Cubemap。无需手动旋转相机。
        cam.RenderToCubemap(cubemap);
        
        DestroyImmediate(tempObj);

        AssetDatabase.CreateAsset(cubemap, savePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        
        Debug.Log($"Cubemap Saved: {savePath}");
    }
}