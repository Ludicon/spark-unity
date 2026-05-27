using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Addons;
using Unity.Collections;
using UnityEngine;

// glTFast import add-on that overrides the default JPEG/PNG image loading, so models loaded at
// runtime get their textures created through our pipeline
//
// We implement IDefaultImageFormatLoader (not plain ITextureImageLoader): it's glTFast's hook for
// *overriding the core PNG/JPEG decode*, selected per image via IsAbleToLoad(ImageFormat). Plain
// ITextureImageLoader is for extension formats (WebP/KTX) that redirect the image source.
//
// Registration is global (RuntimeInitializeOnLoadMethod), so it applies to every runtime
// GltfImport.Load. The editor ScriptedImporter doesn't run RuntimeInitializeOnLoad callbacks, so
// edit-time glTF imports are unaffected.
static class SparkGltfastAddon
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register()
    {
        ImportAddonRegistry.RegisterImportAddon(new SparkImportAddon());
        Debug.Log("[SparkAddon] Registered glTFast image-loader add-on");
    }
}

class SparkImportAddon : ImportAddon<SparkImportAddonInstance> { }

class SparkImportAddonInstance : ImportAddonInstance, IDefaultImageFormatLoader
{
    public override bool SupportsGltfExtension(string extensionName) => false;

    public override void Inject(GltfImportBase gltfImport)
    {
        gltfImport.AddImportAddonInstance(this);
        Debug.Log("[SparkAddon] Injected into a GltfImport");
    }

    public override void Inject(IInstantiator instantiator) { }
    public override void Dispose() { }

    // Claim the core glTF image formats so JPEG/PNG route through us instead of glTFast's built-in
    // ImageConversion loader. glTFast uses two different selection paths:
    //  • External-URI images (.gltf with separate .jpg files): selected via IsAbleToLoad(ImageFormat),
    //    with the format detected from the file extension / mimeType.
    //  • Embedded images (.glb, or data-URIs): selected via IsAbleToLoad(ReadOnlySpan<byte>), by
    //    sniffing the raw bytes — IsAbleToLoad(ImageFormat) is NOT consulted there.
    // We implement both so the add-on works regardless of how the model packs its textures.
    // (IsAbleToLoad(TextureBase) keeps the IDefaultImageFormatLoader default of false — that
    // overload is for extension formats only.)
    public bool IsAbleToLoad(ImageFormat format)
    {
        bool can = format == ImageFormat.Jpeg || format == ImageFormat.Png;
        Debug.Log($"[SparkAddon] IsAbleToLoad(format={format}) → {can}");
        return can;
    }

    public bool IsAbleToLoad(System.ReadOnlySpan<byte> data)
    {
        bool can = IsJpeg(data) || IsPng(data);
        Debug.Log($"[SparkAddon] IsAbleToLoad(data {data.Length}B) → {can}");
        return can;
    }

    static bool IsJpeg(System.ReadOnlySpan<byte> d) =>
        d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;

    static bool IsPng(System.ReadOnlySpan<byte> d) =>
        d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;

    // Required channel-unaware overload — forward to the channel-aware one with all channels.
    public Task<ImageResult> LoadImage(
        NativeArray<byte>.ReadOnly data,
        bool linear,
        bool readable,
        bool generateMipMaps,
        CancellationToken cancellationToken)
        => LoadImage(data, linear, readable, generateMipMaps, cancellationToken, 0xF);

    // Channel-aware override — picks the Spark format from glTFast's per-image channel mask.
    public Task<ImageResult> LoadImage(
        NativeArray<byte>.ReadOnly data,
        bool linear,
        bool readable,
        bool generateMipMaps,
        CancellationToken cancellationToken,
        int channelMask)
    {
        // Decode the compressed bytes into a temporary GPU texture for Spark to sample. `linear`
        // (from the texture's slot usage) sets the colorspace; LoadImage uploads + mips it.
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, generateMipMaps, linear);
        decoded.LoadImage(data.ToArray(), markNonReadable: true);

        // Channel-minimal Spark format from glTFast's per-image channel mask.
        var format = FormatForChannels(channelMask);
        Debug.Log($"[SparkAddon] LoadImage: decoded {data.Length}B → {decoded.width}x{decoded.height} " +
                  $"{decoded.graphicsFormat}; Spark-encoding {format} (linear={linear}, mask=0x{channelMask:X}, mips={generateMipMaps})…");

        var compressed = Spark.EncodeTexture(decoded, format, srgb: !linear, mips: generateMipMaps);

        if (compressed != null)
        {
            Debug.Log($"[SparkAddon] ✓ encoded → {compressed.width}x{compressed.height} {compressed.graphicsFormat}");
            UnityEngine.Object.Destroy(decoded);
            return Task.FromResult(new ImageResult(compressed));
        }

        // Encode declined (format unsupported on this GPU) — fall back to the uncompressed decode.
        Debug.LogWarning($"[SparkAddon] ✗ Spark.EncodeTexture returned null; using uncompressed {decoded.graphicsFormat}");
        return Task.FromResult(new ImageResult(decoded));
    }

    // Smallest Spark format whose contiguous channel set covers the mask's highest set channel.
    // e.g. normal R|G → RG, occlusion R → R, metallic-roughness G|B → RGB. A mask of 0 (image
    // not reached by glTFast's slot pass, e.g. an extension-only texture) falls back to RGBA
    // so we never under-allocate channels.
    static SparkFormat FormatForChannels(int mask)
    {
        if ((mask & 0x8) != 0) return SparkFormat.RGBA;   // A
        if ((mask & 0x4) != 0) return SparkFormat.RGB;    // B
        if ((mask & 0x2) != 0) return SparkFormat.RG;     // G
        if ((mask & 0x1) != 0) return SparkFormat.R;      // R
        return SparkFormat.RGBA;                          // unknown → safe superset
    }
}
