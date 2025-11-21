using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class TerrainMaterialUtils
{
    public static TerrainLayer CreateSolidColorLayer(Color c, string name)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                tex.SetPixel(x, y, c);
        tex.Apply();

        var layer = new TerrainLayer();
        layer.diffuseTexture = tex;
        layer.tileSize = new Vector2(10, 10);
        layer.name = name;
        return layer;
    }
}