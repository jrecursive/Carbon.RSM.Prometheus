# Rust / Carbon Build Dependencies

This file explains what is required for the "set up the missing Rust/Carbon dependency folders and retry the build" path.

## What #2 Actually Means

This repo does not build from source with NuGet alone. It expects local, repo-adjacent game/server assemblies to exist on disk.

The key project setting is:

```xml
<AssemblySearchPaths>..\..\deps\$(GamePlatform);$(AssemblySearchPaths);..\..\carbon</AssemblySearchPaths>
```

That means the compiler searches for referenced assemblies in:

- `deps/linux` or `deps/windows`, depending on the selected build configuration
- the normal MSBuild/SDK search paths
- `carbon` at the repo root

If those folders are missing, restore/build will fail even if the .NET SDK itself is installed correctly.

## Minimum Folder Shape

From the repo root, a real build wants this shape:

```text
Carbon.RSM/
  carbon/
    <Carbon-managed DLLs go here>
  deps/
    linux/
      <RustDedicated Linux managed DLLs, publicized where needed>
    windows/
      <RustDedicated Windows managed DLLs, publicized where needed>
  raw-deps/
    linux/
      RustDedicated_Data/Managed/*.dll
    windows/
      RustDedicated_Data/Managed/*.dll
```

`raw-deps/` is intermediate input.

`deps/` is the build-time output the project actually resolves against.

## What Goes In `deps/linux`

`deps/linux` is built from a matching Linux Rust dedicated server install.

The repo already includes scripts for this:

- `scripts/SteamDownloader.ps1`
- `scripts/unprivate-dependencies.ps1`
- `update-lin-dependencies.bat`
- `update-lin-staging.bat`

### What the scripts do

`scripts/SteamDownloader.ps1`:

- requires PowerShell 6+
- downloads `RustDedicated_Data/Managed/*.dll` for Steam app `258550`
- uses `DepotDownloader.dll`, launched through `dotnet`

`scripts/unprivate-dependencies.ps1`:

- copies the raw managed DLL set into `deps/linux`
- runs `scripts/AssemblyPublicizer/AssemblyPublicizer.exe` against assemblies that need public/internal members exposed
- uses `mono` on Linux to run the publicizer exe

### Which assemblies get publicized

The publicizer script marks these inputs as needing publicizing:

- any DLL whose name contains `Apex`
- any DLL whose name contains `Assembly-CSharp`
- any DLL whose name contains `Facepunch`
- any DLL whose name contains `Rust`
- `NewAssembly.dll`

Everything else in the raw managed directory is copied as-is, except files whose names start with `System.` are skipped by the helper script.

## What Goes In `carbon/`

`carbon/` should contain the Carbon-managed assemblies used to compile against the same Carbon/runtime version you expect to run on.

The project does not point at a nested Carbon server directory. It points directly at repo-root `carbon/`.

That means the simplest working approach is:

1. take the Carbon-managed DLLs from a real Carbon server install or Carbon build output that matches your target environment
2. copy or symlink those DLLs directly into this repo's `carbon/` directory

Do not leave them buried under another subdirectory if you want MSBuild to find them through `AssemblySearchPaths`.

## Why Carbon Files Are Needed Even Though There Is A `PackageReference`

This project does reference `Carbon.Community` through NuGet:

```xml
<PackageReference Include="Carbon.Community" Version="2.0.*" />
```

But that is not the whole story.

The source also depends on runtime-side Carbon APIs and Harmony/module loader types, and the project explicitly adds `carbon/` to `AssemblySearchPaths`, which is a strong sign that the local Carbon assembly set is expected to participate in reference resolution.

In practice:

- NuGet restore still needs to succeed for `Carbon.Community`
- local Carbon DLLs may still be required to satisfy the full compile-time reference graph

## Required Tools For Populating These Folders

For Linux, the practical prerequisite set is:

- working `.NET` SDK/runtime
- `mono`
- `pwsh` (PowerShell 6+)
- network access to download Rust managed assemblies
- access to a Carbon assembly set that matches your target runtime

The repo-local helper scripts specifically assume:

- `pwsh` can run `scripts/SteamDownloader.ps1`
- `dotnet` can launch `DepotDownloader.dll`
- `mono` can launch `scripts/AssemblyPublicizer/AssemblyPublicizer.exe`

