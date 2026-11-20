# Changelog

What changed in each REDbridge release, for the tools that use it (REDcode, EDGEdeck, EDGEbuilder) and for modders. The GitHub release of each version takes its notes from its section here.

How to keep it:

- With each change a client or modder would notice, add a line under **Unreleased**, in its group: **New**, **Changed**, **Fixed** or **Other**. One change per line, in the commit that makes the change. Leave out what only the probe sees.
- Before a release, raise `<Version>` in `src/REDbridge/REDbridge.csproj`, rename **Unreleased** to `## <version> (<date>)` and start a new empty **Unreleased** above it. The release workflow stops before releasing anything if the version has no section.
- Links are full URLs.
- `npm run release:notes` prints the GitHub notes: how to install, the section, and the WolvenKit version it is built with.
- Sections up to 0.10.0 were copied from their GitHub releases; they predate the groups and keep the old name, REDdeck.

## Unreleased

### New

- `anims.sync({ path, clips })` reads the sync tracks of a synced animation set's clips: the extra joints past the rig's own (`syncTrajectory`, `syncTrajectory1`, ...), each a partner's Root as seen from this member's Root at the same moment, which WolvenKit's glTF export leaves out. Each track answers `translation` and `rotation` as `{ times, values }`: times in seconds, values in glTF axes as the GLB's own joints, constant keys as one key at time 0. `path` is a game path, a hash or an `.anims` file; without `clips`, every clip. A clip with no extra joints answers `tracks: []`; a SIMD clip with extra joints answers an `error` (not decoded yet). For REDcode's synced sets (issue #19: https://github.com/Edgemodders/REDbridge/issues/19).

### Fixed

