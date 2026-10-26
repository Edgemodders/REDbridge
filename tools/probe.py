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
        print(f"[{time.time() - s:6.2f}s] {name}: {r}")
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
        print("   streaming box", built["streamingBox"], "xl:", open(built["xl"], encoding="utf-8").read().strip().splitlines()[-3:])
        # Validation refuses a bad scene and writes nothing.
        try:
            b.call("sector.build", project=project, name="bad", scene={"format": 1, "objects": [{"id": "x", "kind": "mesh", "resource": "base\\nope.mesh"}, {"id": "x", "kind": "thing"}]})
            raise AssertionError("a bad scene was accepted")
        except RuntimeError as e:
            print("   bad scene refused:", str(e).splitlines()[0][:100], "...")
            assert not os.path.exists(os.path.join(project, "source", "archive", "edgebuilder", "bad"))
    else:
        print("   mesh.info / sector.build: no cargo_crate mesh and entity found, skipped")

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
