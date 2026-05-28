using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

// Virtual texturing with real-time Spark compression.
//
// A huge logical texture (LOGICAL²) is split into PAGE² pages. Only the pages visible at
// the current zoom are kept resident, in a fixed-size compressed physical atlas. As pages
// come into view they are generated on the GPU (Mandelbrot), Spark-compressed, and copied
// into a free atlas slot; an indirection texture maps virtual page -> atlas slot so the
// display shader can sample the atlas. This is the use case Unity's own VT can't do:
// the resident cache is GPU-compressed at runtime, so it holds far more pages per MB.
public class VirtualTextureMode : SparkDemoMode
{
    public override string DisplayName => "Virtual Tex";

    public SparkFormat format = SparkFormat.RGB;   // BC7_RGB on desktop
    public int iterations = 256;

    [Tooltip("Sub-samples per axis when generating a tile (1 = off, 2 = 4×, 4 = 16×). Higher = smoother fractal, more generation cost.")]
    public int supersample = 1;

    [Tooltip("Max tiles generated + Spark-encoded per frame; the rest stream in over later frames.")]
    public int maxTilesPerFrame = 8;

    const int LOGICAL = 1048576;               // logical texture size
    const int PAGE = 128;                      // page (tile) size
    const int PAGES = LOGICAL / PAGE;          // pages per side at mip 0
    static readonly int PAGE_BITS = Mathf.RoundToInt(Mathf.Log(PAGES, 2));
    static readonly int MAX_MIP = PAGE_BITS;   // log2(PAGES)
    const int ATLAS_TILES = 32;                // atlas is ATLAS_TILES² pages
    const int ATLAS = ATLAS_TILES * PAGE;      // atlas size (4096)
    const int FINER = 2;                       // build the indirection this many mips finer than the render mip
    const int PT = 256;                        // page-table window (cells); fixed, independent of LOGICAL
    const float MIN_SCALE = 0.5f;              // texels per pixel floor -> at most 2 screen pixels per texel

    ComputeShader _gen;
    int _genKernel;
    RenderTexture _tileRGBA;                    // scratch: one generated page (uncompressed)
    Texture2D _atlas;                           // physical cache (compressed)
    Texture2D _pageTable;                       // indirection: virtual page -> atlas slot
    Color32[] _pt;
    Material _mat;
    CommandBuffer _cmd;
    SparkFormat _format;

    readonly PanZoomController _pz = new PanZoomController();

    struct Res { public int slot; public int frame; }
    readonly Dictionary<long, Res> _resident = new Dictionary<long, Res>();
    readonly Stack<int> _freeSlots = new Stack<int>();
    int _frame;

    int _mip, _residentCount, _tilesThisFrame;
    bool _showAtlas = true;

    static readonly int s_idAtlas = Shader.PropertyToID("_Atlas");
    static readonly int s_idPageTable = Shader.PropertyToID("_PageTable");
    static readonly int s_idPagesPerSide = Shader.PropertyToID("_PagesPerSide");
    static readonly int s_idAtlasTiles = Shader.PropertyToID("_AtlasTiles");
    static readonly int s_idPageSize = Shader.PropertyToID("_PageSize");
    static readonly int s_idWindow = Shader.PropertyToID("_Window");
    static readonly int s_idTile = Shader.PropertyToID("_Tile");
    static readonly int s_idSamples = Shader.PropertyToID("_Samples");
    static readonly int s_idDst = Shader.PropertyToID("_Dst");

