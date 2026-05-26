# *Spark for Unity*

GPU texture compression for Unity using the [*Spark*](https://github.com/ludicon/spark) codecs.

*Spark for Unity* is a Unity package that exposes a subset of the [*Spark*](https://github.com/ludicon/spark) codecs through a simple and lightweight API. It enables the use of procedural textures and standard image formats in Unity applications, encoding them at runtime to native GPU formats like BC7, ASTC, and ETC2, using fast, high-quality GPU compute shaders.

This GitHub repository includes a Unity project with several examples:

<img src="Images~/screenshot-slideshow.png" width="49%"> <img src="Images~/screenshot-plasma.png" width="49%">

<img src="Images~/screenshot-mipmap.png" width="49%"> <img src="Images~/screenshot-gltf.png" width="49%">


## Installation

Add the package to your project via the Unity Package Manager:

* **Package Manager → Add package from git URL...** and enter:

  ```
  https://github.com/ludicon/spark-unity.git?path=/Packages/com.ludicon.spark
  ```

* or add it directly to your project's `Packages/manifest.json`:

  ```json
  "com.ludicon.spark": "https://github.com/ludicon/spark-unity.git?path=/Packages/com.ludicon.spark"
  ```

## Features

The *Spark for Unity* package supports a subset of the [*Spark*](https://github.com/ludicon/spark) codecs at a fixed quality level.

The available formats are:

| Channels | BC Format | Mobile Format | BPP |
| -------- | --------- | ------------- | --- |
| RGB      | BC1_RGB   | ETC2_RGB      | 4   |
| R        | BC4_R     | EAC_R         | 4   |
| RG       | BC5_RG    | EAC_RG        | 8   |
| RGB      | BC7_RGB   | ASTC_4x4_RGB  | 8   |
| RGBA     | BC7_RGBA  | ASTC_4x4_RGBA | 8   |

Generic formats (`SparkFormat.R`, `RG`, `RGB`, `RGBA`) auto-resolve to the best format supported on the current GPU.

## Compatibility

*Spark for Unity* has been tested on Metal (macOS, iOS), Vulkan (Android, Windows), OpenGL ES 3.1 (Android), and Direct3D (Windows). It has been tested on Unity versions 6.3 to 6.6.

The API may change, and it has not been tested thoroughly on all platforms and devices. If you encounter any issues, please report them at: https://github.com/Ludicon/spark-unity/issues

## Usage

```csharp
// Encode a texture with a specific format
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

The included `SparkDemo` scene showcases multiple use cases:

- **Slideshow**: Loads textures from `StreamingAssets/Textures/` and compresses them on the fly.
- **Plasma**: Displays a procedural plasma effect and compresses it in real-time.
- **Mipmap**: Loads a texture, generates mipmaps in the GPU and compresses them.
- **glTF**: Loads a glTF model using the glTFast importer and encodes its textures.
- **Benchmark**: Measures the compression performance of every format.

## Frequently Asked Questions

**Why not simply use Unity's [Texture2D.Compress](https://docs.unity3d.com/ScriptReference/Texture2D.Compress.html) instead of *Spark*?**

Texture2D.Compress is a built-in Unity function that compresses textures to DXT/BCn or ETC formats. It's orders of magnitude slower than Spark (from 10 to 1000x slower), produces lower quality results, and does not support BC7 and ASTC formats.

## License

*Spark for Unity* is free for non-commercial use.

- The C# code and Unity project files are released under the [MIT license](LICENSE).
- Use of the [*Spark*](https://ludicon.com/spark) shaders is covered under the [*Spark for Unity* EULA](https://ludicon.com/spark-unity/eula.html).

Stay tuned for details on how to use *Spark for Unity* in commercial projects or contact us at: spark@ludicon.com
