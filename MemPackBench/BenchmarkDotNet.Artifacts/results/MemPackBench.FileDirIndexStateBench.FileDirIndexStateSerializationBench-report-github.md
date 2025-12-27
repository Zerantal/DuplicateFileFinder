```

BenchmarkDotNet v0.15.8, Linux Arch Linux
AMD Ryzen 9 5950X 1.75GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 42.42.42.42424), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.0 (10.0.0, 42.42.42.42424), X64 RyuJIT x86-64-v3


```
| Method                                               | Mean      | Error     | StdDev    | Gen0     | Gen1     | Gen2     | Allocated |
|----------------------------------------------------- |----------:|----------:|----------:|---------:|---------:|---------:|----------:|
| &#39;Serialize V1 (Dictionary) -&gt; bytes&#39;                 | 15.242 ms | 0.2523 ms | 0.2700 ms |  93.7500 |  93.7500 |  93.7500 |  86.51 MB |
| &#39;Deserialize V1 (Dictionary) &lt;- bytes&#39;               | 37.060 ms | 0.1347 ms | 0.1260 ms | 142.8571 | 142.8571 | 142.8571 |  142.3 MB |
| &#39;Serialize V2 (KVP[]) -&gt; bytes&#39;                      | 21.931 ms | 0.4037 ms | 0.3579 ms |  93.7500 |  93.7500 |  93.7500 |   86.9 MB |
| &#39;Deserialize V2 (KVP[]) &lt;- bytes&#39;                    |  4.591 ms | 0.0900 ms | 0.1734 ms |  93.7500 |  93.7500 |  93.7500 |   82.4 MB |
| &#39;Convert V1 -&gt; V2&#39;                                   | 11.837 ms | 0.1226 ms | 0.1087 ms |  93.7500 |  93.7500 |  93.7500 |   82.4 MB |
| &#39;Convert V2 -&gt; V1&#39;                                   | 26.453 ms | 0.5279 ms | 1.0296 ms | 125.0000 | 125.0000 | 125.0000 |  142.3 MB |
| &#39;V1 -&gt; V2 -&gt; disk (convert + serialize V2 + write)&#39;  | 66.150 ms | 0.7931 ms | 0.7031 ms |        - |        - |        - |  164.8 MB |
| &#39;disk -&gt; V2 -&gt; V1 (read + deserialize V2 + convert)&#39; | 48.048 ms | 0.8765 ms | 0.8199 ms | 200.0000 | 200.0000 | 200.0000 | 307.09 MB |