    public override void Activate()
    {
        if (_gen == null)
        {
            _gen = Resources.Load<ComputeShader>("VTMandelbrot");
            if (_gen != null) _genKernel = _gen.FindKernel("Generate");
        }
        if (_gen == null) return;

        _format = Spark.ResolveFormat(format);
        Spark.Preload(_format);

        var compressedFmt = Spark.GetCompressedFormat(_format, srgb: false);

        _tileRGBA = new RenderTexture(PAGE, PAGE, 0, GraphicsFormat.R8G8B8A8_UNorm)
        { enableRandomWrite = true, useMipMap = false };
        _tileRGBA.Create();

        _atlas = new Texture2D(ATLAS, ATLAS, compressedFmt, mipCount: 1,
            TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate)
        { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

        _pageTable = new Texture2D(PT, PT, TextureFormat.RGBA32, mipChain: false, linear: true)
        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        _pt = new Color32[PT * PT];

        _mat = new Material(Resources.Load<Shader>("VTSample")) { hideFlags = HideFlags.HideAndDontSave };
        _mat.SetTexture(s_idAtlas, _atlas);
        _mat.SetTexture(s_idPageTable, _pageTable);
        _mat.SetFloat(s_idAtlasTiles, ATLAS_TILES);
        _mat.SetFloat(s_idPageSize, PAGE);

        _cmd = new CommandBuffer { name = "VT generate + Spark" };

        _resident.Clear();
        _freeSlots.Clear();
        for (int i = ATLAS_TILES * ATLAS_TILES - 1; i >= 0; i--) _freeSlots.Push(i);
        _frame = 0;
        _pz.Invalidate();
    }

    public override void Deactivate()
    {
        if (_tileRGBA != null) { _tileRGBA.Release(); Destroy(_tileRGBA); _tileRGBA = null; }
        if (_atlas != null) { Destroy(_atlas); _atlas = null; }
        if (_pageTable != null) { Destroy(_pageTable); _pageTable = null; }
        if (_mat != null) { Destroy(_mat); _mat = null; }
        if (_cmd != null) { _cmd.Release(); _cmd = null; }
    }

    void OnDestroy() { Deactivate(); }

    public override void OnTick()
    {
        if (_gen == null) return;
        // Cap zoom-in at MIN_SCALE (texels/pixel) so a texel never blows up past 2 screen pixels.
        if (_pz.IsInitialized && _pz.View.targetScale < MIN_SCALE) _pz.View.targetScale = MIN_SCALE;
        _pz.Tick(Time.unscaledDeltaTime, LOGICAL, LOGICAL);
        if (!_pz.IsInitialized) return;
        if (_pz.View.scale < MIN_SCALE) _pz.View.scale = MIN_SCALE;

        _pz.ComputeUV(LOGICAL, LOGICAL, out Vector2 uvOff, out Vector2 uvScale);
        Rect dr = _pz.DisplayRect;
        int rw = Mathf.Max(1, Mathf.RoundToInt(dr.width));
        int rh = Mathf.Max(1, Mathf.RoundToInt(dr.height));

        // Pick the render mip whose texels are ~1:1 with screen pixels; the indirection is built
        // FINER mips below it so each cell can resolve to a resident finer tile (zoom-out fallback).
        float texelsPerPixel = Mathf.Max(uvScale.x, 1e-9f) * LOGICAL;
        _mip = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(texelsPerPixel, 2f)), 0, MAX_MIP);
        int mid = Mathf.Max(0, _mip - FINER);
        int pmM = PAGES >> _mip;
        int pmMid = PAGES >> mid;

        float u0 = uvOff.x, u1 = uvOff.x + uvScale.x * rw;
        float v0 = uvOff.y, v1 = uvOff.y + uvScale.y * rh;
        _frame++;

        // Generation pass — record the desired render-mip tiles (budget-limited) into one
        // command buffer and execute it once below.
        _tilesThisFrame = 0;
        _cmd.Clear();
        _cmd.SetComputeIntParam(_gen, s_idSamples, Mathf.Max(1, supersample));
        int gx0 = PageIndex(Mathf.Min(u0, u1), pmM), gx1 = PageIndex(Mathf.Max(u0, u1), pmM);
        int gy0 = PageIndex(Mathf.Min(v0, v1), pmM), gy1 = PageIndex(Mathf.Max(v0, v1), pmM);
        for (int py = gy0; py <= gy1; py++)
            for (int px = gx0; px <= gx1; px++)
            {
                long key = Key(_mip, px, py);
                if (_resident.TryGetValue(key, out Res r)) { r.frame = _frame; _resident[key] = r; }
                else if (_tilesThisFrame < maxTilesPerFrame)
                {
                    int slot = AllocSlot();
                    RecordTile(pmM, px, py, slot);
                    _resident[key] = new Res { slot = slot, frame = _frame };
                    _tilesThisFrame++;
                }
            }
        if (_tilesThisFrame > 0) Graphics.ExecuteCommandBuffer(_cmd);

