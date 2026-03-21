# Changes Made To Get The Project Building

This file documents the changes made during bring-up of this repository so it can be built and deployed more reliably on Linux for Carbon-based Rust dedicated servers.

It is intentionally written without machine-specific private paths.

## Summary

The repository now:

- has documented local build and deployment workflows
- has Ubuntu helper scripts for installing the required tooling
- has a repo-local script for populating Rust and Carbon build dependencies from an existing server install
- has a cleanup script for returning the repo to a fresh-build state
- builds successfully with:

```bash
dotnet restore
dotnet build RustServerMetrics.sln -c Linux
```

- builds cleanly with:

```text
0 Warning(s)
0 Error(s)
```

Current output artifact:

```text
src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll
```

## Documentation Added

### `PROJECT_ANALYSIS.md`

Added a full architecture and compatibility analysis covering:

- Linux Rust dedicated server suitability
- Carbon dependency
- general architecture
- metric collection flow
- buffering, batching, and upload behavior
- extensibility and backend generalization
- commentary on implementation quirks and improvement opportunities

### `RUST_CARBON_DEPS.md`

Added a dedicated document explaining:

- which local folders the build expects
- what belongs in `deps/linux`, `raw-deps`, and repo-root `carbon`
- how the helper scripts relate to those folders
- how to source dependencies from a local server install or download flow
- version-matching concerns for Rust/Carbon/Harmony

### `RUNBOOK.md`

Added an end-to-end runbook for:

- installing tooling
- populating local dependencies
- restoring and building
- locating the built DLL
- deploying it into a Carbon server
- configuring the module
- verifying backend metrics flow
- troubleshooting common build and runtime problems

## Tooling Scripts Added

### `scripts/install-dotnet-ubuntu.sh`

Added an Ubuntu bootstrap script for `.NET` and `mono`.

Key decisions:

- prefers Ubuntu feeds / Ubuntu `.NET` backports on Ubuntu `22.04+`
- only uses the Microsoft `.NET` package feed on older Ubuntu versions
- installs `.NET` SDKs/runtimes plus `mono-complete`
- supports `--repair-mixup` to recover from Ubuntu/Microsoft `.NET` package conflicts

This was added because the machine setup phase hit real package-feed conflicts while trying to get a usable `dotnet` toolchain.

### `scripts/install-pwsh-ubuntu.sh`

Added a separate Ubuntu bootstrap script for `PowerShell`.

Key decisions:

- installs `powershell` from the Microsoft package repository
- writes an APT pin file so Microsoft-origin `dotnet*`, `aspnet*`, and `netstandard*` packages do not override Ubuntu/backports `.NET`

This keeps `.NET` package ownership clean while still allowing `pwsh` installation, which is needed by the repo’s existing PowerShell helper scripts.

### `scripts/populate-local-rust-carbon-deps.sh`

Added a Linux-friendly dependency population script that:

- accepts a real local Rust dedicated server install path or a symlink to one
- copies `RustDedicated_Data/Managed/*.dll` into `raw-deps/linux/RustDedicated_Data/Managed`
- populates `deps/linux`
- copies Carbon-managed DLLs into repo-root `carbon/` when found

This made the repo much easier to bring up against a real local Rust/Carbon installation without manually copying large DLL sets around.

### `clean.sh`

Added a cleanup script that removes generated local artifacts for a fresh build:

- `deps/`
- `raw-deps/`
- `carbon/`
- `temp/`
- `build/`
- `publish/`
- `artifacts/`
- `.vs/`
- nested `bin/` and `obj/` folders

Supports:

- `--dry-run`
- `--yes`

## Build And Dependency Fixes

### 1. Fixed `MetricsLogger.Awake()` access modifier

Changed:

- `protected override void Awake()`

to:

- `public override void Awake()`

in `src/RustServerMetrics/MetricsLogger.cs`

Reason:

- the project failed to compile with `CS0507`
- the base `SingletonComponent.Awake()` in the current reference set is `public`
- overriding it as `protected` is not valid in this environment

### 2. Fixed `scripts/unprivate-dependencies.ps1` to stop copying framework assemblies into `deps/linux`

Changed:

- `if ($file.Name -like "System.") { continue }`

to:

- `if ($file.Name -like "System.*") { continue }`
- `if ($file.Name -eq "netstandard.dll") { continue }`

Reason:

- the original pattern only matched the literal string `System.`
- it did not actually exclude files such as `System.Net.Http.dll`
- as a result, `deps/linux` ended up containing framework/reference assemblies that should not have been copied there
- that produced noisy and misleading binding conflicts during build, especially around `System.Net.Http`

### 3. Changed the local dependency population script to reuse the repo’s PowerShell publicizer flow

