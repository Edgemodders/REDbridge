# REDbridge: notes for Claude sessions

REDbridge is the .NET process that hosts WolvenKit's headless libraries and exposes them over JSON-RPC for REDcode (https://github.com/Edgemodders/REDcode) and, through REDcode's MCP server `redcode-mcp` (stdio, MIT, in the REDcode repo), for AI assistants. REDbridge itself has no MCP code. It is one of three repos by the same owner: REDnode (runtime scripting), REDcode (the IDE) and REDbridge (the engine host). REDnode's `CLAUDE.md` and `docs/dev-setup.md` set the shared conventions.

## Current status and next step

- **Requests from other repos:** open issues labelled `request` at https://github.com/Edgemodders/REDbridge/issues?q=is%3Aissue+is%3Aopen+label%3Arequest are needs from EDGEbuilder and EDGEdeck, each with the shape that would work. Read them at the start of a session and pick them up when the current phase is done; close each with a note on what shipped.
- **Design:** settled in the REDcode design document, which the owner holds privately. Ask for it before architectural work. Sections 05 (architecture) and the bridge protocol table are the spec for this repo.
- **C0 done (2026-10-11):** the .NET 10 SDK is installed per-user and WolvenKit's `Core`, `Common`, `RED4` and `Modkit` build at the pinned commit. What the output contains, and the low-memory build flags, are in `docs/dev-setup.md`. Decision then: WolvenKit as a git submodule with project references, never as committed binaries. **Since 2026-10-15 WolvenKit comes from its NuGet packages** (`WolvenKitVersion` in `REDbridge.csproj`; the submodule is gone, `npm run wolvenkit:source` clones the matching tag into the ignored `external/WolvenKit` for reading). Dependabot proposes each WolvenKit release; Build CI checks it compiles; before merging, run the probe and REDcode's smoke test against the game. Details in `docs/dev-setup.md` section 3. Release notes now say: built with WolvenKit X from its NuGet packages; WolvenKit's source is https://github.com/WolvenKit/WolvenKit/tree/X (GPL-3.0), this repository has the bridge's.
- **C1 and C2 done (2026-10-11):** the bridge speaks JSON-RPC over stdio with the archive, cr2w, texture and hash groups; `npm run build` and `npm run probe` are the loop. 0.3.0 added `texture.fileToPng` for REDcode's texture viewer. 0.4.0 added `archive.openMod` for EDGEdeck, the mod manager (https://github.com/Edgemodders/EDGEdeck), which starts the bridge without `--game`; the method needs only the hash tables.
- **0.6.0 (2026-10-13, by the EDGEdeck/EDGEbuilder session, with the owner's go-ahead):** `BridgeApi.Mesh.cs` (`mesh.info`, `mesh.infoMany`: CMesh bounds and appearances; entEntityTemplate parts from its CompiledData package, union of bounds) and `BridgeApi.Sector.cs` (`sector.build`: EDGEbuilder scene format 1 to `worldStreamingSector` + `worldStreamingBlock` + `.xl`, written with CR2WWriter into `<project>/source/archive/edgebuilder/<name>/` and `source/resources/archive/pc/mod/<name>.xl`). Node-data flag words (`Uk10`/`Uk11`), buffer flags and version 62 were copied from base-game 2.31 sectors of the same node types; streaming ranges default to max(40, 11 x size) and 0.8 of that. The probe bakes a crate, an entity and a light and reads them back. Closes issues #1 and #2. **Unverified in game**: whether the baked sector streams in; EDGEbuilder B4 tests it.
- **Next:** `project.pack` (build the `.archive` from a project; top priority), C3's `cr2w.getChunk` and `cr2w.patch`, then a `--serve` daemon mode on a named pipe rather than any tray UI (decided 2026-10-11).
- Two machines push here: `git fetch` and `git rebase origin/main` before every push, after committing.

## Releases

- **Release workflow (2026-10-22, REDnode's way, as every Edgemodders repo that can):** `.github/workflows/release.yml`. Run workflow in the Actions tab builds `REDbridge-<version>-win-x64.zip` (the Release build output, framework dependent, `REDbridge.exe` at its root) and keeps it as the run's artifact. With **Make a release** ticked, or `npm run release`, it stops when `v<version>` exists, when not on main, or when CHANGELOG.md has no section for the version, then makes the GitHub release, which tags the commit. Never `gh release create` by hand any more. REDcode's Install REDbridge and EDGEdeck's Get REDbridge take the newest release's `*-win-x64.zip`: keep that name and `REDbridge.exe` at the root.
- **Before a release:** raise `<Version>` in `src/REDbridge/REDbridge.csproj`, rename `## Unreleased` in `CHANGELOG.md` to `## <version> (<date>)` with a new empty Unreleased above, and run the probe and REDcode's smoke test against the game. `tools/release-notes.mjs` (`npm run release:notes`) adds the install paragraph and the WolvenKit line from `WolvenKitVersion`.
- **Changelog:** the Edgemodders convention (groups **New**, **Changed**, **Fixed**, **Other**; a line under Unreleased in the commit that makes the change). Sections up to 0.10.0 were copied from the GitHub releases.

## The license boundary

- This repo is **GPL-3.0** because it links WolvenKit's GPL-3.0 libraries. REDcode is MIT.
- Never copy code from this repo into REDcode, and never let REDcode link the bridge in-process. The only contract between them is JSON-RPC over stdio.
- Never commit WolvenKit binaries, game files or data extracted from the game. Reference WolvenKit as a pinned source dependency.

## Conventions

- Commit straight to `main` and push, in small logical commits. Agree the plan in chat first. PRs only for large or risky changes.
- Git identity is set per repo to `CyberbuildsAI` with the GitHub no-reply email. Never change the global identity.
- Record every tool install in `docs/dev-setup.md`, with versions and exact commands.
- Writing style: plain, short sentences, tables for comparisons. Mark anything unverified as unverified.
- House style is the HUD look. `tools/brand/render.ps1` regenerates the banner, badges and icon; re-run it when versions in the badges change.

## Docs site

- `docs/` is published by GitHub Pages at https://edgemodders.github.io/REDbridge/ through `.github/workflows/pages.yml`. Settings > Pages > Source must be GitHub Actions.
- Write docs as markdown in `docs/`. Jekyll renders them with the HUD layout. Add every new page to `nav` in `docs/_config.yml`.
- Plan a generated reference for the JSON-RPC methods once they exist; it should read the same as REDnode's API reference.
