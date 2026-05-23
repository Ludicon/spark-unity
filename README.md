# Spark Unity

GPU texture compression for Unity using [*Spark*](https://github.com/ludicon/spark) compute shaders.

Real-time texture compression library for Unity.

[*spark-unity*](https://ludicon.com/spark-unity) is a Unity package that exposes a subset of the [*Spark*](https://ludicon.com/spark) codecs through a simple and lightweight API.

It enables the use of standard image formats in WebGL and  WebGPU applications transcoding them at load-time to native GPU formats  like BC7, ASTC, and ETC2, using fast, high-quality GPU encoders.

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

The *spark-unity* package supports a subset of the [*Spark*](https://github.com/ludicon/spark) codecs at a fixed quality level.

The available formats are:

| Channels | BC Format | Mobile Format | Bytes Per Block |
| -------- | --------- | ------------- | --------------- |
| RGB      | BC1_RGB   | ETC2_RGB      | 4               |
| R        | BC4_R     | EAC_R         | 4               |
| RG       | BC5_RG    | EAC_RG        | 8               |
| RGB      | BC7_RGB   | ASTC_4x4_RGB  | 8               |
| RGBA     | BC7_RGBA  | ASTC_4x4_RGBA | 8               |

Generic formats (`SparkFormat.R`, `RG`, `RGB`, `RGBA`) auto-resolve to the best format supported on the current GPU.


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
- **Benchmark**: Measures the compression performance of every format.

## Requirements

- Tested on Unity 6.3 LTS (6000.3.11f1).
- Tested on Unity 6.4 (6000.4.7f1).
- Tested on Unity 6.6 (6000.6.0a5).

## License

[*spark-unity*](https://ludicon.com/spark-unity) is free for non-commercial use.

- The C# code and Unity project files are released under the [MIT license](LICENSE).
- Use of the [*Spark*](https://ludicon.com/spark) shaders is covered under the [*spark-unity* EULA](https://ludicon.com/spark-unity/eula.html).

See https://ludicon.com/spark-unity/#Licensing for details on how to use [*spark-unity*](https://ludicon.com/spark-unity) in commercial projects.
