using UnityEngine;

[ExecuteAlways]
public class GenerateBandMaterialMaps : MonoBehaviour
{
    public GpuTerrainPipeline pipeline;
    public int width = 256;
    public int height = 256;

    [ContextMenu("Generate Band Material Maps")]
    void Generate()
    {
        if (!pipeline)
        {
            Debug.LogError("Assign GpuTerrainPipeline");
            return;
        }

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = (float)x / (width - 1);
                Color c;

                if (u < 0.33f)       // left: rock
                    c = new Color(1, 0, 0, 1);
                else if (u < 0.66f)  // middle: soil
                    c = new Color(0, 1, 0, 1);
                else                 // right: snow
                    c = new Color(0, 0, 1, 1);

                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();

        pipeline.materialMaps = tex;
        Debug.Log("Generated band material map for level 0");
    }
}
