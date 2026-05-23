using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Addons;
using Unity.Collections;
using UnityEngine;

// glTFast import add-on that overrides the default JPEG/PNG image loading, so models loaded at
// runtime (ObjectMode's useRuntimeLoad path) get their textures created through our pipeline —
// later, Spark-encoded on device.
//
// We implement IDefaultImageFormatLoader (not plain ITextureImageLoader): it's glTFast's hook for
// *overriding the core PNG/JPEG decode*, selected per image via IsAbleToLoad(ImageFormat). Plain
// ITextureImageLoader is for extension formats (WebP/KTX) that redirect the image source — not us.
//
// Registration is global (RuntimeInitializeOnLoadMethod), so it applies to every runtime
// GltfImport.Load. The editor ScriptedImporter doesn't run RuntimeInitializeOnLoad callbacks, so
// edit-time glTF imports are unaffected.
//
// STEP 4 (this version): decode the JPEG/PNG, then Spark-encode it on device. Colorspace comes
// from glTFast's per-slot `linear` flag (srgb = !linear). The channel format is fixed RGB for now;
// once gltFast is extended to thread per-texture channel usage (step 3) the addon will pick
// RG/R/RGBA per slot. Fixed RGB is safe for the demo's models: their textures are opaque, and
// normal maps still render correctly because the shader reconstructs Z from X,Y regardless of B.
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

    public Task<ImageResult> LoadImage(
        NativeArray<byte>.ReadOnly data,
        bool linear,
        bool readable,
        bool generateMipMaps,
        CancellationToken cancellationToken)
    {
        // Decode the compressed bytes into a temporary GPU texture for Spark to sample. `linear`
        // (from the texture's slot usage) sets the colorspace; LoadImage uploads + mips it.
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, generateMipMaps, linear);
        decoded.LoadImage(data.ToArray(), markNonReadable: true);
        Debug.Log($"[SparkAddon] LoadImage: decoded {data.Length}B → {decoded.width}x{decoded.height} " +
                  $"{decoded.graphicsFormat}; Spark-encoding (linear={linear}, srgb={!linear}, mips={generateMipMaps})…");

        // Spark-encode on device. Fixed RGB until gltFast threads per-texture channel usage.
        var compressed = Spark.EncodeTexture(decoded, SparkFormat.RGB, srgb: !linear, mips: generateMipMaps);

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
}
