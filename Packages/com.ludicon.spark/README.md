# *Spark for Unity*  

GPU texture compression for Unity using the [*Spark*](https://github.com/ludicon/spark) codecs.

Encode Unity textures to native GPU formats (BC7, ASTC, ETC2, …) at load time:

```csharp
Texture2D compressed = Spark.EncodeTexture(source, SparkFormat.RGB);
```

For documentation, supported formats, examples, and the demo project, see the main repository:

https://github.com/ludicon/spark-unity

## License

*Spark for Unity* is free for non-commercial use.

- The C# code is released under the [MIT license](LICENSE.md).
- Use of the bundled [*Spark*](https://ludicon.com/spark) shaders is covered by the [*Spark for Unity* EULA](https://ludicon.com/spark-unity/eula.html).

Contact us at spark@ludicon.com.
