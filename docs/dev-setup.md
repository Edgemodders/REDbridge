# Development setup

Everything needed to work on REDbridge, in order. A fresh machine should be able to follow this top to bottom. The shared tools (Git, Python, winget) are the same as in [REDnode's setup guide](https://edgemodders.github.io/REDnode/dev-setup.html).

Reference machine: Windows 11 Home (build 26200), 16 GB RAM, no administrator session available.

## 1. Base tools

| Tool | Version used | Why | Install |
|---|---|---|---|
| Git for Windows | 2.55.0 | Source control | `winget install --id Git.Git -e` |
| Node.js | 24 | Runs the npm scripts that wrap the build | `winget install --id CoreyButler.NVMforWindows -e`, then `nvm install 24` |
| Python | 3.14.6 | The probe, brand tooling | Any Python 3.10 or later |
| Chrome or Edge | any | Renders the brand images | Already present on Windows |

Git needs long paths on Windows, because WolvenKit's generated type files exceed the default limit. Without it a clone stops with "Filename too long" and leaves the checkout incomplete.

```powershell
git config --global core.longpaths true
```

## 2. .NET SDK

WolvenKit targets .NET 10 (`global.json` asks for SDK 10.0.302 or a later feature band).

| Tool | Version used | Install |
|---|---|---|
| .NET SDK | 10.0.401 | Per-user, no administrator rights, see below |

The per-user install uses Microsoft's `dotnet-install.ps1`. It needs no UAC prompt, so it works over a remote session. It puts the SDK in `%LOCALAPPDATA%\Microsoft\dotnet`, about 770 MB.

```powershell
$dl = "$env:TEMP\dotnet-install"; New-Item -ItemType Directory -Force $dl | Out-Null
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$dl\dotnet-install.ps1"
$root = "$env:LOCALAPPDATA\Microsoft\dotnet"
& "$dl\dotnet-install.ps1" -Channel 10.0 -InstallDir $root -NoPath
# make it the default dotnet for this user; new shells only
[Environment]::SetEnvironmentVariable('Path', "$root;" + [Environment]::GetEnvironmentVariable('Path','User'), 'User')
& "$root\dotnet.exe" --list-sdks
```

Do **not** set `DOTNET_ROOT` as a user or machine environment variable. Every .NET program on the machine then looks only in that folder, and the per-user folder has only .NET 10: Vortex's .NET 9 check failed that way on the owner's laptop. Set it per process instead (next sections).

A machine-wide `dotnet.exe` in `C:\Program Files\dotnet` may exist as a runtime only. It does not see the per-user SDK, so the per-user folder must come first on `PATH`. With administrator rights, `winget install --id Microsoft.DotNet.SDK.10 -e` is the simpler route.

## 3. WolvenKit

REDbridge uses WolvenKit's headless libraries from WolvenKit's own NuGet packages: `WolvenKit.Core`, `WolvenKit.RED4`, `WolvenKit.Common` and `WolvenKit.Modkit`, all at `WolvenKitVersion` in `src/REDbridge/REDbridge.csproj` (10.0.0-beta.1 since 2026-10-25, for game 2.5; 9.1.0 from 2026-10-15). They are GPL-3.0, and their source is WolvenKit's repository at the tag of that version. This repo never commits WolvenKit's binaries; `dotnet restore` fetches them.

Until 2026-10-15 the libraries were built from a git submodule at `external/WolvenKit`. The package build was checked against that build before the switch: the same files in the output (debug symbols aside), and the probe, REDcode's smoke test, a `.wem` to Ogg export and a mesh to glTF export all pass. Two things the packages need in the project file:

| What | Why |
|---|---|
| A `Reference` to `DirectXTexNet.dll` inside the WolvenKit.Common package | The package ships it as a content file, not a library, so .NET leaves it out of `REDbridge.deps.json` and `texture.toPng` fails with "Could not load file or assembly DirectXTexNet". The Build workflow checks it is listed |
| A direct `PackageReference` to `NAudio.Lame` | Its build step copies `libmp3lame.*.dll` and runs only for a direct reference. The bridge does not encode MP3; it keeps the output the same as before |

**Game versions.** WolvenKit 10.0.0-beta.1 reads streaming sectors' node data only in game 2.5's form. Checked on 2026-10-25 against the 2.5 install and a 2.31 copy: the probe and REDcode's smoke test pass on both; 2.31's quest sectors fail or come back with wrong `nodeData`.

**Updating WolvenKit.** Dependabot (`.github/dependabot.yml`) opens a pull request each week a new WolvenKit release is out, raising `WolvenKitVersion`. The Build workflow (`.github/workflows/build.yml`) checks that it compiles and that the run-time files are in the output. Before merging, on a machine with the game: build into a scratch folder (`dotnet build src/REDbridge -c Release -o <folder>`), run `tools/probe.py` and REDcode's `npm run smoke` against it, then release the bridge.

**Reading WolvenKit's source.** `npm run wolvenkit:source` clones WolvenKit at `WolvenKitVersion` into `external/WolvenKit` (shallow, about a minute), or moves an existing clone to that tag. git ignores the folder. The build does not use it.

### What the bridge ships from WolvenKit

Recorded from the submodule build in C0; the package build gives the same set.

| Group | Files | Notes |
|---|---|---|
| Managed WolvenKit assemblies | `WolvenKit.{Core,RED4,Common,Modkit}.dll` | 29 MB together, GPL-3.0 |
| Oodle decompression | `kraken.dll`, `libkraken.so`, `libkraken.dylib` | Linux and macOS builds exist. Origin and license of these binaries: not yet verified |
| Textures | `DirectXTexNet.dll`, `lib/texconv.dll`, `lib/DirectXTexNetImpl.dll`, `lib/Ijwhost.dll` | Windows only. This is what keeps the bridge Windows-only |
| Wwise audio | `wwtools.dll`, `libwwtools.so`, `lib/COPYING` | `COPYING` is vgmstream's license |
| Audio encoding | `libmp3lame.32.dll`, `libmp3lame.64.dll`, `opus-tools/opus{dec,enc,info}.exe` | LAME is LGPL and comes from NAudio.Lame. The bridge does not need MP3 encoding; drop it when trimming |
| Data | `Resources/soundEvents.json` | |
| NuGet packages | 49, listed in `WolvenKit.Modkit.deps.json` | Includes ClearScript V8 (scripting), EF Core Sqlite, SharpGLTF, SharpDX, NAudio, YamlDotNet, Newtonsoft.Json |

The ClearScript V8 native library (`ClearScriptV8.win-x64.dll`) is not in this build output. It is expected to appear on a runtime-specific publish (`dotnet publish -r win-x64`), unverified. The bridge does not need it: the script host was dropped with REDcode's script runner.

## 4. Repository and GitHub

```powershell
git clone https://github.com/Edgemodders/REDbridge.git
cd REDbridge
git config user.name  "CyberbuildsAI"
git config user.email "340969962+CyberbuildsAI@users.noreply.github.com"
```

## 5. Building and running the bridge

From clone to a working bridge. Verified on both reference machines on 2026-10-11.

```powershell
git clone https://github.com/Edgemodders/REDbridge.git
cd REDbridge
npm run build:full                              # dotnet restore, then dotnet build
npm run probe -- "<Cyberpunk 2077 folder>"      # runs tools\probe.py against the Release build
```

Day to day:

| Script | Runs |
|---|---|
| `npm run build` | `dotnet build src\REDbridge\REDbridge.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false -v q` |
| `npm run build:full` | `dotnet restore`, then `npm run build`. Needed after a clone or when packages change |
| `npm run update` | `git pull`, then `npm run build:full` |
| `npm run wolvenkit:source` | Fetches WolvenKit's source at `WolvenKitVersion` into `external/WolvenKit`, for reading |
| `npm run probe -- "<game folder>"` | `python -I tools\probe.py src\REDbridge\bin\Release\net10.0\REDbridge.exe "<game folder>"` |
| `npm run clean` | `dotnet clean` |

Notes:

- The first `npm run build:full` downloads WolvenKit's packages and their dependencies from NuGet; later builds take seconds. Output: `src\REDbridge\bin\Release\net10.0\REDbridge.exe` with about 200 MB beside it.
- The probe starts the bridge with the game folder, loads the archive indexes (the game does not need to be running) and calls every method: list and search, CR2W to JSON and back with a byte comparison, texture to PNG, hashing. About 5 to 8 s. Output goes to `probe-out`, which git ignores.
- The npm scripts run `dotnet` through `tools/dotnet.mjs`, which prefers `DOTNET_ROOT`, then the per-user SDK in `%LOCALAPPDATA%\Microsoft\dotnet`, then `dotnet` on `PATH`. So the per-user install from section 2 works without changing `PATH`. The probe still needs `DOTNET_ROOT` for the per-user runtime (next section).
- The REDcode extension checks the bridge's version at startup and says when the bridge is older than it needs. `npm run update` here fixes that.

### .NET runtime location

With a machine-wide SDK (`winget install --id Microsoft.DotNet.SDK.10 -e`) nothing more is needed. With the per-user SDK from section 2, `REDbridge.exe` needs `DOTNET_ROOT` in the environment of whatever starts it, or it fails to find a runtime. Set it for that process only, never for the user (see section 2):

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"   # this shell only
```

REDcode (`redcode.dotnetRoot`) and EDGEdeck (`edgedeck.dotnetRoot`, which defaults to the per-user folder when it has .NET 10) pass it to the bridge process themselves.

### Oodle

On first run the bridge copies the game's `oo2ext_7_win64.dll` to `%LOCALAPPDATA%` and loads it from there, as WolvenKit does, and logs "Using the game's Oodle library". Without it WolvenKit falls back to the bundled `kraken.dll` and warns that results may differ.

## 6. Docs site and brand images

The site at https://edgemodders.github.io/REDbridge/ is built by `.github/workflows/pages.yml` on every push to `main`. **Settings > Pages > Source** must be **GitHub Actions**.

```powershell
.\tools\brand\render.ps1   # banner, social preview, icon, badges
```

| Output | Used by |
|---|---|
| `docs/assets/brand/banner.png` | README header |
| `docs/assets/brand/social.png` | Upload by hand in Settings > General > Social preview |
| `docs/assets/brand/icon.png` | Site and app icon |
| `docs/assets/brand/badges/*.svg` | README badges. Edit the list in `tools/brand/badges.py` |
