"""Exercises the bridge over its real protocol: JSON-RPC 2.0, Content-Length framed, on stdio.

    python -I tools/probe.py <path to REDbridge.exe or .dll> "<Cyberpunk 2077 folder>" [out dir]
    npm run probe -- "<Cyberpunk 2077 folder>"        (same thing, using the Release build)

Standard library only. The bridge's stderr log is passed through.
"""
import json
import os
import shutil
import subprocess
import struct
import sys
import time
import zlib


def write_png(path, size=256):
    """A plain RGBA PNG with a gradient, for the import checks."""
    rows = b"".join(b"\x00" + bytes(v for x in range(size) for v in (x % 256, y % 256, 128, 255)) for y in range(size))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


class Bridge:
    def __init__(self, exe, game):
        exe = os.path.abspath(exe)
        cmd = ([exe] if exe.lower().endswith(".exe") else ["dotnet", exe]) + ["--game", game]
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr)
        self.n = 0

    def call(self, method, **params):
        self.n += 1
        body = json.dumps({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params}).encode()
        self.p.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
        self.p.stdin.flush()
        headers = {}
        while True:
            line = self.p.stdout.readline()
            if not line or line == b"\r\n":
                break
            k, v = line.decode().split(":", 1)
            headers[k.strip().lower()] = v.strip()
        msg = json.loads(self.p.stdout.read(int(headers["content-length"])))
        if "error" in msg:
            raise RuntimeError(f"{method}: {msg['error'].get('message')}")
        return msg["result"]

    def close(self):
        self.p.stdin.close()
        self.p.wait(timeout=30)


