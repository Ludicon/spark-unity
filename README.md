# Spark Unity

GPU texture compression for Unity using [Spark](https://github.com/ludicon/spark) compute shaders.

![Screenshot](spark-unity-screenshot.png)

## Supported Formats

| Format | Type | BPP |
|--------|------|-----|
| BC1_RGB | Desktop | 4 |
| BC3_RGBA | Desktop | 8 |
| BC4_R | Desktop | 4 |
| BC5_RG | Desktop | 8 |
| BC7_RGB, BC7_RGBA | Desktop | 8 |
| ASTC_4x4_RGB, ASTC_4x4_RGBA | Mobile | 8 |
| ETC2_RGB | Mobile | 4 |
| ETC2_RGBA | Mobile | 8 |
| EAC_R | Mobile | 4 |
| EAC_RG | Mobile | 8 |

Generic formats (`SparkFormat.R`, `RG`, `RGB`, `RGBA`) auto-resolve to the best format supported on the current GPU.

## Usage

```csharp
// Encode a texture
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.BC7_RGB);

// Auto-select best format for current GPU
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB);

// With quality and sRGB options
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB, SparkQuality.High, srgb: true);

// Preload shaders to avoid first-encode hitch
Spark.Preload(SparkQuality.Medium, SparkFormat.RGB, SparkFormat.RGBA);

// Release cached resources when done
Spark.ReleaseCache();
```

## Demo

The included `SparkDemo` scene loads PNG textures from `StreamingAssets/SparkTextures/` and provides a UI to compare original vs compressed textures across formats and quality levels.

## Requirements

Tested on Unity 6.3 LTS (6000.3.11f1).
