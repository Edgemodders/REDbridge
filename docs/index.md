# RED<span class="y">bridge</span> docs

REDbridge hosts WolvenKit's engine in a local process and exposes it over JSON-RPC to REDcode, EDGEdeck, REDnode, other Edgemodders tools and AI assistants. The latest release is [0.10.0](https://github.com/Edgemodders/REDbridge/releases/latest), checked against game 2.31.

| Document | What it covers |
|---|---|
| [README](https://github.com/Edgemodders/REDbridge#readme) | How to get REDbridge, every method by group, how a client talks to it, what a mesh or texture import keeps |
| [Changelog](https://github.com/Edgemodders/REDbridge/blob/main/CHANGELOG.md) | What changed in each release, with each method's parameters |
| [Dev setup](dev-setup.md) | Every tool and step needed to build REDbridge on a new machine |

## Get REDbridge

You rarely install it by hand: REDcode (**REDcode: Install REDbridge**), EDGEdeck (**Get REDbridge**) and REDnode (the Mod Manager's **Get REDbridge**) each fetch the latest release for you, and REDnode also uses the copy EDGEdeck or REDcode fetched. By hand: unzip `REDbridge-<version>-win-x64.zip` from [Releases](https://github.com/Edgemodders/REDbridge/releases); it needs Windows x64 and the .NET 10 runtime.

The architecture is in the [REDcode design document](https://edgemodders.github.io/REDcode/design.html) (section 06, Architecture, with its bridge protocol table).

## Roadmap status

| Phase | Status |
|---|---|
| C0 Decide | Done. WolvenKit's headless libraries build on the reference machine; findings in Dev setup |
| C1 Bridge MVP | Done. The bridge answers every planned method over stdio; verified against a game install with `tools/probe.py` |
| C2 Browse | Done on the bridge side: `archive.tree` (folder index over 583,798 files, built in about 1 s), `archive.extractMany`, `cr2w.fileToJson` |
| C3 Edit | Done. `texture.fileToPng` for the texture viewer. Document sessions for the CR2W editor (0.5.0): `cr2w.open` parses once and keeps the file in memory, `cr2w.getNode` reads one node with arrays paged (60,303 localization rows open in about 0.4 s and page in under 1 ms), `cr2w.patch` applies JSON Patch operations atomically and returns the inverse for undo, `cr2w.save` writes through WolvenKit's writer, `cr2w.describeType` and `cr2w.createInstance` drive the property grid, `cr2w.search` finds values and property names (60,303 rows in about 0.2 s), `cr2w.getNodes` reads several nodes in one call. 0.5.1: `texture.toPng` and `texture.fileToPng` flip the image vertically as WolvenKit does, so textures come out the right way up and atlas rectangles line up |
| For EDGEdeck | Done: `archive.openMod` (0.4.0) lists the resources inside any `.archive` on disk without loading a game, so the mod manager can show conflicts between mod archives |
| For EDGEbuilder | Done (0.6.0): `mesh.info` and `mesh.infoMany` read a mesh's bounding box and appearance names, or an entity template's mesh components with their offsets and the union of their boxes; `sector.build` bakes an [EDGEbuilder scene](https://edgemodders.github.io/EDGEbuilder/scene.html) into a `worldStreamingSector` (mesh, entity and light nodes with node data), a `worldStreamingBlock` with the streaming box, and an ArchiveXL `.xl` that patches the block into the world, written into a REDcode project. Verified by the probe: WolvenKit reads the files back. Not yet loaded in the game |
| C4 Ship | Done. `project.pack` (0.6.1) packs a folder into `<modName>.archive` through WolvenKit's packer and reports the paths with capital letters or spaces, the same warning WolvenKit gives. 0.7.0: `project.export` and `project.import` over WolvenKit's Modkit, on a project's layout (`source/archive` to `source/raw` and back), each returning the files it wrote. Export refuses types WolvenKit has no export for instead of dumping their buffers, and clears WolvenKit's per-process cache of exported files first, which skipped a second export of the same file. Import of an image gives the existing texture's setup to WolvenKit, as its app's import dialog does, and puts back the flags WolvenKit's settings have no place for: a re-imported texture keeps all ten setup fields and comes back the right way up. After 0.10.0 (issue #8, for EDGEmat): a new texture can be given a `texture` setup, the preset `color`, `normal` or `grayscale` or the setup's fields, and the import reports the setup it wrote. Not yet released or tried in the game |
| C5 View | Done on the bridge side: `sound.events` (0.7.2) for REDcode's audio player, `tweakdb.*` (0.8.0) for its TweakDB view, `loc.*` (0.10.0) for its LocKeys view. No script host: REDcode dropped its script runner |
| For EDGEwave | Done (0.9.0): `sound.importWavs` overrides game sound effects in a project from `<hash>.wav` files |
| Game 2.5 | Open. Most files read; CR2W version 0xC3 files and the new `worldNodeDataBuffer` do not ([issue #10](https://github.com/Edgemodders/REDbridge/issues/10)). Needs a WolvenKit release that reads 2.5 |
| For EDGEquest | Done on the bridge side (after 0.10.0, issue #9): `loc.lines` looks spoken lines up by stringId (a scene line's `locstringId.ruid`) with their subtitle texts, file and row, `.wem` voice-over files and lengths; `loc.searchLines` finds them by words (an index of 104,895 en-us lines from 3,800 subtitle files, built on first use in about 1.3 s, then a few milliseconds a search); `loc.newLinesFile` gives a project an empty subtitle file named in its `.xl`. Checked by the probe and against `sq030_01_dam_meetup.scene` (63 of its ruids have text, 50 a voice and a length). Not yet released or tried in the game |

Source code and issues: [github.com/Edgemodders/REDbridge](https://github.com/Edgemodders/REDbridge)
