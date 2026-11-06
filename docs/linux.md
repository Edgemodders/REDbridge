---
title: Linux
---

# REDbridge on Linux

**Status: a future task, not scheduled.** The owner wants it (2026-11-06). A spike that day found that most of REDbridge runs on Linux with no code changes. Textures are the blocker.

## The spike

| | |
|---|---|
| Date | 2026-11-06 |
| Build | REDbridge 0.12.0, WolvenKit 10.0.0-beta.1, `dotnet publish -c Release -r linux-x64 --self-contained true` (148 MB), from a copy of the source |
| Where it ran | WSL 2, Ubuntu 24.04, reading the Windows install of game 2.5 at `/mnt/e/...` |
| Check | `tools/linux-check.py`, on Linux and on Windows, comparing the files each wrote |
| Code changed | None |

**Not tested:** a real Linux install, a game under Proton, and the game loading an archive packed on Linux.

## What works and what does not

| Area | On Linux |
|---|---|
| `archive.*`, `hash.*`, `sound.events`, `tweakdb.*`, `loc.*`, `mesh.items`, `mesh.info`, `sector.find`, `sector.nodes`, `sector.build` | Work |
| `archive.extract` | Works. A mesh and a texture came out byte for byte the same as on Windows |
| `cr2w.toJson`, `cr2w.fromJson`, documents | Work. The JSON differs from Windows only in its export time and line endings. A rebuilt mesh is 3% larger (306,637 bytes, 296,907 on Windows, where it matches the original exactly), since it is compressed with Kraken rather than the game's Oodle; the Windows build reads it back to the same JSON |
| `project.pack` | Works. A file's id is the hash of its path with backslashes, as the game wants. The Windows build reads the archive back byte for byte |
| `project.export` of a mesh | Works, with two faults. The `.glb` is the same size as on Windows. **41 of the 43 files land in one folder with backslashes in their names** (`base\surfaces\...json`). **The images are missing:** 150 files on Windows, 43 on Linux, 105 of the difference images |
| `project.import` of a `.glb` | Works |
| `texture.toPng`, `texture.fileToPng`, `texture.imageToPng`, `project.import` of an image | **Fail:** `The type initializer for 'DirectXTexNet.TexHelper' threw an exception`, or RedImage calling a valid PNG invalid |
| `sound.importWavs` | **Not reached:** the check's sound id was made up. It starts `opus-tools/opusenc.exe`, so it should fail on real Linux. Under WSL an `.exe` still starts, so WSL cannot show this |

## The plan

Effort figures are rough estimates.

### L1: a Linux build without textures (1 to 2 days)

- Add a `linux-x64` zip to the release workflow. Keep the `*-win-x64.zip` name and `REDbridge.exe` as they are, since every client looks for them.
- Fix the backslash file names. WolvenKit builds them from depot paths, and `FileEntry` converts separators only on macOS. Fix in the bridge, or upstream.
- Use Linux `opus-tools` for `sound.importWavs`. `BridgeApi.SoundImport.cs` names the `.exe` files. Find them on the system or ship them.
- Make texture calls fail with a clear message rather than a type initializer error.
- Verify the origin and license of `libkraken.so` before shipping it (open in [dev setup](dev-setup.html)).
- Test a Linux-packed archive in the game.
- Drop what Linux does not use from its zip (`lib/`, `kraken.dll`, `wwtools.dll`, `opus-tools/*.exe`).

### L2: textures (1 to 3 weeks)

WolvenKit's texture code calls DirectXTexNet, a C++/CLI library that runs only on Windows (`lib/DirectXTexNetImpl.dll`, `lib/Ijwhost.dll`, `lib/texconv.dll`). It is used directly in nine WolvenKit files, mainly `RedImage.cs` (1,055 lines), `RedImage.Lut.cs` and `DDS/Texconv.cs`, and in Modkit's `Uncook.cs`, `Import.cs` and the mlmask tools.

| Option | Gives | Cost |
|---|---|---|
| Change WolvenKit: a cross-platform texture library behind the same calls, sent upstream | Everything, including images in mesh exports | Needs upstream to accept it, or a fork. REDbridge now uses WolvenKit's NuGet packages, so a fork means building them |
| Our own texture code in REDbridge, for `texture.*` and image imports | PNG conversion both ways | Mesh exports still lack images. Our `.xbm` output must satisfy the game |

Recommended, unverified: try changing WolvenKit first. Candidates to look at: DirectXTex's own Linux build through a small C shim, and a managed BC encoder such as BCnEncoder.NET. Check their licenses, formats (the game uses BC1 to BC7) and that their output matches, against the Windows build.

### L3: the tools that start it (2 to 4 days)

REDcode (`bridgeUpdater.ts`, `bridgeClient.ts`), EDGEdeck (`bridgeInstall.ts`, `bridgeClient.ts`) and REDnode (`bridge-install.js`, `redbridge.js`) look for `REDbridge.exe` and the `win-x64` zip by name. REDcode also lists running bridges through PowerShell. A search found Windows-specific code in EDGEmat, EDGEbuilder, EDGEwave and REDsave too; how much of it concerns REDbridge is unverified.

Each tool needs: pick the zip for the platform, the executable's name, a .NET lookup (or a self-contained build), and Linux game folders (Steam's `steamapps/common`).

## Open questions

- **What is Linux for?** Automated checks and servers need only L1. Modders on Linux using EDGEdeck or REDcode need all three, since EDGEmat and texture previews depend on textures.
- **REDnode under Proton:** its plugin and host run inside the game's Windows environment, so it may want the Windows build rather than this one. Whether that build runs there is untested.
- **Self-contained or not:** 148 MB with .NET inside, against asking players to install .NET 10.
- **Does the game accept Kraken's output?** The Windows build reads it, but the game has not tried it.
