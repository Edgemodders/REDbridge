"""Runs each REDbridge feature on its own and reports what works, so one failure does not hide the rest.
For the move to Linux: docs/linux.md.

    python3 -I tools/linux-check.py <REDbridge executable> <game folder> <out dir> <result json>

Reads the game folder only. Everything it writes goes under <out dir>. Run it on Windows too, and compare
the two result files' "kept" hashes. Under WSL, a Windows .exe still starts, so opus-tools can look fine there.
sound.importWavs here stops at its made-up sound id: give it a real one to reach opusenc.
"""
import hashlib
import json
import os
import struct
import subprocess
import sys
import time
import wave
import zlib

exe, game, out, result_file = sys.argv[1:5]
os.makedirs(out, exist_ok=True)
log = open(os.path.join(out, "bridge.log"), "wb")


class Dead(Exception):
    pass


class Bridge:
    def __init__(self):
        self.p = subprocess.Popen([exe, "--game", game], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log)
        self.n = 0

    def call(self, method, **params):
        self.n += 1
        body = json.dumps({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params}).encode()
        try:
            self.p.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
            self.p.stdin.flush()
        except OSError as e:
            raise Dead(str(e))
        headers = {}
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise Dead(f"the bridge ended with code {self.p.wait()}")
            if line == b"\r\n":
                break
            k, v = line.decode().split(":", 1)
            headers[k.strip().lower()] = v.strip()
        msg = json.loads(self.p.stdout.read(int(headers["content-length"])))
        if "error" in msg:
            raise RuntimeError(msg["error"].get("message"))
        return msg["result"]


b = Bridge()
results = []
kept = {}


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def step(name, fn):
    global b
    s = time.time()
    try:
        r = fn()
        text = str(r)
        results.append({"name": name, "ok": True, "s": round(time.time() - s, 2), "result": text[:300]})
        print(f"PASS {time.time() - s:7.2f}s {name}: {text[:150]}", flush=True)
        return r
    except Dead as e:
        results.append({"name": name, "ok": False, "dead": True, "error": str(e)})
        print(f"DEAD {name}: {e}", flush=True)
        b = Bridge()
    except Exception as e:
        results.append({"name": name, "ok": False, "error": f"{type(e).__name__}: {str(e)[:400]}"})
        print(f"FAIL {time.time() - s:7.2f}s {name}: {type(e).__name__}: {str(e)[:300]}", flush=True)
    return None


def png(path, size=64):
    rows = b"".join(b"\x00" + bytes(v for x in range(size) for v in (x * 4 % 256, y * 4 % 256, 128, 255)) for y in range(size))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


# Reading the game
step("bridge.info", lambda: (lambda r: {k: r.get(k) for k in ("version", "gameLoaded", "files", "oodle")})(b.call("bridge.info")))
step("archive.archives", lambda: len(b.call("archive.archives")))
step("archive.list", lambda: [i["name"] for i in b.call("archive.list", prefix="base\\characters", limit=2)["items"]])
step("archive.tree", lambda: str(b.call("archive.tree", path="base\\characters"))[:120])
xbms = step("archive.search .xbm", lambda: [i["name"] for i in b.call("archive.search", query=".xbm", limit=5)["items"]]) or []
step("hash.fnv1a64", lambda: b.call("hash.fnv1a64", value="base\\characters"))
step("sound.events", lambda: b.call("sound.events")["count"])
step("tweakdb.types", lambda: b.call("tweakdb.types")["records"])
step("tweakdb.flat", lambda: b.call("tweakdb.flat", path="Items.money.quality")["value"])
step("tweakdb.search", lambda: b.call("tweakdb.search", text="Pants_01_basic_01", limit=2)["total"])
step("loc.languages", lambda: len(b.call("loc.languages")))
step("loc.get", lambda: b.call("loc.get", keys=["Story-base-gameplay-static_data-database-items-clothing-Pants-Pants_01_basic_01_displayName"]))
step("loc.searchLines", lambda: b.call("loc.searchLines", query="choom", language="en-us", limit=2)["total"])

# Files: the same bytes as on Windows are compared after both runs.
converse = "base\\characters\\garment\\player_equipment\\feet\\s1_055_shoe__converse\\s1_055_pma_shoe__converse.mesh"
step("mesh.items", lambda: len(b.call("mesh.items", path=converse)["items"]))
step("mesh.info", lambda: b.call("mesh.info", path=converse).get("radius", "read"))


def extract(path, name):
    target = os.path.join(out, name)
    b.call("archive.extract", path=path, outFile=target)
    kept[name] = sha(target)
    return kept[name][:16]


step("archive.extract (a mesh)", lambda: extract(converse, "shoe.mesh"))


def round_trip():
    j = b.call("cr2w.toJson", path=converse, outFile=os.path.join(out, "shoe.json"))
    kept["shoe.json"] = sha(j["file"])
    back = os.path.join(out, "shoe.back.mesh")
    b.call("cr2w.fromJson", jsonFile=j["file"], outFile=back)
    kept["shoe.back.mesh"] = sha(back)
    return os.path.getsize(back)


step("cr2w.toJson and fromJson", round_trip)


def document():
    d = b.call("cr2w.open", path=converse)
    doc = d["doc"] if "doc" in d else d["id"]
    node = b.call("cr2w.getNode", doc=doc)
    b.call("cr2w.close", doc=doc)
    return str(node)[:80]


step("cr2w.open and getNode", document)
step("cr2w.describeType", lambda: str(b.call("cr2w.describeType", type="CMesh"))[:80])

