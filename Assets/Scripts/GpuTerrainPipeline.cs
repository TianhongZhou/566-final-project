using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using System.Text;

public class GpuTerrainPipeline : MonoBehaviour
{
    

    [Header("Terrain")]
    public Terrain targetTerrain;       
    public float terrainSizeXZ = 10000f;
    public float terrainMaxHeight = 3000f; 

    [Header("Shaders")]
    public ComputeShader erosionCS;

    [Header("Pipeline Settings")]
    public float baseResolution = 256f;

    [Header("Input")]
    public Texture2D inputHeight;

    [System.Serializable]
    public struct MaterialLUT
    {
        // rain weight
        public Vector3 rain;

        // slope exponent n (per material)
        public Vector3 n;      

        // drainage exponent m (per material)
        public Vector3 m;

        // stream power erosion coefficient k_e (per material)
        public Vector3 kE;

        // talus angle in degrees (per material)
        public Vector3 talusDeg;

        // sediment creation coeff (per material)
        public Vector3 kC;

        // deposition strength multiplier (per material)
        public Vector3 kD;
    }

    public Texture2D materialMaps;

    [Header("Material LUT")]
    public MaterialLUT materialLUT = new MaterialLUT
    {
        rain = new Vector3(0.5f, 1.0f, 1.5f),
        n = new Vector3(2.2f, 2.0f, 1.8f),
        m = new Vector3(0.7f, 0.8f, 1.0f),
        kE = new Vector3(2e-4f, 5e-4f, 8e-4f),
        talusDeg = new Vector3(40f, 30f, 20f),
        kC = new Vector3(0.05f, 0.10f, 0.15f),
        kD = new Vector3(0.05f, 0.10f, 0.20f),
    };

    int kCopyFromTexture;
    int kUpsample2x;
    int kFlowRouting;
    int kErode;
    int kThermal;
    int kDeposit;
    int kRetarget;
    int kBreach;
    int kClear;

    [System.Serializable]
    public struct LevelSchedule
    {
        public int erosionSteps; 
        public int thermalSteps;  
        public int depositionSteps;
    }

    [SerializeField]
    public LevelSchedule[] levels = new LevelSchedule[]
    {
        new LevelSchedule { erosionSteps = 200, thermalSteps = 200, depositionSteps = 200 },
        new LevelSchedule { erosionSteps = 200, thermalSteps = 200, depositionSteps = 200 },
        new LevelSchedule { erosionSteps = 200, thermalSteps = 200, depositionSteps = 200 }
    };

    private Dictionary<int, RenderTexture> heightRTs = new Dictionary<int, RenderTexture>();
    private Dictionary<int, RenderTexture> streamRTs = new Dictionary<int, RenderTexture>();
    private Dictionary<int, RenderTexture> sedimentRTs = new Dictionary<int, RenderTexture>();
    private Dictionary<int, RenderTexture> tempRTs = new Dictionary<int, RenderTexture>();
    private Dictionary<int, RenderTexture> snowRTs = new Dictionary<int, RenderTexture>();

    [Header("Erosion Parameters")]
    //[Range(0.1f, 5.0f)]
    public float erosionDT = 1.0f;
    //[Range(1.0f, 5.0f)]
    public float flowP = 1.3f;
    //[Range(1.0f, 5.0f)]
    public float rainIntensity = 2.6f;

    [Header("Thermal Parameters")]
    //[Range(20f, 50f)]
    public float talus = 30f;
    //[Range(0.1f, 2.0f)]
    public float thermalDT = 1.0f;

    [Header("Deposition Parameters")]
    //[Range(0.1f, 5.0f)]
    public float depositionStrength = 1.0f;

    RenderTexture _origHeightAtFinal;

