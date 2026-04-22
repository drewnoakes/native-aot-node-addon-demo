# Native AOT Node.js Addon Demo

A minimal example of a Node.js native addon written in C# using [.NET Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/). The addon is compiled to a native shared library and loaded directly into Node.js via [N-API](https://nodejs.org/api/n-api.html) — no node-gyp required.

Based on the blog post: [Writing Node.js addons with .NET Native AOT](https://devblogs.microsoft.com/dotnet/writing-nodejs-addons-with-dotnet-native-aot/)

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or later)
- [Node.js](https://nodejs.org/) (v18 or later)
- A C/C++ toolchain (Native AOT requires a platform linker — e.g. Visual Studio with the "Desktop development with C++" workload on Windows, `clang` on macOS, or `gcc`/`clang` on Linux)

## Build

Publish the .NET project with Native AOT. The included MSBuild target automatically copies the output to `NativeAddon.node` in the repo root:

```
dotnet publish NativeAddon -c Release -r win-x64
```

> On Linux use `-r linux-x64`, on macOS use `-r osx-x64` (or `osx-arm64` for Apple Silicon).

## Run

```
node demo.js
```

Expected output:

```
Hello, World! Greetings from .NET Native AOT.
2 + 3 = 5
```

## How it works

1. **Native AOT** compiles the C# project into a native shared library (`.dll` / `.so` / `.dylib`).
2. The library exports `napi_register_module_v1`, the entry point that Node.js looks for when loading a `.node` addon.
3. Inside that entry point, C# functions are registered on the JavaScript `exports` object using N-API calls.
4. N-API functions are resolved from the host process (`node.exe`) at runtime using a custom `NativeLibrary.SetDllImportResolver`.
5. Node.js loads the addon via `require('./NativeAddon.node')` and calls into the C# functions directly.

## Project structure

```
├── NativeAddon/
│   ├── NativeAddon.csproj   # Project file — enables Native AOT and copies output to .node
│   └── Addon.cs             # The addon: N-API interop, marshalling helpers, exported functions
├── demo.js                  # Node.js script that loads and calls the addon
└── README.md
```
