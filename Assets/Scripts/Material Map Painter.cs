using UnityEngine;

[ExecuteAlways]
public class MaterialMapPainter : MonoBehaviour
{
    public GpuTerrainPipeline pipeline;
    public Terrain targetTerrain;
    public float brushWorldRadius = 50f;  
    [Range(0f, 1f)]
    public float brushStrength = 1f;     
    public enum PaintChannel { Rock, Soil, Snow }
    public PaintChannel channel = PaintChannel.Rock;

    [HideInInspector]
    public bool paintingEnabled = true;

    public void PaintAtWorldPos(Vector3 worldPos)
    {
        if (!pipeline || !pipeline.materialMaps || !targetTerrain)
            return;

        Texture2D tex = pipeline.materialMaps;

        int w = tex.width;
        int h = tex.height;

        Vector3 terrainPos = targetTerrain.transform.position;
        Vector3 local = worldPos - terrainPos;
        Vector3 size = targetTerrain.terrainData.size;

        float u = Mathf.Clamp01(local.x / size.x);
        float v = Mathf.Clamp01(local.z / size.z);

        int centerX = Mathf.RoundToInt(u * (w - 1));
        int centerY = Mathf.RoundToInt(v * (h - 1));

        float texRadius = brushWorldRadius / size.x * w;
        int rPix = Mathf.CeilToInt(texRadius);

        Color targetColor;
        switch (channel)
        {
            case PaintChannel.Soil:
                targetColor = new Color(0f, 1f, 0f, 1f);
                break;
            case PaintChannel.Snow:
                targetColor = new Color(0f, 0f, 1f, 1f);
                break;
            default:
                targetColor = new Color(1f, 0f, 0f, 1f); 
                break;
        }

        for (int y = -rPix; y <= rPix; ++y)
        {
            int py = centerY + y;
            if (py < 0 || py >= h) continue;

            for (int x = -rPix; x <= rPix; ++x)
            {
                int px = centerX + x;
                if (px < 0 || px >= w) continue;

                float dist = Mathf.Sqrt(x * x + y * y);
                if (dist > texRadius) continue;

                tex.SetPixel(px, py, targetColor);
            }
        }

        tex.Apply();
        ApplyMaterialMapToTerrain();
    }

    void SetupTerrainLayers()
    {
        var td = targetTerrain.terrainData;

        var rockLayer = TerrainMaterialUtils.CreateSolidColorLayer(
            new Color(1.0f, 0.0f, 0.0f), "Rock");
        var soilLayer = TerrainMaterialUtils.CreateSolidColorLayer(
            new Color(0.0f, 1.0f, 0.0f), "Soil");
        var snowLayer = TerrainMaterialUtils.CreateSolidColorLayer(
            new Color(0.0f, 0.0f, 1.0f), "Snow");

        td.terrainLayers = new TerrainLayer[] { rockLayer, soilLayer, snowLayer };
    }

    public void ApplyMaterialMapToTerrain()
    {
        if (pipeline.materialMaps == null)
        {
            Debug.LogError("materialMaps is null");
            return;
        }
        var td = targetTerrain.terrainData;

        int alphaRes = pipeline.materialMaps.width;
        td.alphamapResolution = alphaRes;

        int w = alphaRes;
        int h = alphaRes;
        SetupTerrainLayers();
        int numLayers = td.terrainLayers.Length;

        float[,,] alphas = new float[h, w, numLayers];

        for (int y = 0; y < h; y++)
        {
            float v = (float)y / (h - 1);
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / (w - 1);

                Color c = pipeline.materialMaps.GetPixelBilinear(u, v);

                float r = c.r;
                float g = c.g;
                float b = c.b;

                float sum = r + g + b;
                if (sum < 1e-6f)
                {
                    r = 1; g = 0; b = 0; sum = 1;
                }

                r /= sum;
                g /= sum;
                b /= sum;

                alphas[y, x, 0] = r;
                alphas[y, x, 1] = g;
                alphas[y, x, 2] = b;
            }
        }

        td.SetAlphamaps(0, 0, alphas);
    }
}