# Spark Unity

GPU texture compression for Unity using [Spark](https://github.com/ludicon/spark) compute shaders.

![Screenshot](spark-unity-screenshot.png)

## Supported Formats

| Channels | BC Format | Mobile Format | BPP |
|----------|-----------|---------------|-----|
| RGB      | BC1_RGB   | ETC2_RGB      | 4   |
| R        | BC4_R     | EAC_R         | 4   |
| RG       | BC5_RG    | EAC_RG        | 8   |
| RGB      | BC7_RGB   | ASTC_4x4_RGB  | 8   |
| RGBA     | BC7_RGBA  | ASTC_4x4_RGBA | 8   |

Generic formats (`SparkFormat.R`, `RG`, `RGB`, `RGBA`) auto-resolve to the best format supported on the current GPU.

## Usage

```csharp
// Encode a texture
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.BC7_RGB);

// Auto-select best format for current GPU
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB);

// With sRGB options
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB, srgb: true);

// Preload shaders to avoid first-encode hitch
Spark.Preload(SparkFormat.RGB, SparkFormat.RGBA);

// Release cached resources when done
Spark.ReleaseCache();
```

## Demo

The included `SparkDemo` scene loads PNG textures from `StreamingAssets/SparkTextures/` and provides a UI to compare original vs compressed textures across formats and quality levels.

## Requirements

- Tested on Unity 6.3 LTS (6000.3.11f1).
- Tested on Unity 6.4 (6000.4.7f1).
- Tested on Unity 6.6 (6000.6.0a5).

## License

spark-unity is free for non-commercial use.

    The C# code and Unity project files are released under MIT license.
    Use of the Spark shaders is covered under the spark-unity EULA.

See https://ludicon.com/spark-unity/#Licensing for details on how to use spark-unity in commercial projects.