# Mod archives
mods = os.path.join(game, "archive", "pc", "mod")
archives = [f for f in (os.listdir(mods) if os.path.isdir(mods) else []) if f.lower().endswith(".archive")]
if archives:
    step("archive.openMod", lambda: b.call("archive.openMod", path=os.path.join(mods, archives[0]))["files"])

# Worlds
sectors = step("sector.find", lambda: len(b.call("sector.find")["sectors"]))


def nodes():
    box = {"min": [-1700, 1200, 0], "max": [-1650, 1250, 50]}
    near = b.call("sector.find", box=box, kinds=["exterior"], levels=[0])["sectors"]
    return b.call("sector.nodes", path=near[0]["path"], bounds=True).get("total", "read")


step("sector.nodes", nodes)
project = os.path.join(out, "scene_project")
os.makedirs(project, exist_ok=True)
step("sector.build", lambda: os.path.basename(b.call("sector.build", project=project, name="check", scene={"format": 1, "objects": [{"id": "a", "kind": "light", "position": {"x": 0, "y": 0, "z": 0}}]})["sector"]))

# Textures: WolvenKit's Texconv throws off Windows.
xbm = next((x for x in xbms if x.endswith(".xbm")), None)
if xbm:
    step("texture.toPng", lambda: b.call("texture.toPng", path=xbm, outFile=os.path.join(out, "t.png"))["bytes"])
    step("archive.extract (a texture)", lambda: extract(xbm, "t.xbm"))
    step("texture.fileToPng", lambda: b.call("texture.fileToPng", file=os.path.join(out, "t.xbm"), outFile=os.path.join(out, "t2.png"))["bytes"])
png(os.path.join(out, "in.png"))
step("texture.imageToPng", lambda: b.call("texture.imageToPng", file=os.path.join(out, "in.png"), outFile=os.path.join(out, "t3.png"))["bytes"])
raw = os.path.join(out, "raw")
arc = os.path.join(out, "arc")
os.makedirs(os.path.join(raw, "mymod"), exist_ok=True)
os.makedirs(os.path.join(arc, "mymod"), exist_ok=True)
png(os.path.join(raw, "mymod", "p.png"))
step("project.import (a PNG into .xbm)", lambda: b.call("project.import", file=os.path.join(raw, "mymod", "p.png"), rawDir=raw, archiveDir=arc, texture="color")["ok"])


# Meshes: Modkit, through SharpGLTF; glTF import needs no texconv unless it carries textures.
def mesh_export():
    a = os.path.join(out, "m_arc")
    r = os.path.join(out, "m_raw")
    sub = os.path.join(a, "shoe")
    os.makedirs(sub, exist_ok=True)
    os.makedirs(r, exist_ok=True)
    target = os.path.join(sub, "shoe.mesh")
    b.call("archive.extract", path=converse, outFile=target)
    res = b.call("project.export", file=target, archiveDir=a, rawDir=r)
    kept["glb"] = [os.path.relpath(w, r) if os.path.isabs(w) else w for w in res["written"]]
    return res["ok"], res["written"][:2]


step("project.export (a mesh into .glb)", mesh_export)


def mesh_import():
    a = os.path.join(out, "m_arc")
    r = os.path.join(out, "m_raw")
    glb = next(os.path.join(d, f) for d, _, fs in os.walk(r) for f in fs if f.endswith(".glb"))
    res = b.call("project.import", file=glb, rawDir=r, archiveDir=a)
    return res["ok"], res["written"][:2]


step("project.import (a .glb into .mesh)", mesh_import)


# Packing: on Linux compression is kraken's, not the game's Oodle.
def pack():
    src = os.path.join(out, "pack_src")
    os.makedirs(os.path.join(src, "mymod"), exist_ok=True)
    with open(os.path.join(src, "mymod", "shoe.mesh"), "wb") as f, open(os.path.join(out, "shoe.mesh"), "rb") as g:
        f.write(g.read())
    dest = os.path.join(out, "pack_out")
    os.makedirs(dest, exist_ok=True)
    r = b.call("project.pack", dir=src, outDir=dest, modName="check")
    made = next(os.path.join(dest, f) for f in os.listdir(dest) if f.endswith(".archive"))
    kept["pack.size"] = os.path.getsize(made)
    opened = b.call("archive.openMod", path=made)
    item = opened["items"][0]["name"]
    back = os.path.join(out, "pack.back.mesh")
    b.call("archive.extractFromMod", archivePath=made, items=[{"path": item, "outFile": back}])
    return opened["files"], os.path.getsize(made), "same bytes" if sha(back) == sha(os.path.join(out, "shoe.mesh")) else "BYTES DIFFER"


step("project.pack and read back", pack)


# Sound
def wav(path):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(48000)
        w.writeframes(b"\x00\x10" * 4800)


wd = os.path.join(out, "wavs")
os.makedirs(wd, exist_ok=True)
wav(os.path.join(wd, "123456.wav"))
step("sound.importWavs (opus-tools .exe)", lambda: str(b.call("sound.importWavs", wavFiles=[os.path.join(wd, "123456.wav")], archiveDir=os.path.join(out, "snd")))[:150])

try:
    b.p.stdin.close()
    b.p.wait(timeout=30)
except Exception:
    b.p.kill()
with open(result_file, "w") as f:
    json.dump({"results": results, "kept": kept}, f, indent=1)
print(f"\n{sum(r['ok'] for r in results)} of {len(results)} passed")