def main():
    exe, game = sys.argv[1], sys.argv[2]
    out = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.getcwd(), "probe-out")
    b = Bridge(exe, game)
    t = time.time()

    def step(name, fn):
        s = time.time()
        r = fn()
        text = str(r)
        print(f"[{time.time() - s:6.2f}s] {name}: {text if len(text) <= 600 else text[:600] + f' ... ({len(text):,} characters)'}")
        return r

    info = step("bridge.info", lambda: b.call("bridge.info"))
    assert info["gameLoaded"], "game not loaded"
    archives = step("archive.archives", lambda: [(a["name"], a["files"]) for a in b.call("archive.archives")][:4])
    page = step("archive.list base/characters", lambda: b.call("archive.list", prefix="base\\characters", limit=3))
    mods_dir = os.path.join(game, "archive", "pc", "mod")
    mod_archives = [f for f in (os.listdir(mods_dir) if os.path.isdir(mods_dir) else []) if f.lower().endswith(".archive")]
    if mod_archives:
        opened = step("archive.openMod " + mod_archives[0], lambda: b.call("archive.openMod", path=os.path.join(mods_dir, mod_archives[0]))["files"])
        print("   first:", [i["name"] for i in b.call("archive.openMod", path=os.path.join(mods_dir, mod_archives[0]))["items"][:3]])
    else:
        print("   archive.openMod: no mod archives in archive/pc/mod, skipped")
    print("   first:", [i["name"] for i in page["items"]])
    hits = step("archive.search .xbm", lambda: b.call("archive.search", query=".xbm", limit=5))
    step("hash.fnv1a64", lambda: b.call("hash.fnv1a64", value="base\\characters"))
    step("sound.events", lambda: (lambda r: (r["count"], r["events"][0]))(b.call("sound.events")))
    print("   ui_menu_value_up hashes to 2913248083:", any(e["name"] == "ui_menu_value_up" and e["hash"] == 2913248083 for e in b.call("sound.events")["events"]))
    step("tweakdb.names (offset bits ignored)", lambda: b.call("tweakdb.names", ids=["0xABCDEF0BF5E188EC"]))
    step("tweakdb.types (loads TweakDB)", lambda: (lambda r: (r["file"], r["records"], r["types"][0]))(b.call("tweakdb.types")))
    step("tweakdb.flat Items.money.quality", lambda: b.call("tweakdb.flat", path="Items.money.quality")["value"])
    step("tweakdb.records Clothing", lambda: (lambda r: (r["total"], r["items"][0]))(b.call("tweakdb.records", type="Clothing", flats=["entityName", "displayName", "equipArea"], limit=1, resolveLocKeys=True)))
    converse = "base\\characters\\garment\\player_equipment\\feet\\s1_055_shoe__converse\\s1_055_pma_shoe__converse.mesh"
    items = step("mesh.items (Converse high-tops)", lambda: (lambda r: (len(r["items"]), len(r["looks"]), r["items"][0]["record"] if r["items"] else None))(b.call("mesh.items", path=converse)))
    print("   red_yellow_graphic is Items.CasualShoes_01_basic_01:", any(i["record"] == "Items.CasualShoes_01_basic_01" for i in b.call("mesh.items", path=converse, appearance="red_yellow_graphic")["items"]))

    ent = b.call("archive.list", prefix="base\\characters", limit=50)["items"]
    ent = next((i for i in ent if i["name"].endswith(".ent")), None) or next(iter(ent))
    j = step(f"cr2w.toJson {ent['name']}", lambda: b.call("cr2w.toJson", path=ent["name"], outFile=os.path.join(out, "file.json")))
    step("hash.resolve", lambda: b.call("hash.resolve", hash=ent["hash"]))
    step("cr2w.fromJson (round trip)", lambda: b.call("cr2w.fromJson", jsonFile=j["file"], outFile=os.path.join(out, "file.roundtrip.bin")))
    orig = step("archive.extract (original)", lambda: b.call("archive.extract", path=ent["name"], outFile=os.path.join(out, "file.original.bin")))
    print("   round trip bytes equal:", os.path.getsize(j["file"].replace("file.json", "file.roundtrip.bin")) == orig["bytes"])

    xbm = next((i for i in hits["items"] if i["name"].endswith(".xbm")), None)
    if xbm:
        step(f"texture.toPng {xbm['name']}", lambda: b.call("texture.toPng", path=xbm["name"], outFile=os.path.join(out, "texture.png")))

    # mesh.info and sector.build, for EDGEbuilder: a crate mesh, a crate entity and a light baked into a sector.
    crates = b.call("archive.search", query="cargo_crate", limit=200)["items"]
    mesh = next((i for i in crates if i["name"].endswith(".mesh")), None)
    ent = next((i for i in crates if i["name"].endswith(".ent")), None)
    if mesh and ent:
        mi = step(f"mesh.info {mesh['name']}", lambda: b.call("mesh.info", path=mesh["name"]))
        assert mi["type"] == "mesh" and mi["bounds"] and len(mi["bounds"]["min"]) == 3, mi
        ei = step(f"mesh.info {ent['name']}", lambda: b.call("mesh.info", path=ent["name"]))
        assert ei["type"] == "entity", ei
        print("   entity parts:", [(p["mesh"].split("\\")[-1], p["bounds"] is not None) for p in ei["parts"]][:4], "appearances:", ei["appearances"][:4])
        many = step("mesh.infoMany", lambda: b.call("mesh.infoMany", paths=[mesh["name"], ent["name"], "base\\nope.mesh"]))
        assert many[2]["error"] and not many[0]["error"], many
        scene = {
            "format": 1, "name": "Probe Scene", "game": "2.31",
            "objects": [
                {"id": "crate_a", "kind": "mesh", "resource": mesh["name"], "appearance": (mi["appearances"] or ["default"])[0],
                 "transform": {"position": [-1200.0, 1500.0, 20.0], "rotation": [0, 0, 0.3826834, 0.9238795], "scale": [1, 1, 1]}},
                {"id": "crate_b", "kind": "entity", "resource": ent["name"],
                 "transform": {"position": [-1198.0, 1500.0, 20.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}, "streaming": {"primary": 120, "secondary": 100}},
                {"id": "lamp", "kind": "light", "props": {"type": "spot", "color": [255, 200, 120], "intensity": 800, "radius": 12, "outerAngle": 60},
                 "transform": {"position": [-1199.0, 1500.0, 23.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}},
            ],
        }
        project = os.path.join(out, "project")
        os.makedirs(project, exist_ok=True)
        built = step("sector.build", lambda: b.call("sector.build", project=project, scene=scene))
        assert built["nodes"] == 3 and os.path.exists(built["sector"]) and os.path.exists(built["block"]) and os.path.exists(built["xl"]), built
        back = step("cr2w.fileToJson (the sector reads back)", lambda: b.call("cr2w.fileToJson", file=built["sector"]))
        root = json.loads(back["json"])["Data"]["RootChunk"]
        kinds = [n["Data"]["$type"] for n in root["nodes"]]
        data = root["nodeData"]["Data"]
        assert kinds == ["worldMeshNode", "worldEntityNode", "worldStaticLightNode"], kinds
        # Game 2.5's node-data record (worldCompiledNodeInstanceSetupInfo): one entry per node, in node order.
        assert len(data) == 3 and [d["nodeIdx"] for d in data] == ["0", "1", "2"], [d.get("nodeIdx") for d in data]
        assert data[1]["streamingDistance"] == 120 and data[1]["secondaryRefPointDistance"] == 100, data[1]
        crate_ref = b.call("hash.fnv1a64", value="edgebuilder/probe_scene/crate_a")["hash"]
        assert data[0]["globalNodeId"]["hash"] == crate_ref, (data[0]["globalNodeId"], crate_ref)
        print("   sector version", root["version"], "nodes", kinds, "crate_a at", data[0]["transform"]["position"], "streams from", data[0]["streamingRefPoint"])
        blockBack = step("cr2w.fileToJson (the block reads back)", lambda: b.call("cr2w.fileToJson", file=built["block"]))
        desc = json.loads(blockBack["json"])["Data"]["RootChunk"]["descriptors"]
        assert len(desc) == 1 and desc[0]["data"]["DepotPath"]["$value"].endswith("probe_scene.streamingsector"), desc
        # ArchiveXL's streaming: blocks: form in <name>.archive.xl, as EDGEbuilder's bake probe loaded in game 2.5 (issue #13).
        xl_lines = open(built["xl"], encoding="utf-8").read().strip().splitlines()
        assert built["xl"].endswith("probe_scene.archive.xl"), built["xl"]
        assert xl_lines[-3:] == ["streaming:", "  blocks:", "    - " + built["blockPath"]], xl_lines
        assert not os.path.exists(os.path.join(os.path.dirname(built["xl"]), "probe_scene.xl")), "the old probe_scene.xl is still there"
        print("   streaming box", built["streamingBox"], "xl:", xl_lines[-3:], "warnings:", built["warnings"])
        # sector.nodes reads the baked sector back from the project's loose files (issues #11 and #12).
        mine = step("sector.nodes (the baked sector, root)", lambda: b.call("sector.nodes", path=built["sectorPath"], root=os.path.join(project, "source", "archive")))
        assert [i["kind"] for i in mine["items"]] == ["worldMeshNode", "worldEntityNode", "worldStaticLightNode"], mine["items"]
        first = mine["items"][0]
        assert first["resource"] == mesh["name"] and first["ref"] == "edgebuilder/probe_scene/crate_a" and first["hash"] == crate_ref, first
        assert [round(v, 3) for v in first["transform"]["position"]] == [-1200.0, 1500.0, 20.0], first["transform"]
        assert mine["items"][1]["resource"] == ent["name"] and mine["items"][1]["streaming"] == {"primary": 120, "secondary": 100}, mine["items"][1]
        # A solid mesh (issue #15): the node EDGEbuilder's probe proved in game, and whether the mesh can be solid.
        assert isinstance(mi["hasCollision"], bool) and ei["hasCollision"] is None, (mi["hasCollision"], ei["hasCollision"])
        solid_scene = {"format": 1, "name": "Probe Solid", "objects": [
            {"id": "solid", "kind": "mesh", "resource": mesh["name"], "solid": True, "transform": {"position": [-1200.0, 1500.0, 20.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}},
            {"id": "plain", "kind": "mesh", "resource": mesh["name"], "transform": {"position": [-1198.0, 1500.0, 20.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}},
            {"id": "far", "kind": "mesh", "resource": mesh["name"], "streaming": {"primary": 120, "secondary": 96}, "transform": {"position": [-1196.0, 1500.0, 20.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}},
            {"id": "far_solid", "kind": "mesh", "resource": mesh["name"], "solid": True, "streaming": {"primary": 150}, "transform": {"position": [-1194.0, 1500.0, 20.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}},
        ]}
        solid = step("sector.build (solid: true)", lambda: b.call("sector.build", project=project, scene=solid_scene))
        assert any("no collision data" in w for w in solid["warnings"]) == (not mi["hasCollision"]), (mi["hasCollision"], solid["warnings"])
        solid_nodes = json.loads(b.call("cr2w.fileToJson", file=solid["sector"])["json"])["Data"]["RootChunk"]["nodes"]
        assert [n["Data"]["$type"] for n in solid_nodes] == ["worldInstancedDestructibleMeshNode", "worldMeshNode", "worldMeshNode", "worldInstancedDestructibleMeshNode"], solid_nodes
        node = solid_nodes[0]["Data"]
        assert node["simulationType"] == "Kinematic" and node["isDestructible"] == 0 and node["turnDynamicOnImpulse"] == 0, node
        assert node["mesh"]["DepotPath"]["$value"] == mesh["name"] and len(node["instanceTransforms"]) == 1, node
        # A scene's streaming distance is also a mesh's hide distance; without one the game decides (issue #16).
        hides = [n["Data"]["forceAutoHideDistance"] for n in solid_nodes]
        assert hides == [0, 0, 120, 150], hides
        print("   forceAutoHideDistance:", hides)
        print("   solid:", mesh["name"].split("\\")[-1], "has collision:", mi["hasCollision"], "warnings:", solid["warnings"])
        # Validation refuses a bad scene and writes nothing.
        try:
            b.call("sector.build", project=project, name="bad", scene={"format": 1, "objects": [{"id": "x", "kind": "mesh", "resource": "base\\nope.mesh"}, {"id": "x", "kind": "thing"}]})
            raise AssertionError("a bad scene was accepted")
        except RuntimeError as e:
            print("   bad scene refused:", str(e).splitlines()[0][:100], "...")
            assert not os.path.exists(os.path.join(project, "source", "archive", "edgebuilder", "bad"))

        # Mod files (issue #12): mesh.info and sector.build read a project's loose files and a mod's archive.
        loose = os.path.join(project, "source", "archive")
        copy_mesh, copy_ent = "redbridge_probe\\crate_copy.mesh", "redbridge_probe\\crate_copy.ent"
        b.call("archive.extract", path=mesh["name"], outFile=os.path.join(loose, copy_mesh))
        b.call("archive.extract", path=ent["name"], outFile=os.path.join(loose, copy_ent))
        lm = step("mesh.info (loose file, root)", lambda: b.call("mesh.info", path=copy_mesh, root=loose))
        assert lm["type"] == "mesh" and lm["bounds"] == mi["bounds"] and lm["path"] == copy_mesh, lm
        le = step("mesh.info (loose entity, its parts from the game)", lambda: b.call("mesh.info", path=copy_ent.replace("\\", "/"), root=loose))
        assert le["type"] == "entity" and le["bounds"] == ei["bounds"] and all(p["error"] is None for p in le["parts"]), le
        missing = b.call("mesh.infoMany", paths=[copy_mesh, "redbridge_probe\\nope.mesh", "..\\..\\..\\scene.json"], root=loose)
        assert missing[0]["error"] is None and "nor in the game's archives" in missing[1]["error"] and missing[2]["error"], missing
        print("   not found:", missing[1]["error"][:90], "| outside the folder:", missing[2]["error"][:60])
        try:
            b.call("mesh.info", path=copy_mesh)
            raise AssertionError("a loose file was found without root")
        except RuntimeError as e:
            print("   without root:", str(e).splitlines()[0][:80])
        one = {"id": "copy", "kind": "mesh", "resource": copy_mesh, "transform": {"position": [-1200.0, 1505.0, 20.0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1]}}
        mod_built = step("sector.build (a loose mesh, root)", lambda: b.call("sector.build", project=project, name="probe_mod", root=loose, scene={"format": 1, "objects": [one]}))
        assert mod_built["nodes"] == 1 and not mod_built["warnings"], mod_built
        try:
            b.call("sector.build", project=project, name="probe_mod_nobounds", scene={"format": 1, "objects": [one]})
            raise AssertionError("a resource found nowhere, without bounds, was accepted")
        except RuntimeError as e:
            print("   no root, no bounds: refused:", str(e).splitlines()[1].strip()[:110])
        with_bounds = dict(one, bounds={"min": lm["bounds"]["min"], "max": lm["bounds"]["max"]})
        warned = b.call("sector.build", project=project, name="probe_mod_bounds", scene={"format": 1, "objects": [with_bounds]})
        assert warned["nodes"] == 1 and any("needs the mod" in w for w in warned["warnings"]), warned
        print("   no root, scene bounds: warning:", warned["warnings"][0][:110])

        # A mod archive in the game's mod folder with a mesh or entity the game does not have, if there is one.
        found = None
        for f in mod_archives:
            for item in b.call("archive.openMod", path=os.path.join(mods_dir, f))["items"]:
                if item["name"].endswith((".mesh", ".ent")) and not b.call("mesh.infoMany", paths=[item["name"]])[0]["error"] is None:
                    found = (os.path.join(mods_dir, f), item["name"])
                    break
            if found:
                break
        if found:
            am = step(f"mesh.info (from {os.path.basename(found[0])})", lambda: b.call("mesh.info", path=found[1], archive=found[0]))
            assert am["error"] is None and am["type"] in ("mesh", "entity"), am
            if am["parts"]:
                print("   parts:", [(p["mesh"].split("\\")[-1], p["bounds"] is not None, (p["error"] or "")[:50]) for p in am["parts"]][:5])
        else:
            print("   mesh.info from a mod archive: no mod mesh or entity outside the game's archives, skipped")
    else:
        print("   mesh.info / sector.build: no cargo_crate mesh and entity found, skipped")

    # sector.find and sector.nodes (issue #11, for EDGEbuilder's world map and inspector): the world's sectors and a sector's nodes.
    every = step("sector.find (reads both streaming blocks)", lambda: b.call("sector.find"))
    secs = every["sectors"]
    assert every["total"] > 20000 and {"exterior", "interior", "quest"} <= {s["kind"] for s in secs}, every["total"]
    assert {s["block"] for s in secs} == {"base", "ep1"}, {s["block"] for s in secs}
    gridded = [s for s in secs if s["grid"]]
    assert gridded and all(s["path"].endswith(f"_{s['level']}.streamingsector") for s in gridded), "a grid name and its level disagree"
    print(f"   {every['total']} sectors, {len(gridded)} with a grid; first:", {k: secs[0][k] for k in ("path", "kind", "grid", "level", "block", "archive")})
    near = step("sector.find (a box in Little China, exterior)", lambda: b.call("sector.find", box={"min": [-1700, 1200, 0], "max": [-1650, 1250, 50]}, kinds=["exterior"], levels=[0]))
    assert near["total"] > 0 and all(s["kind"] == "exterior" and s["level"] == 0 for s in near["sectors"]), near["total"]
    busiest = max(near["sectors"][:12], key=lambda s: b.call("sector.nodes", path=s["path"], kinds=["worldInstancedMeshNode"])["total"])
    nodes = step(f"sector.nodes {busiest['path'].split(chr(92))[-1]} (bounds)", lambda: b.call("sector.nodes", path=busiest["path"], bounds=True))
    items = nodes["items"]
    assert items and all(len(i["transform"]["position"]) == 3 and len(i["transform"]["rotation"]) == 4 for i in items)
    inst = [i for i in items if i["instances"]]
    assert inst, "no instanced mesh in the busiest sector"
    outside = 0
    for e in inst:
        mn, mx = e["bounds"]["min"], e["bounds"]["max"]
        outside += sum(1 for c in e["instances"] if any(c["position"][k] < mn[k] - 5 or c["position"][k] > mx[k] + 5 for k in range(3)))
    assert outside == 0, f"{outside} instanced copies lie outside the box the sector keeps for them"
    shown = [i for i in items if i["resource"]]
    print(f"   {nodes['total']} entries of {nodes['nodes']} nodes, {len(shown)} with a resource, {sum(len(i['instances']) for i in inst)} instanced copies, {sum(1 for i in items if i['ref'])} refs")
    only = b.call("sector.nodes", path=busiest["path"], kinds=["entityNode", "worldCookedStaticMeshNode"])
    assert only["items"] and {i["kind"] for i in only["items"]} <= {"worldEntityNode", "worldCookedStaticMeshNode"}, {i["kind"] for i in only["items"]}
    try:
        b.call("sector.nodes", path=mesh["name"] if mesh else "base\\nope.streamingsector")
        raise AssertionError("sector.nodes took a file that is not a sector")
    except RuntimeError as e:
        print("   not a sector:", str(e)[:90])

    # loc.searchLines, loc.lines and loc.newLinesFile (issue #9, for EDGEquest): spoken lines with their voice-over and length.
    found = step("loc.searchLines 'choom' (builds the index)", lambda: (lambda r: (r["total"], r["items"][0]))(b.call("loc.searchLines", query="choom", language="en-us", limit=50)))
    assert found[0] > 0, found
    again = step("loc.searchLines 'need help' (index kept)", lambda: b.call("loc.searchLines", query="need help", language="en-us", limit=50))
    ids = [i["stringId"] for i in b.call("loc.searchLines", query="choom", language="en-us", limit=50)["items"]] + [i["stringId"] for i in again["items"]]
    lines = step(f"loc.lines ({len(ids)} ids)", lambda: b.call("loc.lines", stringIds=ids + ["1"], language="en-us"))
    assert len(lines) == len(ids) + 1 and all(l["female"] is not None and l["file"] for l in lines[:-1]) and lines[-1]["female"] is None, lines[-1]
    voiced = [l for l in lines if l["voice"] and l["length"]]
    assert voiced, "no line came back with a voice and a length"
    print(f"   {len(voiced)} of {len(ids)} voiced; first:", {k: voiced[0][k] for k in ("stringId", "male", "file", "index", "voice", "length", "otherVoices")})
    lines_project = os.path.join(out, "lines-project")
    shutil.rmtree(lines_project, ignore_errors=True)
    os.makedirs(os.path.join(lines_project, "source", "resources"))
    with open(os.path.join(lines_project, "source", "resources", "probe.archive.xl"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# kept as it is\nlocalization:\n  onscreens:\n    en-us: probe\\localization\\en-us.json\n")
    made = step("loc.newLinesFile", lambda: b.call("loc.newLinesFile", projectDir=lines_project, modName="Probe Lines", language="en-us"))
    xl_text = open(os.path.join(lines_project, made["xl"]), encoding="utf-8").read()
    assert made["created"] and made["xlWritten"] and "  subtitles:\n    en-us: probe_lines\\localization\\en-us\\subtitles\\probe_lines.json" in xl_text and xl_text.startswith("# kept as it is\n"), xl_text
    assert not b.call("loc.newLinesFile", projectDir=lines_project, modName="Probe Lines", language="en-us")["created"]
    # The project's own entry for a game stringId wins: copy the game file's entry into the project file with new text.
    target = voiced[0]
    game_json = json.loads(b.call("cr2w.toJson", path=target["file"])["json"])
    entries = game_json["Data"]["RootChunk"]["root"]["Data"]["entries"]
    mine = next(e for e in entries if str(e["stringId"]) == target["stringId"])
    mine["femaleVariant"] = mine["maleVariant"] = "REDbridge probe line"
    game_json["Data"]["RootChunk"]["root"]["Data"]["entries"] = [mine]
    b.call("cr2w.fromJson", outFile=os.path.join(lines_project, made["file"]), json=json.dumps(game_json))
    over = step("loc.lines with projectDir", lambda: b.call("loc.lines", stringIds=[target["stringId"]], language="en-us", projectDir=lines_project)[0])
    assert over["male"] == "REDbridge probe line" and over["source"] == "project" and over["voice"] == target["voice"], over
    hit = b.call("loc.searchLines", query="redbridge probe", language="en-us", projectDir=lines_project)
    assert hit["total"] == 1 and hit["items"][0]["stringId"] == target["stringId"], hit

    # project.import with a texture setup (issue #8, for EDGEmat): a new .xbm per preset, an object, and the plain default.
    tex_project = os.path.join(out, "texture-project")
    shutil.rmtree(tex_project, ignore_errors=True)
    raw, arc = os.path.join(tex_project, "source", "raw"), os.path.join(tex_project, "source", "archive")
    expected = {
        "color": ("TEXG_Generic_Color", "TCM_QualityColor", True),
        "normal": ("TEXG_Generic_Normal", "TCM_Normalmap", False),
        "grayscale": ("TEXG_Generic_Grayscale", "TCM_QualityR", False),
    }
    for preset, (group, compression, gamma) in expected.items():
        png = os.path.join(raw, "probe", f"plain_{preset}01.png")
        write_png(png)
        r = step(f"project.import texture={preset}", lambda: b.call("project.import", file=png, rawDir=raw, archiveDir=arc, texture=preset)["texture"])
        assert (r["group"], r["compression"], r["isGamma"], r["hasMipchain"]) == (group, compression, gamma, True), r
    png = os.path.join(raw, "probe", "plain_object01.png")
    write_png(png)
    r = step("project.import texture={group, compression, isGamma, hasMipchain}", lambda: b.call("project.import", file=png, rawDir=raw, archiveDir=arc, texture={"group": "TEXG_Generic_Normal", "compression": "TCM_Normalmap", "isGamma": False, "hasMipchain": False})["texture"])
    assert (r["group"], r["compression"], r["isGamma"], r["hasMipchain"]) == ("TEXG_Generic_Normal", "TCM_Normalmap", False, False), r
    # Re-imported without texture, it keeps the setup it has; with a texture, the texture wins.
    r = b.call("project.import", file=png, rawDir=raw, archiveDir=arc)["texture"]
    assert (r["group"], r["compression"], r["hasMipchain"]) == ("TEXG_Generic_Normal", "TCM_Normalmap", False), r
    r = b.call("project.import", file=png, rawDir=raw, archiveDir=arc, texture="color")["texture"]
    assert (r["group"], r["compression"], r["isGamma"]) == ("TEXG_Generic_Color", "TCM_QualityColor", True), r
    png = os.path.join(raw, "probe", "plain_default01.png")
    write_png(png)
    step("project.import (no texture: WolvenKit's default)", lambda: b.call("project.import", file=png, rawDir=raw, archiveDir=arc)["texture"])
    try:
        b.call("project.import", file=png, rawDir=raw, archiveDir=arc, texture="shiny")
        raise AssertionError("an unknown preset was accepted")
    except RuntimeError as e:
        print("   unknown preset refused:", str(e).splitlines()[0][:90], "...")

    b.close()
    print(f"done in {time.time() - t:.1f}s, outputs in {out}")


if __name__ == "__main__":
    main()
