// BenchmarkPackedStringPool.csproj
// <Project Sdk="Microsoft.NET.Sdk">
//   <PropertyGroup>
//     <OutputType>Exe</OutputType>
//     <TargetFramework>net8.0</TargetFramework>
//     <ImplicitUsings>enable</ImplicitUsings>
//     <Nullable>enable</Nullable>
//     <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
//   </PropertyGroup>
//   <ItemGroup>
//     <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
//     <PackageReference Include="MemoryPack" Version="1.*" />
//   </ItemGroup>
// </Project>

using BenchmarkDotNet.Running;
using MemPackBench.FileDirIndexStateBench;

// BenchmarkRunner.Run<PoolBench>();
BenchmarkRunner.Run<FileDirIndexStateSerializationBench>();