The first version of `scripts/populate-local-rust-carbon-deps.sh` called `AssemblyPublicizer.exe` directly through `mono`.

This caused overwrite/write failures in practice.

The script was changed to invoke the repo’s existing and already-accepted workflow instead:

```text
pwsh scripts/unprivate-dependencies.ps1
```

Reason:

- avoids duplicating logic already present in the repo
- keeps one source of truth for which assemblies get copied vs. publicized
- matched the flow that was already expected by the existing batch scripts

### 4. Removed `Stack<T>` ambiguity by replacing it with list-based traversal

Changed the target-method scans in:

- `src/RustServerMetrics/HarmonyPatches/Delayed/ObjectWorkQueue_RunJob_Patch.cs`
- `src/RustServerMetrics/HarmonyPatches/Delayed/RPCServer_Attribute_Method_Patch.cs`

from `Stack<Type>`-based traversal to a simple `List<Type>`-based depth-first traversal.

Reason:

- the build failed with `CS0433`
- `Stack<T>` was resolving ambiguously from both `System` and `mscorlib` in the current reference environment
- replacing it with `List<Type>` kept the traversal behavior without relying on the ambiguous type

### 5. Cleanly resolved all remaining build warnings

The project was brought from "build succeeds with warnings" to "build succeeds with zero warnings".

#### 5a. Fixed `MethodInfo` comparison warning

Updated the insertion-point lookup in:

- `src/RustServerMetrics/HarmonyPatches/BasePlayer_PerformanceReport_Patch.cs`

Reason:

- the build warned about a possible unintended reference comparison (`CS0252`)
- `x.operand` was being compared directly without first asserting that it was a `MethodInfo`

Resolution:

- changed the predicate to pattern-match `x.operand` as `MethodInfo` before comparing it to the target method

#### 5b. Replaced obsolete `UnityWebRequest` error properties

Updated:

- `src/RustServerMetrics/ReportUploader.cs`

Reason:

- `UnityWebRequest.isNetworkError` is obsolete
- `UnityWebRequest.isHttpError` is obsolete

Resolution:

- replaced network error detection with:
  - `request.result == UnityWebRequest.Result.ConnectionError`
- replaced HTTP error detection with:
  - `request.result == UnityWebRequest.Result.ProtocolError || request.result == UnityWebRequest.Result.DataProcessingError`

This keeps the code aligned with the current UnityWebRequest API surface exposed by the reference assemblies.

#### 5c. Replaced obsolete `ClientRPCPlayer(...)` usage

Updated:

- `src/RustServerMetrics/MetricsLogger.cs`

Reason:

- `BaseEntity.ClientRPCPlayer<T1, T2>(...)` is obsolete in the current Rust server reference set

Resolution:

- replaced:
  - `ClientRPCPlayer(null, player, "GetPerformanceReport", "legacy", requestId)`
- with:
  - `ClientRPC(RpcTarget.Player("GetPerformanceReport", player), "legacy", requestId)`

This uses the newer `RpcTarget`-based API exposed by the current publicized Rust assemblies.

## Build Process Validated

The validated high-level build sequence is now:

1. install `.NET`, `mono`, and `pwsh`
2. populate Rust and Carbon local dependency folders
3. run:

```bash
dotnet restore
dotnet build RustServerMetrics.sln -c Linux
```

That sequence now completes successfully and produces:

```text
src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll
```

It also completes with:

```text
0 Warning(s)
0 Error(s)
```

## Dependency Workflow Validated

The dependency workflow that was validated is:

1. clean the repo-local generated state
2. populate `raw-deps/linux`
3. publicize/copy into `deps/linux`
4. populate repo-root `carbon/`
5. restore and build

Important validated behavior:

- `deps/linux` should not contain `System.*` assemblies
- `deps/linux` should not contain `netstandard.dll`
- Rust game assemblies and Carbon assemblies need to be version-aligned closely enough for the Harmony transpilers to remain valid

## Deployment Path Confirmed

The deployment target for the Linux build remains:

```text
<rust-server>/carbon/managed/modules/Carbon.Linux.RSM.dll
```

The project README warning still applies:

- do not replace or delete the Harmony module DLL while the Rust server is running

## Current Build Status

The Linux build currently succeeds cleanly:

- `dotnet restore`: success
- `dotnet build RustServerMetrics.sln -c Linux`: success
- warnings: `0`
- errors: `0`

## Practical Outcome

The repository moved from:

- incomplete machine setup
- dependency population issues
- build-breaking source incompatibilities

to:

- documented operator workflow
- repeatable dependency bring-up
- clean rebuild workflow
- successful Linux build output for Carbon deployment
