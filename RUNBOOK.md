# Build And Install Runbook

This runbook is for building `Carbon.Linux.RSM.dll` on Ubuntu and installing it into a Carbon-based Rust dedicated server.

It assumes:

- Ubuntu `22.04` or `24.04`
- a local checkout of this repository
- a real Rust dedicated server install available on disk
- Carbon installed on the target Rust server you want to run the module on

This runbook is Linux-first because that is the practical target for Rust dedicated servers.

## 1. Confirm The Repo Layout

From the repo root, verify these helper scripts exist:

```bash
ls scripts/install-dotnet-ubuntu.sh
ls scripts/install-pwsh-ubuntu.sh
ls scripts/populate-local-rust-carbon-deps.sh
```

If any of those are missing, update your checkout first.

## 2. Install Build Tooling On The Ubuntu Machine

Install `.NET`, `.NET` runtimes, and `mono`:

```bash
./scripts/install-dotnet-ubuntu.sh
```

Install `PowerShell`:

```bash
./scripts/install-pwsh-ubuntu.sh
```

### Verify the tooling

```bash
dotnet --list-sdks
dotnet --list-runtimes
mono --version
pwsh --version
```

You want all four commands to work.

Notes:

- For this project, keep `.NET` on Ubuntu feeds / Ubuntu backports.
- Use the Microsoft repo only for `powershell`.
- The helper scripts already follow that split.

## 3. Decide How You Will Source Rust And Carbon DLLs

There are two practical options.

### Option A: Use a real local Rust/Carbon server install

This is the preferred option if you already have a matching server install on disk.

You can point the script at:

- the real install directory directly
- or a symlink to that directory

### Option B: Download Rust managed DLLs into the repo

This repo also includes PowerShell helpers that can download Rust managed DLLs with `DepotDownloader`.

That is useful if you do not already have a local Rust server install, but Option A is simpler if you do.

## 4. Populate Repo-Local Build Dependencies

### Preferred: copy from a real local server install

Run:

```bash
./scripts/populate-local-rust-carbon-deps.sh --server-root /path/to/rust-server
```

If the Rust server path is a symlink, that is fine:

```bash
./scripts/populate-local-rust-carbon-deps.sh --server-root /path/to/rust-server-symlink
```

If Carbon DLLs are not under the default `carbon/managed` path, pass them explicitly:

```bash
./scripts/populate-local-rust-carbon-deps.sh \
  --server-root /path/to/rust-server \
  --carbon-managed-dir /path/to/rust-server/carbon/managed
```

### What the script does

It populates:

- `raw-deps/linux/RustDedicated_Data/Managed`
- `deps/linux`
- `carbon`

The project resolves compile-time assemblies from `deps/linux` and `carbon`.

### Verify the copied dependencies

```bash
find raw-deps/linux/RustDedicated_Data/Managed -maxdepth 1 -name '*.dll' | wc -l
find deps/linux -maxdepth 1 -name '*.dll' | wc -l
find carbon -maxdepth 1 -name '*.dll' | wc -l
```

All three should be non-zero for a normal Linux Carbon build workflow.

## 5. Alternative: Download Rust DLLs Instead Of Copying Them

If you do not want to point at a local server install, you can use the repo’s bundled PowerShell flow:

```bash
pwsh scripts/SteamDownloader.ps1 -steam_appid 258550 -platform linux -deps_dir "../raw-deps"
pwsh scripts/unprivate-dependencies.ps1 -outputPath "deps/linux/" -inputPath "raw-deps/linux/RustDedicated_Data/Managed"
```

If you target Rust staging:

```bash
pwsh scripts/SteamDownloader.ps1 -steam_appid 258550 -steam_branch staging -platform linux -deps_dir "../raw-deps"
pwsh scripts/unprivate-dependencies.ps1 -outputPath "deps/linux/" -inputPath "raw-deps/linux/RustDedicated_Data/Managed"
```

This only covers Rust managed DLLs. You still need to populate repo-root `carbon/` with matching Carbon-managed DLLs.

## 6. Restore NuGet Packages

Run:

```bash
dotnet restore
```

If restore fails:

- fix the machine’s `.NET` installation first
- then fix network or NuGet feed issues
- do not move on to build until restore succeeds

## 7. Build The Linux Module

Run:

```bash
dotnet build RustServerMetrics.sln -c Linux
```

This project’s Linux configuration produces `Carbon.Linux.RSM.dll`.

### Find the built DLL

On SDK-style projects, the final DLL is usually somewhere under `src/RustServerMetrics/bin/Linux/`.

Use:

```bash
find src/RustServerMetrics/bin/Linux -name 'Carbon.Linux.RSM.dll'
```

Typical output will look like one of these:

- `src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll`
- `src/RustServerMetrics/bin/Linux/Carbon.Linux.RSM.dll`

Use the actual path returned by `find`.

## 8. Sanity-Check The Build Artifact

Before deployment, verify the file exists:

```bash
find src/RustServerMetrics/bin/Linux -name 'Carbon.Linux.RSM.dll' -ls
```

If that returns nothing:

- the build did not complete successfully
- or the output path is not what you expected

Do not deploy until you have the DLL in hand.

