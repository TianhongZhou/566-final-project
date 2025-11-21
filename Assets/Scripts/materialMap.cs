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

        Vector2 center = new Vector2(0.5f, 0.5f);

        for (int y = 0; y < height; y++)
        {
            float v = (float)y / (height - 1);

            for (int x = 0; x < width; x++)
            {
                float u = (float)x / (width - 1);

                Vector2 uv = new Vector2(u, v);
                float dist = Vector2.Distance(uv, center);

                Color c;

                if (dist < 0.15f)
                {
                    c = new Color(0f, 0f, 1f, 1f);
                }
                else if (dist < 0.32f)
                {
                    c = new Color(0f, 1f, 0f, 1f);
                }
                else
                {
                    c = new Color(1f, 0f, 0f, 1f);
                }

                tex.SetPixel(x, y, c);
            }
        }

        //for (int y = 0; y < height; y++)
        //{
        //    for (int x = 0; x < width; x++)
        //    {
        //        float u = (float)x / (width - 1);
        //        Color c;

        //        if (u < 0.33f)       // left: rock
        //            c = new Color(1, 0, 0, 1);
        //        else if (u < 0.66f)  // middle: soil
        //            c = new Color(0, 1, 0, 1);
        //        else                 // right: snow
        //            c = new Color(0, 0, 1, 1);

        //        tex.SetPixel(x, y, c);
        //    }
        //}
        tex.Apply();

        pipeline.materialMaps = tex;
        Debug.Log("Generated band material map for level 0");
    }
}