- `project.export` of an `.anims` with `rootMotion` writes spline root motion (`animSplineCompressedMotionExtraction`) keyed in seconds. WolvenKit 10.0.0-beta.1 keyed it by the clip's fraction, so the motion played in the clip's first second: the Trauma Team AV's 7.3 s flight in `synced__q001_00c_trauma_arrives_soldiers__av` now ends at 7.3 s. Reported to WolvenKit: https://github.com/WolvenKit/WolvenKit/issues/3304 (issue #19).
- `project.import` of an `.anims.glb` with a spline root motion clip stores its root motion as linear (`animLinearCompressedMotionExtraction`), keyed in seconds, and says so in `notes`. WolvenKit's spline encoder wrote every joint's keys into the root motion, so the imported clip could not be exported or read again ("Motion extraction in joint 1"). Whether the game plays such a clip as it played the spline one is unverified (issue #19).

## 0.16.0 (2026-11-19)

### New

- `sector.build` takes `removals`: node-data entries of the game's own sectors to remove, each `{ sector, index, kind, hash, proxies }` (`index` as `sector.nodes` gives it, `kind` the class of the node the entry places), written as ArchiveXL node deletions (`streaming: sectors:` with `expectedNodes`, the sector's entry count, and `nodeDeletions`) into the scene's `.archive.xl`. Each is checked against the sector first: the entry in range, its node of that class, and with a hash, the entry's own global id. One that fails is not written and comes back in `refused` with why; what was written is in `removed`, with the node and id. An entity's proxy meshes, which the game shows while the entity is not there, are removed with it (`proxyOf` in `removed`; `proxies: false` keeps them); the first search around a place reads a few hundred sectors (about 30 to 40 s), and what it finds is kept in `%LOCALAPPDATA%\REDbridge\cache` for that game version. A scene with removals and no objects writes only the `.xl`, and removes the sector and block of an earlier bake. For EDGEbuilder's doors (issue #18: https://github.com/Edgemodders/REDbridge/issues/18), in the form its D0 probe found ArchiveXL 1.28.8 takes in game. EDGEbuilder's D1b test passed in game on 2026-11-19 with this build: its bake of V's door and the elevator door wrote four deletions, the two doors and the two stand-ins REDbridge found (one in an interior sector), and both doorways stayed open with EDGEbuilder off.

## 0.15.0 (2026-11-18)

### New

- **`.anims` import from glTF** (issue #14, https://github.com/Edgemodders/REDbridge/issues/14, for EDGEfab's F7b-1): `project.import` of a `<name>.anims.glb` (or `.gltf`) writes `<name>.anims` through WolvenKit's ImportAnims, where it used to refuse it as a mesh with no mesh to import over.
  - Where the set comes from: with `keep` (the default), the set already at the target is imported over, and its clips the GLB lacks stay. For a new set, pass `template` (an `.anims` to start from, a game path or a file) or the new `rig` (a game rig: an empty set on it). `keep: false` starts again from the template or rig.
  - The answer's `anims` gives the set as written (`clips`, as `anims.info` reads them, and `rig`), with `from`, `updated`, `added`, `kept`, `skipped` (each with why) and WolvenKit's `notes`.
  - A GLB WolvenKit cannot take writes nothing, and the error says why: a joint the rig lacks, a morph (weights) channel, two clips with one name, root motion it cannot encode.
  - Checked on game 2.5: a clip exported with `rootMotion: true` and imported back keeps its length, frame count and root motion. Its root moves 14.10 m both before and after. WolvenKit stores the root motion as `animLinearCompressedMotionExtraction` whatever the game's clip used. Whether the game plays an imported clip is unverified.
  - Example:

    ```json
    { "method": "project.import", "params": { "file": "<project>/source/raw/mymod/jump.anims.glb", "rawDir": "<project>/source/raw", "archiveDir": "<project>/source/archive", "rig": "base\\characters\\base_entities\\man_base\\man_base.rig" } }
    ```

## 0.14.0 (2026-11-16)

### New

- **`anims.info`** (issues #14 and #17, https://github.com/Edgemodders/REDbridge/issues/14, https://github.com/Edgemodders/REDbridge/issues/17, for EDGEdir's animation index): the clips of animation sets without decoding any, each with its name, `duration`, `frames`, `fps`, `type` (`Normal` or additive), `rootMotion` (the motion extraction's type, or null), `buffer` (`compressed` or `simd`) and `joints`, with the set's `rig`. Give `files` (game paths or hashes), or none for every `.anims` in the game a page at a time (`offset`, `limit` up to 1,000). A file that does not read answers its `error`, and the rest still answer. On game 2.5: 14,028 sets, about 16 ms a set, so the whole game in about 4 minutes.

  ```json
  { "method": "anims.info", "params": { "files": ["base\\animations\\quest_custom\\q001\\q001_00c_rescuing_girl\\q001_00c_trauma_arrives\\q001_00c_trauma_arrives_male_average.anims"] } }
  ```
- **`options.animations` for `project.export`** of an `.anims` (#17): only the named clips go into the glTF, the same glTF as before otherwise. A name the set lacks is refused with the name and the set's clip count. Only the chosen clips are decoded (the file is still read whole): one clip of `generic_male_average__stand__2h_on_sides__01.anims` exports in 1 s as 150 KB, the whole set in 7 s as 42 MB.
- **`extension` for `archive.list`** (#17): only files with that ending, any case, with or without the dot (`".anims"`). `total` and the pages count only those: the game's 14,028 `.anims` in three pages of 5,000, the first in about 3 s.

## 0.13.0 (2026-11-15)

### New

- **`project.export` for animation** (part of issue #14, https://github.com/Edgemodders/REDbridge/issues/14, for REDcode's animation viewer, step A2). Three new `options`, checked on game 2.5:
  - `rig` (or `rigs`, a list) for a `.mesh`: the mesh skinned on that rig's skeleton, the whole skeleton in the GLB. A game path or a `.rig` file on disk or under `archiveDir`. Several rigs are joined into one skeleton. A body needs its deformations rig (a garment likely too, unverified), `base\characters\base_entities\man_base\deformations_rigs\man_base_deformations.rig` (or `woman_base`), which has the animation bones (`Root`, `Hips`, `Spine`, ...) and the muscle bones. Add `man_base.rig` in `rigs` for the six bones only it has (`LeftEye`, `RightEye`, `WeaponLeft`, `WeaponRight`, `Trajectory`, `reference_joint`). A mesh with bones the rig lacks is refused with their names, where WolvenKit wrote a GLB that failed to save.
  - `appearance` for a `.ent`: one `<name>.ent.glb` with the entity in that appearance (its name, the `.app` appearance's name, or `"default"` for the first). Every rig the entity's animated components name (a character's body, deformations and face rigs) is joined into one skeleton. Skinned meshes of the entity, its appearance and the appearance's parts are on it, with a morph target's base mesh unmorphed. Rigid meshes (a vehicle's body, doors, engine and steering wheel, a character's weapon) hang under the bone their slot names, so they follow it. The answer's `entity` lists the appearance, the rigs, the joint count, each mesh with what it is on (`skin`, `bone <name>`, another component, or `root`), and what was left out with why: the appearance's far proxy, which would cover the real meshes, components switched off in the appearance, and files not in the archives. A Trauma Team paramedic comes out as 22 meshes on 512 joints, an Archer Hella as 21 parts on 51.
  - `rootMotion: true` for an `.anims`: the root motion on the `Root` node's translation and rotation channels, which WolvenKit's default export leaves out, so a clip moves through the scene. The clip's `extras.rootMotionType` then names the encoding (`TransformSplineSparseFrames`, `TransformLinearSparseFrames`, ...); without it, it says `Unknown`.

  ```json
  { "method": "project.export", "params": { "file": "<project>/source/archive/ep1/characters/entities/corpo/corpo__ep1_traumateam_ma.ent", "archiveDir": "<project>/source/archive", "rawDir": "<project>/source/raw", "options": { "appearance": "corpo__traumateam_ma_paramedic__lvl1_01", "meshMaterials": false } } }
  ```

### Other

- Not in this release from issue #14: `.anims` import, `anims.info` and rig import, for EDGEfab later. Not done in an entity export: a skinned component's chunk mask (submeshes it hides; rigid ones keep theirs) and each component's mesh appearance; the materials are the mesh's own. A vehicle's shadow-only mesh (`body_shadow`) is kept, since nothing in the component marks it.
- WolvenKit 10.0.0-beta.1's multi-mesh export fails without materials (it saves an empty material list). With `meshMaterials: false`, REDbridge runs the same steps itself.

## 0.12.1 (2026-11-08)

### Changed

- **`sector.build`: a mesh's `streaming.primary` is also its hide distance** (issue #16, https://github.com/Edgemodders/REDbridge/issues/16). Mesh nodes, solid ones too, get `forceAutoHideDistance` set to the scene's `streaming.primary`, since the game otherwise hides a small mesh by its size whatever its streaming distance says (200 crates at 120 m vanished at about 60 m). With no `streaming` it stays 0 and the game decides, so bakes by size are as before.

## 0.12.0 (2026-11-05)

### New

- **`sector.find` and `sector.nodes`** (issue #11, https://github.com/Edgemodders/REDbridge/issues/11), for EDGEbuilder's world map, sector view and in-game inspector.
  - `sector.find({ box?, kinds?, levels? })` lists the streaming sectors of Night City's two streaming blocks, the base game's and Phantom Liberty's (26,347 in game 2.5): each with its path, kind (`exterior`, `interior`, `quest`, `navigation`, `alwaysLoaded`), grid and level from names like `exterior_-12_-7_0_1`, streaming box, the block that lists it, the archive it is in and its number of variants. Optionally only those whose box meets `box`, of the given kinds and levels. The list is read once per bridge, in about 2.5 s, and kept: later calls take milliseconds.
  - `sector.nodes(path, { kinds?, bounds?, archive?, root? })` lists one sector's placed things, one item per node-data entry: the node's class, its reference path when known, its global node id, the resource it shows (mesh, entity template, decal material, effect, ...) and appearance, the world transform, its streaming ranges, and for instanced meshes one world placement per copy. Game 2.5's cooked nodes hold several meshes each; every item names its own. With `bounds: true` every mesh item has a world box (each mesh read once per bridge); instanced meshes always have the one the sector keeps. `archive` and `root` read a mod's sector, as for `mesh.info`.

  ```json
  { "method": "sector.find", "params": { "box": { "min": [-1700, 1200, 0], "max": [-1650, 1250, 50] }, "kinds": ["exterior"] } }
  { "method": "sector.nodes", "params": { "path": "base\\worlds\\03_night_city\\_compiled\\default\\exterior_-25_18_0_0.streamingsector", "bounds": true } }
  ```

  Phantom Liberty's sectors have their own paths (`...\default\ep1\...`), so none replaces a base sector of the same path in 2.5 (`replaces` is false for all). Mod sectors are read by `sector.nodes` only; `sector.find` lists the game's.
- **`mesh.info`, `mesh.infoMany` and `sector.build` read mods** (issue #12, https://github.com/Edgemodders/REDbridge/issues/12), for EDGEbuilder's Browse of mod assets. Two optional parameters name where to look before the game's archives: `archive`, a mod's `.archive` anywhere on disk, and `root`, a folder of loose files laid out as in an archive, such as a REDcode project's `source\archive`. The answer is the same as for a game file, and an entity's parts are looked up the same way, so a part may be in the mod or in the game.

  ```json
  { "method": "mesh.infoMany", "params": { "paths": ["edgefab\\chair\\chair.mesh"], "archive": "E:\\...\\archive\\pc\\mod\\edgefab_chair.archive" } }
  { "method": "mesh.infoMany", "params": { "paths": ["edgefab\\chair\\chair.mesh"], "root": "E:\\...\\MyMod\\source\\archive" } }
  ```

  The archive is opened for the call and closed after it, so REDcode or WolvenKit can pack the mod again in between; reading a mod archive's index takes a few milliseconds.
- **A solid mesh in `sector.build`** (issue #15, https://github.com/Edgemodders/REDbridge/issues/15), for EDGEbuilder's Solid switch. `"solid": true` on a scene's mesh writes a kinematic `worldInstancedDestructibleMeshNode` in place of `worldMeshNode`: it stops V, bullets, melee and cars, stays put, looks the same and takes an appearance. EDGEbuilder's bake probe proved this node in game 2.5, and `sector.build` writes the same bytes. Without `solid`, a bake stays as it was. A mesh with no collision data still bakes, with a warning that names it, since it shows but is not solid; `solid` on an entity or a light is a warning too, and they bake as usual.

  ```json
  { "id": "crate", "kind": "mesh", "resource": "base\\...\\cargo_crate_a.mesh", "solid": true, "transform": { } }
  ```

- **`mesh.info` and `mesh.infoMany` say `hasCollision`** (issue #15): true when a mesh has `meshMeshParamPhysics`, so an editor can say before a bake which objects cannot be solid. An entity's is null.
- **An entity's part that could not be read** now says why, in the part's new `error` field, and has no bounds.

### Changed

- **`sector.build` takes a resource that is not in the game's archives** when the scene gives the object's `bounds`: a warning names the resource, so an editor can say which mods the bake needs. Without bounds, or without `archive` or `root` that has it, it is still refused, and the message says what to give.

- **`sector.build` writes the `.xl` in ArchiveXL's `streaming: blocks:` form** (issue #13, https://github.com/Edgemodders/REDbridge/issues/13), the form EDGEbuilder's bake probe loaded in game 2.5 with ArchiveXL 1.28.6, instead of a resource patch of the world's `all.streamingblock`:

  ```yaml
  streaming:
    blocks:
      - edgebuilder\<name>\<name>.streamingblock
  ```

- **The file is now `<name>.archive.xl`**, so it sits beside `<name>.archive` when the project is packed under that mod name. A `<name>.xl` written by an earlier `sector.build` is removed, with a warning, so the block is not added twice; one that REDbridge did not write is left alone.

### Fixed

- **`cr2w.search` finds a material's values** (REDcode issue #20, https://github.com/Edgemodders/REDcode/issues/20): a search for `mlsetup`, a setup's file name or `MultilayerSetup` now finds them in a mesh's `localMaterialBuffer.materials.<n>.values`, where it found nothing before. A hit's preview is `MultilayerSetup: <path>`.
- **`mesh.items` finds outfit sets, glasses, helmets, masks and more** (REDcode issue #17, https://github.com/Edgemodders/REDcode/issues/17): 23 of the 229 appearance files it reads were skipped, because some of their looks have no component list at all. Among them both outfit sets (`outfit\set_01.app`, base game and Phantom Liberty), every pair of glasses, several helmets and masks, food, drinks and two cyberware. The cop jacket's mesh now gives `Items.Cop_01_Set_Jacket` in its two looks, where it gave nothing.
- **`cr2w.search` previews end with the file name**: a long preview is cut in the middle, not at the end, and a reference shows its path alone, without its type and hash.

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