## 9. Stop The Target Rust Server

Stop the target Rust dedicated server before replacing the module DLL.

This is important. The project README explicitly warns against updating or deleting Harmony module DLLs while the server is running.

## 10. Install The Built DLL Into The Rust Server

Copy the built DLL into the target Carbon modules folder:

```bash
cp /path/to/Carbon.Linux.RSM.dll /path/to/your/rust-server/carbon/managed/modules/
```

If the file already exists, replace it only while the server is stopped.

The target path should be:

```text
<rust-server>/carbon/managed/modules/Carbon.Linux.RSM.dll
```

## 11. Start The Rust Server

Start the server normally.

Watch the server logs for the module loading message and any immediate Harmony patch or configuration errors.

## 12. Configure The Module

Once the server has started, edit:

```text
HarmonyMods_Data/ServerMetrics/Configuration.json
```

At minimum, set:

- `Enabled`
- `Influx Database Url`
- `Influx Database Name`
- `Influx Database User`
- `Influx Database Password`
- `Server Tag`

Example:

```json
{
  "Enabled": true,
  "Influx Database Url": "https://my-influx-database:8086",
  "Influx Database Name": "rust-server-metrics",
  "Influx Database User": "my-database-user",
  "Influx Database Password": "my-super-secret-password",
  "Server Tag": "my-server-01",
  "Debug Logging": false,
  "Amount of metrics to submit in each request": 1000
}
```

## 13. Reload The Configuration

From the Rust server console or RCON:

```text
servermetrics.reloadcfg
```

Then check status:

```text
servermetrics.status
```

You want to see:

- `Ready: True`
- uploader running when work exists
- buffer not growing without bound

## 14. Verify The Backend Side

This project is built around:

- InfluxDB `1.8`
- Grafana

Important backend notes from the repo:

- InfluxDB `2.x+` is not supported by this module as written
- the README recommends setting `max-values-per-tag = 0`
- the README recommends setting `max-series-per-database = 0`
- use a sensible retention policy because player metrics can create high cardinality

If backend writes fail, the server may still run, but metrics can be lost.

## 15. Recommended End-To-End Smoke Test

After deployment:

1. start the Rust server
2. confirm the module loads
3. run `servermetrics.reloadcfg`
4. run `servermetrics.status`
5. let the server run for a few minutes
6. confirm points are landing in InfluxDB
7. confirm the Grafana dashboard starts showing data

## 16. Rebuild / Redeploy Cycle

For later updates:

1. stop the Rust server
2. rebuild with `dotnet build RustServerMetrics.sln -c Linux`
3. copy the new `Carbon.Linux.RSM.dll` into `carbon/managed/modules`
4. start the server again

Do not hot-swap the DLL while the server is live.

## 17. Troubleshooting

### `dotnet` commands fail before restore/build starts

Fix the machine setup first:

```bash
./scripts/install-dotnet-ubuntu.sh
```

Then re-run:

```bash
dotnet --list-sdks
dotnet --list-runtimes
```

If you hit an Ubuntu/Microsoft feed conflict such as:

- `dotnet-host-10.0 : Conflicts: dotnet-host`

reset the installed `.NET` packages and reinstall from the Ubuntu/backports side:

```bash
./scripts/install-dotnet-ubuntu.sh --repair-mixup
```

### `pwsh` is missing

Install it:

```bash
./scripts/install-pwsh-ubuntu.sh
```

### Build fails with missing Rust assemblies

Re-populate the Rust dependency folders:

```bash
./scripts/populate-local-rust-carbon-deps.sh --server-root /path/to/rust-server
```

Then verify:

```bash
find deps/linux -maxdepth 1 -name '*.dll' | wc -l
```

### Build fails with missing Carbon assemblies

Make sure repo-root `carbon/` contains the matching Carbon DLLs:

```bash
find carbon -maxdepth 1 -name '*.dll' | wc -l
```

If needed, re-run:

```bash
./scripts/populate-local-rust-carbon-deps.sh \
  --server-root /path/to/rust-server \
  --carbon-managed-dir /path/to/rust-server/carbon/managed
```

### Build succeeds but the module fails at runtime

The most common cause is version mismatch between:

- Rust server DLLs used for compile-time deps
- Carbon DLLs used for compile-time deps
- the actual Rust/Carbon runtime where you deployed the built module

Use matching branches/builds. This project contains Harmony transpilers, so upstream IL drift matters.

### Metrics do not appear in Grafana

Check:

- `Configuration.json`
- InfluxDB version and auth
- whether the InfluxDB write endpoint is reachable
- `servermetrics.status`
- server logs for HTTP or configuration errors

## 18. Minimum Command Sequence

If you just want the shortest successful path on Ubuntu:

```bash
./scripts/install-dotnet-ubuntu.sh
./scripts/install-pwsh-ubuntu.sh
./scripts/populate-local-rust-carbon-deps.sh --server-root /path/to/rust-server
dotnet restore
dotnet build RustServerMetrics.sln -c Linux
find src/RustServerMetrics/bin/Linux -name 'Carbon.Linux.RSM.dll'
```

Then stop the Rust server and copy the built DLL into:

```text
<rust-server>/carbon/managed/modules/
```
