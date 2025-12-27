```

BenchmarkDotNet v0.15.8, Linux Arch Linux
AMD Ryzen 9 5950X 1.75GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 42.42.42.42424), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.0 (10.0.0, 42.42.42.42424), X64 RyuJIT x86-64-v3

InvocationCount=1  UnrollFactor=1  

```
| Method                                              | Count   | MaxLen | AccessPercent | Mean      | Error    | StdDev   | Median    | Gen0       | Gen1       | Gen2      | Allocated |
|---------------------------------------------------- |-------- |------- |-------------- |----------:|---------:|---------:|----------:|-----------:|-----------:|----------:|----------:|
| **Serialize_PackedPool_CustomBinary_Prealloc**          | **5000000** | **50**     | **1**             |  **15.71 ms** | **0.298 ms** | **0.769 ms** |  **15.70 ms** |          **-** |          **-** |         **-** | **140.65 MB** |
| Serialize_PackedPool_MemoryPack                     | 5000000 | 50     | 1             |  66.36 ms | 0.163 ms | 0.144 ms |  66.32 ms |          - |          - |         - | 140.65 MB |
| Serialize_StringArray_CustomBinary_Prealloc         | 5000000 | 50     | 1             | 216.96 ms | 1.632 ms | 1.527 ms | 216.20 ms |          - |          - |         - | 140.65 MB |
| Serialize_StringArrayContainer_MemoryPack           | 5000000 | 50     | 1             | 129.69 ms | 0.745 ms | 0.582 ms | 129.72 ms |          - |          - |         - | 159.72 MB |
| Deserialize_PackedPool_CustomBinary                 | 5000000 | 50     | 1             |  21.59 ms | 0.412 ms | 0.536 ms |  21.62 ms |          - |          - |         - | 140.65 MB |
| Deserialize_PackedPool_MemoryPack                   | 5000000 | 50     | 1             |  11.51 ms | 0.924 ms | 2.725 ms |  12.94 ms |          - |          - |         - | 140.65 MB |
| Deserialize_StringArray_CustomBinary                | 5000000 | 50     | 1             | 385.11 ms | 1.288 ms | 1.205 ms | 385.08 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_StringArrayContainer_MemoryPack         | 5000000 | 50     | 1             | 359.11 ms | 2.396 ms | 2.241 ms | 358.37 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_PackedPool_Custom_ThenDecodeSample      | 5000000 | 50     | 1             |  26.87 ms | 0.356 ms | 0.297 ms |  26.92 ms |          - |          - |         - | 144.27 MB |
| Deserialize_PackedPool_MemoryPack_ThenDecodeSample  | 5000000 | 50     | 1             |  19.83 ms | 0.872 ms | 2.557 ms |  20.80 ms |          - |          - |         - | 144.27 MB |
| Deserialize_StringArray_CustomBinary_ThenReadSample | 5000000 | 50     | 1             | 409.14 ms | 6.330 ms | 5.286 ms | 410.02 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_StringArray_MemoryPack_ThenReadSample   | 5000000 | 50     | 1             | 379.23 ms | 3.987 ms | 3.729 ms | 378.24 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| **Serialize_PackedPool_CustomBinary_Prealloc**          | **5000000** | **50**     | **5**             |  **16.11 ms** | **0.317 ms** | **0.919 ms** |  **16.01 ms** |          **-** |          **-** |         **-** | **140.65 MB** |
| Serialize_PackedPool_MemoryPack                     | 5000000 | 50     | 5             |  66.32 ms | 0.445 ms | 0.347 ms |  66.31 ms |          - |          - |         - | 140.65 MB |
| Serialize_StringArray_CustomBinary_Prealloc         | 5000000 | 50     | 5             | 217.13 ms | 0.851 ms | 0.664 ms | 216.99 ms |          - |          - |         - | 140.65 MB |
| Serialize_StringArrayContainer_MemoryPack           | 5000000 | 50     | 5             | 129.64 ms | 0.574 ms | 0.480 ms | 129.56 ms |          - |          - |         - | 159.72 MB |
| Deserialize_PackedPool_CustomBinary                 | 5000000 | 50     | 5             |  21.24 ms | 0.421 ms | 0.517 ms |  21.28 ms |          - |          - |         - | 140.65 MB |
| Deserialize_PackedPool_MemoryPack                   | 5000000 | 50     | 5             |  11.61 ms | 0.922 ms | 2.720 ms |  13.16 ms |          - |          - |         - | 140.65 MB |
| Deserialize_StringArray_CustomBinary                | 5000000 | 50     | 5             | 403.47 ms | 7.360 ms | 6.524 ms | 405.46 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_StringArrayContainer_MemoryPack         | 5000000 | 50     | 5             | 380.96 ms | 4.221 ms | 3.949 ms | 380.87 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_PackedPool_Custom_ThenDecodeSample      | 5000000 | 50     | 5             |  54.27 ms | 0.722 ms | 0.675 ms |  54.17 ms |  1000.0000 |          - |         - | 158.75 MB |
| Deserialize_PackedPool_MemoryPack_ThenDecodeSample  | 5000000 | 50     | 5             |  45.47 ms | 0.909 ms | 1.639 ms |  45.18 ms |  1000.0000 |          - |         - | 158.75 MB |
| Deserialize_StringArray_CustomBinary_ThenReadSample | 5000000 | 50     | 5             | 412.78 ms | 2.980 ms | 2.788 ms | 411.72 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_StringArray_MemoryPack_ThenReadSample   | 5000000 | 50     | 5             | 390.57 ms | 5.762 ms | 4.811 ms | 389.45 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| **Serialize_PackedPool_CustomBinary_Prealloc**          | **5000000** | **50**     | **10**            |  **17.25 ms** | **0.340 ms** | **0.577 ms** |  **17.15 ms** |          **-** |          **-** |         **-** | **140.65 MB** |
| Serialize_PackedPool_MemoryPack                     | 5000000 | 50     | 10            |  69.05 ms | 0.711 ms | 0.665 ms |  69.01 ms |          - |          - |         - | 140.65 MB |
| Serialize_StringArray_CustomBinary_Prealloc         | 5000000 | 50     | 10            | 223.58 ms | 1.419 ms | 1.258 ms | 223.44 ms |          - |          - |         - | 140.65 MB |
| Serialize_StringArrayContainer_MemoryPack           | 5000000 | 50     | 10            | 133.69 ms | 2.021 ms | 1.578 ms | 133.36 ms |          - |          - |         - | 159.72 MB |
| Deserialize_PackedPool_CustomBinary                 | 5000000 | 50     | 10            |  22.16 ms | 0.430 ms | 0.742 ms |  21.85 ms |          - |          - |         - | 140.65 MB |
| Deserialize_PackedPool_MemoryPack                   | 5000000 | 50     | 10            |  12.59 ms | 0.804 ms | 2.159 ms |  13.47 ms |          - |          - |         - | 140.65 MB |
| Deserialize_StringArray_CustomBinary                | 5000000 | 50     | 10            | 410.07 ms | 8.070 ms | 7.926 ms | 410.39 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_StringArrayContainer_MemoryPack         | 5000000 | 50     | 10            | 394.85 ms | 7.372 ms | 6.156 ms | 394.95 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_PackedPool_Custom_ThenDecodeSample      | 5000000 | 50     | 10            |  85.19 ms | 1.703 ms | 1.822 ms |  85.40 ms |  2000.0000 |          - |         - | 176.87 MB |
| Deserialize_PackedPool_MemoryPack_ThenDecodeSample  | 5000000 | 50     | 10            |  74.84 ms | 1.374 ms | 1.218 ms |  74.85 ms |  2000.0000 |          - |         - | 176.87 MB |
| Deserialize_StringArray_CustomBinary_ThenReadSample | 5000000 | 50     | 10            | 420.38 ms | 2.376 ms | 1.984 ms | 420.28 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
| Deserialize_StringArray_MemoryPack_ThenReadSample   | 5000000 | 50     | 10            | 402.73 ms | 7.868 ms | 8.079 ms | 402.19 ms | 23000.0000 | 22000.0000 | 1000.0000 | 400.51 MB |