        // Indirection pass — for each fine cell, point at the nearest resident tile. The window is
        // a fixed PT×PT block relative to (cx0,cy0), so its size is independent of LOGICAL.
        int cx0 = PageIndex(Mathf.Min(u0, u1), pmMid), cx1 = PageIndex(Mathf.Max(u0, u1), pmMid);
        int cy0 = PageIndex(Mathf.Min(v0, v1), pmMid), cy1 = PageIndex(Mathf.Max(v0, v1), pmMid);
        cx1 = Mathf.Min(cx1, cx0 + PT - 1);
        cy1 = Mathf.Min(cy1, cy0 + PT - 1);
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                if (Resolve(mid, _mip, cx, cy, out int slot, out int k))
                    _pt[(cy - cy0) * PT + (cx - cx0)] = new Color32((byte)(slot % ATLAS_TILES), (byte)(slot / ATLAS_TILES), (byte)k, 255);
                else
                    _pt[(cy - cy0) * PT + (cx - cx0)] = default;
            }

        _pageTable.SetPixels32(_pt);
        _pageTable.Apply(false);
        _mat.SetFloat(s_idPagesPerSide, pmMid);
        _mat.SetVector(s_idWindow, new Vector4(cx0, cy0, cx1 - cx0 + 1, cy1 - cy0 + 1));
        _residentCount = _resident.Count;
    }

    static int PageIndex(float uv, int pm) => Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv) * pm), 0, pm - 1);

    // Resolve the fine cell (mid, cx, cy) to the nearest resident tile to the desired render mip:
    // exact first, then coarser ancestors, then finer descendants. k is the resident tile's mip
    // minus mid, which the shader uses to index the right sub-region.
    bool Resolve(int mid, int m, int cx, int cy, out int slot, out int k)
    {
        if (TryAt(m, mid, cx, cy, out slot, out k)) return true;
        for (int L = m + 1; L <= MAX_MIP; L++) if (TryAt(L, mid, cx, cy, out slot, out k)) return true;
        for (int L = m - 1; L >= mid; L--) if (TryAt(L, mid, cx, cy, out slot, out k)) return true;
        slot = 0; k = 0;
        return false;
    }

    // Pack (mip, px, py) into a unique page id. long keeps headroom well beyond any usable
    // LOGICAL — the real ceiling is float precision in the generator, not the key width.
    static long Key(int mip, int px, int py) => ((long)mip << (PAGE_BITS * 2)) | ((long)py << PAGE_BITS) | (uint)px;

    bool TryAt(int level, int mid, int cx, int cy, out int slot, out int k)
    {
        k = level - mid;   // mid <= level for every caller, so k >= 0
        long key = Key(level, cx >> k, cy >> k);
        if (_resident.TryGetValue(key, out Res r))
        {
            r.frame = _frame;   // keep displayed tiles (including fallbacks) alive
            _resident[key] = r;
            slot = r.slot;
            return true;
        }
        slot = 0;
        return false;
    }

    int AllocSlot()
    {
        if (_freeSlots.Count > 0) return _freeSlots.Pop();

        // Evict the least-recently-used page that isn't needed this frame.
        long evict = -1;
        int oldest = int.MaxValue;
        foreach (var kv in _resident)
            if (kv.Value.frame < _frame && kv.Value.frame < oldest) { oldest = kv.Value.frame; evict = kv.Key; }

        if (evict < 0) return 0;
        int slot = _resident[evict].slot;
        _resident.Remove(evict);
        return slot;
    }

    // Record one tile into the shared command buffer: generate it, then Spark-encode straight
    // into its atlas slot. The whole batch is executed once per frame in OnTick.
    // Note, multiple tiles must execute sequentially because they share a _tileRGBA scratch
    // texture. It may be beneficial to allocate multiple _tileRGBA textures and dispatch
    // them in parallel to avoid blocking.
    void RecordTile(int pm, int px, int py, int slot)
    {
        float pageUV = 1f / pm;
        _cmd.SetComputeVectorParam(_gen, s_idTile, new Vector4(px * pageUV, py * pageUV, pageUV, iterations));
        _cmd.SetComputeTextureParam(_gen, _genKernel, s_idDst, _tileRGBA);
        _cmd.DispatchCompute(_gen, _genKernel, PAGE / 8, PAGE / 8, 1);
        Spark.EncodeTexture(_cmd, _tileRGBA, _atlas, _format, 0, 0,
                            (slot % ATLAS_TILES) * PAGE, (slot / ATLAS_TILES) * PAGE);
    }

    public override void OnGUIBackground(Rect bounds)
    {
        if (_mat == null) return;
        _pz.Configure(bounds, Controller, LOGICAL, LOGICAL);

        if (Event.current != null && Event.current.type != EventType.Repaint) return;

        _pz.ComputeUV(LOGICAL, LOGICAL, out Vector2 uvOff, out Vector2 uvScale);
        Rect dr = _pz.DisplayRect;
        int rw = Mathf.Max(1, Mathf.RoundToInt(dr.width));
        int rh = Mathf.Max(1, Mathf.RoundToInt(dr.height));
        Rect uv = new Rect(uvOff.x, uvOff.y, uvScale.x * rw, uvScale.y * rh);

        Matrix4x4 prev = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        Graphics.DrawTexture(dr, Texture2D.whiteTexture, uv, 0, 0, 0, 0, Color.white, _mat);
        GUI.matrix = prev;
    }

    public override void OnGUIForeground(Rect bounds)
    {
        if (_gen == null)
        {
            GUI.Label(new Rect(bounds.x + 10, bounds.y + 40, bounds.width, 60),
                "VTMandelbrot.compute / VTSample.shader missing from Resources.");
            return;
        }

        long atlasVMem = GraphicsFormatUtility.ComputeMipChainSize(ATLAS, ATLAS, _atlas.graphicsFormat, 1);
        long logicalRGBA = (long)LOGICAL * LOGICAL * 4;
        var text =
            $"<b>Virtual Texture</b>  logical {LOGICAL}² ({FormatBytes(logicalRGBA)} RGBA)\n" +
            $"atlas {ATLAS}² {_atlas.format} ({FormatBytes(atlasVMem)})  mip {_mip}\n" +
            $"pages encoded this frame {_tilesThisFrame}  FPS {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0}";
        GUI.Label(new Rect(bounds.x + 8, bounds.y + 8, bounds.width - 16, 70), text, OverlayStyle());

        // Atlas thumbnail (the resident compressed cache) on the right edge.
        if (_showAtlas && _atlas != null)
        {
            float side = Mathf.Min(220f, bounds.width * 0.35f, bounds.height - 130f);
            Rect box = new Rect(bounds.xMax - side - 8f, bounds.y + 80f, side, side);
            Color pc = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(box.x - 3f, box.y - 18f, box.width + 6f, box.height + 21f), Texture2D.whiteTexture);
            GUI.color = pc;
            GUI.Label(new Rect(box.x, box.y - 17f, side, 16f), $"Atlas {ATLAS}²", OverlayStyle());
            GUI.DrawTexture(box, _atlas, ScaleMode.ScaleToFit, false);
        }

        const float btnW = 90f, btnH = 24f, gap = 8f;
        float bx = bounds.x + (bounds.width - (btnW * 2f + gap)) * 0.5f;
        float by = bounds.y + bounds.height - 32f;
        if (GUI.Button(new Rect(bx, by, btnW, btnH), "Reset View"))
            _pz.Invalidate();
        if (GUI.Button(new Rect(bx + btnW + gap, by, btnW, btnH), _showAtlas ? "Atlas: On" : "Atlas: Off"))
            _showAtlas = !_showAtlas;

        _pz.HandleInput(Controller);
    }

    static GUIStyle s_overlay;
    static GUIStyle OverlayStyle()
    {
        return s_overlay ?? (s_overlay = new GUIStyle(GUI.skin.label)
        { richText = true, fontSize = 12, normal = { textColor = Color.white } });
    }

    static string FormatBytes(long n)
    {
        if (n >= 1 << 30) return $"{n / (float)(1 << 30):F2} GB";
        if (n >= 1 << 20) return $"{n / (float)(1 << 20):F1} MB";
        if (n >= 1 << 10) return $"{n / (float)(1 << 10):F0} KB";
        return $"{n} B";
    }
}
