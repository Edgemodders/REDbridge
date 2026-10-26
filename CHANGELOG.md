# Changelog

What changed in each REDbridge release, for the tools that use it (REDcode, EDGEdeck, EDGEbuilder) and for modders. The GitHub release of each version takes its notes from its section here.

How to keep it:

- With each change a client or modder would notice, add a line under **Unreleased**, in its group: **New**, **Changed**, **Fixed** or **Other**. One change per line, in the commit that makes the change. Leave out what only the probe sees.
- Before a release, raise `<Version>` in `src/REDbridge/REDbridge.csproj`, rename **Unreleased** to `## <version> (<date>)` and start a new empty **Unreleased** above it. The release workflow stops before releasing anything if the version has no section.
- Links are full URLs.
- `npm run release:notes` prints the GitHub notes: how to install, the section, and the WolvenKit version it is built with.
- Sections up to 0.10.0 were copied from their GitHub releases; they predate the groups and keep the old name, REDdeck.

## Unreleased

## 0.11.0 (2026-10-26)

### Changed

- **Reads game 2.5** (issue #10): built with WolvenKit 10.0.0-beta.1, WolvenKit's first build with support for game 2.5, instead of 9.1.0. All 3,041 quest streaming sectors of 2.5 read; 9.1.0 failed 262 of them because they hold node classes new in 2.5. The game's CR2W version did not change: 2.31's files are version 195 (0xC3) too. In other file types the JSON only gains the fields 2.5 added, such as `boundingBoxLocalBoundsOverride` on skinned mesh components and `astcQuality` in texture setups; no field was renamed or dropped in the types compared (30 file types, 5 files each).
- **Game 2.31's streaming sectors no longer read right.** WolvenKit 10.0.0-beta.1 reads node data and instanced mesh transforms only in 2.5's form. On a 2.31 install, 383 of 3,054 quest sectors fail ("Non-zero padding bytes found"), the others come back with wrong `nodeData` entries, and `worldInstancedMeshNode` loses its transforms. Every other method still passes the probe and REDcode's smoke test on 2.31.
- **A streaming sector's `nodeData` has new field names** in `cr2w.*` JSON. WolvenKit 10 reads each entry as the game's `worldCompiledNodeInstanceSetupInfo`, with one entry per node in node order:

  | Was (WolvenKit 9.1) | Now |
  |---|---|
  | `NodeIndex` (wrong on 2.5) | `nodeIdx`, a number as text |
  | `Position`, `Orientation` | `transform.position`, `transform.orientation` |
  | `Scale` | `scale` |
  | `Pivot` | `secondaryRefPointPosition` |
  | `Bounds.Min`, `Bounds.Max` | `streamingRefPoint`, `preboostStreamingRefPoint`: never bounds |
  | `Id` | `patchedNode` |
  | `QuestPrefabRefHash`, resolved to the node path | `globalNodeId.hash`, a number only. It is WolvenKit's NodeRef hash: FNV-1a 64 of the path with every `#` left out |
  | `UkHash1` | `proxyMeshId.hash` |
  | `CookedPrefabData` | `externalInplaceResourceHash` |
  | `MaxStreamingDistance`, `UkFloat1` | `secondaryRefPointDistance`, `streamingDistance` |
  | `Uk10` to `Uk14` | `nodeFlags`, `streamingPriority`, `todvisMask`, `instanceIndex`, `dataGroup` |
- **`sector.build` writes game 2.5 sectors**: version 68 and 2.5's node-data record, with the node flags, streaming priority and reference points of base-game 2.5 mesh, entity and light nodes. The scene format and the result are unchanged. Not yet tried in the game.

### New

- **Spoken lines in `loc.*`** (issue #9, for EDGEquest): the subtitles of the base game and Phantom Liberty, read offline from `localization\<language>\subtitles\**\*.json`, with their voice-over files and lengths. A scene line's `locstringId.ruid` is the line's `stringId`. The first call per language builds an index in about 3 s; searches after it take a few milliseconds.
  - **`loc.lines(stringIds, language, projectDir?)`**: one result per stringId (decimal text), with both texts, the subtitle file and the row (`index`) in it, `voice` (the `.wem` paths from `voiceovermap.json` and `voiceovermap_1.json`), `length` (seconds, from `stringidvariantlengthsreport.json`) and `otherVoices` (the helmet, holocall and rewinded maps that have it).
  - **`loc.searchLines(query, language, limit, projectDir?)`**: lines with every word of the query in either text, or the stringId given as a number, as `{ total, items }`.
  - **`loc.projectLinesFiles(projectDir, language)`** and **`loc.newLinesFile(projectDir, modName, language)`**: a project's subtitle files, and an empty one when it has none, written to `source/archive/<mod>/localization/<language>/subtitles/<mod>.json` and named in its `.xl` under `localization: subtitles:` without rewriting the rest of the file.
  - With a project folder, the project's own subtitle entries win over the game's, as for onscreens: the files its `.xl` files name under `localization: subtitles:`, and any under `source/archive` in a `<language>\subtitles` folder.
- **`project.import` takes a `texture` setup** (issue #8, for EDGEmat) for the `.xbm` an image import writes. It is a preset, `"color"` (`TEXG_Generic_Color`, `TCM_QualityColor`, gamma on), `"normal"` (`TEXG_Generic_Normal`, `TCM_Normalmap`) or `"grayscale"` (`TEXG_Generic_Grayscale`, `TCM_QualityR`, `TRF_Grayscale`), or a texture group name, or an object `{ group, compression, rawFormat, isGamma, hasMipchain, isStreamable }` whose missing fields come from the group's defaults in WolvenKit. Given, it wins over an existing texture's setup. Without it, imports work as before.
- **`project.import` reports the texture setup** it wrote, read back from the `.xbm`, as `texture: { group, compression, rawFormat, isGamma, hasMipchain, isStreamable }`.
- **`mesh.items(path, appearance?, language?)`**, for REDcode's Find Items in the mesh viewer: the TweakDB item records whose look shows a mesh, found backwards along the chain from record to factory `.csv` to root `.ent` to `.app` look to mesh. Each item has its record, type, display name, `appearanceName`, the mesh appearance it shows, the `.app` and its look, and the root entity with its entries for that look (`s1_casual_01_basic_01_&Male`). `looks` lists every `.app` look that shows the mesh, also those no item reaches. With `appearance`, only the looks that show that mesh appearance. The first call builds an index from file headers in under a second after TweakDB loads; a call after it takes a fraction of a second. Covers items that pick their look by `appearanceName` (clothing: 2,375 records in game 2.5); weapons, which pick theirs by visual tags, are not found, nor a mesh that only a part entity holds.

## 0.10.0 (2026-10-16)

New in 0.10.0: **`loc.*`**, the game's on-screen texts (LocKeys), read offline from each language's `onscreens.json` in the base game and Phantom Liberty. REDcode's LocKeys view and its `loc_*` MCP tools use them.
- **`loc.languages`** lists the 19 languages the game has text for, and which of the base game and Phantom Liberty have each.
- **`loc.search`** finds entries by `LocKey#123` or a number, a secondary key, or words that must all be in the text.
- **`loc.get`** looks entries up by key, for hovers and for showing the text beside a `LocKey#...` value.
- Every entry carries its onscreens file and its row in that file, so a client can open it there.
- With a project folder, the project's own onscreens files win over the game's, as ArchiveXL makes them do in game. These are the files its `.xl` files name under `localization: onscreens:`, and any under `source/archive` in a `<language>\onscreens` folder.
- **`loc.projectFiles`** lists a project's onscreens files for a language.
- **`loc.newFile`** makes one when there is none. It writes `source/archive/<mod>/localization/<language>.json` and names it in the project's `.xl`, adding lines without rewriting the rest of the file.

## 0.9.0 (2026-10-16)

New in 0.9.0:
- **`sound.importWavs`** (issue #6) overrides game sound effects in a project from WAV files named after the sound's hash (`832549876.wav`).
  - It writes the changed `.opuspak` files and a new `sfx_container.opusinfo` into the project's `source/archive`, and earlier overrides there stay.
  - Each WAV is encoded with opus-tools the way WolvenKit's sound import does it, on a copy, so the original file is left alone.
- **`project.import` takes a `template`** (issue #5), for turning a GLB into a new asset.
  - WolvenKit only imports a model over an existing `.mesh`. The template, a game path or a `.mesh` on disk, is copied to the target first.
  - Each appearance is then sized to the new submesh count, and the result reports the submeshes, the GLB's material names per submesh, the material entries and the appearances.
  - The README has a table of what a mesh import keeps.
- **WolvenKit now comes from its NuGet packages** (`WolvenKit.Core`, `.RED4`, `.Common`, `.Modkit` 9.1.0) instead of a submodule build. The output is the same apart from WolvenKit's debug symbols, so the zip is smaller.

## 0.8.0 (2026-10-15)

New in 0.8.0: **TweakDB, read offline** from the game's `r6/cache/tweakdb_ep1.bin`, or `tweakdb.bin` when there is none. The first TweakDB call loads it in about 12 s.
- `tweakdb.types`: the record types with their counts.
- `tweakdb.records`: records by type or path prefix, paged, each with the flats you ask for. Short type names such as `Clothing` work, and subtypes are included.
- `tweakdb.record`: one record with every flat.
- `tweakdb.flat`: one flat by path.
- `tweakdb.search`: records whose path contains every word.
- `tweakdb.names`: ids to paths. It needs no load and ignores offset bits (issue #3).

In flat values, record links are paths and LocKeys are `{ key, text }`, with the text filled in when `resolveLocKeys` is set (issue #4).

New in 0.7.2: `sound.events`, the sound event names WolvenKit knows, with their Wwise hashes. They name the event groups in `sfx_container.opusinfo`.

## 0.7.1 (2026-10-14)

New in 0.7.1:
- `archive.extractFromMod`: extracts resources, by game path or hash, from any `.archive` on disk, such as a mod in REDdeck's library, the way `archive.extractMany` does for the game's archives. No game needed. A resource that is missing or fails is listed, and the rest are extracted. REDcode's `redcode.extractToProject` uses it so REDdeck can add resources from a mod's archives to the open project.

## 0.7.0 (2026-10-14)

New in 0.7.0, for REDcode's Export and Import:
- `project.export`: a game file under a project's `source/archive` to the same path under `source/raw` through WolvenKit's exporter (glTF for meshes with their material textures, an image for textures, WAV for sounds, and the other types WolvenKit converts), returning every file written. Types WolvenKit cannot export are refused instead of having their buffers dumped. WolvenKit's per-process memory of exported files is cleared first, so exporting the same file twice works.
- `project.import`: a raw file back into `source/archive`. A re-imported texture keeps all its settings: the bridge hands WolvenKit the existing texture's setup, as WolvenKit's import dialog does, and restores the flags its import settings cannot carry.
- `texture.imageToPng`: a plain TGA, DDS, TIFF, BMP, JPG or PNG to PNG, for viewing raw textures.

## 0.6.1 (2026-10-14)

New in 0.6.1, for REDcode's Pack:
- `project.pack`: packs a folder (a project's `source/archive`) into `<modName>.archive` through WolvenKit's packer, the same archive WolvenKit's Pack writes, and reports the paths with capital letters or spaces, the warning WolvenKit gives because the game may not resolve them. Checked against a WolvenKit pack of the same project: same resources, same sizes.

## 0.6.0 (2026-10-13)

New in 0.6.0, for EDGEbuilder:
- `mesh.info` and `mesh.infoMany`: a mesh's bounding box and appearance names, or an entity template's mesh components with their offsets and the union of their boxes.
- `sector.build`: bakes an EDGEbuilder scene (format 1, see https://edgemodders.github.io/EDGEbuilder/scene.html) into a `worldStreamingSector` with mesh, entity and light nodes, a `worldStreamingBlock` with the streaming box, and an ArchiveXL `.xl` that patches the block into the world, written into a REDcode project. Verified by the probe against the game's files; not yet loaded in the game.

## 0.5.1 (2026-10-13)

Since 0.4.0:

- 0.5.0: document sessions for REDcode's CR2W editor: `cr2w.open`, `cr2w.getNode`, `cr2w.getNodes`, `cr2w.patch` (JSON Patch with the inverse for undo), `cr2w.save`, `cr2w.close`, `cr2w.search`, `cr2w.describeType`, `cr2w.createInstance`. Small array elements carry their value inline.
- 0.5.1: `texture.toPng` and `texture.fileToPng` write the image the right way up. Game textures are stored bottom-up and WolvenKit flips them on export; 0.5.0 did not, so textures showed upside down and atlas rectangles sat on the wrong rows.

## 0.4.0 (2026-10-13)

Methods: info, archive.list, archive.extract, archive.openMod, cr2w.read, project.* (see README).
