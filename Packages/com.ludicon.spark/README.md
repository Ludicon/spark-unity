# Spark

GPU texture compression for Unity using [Spark](https://github.com/ludicon/spark) compute shaders.

## Installation

Add the package to your project via the Unity Package Manager:

* **Package Manager → Add package from git URL…** and enter:

  ```
  https://github.com/ludicon/spark-unity.git?path=/Packages/com.ludicon.spark
  ```

* or add it directly to your project's `Packages/manifest.json`:

  ```json
  "com.ludicon.spark": "https://github.com/ludicon/spark-unity.git?path=/Packages/com.ludicon.spark"
  ```

## Usage

```csharp
Texture2D source = ...;
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB);
```

See `SparkDemo.cs` in the repository for a complete example.

## Supported formats

| Channels | BC Format | Mobile Format | BPP |
|----------|-----------|---------------|-----|
| RGB      | BC1_RGB   | ETC2_RGB      | 4   |
| R        | BC4_R     | EAC_R         | 4   |
| RG       | BC5_RG    | EAC_RG        | 8   |
| RGB      | BC7_RGB   | ASTC_4x4_RGB  | 8   |
| RGBA     | BC7_RGBA  | ASTC_4x4_RGBA | 8   |

Generic formats (`SparkFormat.R`, `RG`, `RGB`, `RGBA`) auto-resolve to the best format supported on the current GPU.

## Requirements

* Unity 2022.3 or later
* A GPU that supports compute shaders

## License

The C# runtime is released under the MIT license. The bundled Spark encoder shaders are covered by the Spark EULA — see [LICENSE](https://github.com/ludicon/spark-unity/blob/main/Packages/com.ludicon.spark/LICENSE.md).