This repo now also includes:

- `scripts/install-dotnet-ubuntu.sh`
- `scripts/install-pwsh-ubuntu.sh`
- `scripts/populate-local-rust-carbon-deps.sh`

## Using A Real Local Server Install

Yes, you can use a real local Rust dedicated server install for this repo.

You do not need to duplicate that install into the repository. A script can copy
from:

- the real install path directly
- or a symlink that points to the real install

The new helper script does exactly that:

```bash
scripts/populate-local-rust-carbon-deps.sh --server-root /path/to/rust-server
```

If the target install is a Carbon server and it has `carbon/managed`, the script
will also copy those Carbon DLLs into repo-root `carbon/`.

If Carbon lives somewhere else, you can point at it explicitly:

```bash
scripts/populate-local-rust-carbon-deps.sh \
  --server-root /path/to/rust-server \
  --carbon-managed-dir /path/to/rust-server/carbon/managed
```

What it does:

- copies `RustDedicated_Data/Managed/*.dll` into `raw-deps/linux/RustDedicated_Data/Managed`
- publicizes the required assemblies into `deps/linux`
- copies Carbon managed DLLs into repo-root `carbon/` when available

## Exact Commands To Populate Linux Rust Deps

Run these from the repo root:

```bash
pwsh scripts/SteamDownloader.ps1 -steam_appid 258550 -platform linux -deps_dir "../raw-deps"
pwsh scripts/unprivate-dependencies.ps1 -outputPath "deps/linux/" -inputPath "raw-deps/linux/RustDedicated_Data/Managed"
```

If you are targeting the staging branch of Rust instead of public/release:

```bash
pwsh scripts/SteamDownloader.ps1 -steam_appid 258550 -steam_branch staging -platform linux -deps_dir "../raw-deps"
pwsh scripts/unprivate-dependencies.ps1 -outputPath "deps/linux/" -inputPath "raw-deps/linux/RustDedicated_Data/Managed"
```

The `.bat` files in the repo are wrappers around exactly this flow.

## Version Matching Rules

These are important for this project in particular because it uses Harmony patches and some transpilers rely on exact method names and IL patterns.

You want all of the following to match each other as closely as possible:

- the Rust server branch you downloaded DLLs from
- the `deps/linux` output you generated from those DLLs
- the Carbon assembly set in `carbon/`
- the runtime environment you expect to load the produced `Carbon.Linux.RSM.dll`

If you mix public Rust DLLs with staging Carbon binaries, or mix old Carbon binaries with newer Rust server DLLs, build success does not guarantee runtime success. Harmony transpilers are especially sensitive to upstream IL changes.

## Windows Notes

The same pattern exists for Windows:

- `update-win-dependencies.bat`
- `update-win-staging.bat`
- `deps/windows`

If you build with `-c Windows`, the project switches `GamePlatform` to `windows` and resolves against `deps/windows` instead of `deps/linux`.

## NuGet Restore Requirement

This repo does not include a `NuGet.config`, so restore will use the machine's configured/default feeds.

You still need:

- a working `dotnet restore`
- a feed that can resolve `Carbon.Community` `2.0.*`

If restore fails before assembly resolution, fix the NuGet side first. If restore succeeds but compilation fails on missing references, fix `deps/linux` and `carbon/`.

## Recommended Verification Order

1. Install the .NET/Mono tooling.
2. Install PowerShell 7 if you want to run the bundled `.ps1` scripts on Linux.
   Recommended helper: `scripts/install-pwsh-ubuntu.sh`
3. Populate `raw-deps/linux` and generate `deps/linux`.
4. Populate repo-root `carbon/` with the matching Carbon-managed DLLs.
5. Run `dotnet restore`.
6. Run `dotnet build RustServerMetrics.sln -c Linux`.

## What I Would Check First If Build Still Fails

- missing `deps/linux` folder
- missing repo-root `carbon/` folder
- wrong Rust branch versus Carbon branch
- missing `pwsh`
- `dotnet` host/runtime still broken
- NuGet restore failing for `Carbon.Community`
- first missing assembly name in the compiler output

That last item matters most. Once the machine can actually build, the first missing assembly or namespace error will tell you exactly which local dependency is still absent.