    void EnsureFinalOrigRT(int w, int h)
    {
        if (_origHeightAtFinal != null && (_origHeightAtFinal.width != w || _origHeightAtFinal.height != h))
        {
            _origHeightAtFinal.Release();
            _origHeightAtFinal = null;
        }
        if (_origHeightAtFinal == null)
        {
            _origHeightAtFinal = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat);
            _origHeightAtFinal.enableRandomWrite = true;
            _origHeightAtFinal.filterMode = FilterMode.Bilinear;
            _origHeightAtFinal.wrapMode = TextureWrapMode.Clamp;
            _origHeightAtFinal.Create();
        }
    }


    void Start()
    {
        if (!inputHeight) { Debug.LogError("Missing input height"); return; }
        if (!erosionCS) { Debug.LogError("Missing Erosion.compute"); return; }
        if (!targetTerrain) { Debug.LogError("Missing Terrain"); return; }

        targetTerrain.terrainData = Instantiate(targetTerrain.terrainData);

        InitKernels();
        InitRenderTextures();

        // Main algorithm
        RunMultiscalePipeline();

        // Update Terrain
        int rt = Mathf.Max(0, levels.Length - 1);
        UpdateTerrainFromRT(heightRTs[rt], targetTerrain, terrainSizeXZ, terrainMaxHeight);
    }

    void InitKernels()
    {
        kCopyFromTexture = erosionCS.FindKernel("CopyFromTexture");
        kUpsample2x = erosionCS.FindKernel("Upsample2x");  
        kFlowRouting = erosionCS.FindKernel("FlowRouting"); 
        kErode = erosionCS.FindKernel("ErosionStep"); 
        kThermal = erosionCS.FindKernel("ThermalStep"); 
        kDeposit = erosionCS.FindKernel("DepositStep");
        kRetarget = erosionCS.FindKernel("Retargeting");
        kBreach = erosionCS.FindKernel("Breaching");
    }

    RenderTexture CreateRT(int w, int h)
    {
        var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.RFloat, 0);
        desc.enableRandomWrite = true;
        var rt = new RenderTexture(desc);
        rt.Create();
        return rt;
    }

    void CopyTextureToRT(Texture2D src, RenderTexture dst)
    {
        erosionCS.SetTexture(kCopyFromTexture, "_InHeightTex", src);
        erosionCS.SetTexture(kCopyFromTexture, "_OutHeightRT", dst);
        DispatchFor(dst, kCopyFromTexture);
    }

    void InitRenderTextures()
    {
        int w = (int)baseResolution;
        int h = (int)baseResolution;

        for (int level = 0; level < levels.Length; level++)
        {
            int key = level;

            if (!heightRTs.ContainsKey(level))
                heightRTs[level] = CreateRT(w, h);
            if (!streamRTs.ContainsKey(level))
                streamRTs[level] = CreateRT(w, h);
            if (!sedimentRTs.ContainsKey(level))
                sedimentRTs[level] = CreateRT(w, h);
            if (!tempRTs.ContainsKey(level))
                tempRTs[level] = CreateRT(w, h);
            if (!snowRTs.ContainsKey(level))
                snowRTs[level] = CreateRT(w, h);

            w *= 2;
            h *= 2;
        }

        if (levels.Length == 0)
        {
            if (!heightRTs.ContainsKey(0))
                heightRTs[0] = CreateRT(w, h);
            if (!snowRTs.ContainsKey(0))
                snowRTs[0] = CreateRT(w, h);
        }

        CopyTextureToRT(inputHeight, heightRTs[0]);
        foreach (var kv in snowRTs)
        {
            ClearRT(kv.Value);
        }
    }

    void RunMultiscalePipeline()
    {
        if (levels.Length == 0) return;

        Debug.Log($"Starting multi-scale pipeline with {levels.Length} levels");

        for (int level = 0; level < levels.Length; level++)
        {
            Debug.Log($"--- Processing Level {level} ---");

            RenderTexture height = heightRTs[level];
            RenderTexture stream = streamRTs[level];
            RenderTexture sediment = sedimentRTs[level];
            RenderTexture temp = tempRTs[level];
            RenderTexture snow = snowRTs[level];

            var schedule = levels[level];

            ClearRT(temp);
            ClearRT(sediment);
            ClearRT(stream);

            // U: Upsample if not first level
            if (level > 0)
            {
                Debug.Log($"  Upsampling from level {level - 1}");
                Upsample2x(heightRTs[level - 1], height);
                Debug.Log(height.width);
            }

            if (level == levels.Length - 1)
            {
                EnsureFinalOrigRT(height.width, height.height);
                Graphics.CopyTexture(height, _origHeightAtFinal);
            }

            // E: Erosion steps
            Debug.Log($"  Erosion: {schedule.erosionSteps} steps");
            for (int i = 0; i < schedule.erosionSteps; i++)
            {
                FlowRoutingOnce(height, stream, snow, temp);
                Swap(ref stream, ref temp);
                ErodeOnce(height, stream, snow, temp);
                Swap(ref height, ref temp);
            }

            // T: Thermal relaxation steps
            Debug.Log($"  Thermal: {schedule.thermalSteps} steps");
            for (int i = 0; i < schedule.thermalSteps; i++)
            {
                ThermalOnce(height, snow, temp);
                Swap(ref height, ref temp);
            }

            // D: Deposition steps
            Debug.Log($"  Deposition: {schedule.depositionSteps} steps");
            for (int i = 0; i < schedule.depositionSteps; i++)
            {
                FlowRoutingOnce(height, stream, snow, temp);
                Swap(ref stream, ref temp);
                DepositOnce(height, stream, sediment, snow, temp);
                Swap(ref height, ref temp);
            }

            // Store result back
            heightRTs[level] = height;
            streamRTs[level] = stream;
            sedimentRTs[level] = sediment;
            tempRTs[level] = temp;
            snowRTs[level] = snow;
        }

        if (levels.Length > 0)
        {
            // fetch final-level RTs
            RenderTexture finalHeight = heightRTs[levels.Length - 1];
            RenderTexture finalTemp = tempRTs[levels.Length - 1];
            RenderTexture finalStream = streamRTs[levels.Length - 1];
            RenderTexture finalSnow = snowRTs[levels.Length - 1];

            FlowRoutingOnce(finalHeight, finalStream, finalSnow, finalTemp);
            Swap(ref finalStream, ref finalTemp);

            // (B) Multi-scale partial breaching (coarse->fine)
            Debug.Log("Breaching to remove pits");
            MultiBreaching(finalHeight, finalTemp);
            Swap(ref finalHeight, ref finalTemp);

            // (R) Diffusion-based retargeting (paper math, uses pristine original snapshot)
            Debug.Log("Retargeting to preserve ridges");
            DiffuseRetarget(finalHeight, _origHeightAtFinal, finalStream, finalTemp);
            Swap(ref finalHeight, ref finalTemp);

            // store back
            heightRTs[levels.Length - 1] = finalHeight;
            tempRTs[levels.Length - 1] = finalTemp;
        }

        Debug.Log("Multi-scale pipeline complete");
    }

    void ClearRT(RenderTexture rt)
    {
        SetCommonUniforms(rt);
        erosionCS.SetTexture(kClear, "_RTToClear", rt);
        DispatchFor(rt, kClear);
    }

    void Upsample2x(RenderTexture src, RenderTexture dst)
    {
        SetCommonUniforms(src);
        erosionCS.SetTexture(kUpsample2x, "_InHeightRT", src);
        erosionCS.SetTexture(kUpsample2x, "_OutHeightRT", dst);
        DispatchFor(dst, kUpsample2x);
    }

    void FlowRoutingOnce(RenderTexture height, RenderTexture stream, RenderTexture snow, RenderTexture dst)
    {
        SetCommonUniforms(height);
        SetErosionUniforms();
        SetMaterialUniforms(kFlowRouting);

        erosionCS.SetTexture(kFlowRouting, "_InHeightRT", height);
        erosionCS.SetTexture(kFlowRouting, "_StreamRT", stream);
        erosionCS.SetTexture(kFlowRouting, "_OutStreamRT", dst);
        erosionCS.SetTexture(kFlowRouting, "_SnowRT", snow);
        DispatchFor(height, kFlowRouting);
    }

    void ErodeOnce(RenderTexture height, RenderTexture stream, RenderTexture snow, RenderTexture dst)
    {
        SetCommonUniforms(height);
        SetErosionUniforms();
        SetMaterialUniforms(kErode);

        erosionCS.SetTexture(kErode, "_InHeightRT", height);
        erosionCS.SetTexture(kErode, "_StreamRT", stream);
        erosionCS.SetTexture(kErode, "_OutHeightRT", dst);
        erosionCS.SetTexture(kErode, "_SnowRT", snow);
        DispatchFor(height, kErode);
    }

    void ThermalOnce(RenderTexture height, RenderTexture snow, RenderTexture dst)
    {
        SetCommonUniforms(height);
        SetThermalUniforms();
        SetMaterialUniforms(kThermal);

        erosionCS.SetTexture(kThermal, "_InHeightRT", height);
        erosionCS.SetTexture(kThermal, "_TempRT", dst);
        erosionCS.SetTexture(kThermal, "_SnowRT", snow);
        DispatchFor(height, kThermal);
    }

    void DepositOnce(RenderTexture height, RenderTexture stream, RenderTexture sediment, RenderTexture snow, RenderTexture dst)
    {
        SetCommonUniforms(height);
        SetDepositionUniforms();
        SetMaterialUniforms(kDeposit);

        erosionCS.SetTexture(kDeposit, "_InHeightRT", height);
        erosionCS.SetTexture(kDeposit, "_StreamRT", stream);
        erosionCS.SetTexture(kDeposit, "_SedimentRT", sediment);
        erosionCS.SetTexture(kDeposit, "_OutHeightRT", dst);
        erosionCS.SetTexture(kDeposit, "_SnowRT", snow);
        DispatchFor(height, kDeposit);
    }

    void DiffuseRetarget(RenderTexture height, RenderTexture origAtThisLevel, RenderTexture stream, RenderTexture dst)
    {
        SetCommonUniforms(height);
        erosionCS.SetTexture(kRetarget, "_InHeightRT", height);
        erosionCS.SetTexture(kRetarget, "_OrigHeightRT", origAtThisLevel);
        erosionCS.SetTexture(kRetarget, "_StreamRT", stream);
        erosionCS.SetTexture(kRetarget, "_TempRT", dst);

        erosionCS.SetFloat("_A0", 2.0f);         // ridge/peak threshold
        erosionCS.SetInt("_RetargetIters", 300);
        erosionCS.SetFloat("_DiffusionLambda", 0.25f);

        DispatchFor(height, kRetarget);
    }

    void MultiBreaching(RenderTexture height, RenderTexture dst)
    {
        int[] radii = new[] { 8, 4, 2, 1 };

        SetCommonUniforms(height);
        erosionCS.SetInt("_BreachIters", 12);
        erosionCS.SetFloat("_CarveEps", 1e-3f);

        foreach (int r in radii)
        {
            erosionCS.SetInt("_Radius", r);

            // Pass 0: horizontal blur Δ -> dst
            erosionCS.SetInt("_BlurPass", 0);
            erosionCS.SetTexture(kBreach, "_InHeightRT", height);
            erosionCS.SetTexture(kBreach, "_TempRT", dst);
            DispatchFor(height, kBreach);

            // Pass 1: vertical blur Δ -> dst
            erosionCS.SetInt("_BlurPass", 1);
            erosionCS.SetTexture(kBreach, "_InHeightRT", height);
            erosionCS.SetTexture(kBreach, "_TempRT", dst);
            DispatchFor(height, kBreach);

            // Pass 2: apply T' = T - Δ_blur -> dst
            erosionCS.SetInt("_BlurPass", 2);
            erosionCS.SetTexture(kBreach, "_InHeightRT", height);
            erosionCS.SetTexture(kBreach, "_TempRT", dst);
            DispatchFor(height, kBreach);

            Swap(ref height, ref dst);
        }

        heightRTs[levels.Length - 1] = height;
        tempRTs[levels.Length - 1] = dst;
    }

    void SetCommonUniforms(RenderTexture rt)
    {
        erosionCS.SetInt("_Width", rt.width);
        erosionCS.SetInt("_Height", rt.height);
        erosionCS.SetFloat("_CellSize", 1.0f);
        erosionCS.SetFloat("_MaxHeight", terrainMaxHeight);
    }

    void SetErosionUniforms()
    {
        erosionCS.SetFloat("_ErosionDT", erosionDT);
        erosionCS.SetFloat("_FlowP", flowP);
        erosionCS.SetFloat("_RainIntensity", rainIntensity);
    }

    void SetThermalUniforms()
    {
        erosionCS.SetFloat("_Talus", talus);
        erosionCS.SetFloat("_ThermalDT", thermalDT);
    }

    void SetDepositionUniforms()
    {
        erosionCS.SetFloat("_DepositionStrength", depositionStrength);
        erosionCS.SetFloat("_RainIntensity", rainIntensity);
        erosionCS.SetFloat("_FlowP", flowP);
    }

    void SetMaterialUniforms(int kernel)
    {
        Texture2D mat = materialMaps;

        if (mat != null)
        {
            erosionCS.SetTexture(kernel, "_MaterialTex", mat);
        }

        erosionCS.SetVector("_Mat_rain", new Vector4(materialLUT.rain.x, materialLUT.rain.y, materialLUT.rain.z, 0f));
        erosionCS.SetVector("_Mat_n", new Vector4(materialLUT.n.x, materialLUT.n.y, materialLUT.n.z, 0f));
        erosionCS.SetVector("_Mat_m", new Vector4(materialLUT.m.x, materialLUT.m.y, materialLUT.m.z, 0f));
        erosionCS.SetVector("_Mat_kE", new Vector4(materialLUT.kE.x, materialLUT.kE.y, materialLUT.kE.z, 0f));
        erosionCS.SetVector("_Mat_talusDeg", new Vector4(materialLUT.talusDeg.x, materialLUT.talusDeg.y, materialLUT.talusDeg.z, 0f));
        erosionCS.SetVector("_Mat_kC", new Vector4(materialLUT.kC.x, materialLUT.kC.y, materialLUT.kC.z, 0f));
        erosionCS.SetVector("_Mat_kD", new Vector4(materialLUT.kD.x, materialLUT.kD.y, materialLUT.kD.z, 0f));
    }

    void DispatchFor(RenderTexture rt, int kernel)
    {
        int gx = (rt.width + 7) / 8;
        int gy = (rt.height + 7) / 8;
        erosionCS.Dispatch(kernel, gx, gy, 1);
    }

    static void Swap(ref RenderTexture a, ref RenderTexture b)
    {
        var t = a; a = b; b = t;
    }

    void UpdateTerrainFromRT(RenderTexture srcRT, Terrain terrain, float sizeXZ, float maxHeight)
    {
        var td = terrain.terrainData;

        int terrainRes = Mathf.ClosestPowerOfTwo(Mathf.Min(srcRT.width, srcRT.height)) + 1;
        td.heightmapResolution = terrainRes;
        td.size = new Vector3(sizeXZ, maxHeight, sizeXZ);

        AsyncGPUReadback.Request(srcRT, 0, (req) =>
        {
            if (req.hasError) { Debug.LogError("GPU readback failed"); return; }

            var data = req.GetData<float>(); 
            int w = srcRT.width, h = srcRT.height;

            var heights = new float[terrainRes, terrainRes];
            for (int y = 0; y < terrainRes; y++)
            {
                float v = (float)y / (terrainRes - 1);
                float srcY = v * (h - 1);
                int y0 = Mathf.FloorToInt(srcY);
                int y1 = Mathf.Min(y0 + 1, h - 1);
                float ty = srcY - y0;

                for (int x = 0; x < terrainRes; x++)
                {
                    float u = (float)x / (terrainRes - 1);
                    float srcX = u * (w - 1);
                    int x0 = Mathf.FloorToInt(srcX);
                    int x1 = Mathf.Min(x0 + 1, w - 1);
                    float tx = srcX - x0;

                    float h00 = data[y0 * w + x0];
                    float h10 = data[y0 * w + x1];
                    float h01 = data[y1 * w + x0];
                    float h11 = data[y1 * w + x1];

                    float hx0 = Mathf.Lerp(h00, h10, tx);
                    float hx1 = Mathf.Lerp(h01, h11, tx);
                    float hxy = Mathf.Lerp(hx0, hx1, ty);

                    heights[y, x] = Mathf.Clamp01(hxy);
                }
            }

            td.SetHeights(0, 0, heights);
            Debug.Log($"Terrain updated: RT {inputHeight.width}x{inputHeight.height} -> heights {terrainRes}x{terrainRes}");

            SaveHeightPNGFromData(data, w, h, "final_height.png");
        });
    }

    void SaveHeightPNGFromData(NativeArray<float> data, int width, int height, string fileName)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float h = data[y * width + x];
                h = Mathf.Clamp01(h);            
                Color c = new Color(h, h, h, 1); 
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();

        byte[] bytes = tex.EncodeToPNG();
        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllBytes(path, bytes);

        Debug.Log($"Saved height PNG to: {path}");
        Destroy(tex);
    }

    void PrintRTValues(RenderTexture rt, string name = "RT", int sampleStep = 32)
    {
        if (rt == null)
        {
            Debug.LogError($"[RTDebug] {name} is null.");
            return;
        }

        AsyncGPUReadback.Request(rt, 0, TextureFormat.RFloat, req =>
        {
            if (req.hasError)
            {
                Debug.LogError($"[RTDebug] GPU readback failed for {name}");
                return;
            }

            var data = req.GetData<float>();
            int w = rt.width;
            int h = rt.height;

            float minH = float.MaxValue;
            float maxH = float.MinValue;
            double sum = 0.0;

            int count = w * h;
            for (int i = 0; i < count; i++)
            {
                float v = data[i];
                if (v < minH) minH = v;
                if (v > maxH) maxH = v;
                sum += v;
            }
            float avg = (float)(sum / count);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[RTDebug] {name} ({w}x{h})  min={minH:F4}, max={maxH:F4}, avg={avg:F4}");
            sb.AppendLine("Sampled values:");
            for (int y = 0; y < h; y += sampleStep)
            {
                sb.Append($"Row {y:D3}: ");
                for (int x = 0; x < w; x += sampleStep)
                {
                    float v = data[y * w + x];
                    sb.Append($"{v:F3} ");
                }
                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        });
    }
}