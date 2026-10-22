// Prints the release notes for one version from its section of CHANGELOG.md. The release workflow
// uses it for the GitHub release, and stops before releasing anything when the version has no
// section. The same convention as every Edgemodders repo (REDnode's tools/release/notes.mjs).
//
//   node tools/release-notes.mjs [version]       markdown for GitHub: how to install, the section,
//                                                and the WolvenKit version it is built with
//   --out <file>                                 writes the notes to a file (UTF-8)
//
// The version defaults to <Version> in src/REDbridge/REDbridge.csproj. npm run release:notes runs it.
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const args = process.argv.slice(2);
const outAt = args.indexOf("--out");
const out = outAt >= 0 ? args.splice(outAt, 2)[1] : null;
const csproj = readFileSync(root + "src/REDbridge/REDbridge.csproj", "utf8");
const version = args.find((a) => !a.startsWith("--")) ?? csproj.match(/<Version>([^<]+)<\/Version>/)[1];
const wolvenKit = csproj.match(/<WolvenKitVersion>([^<]+)<\/WolvenKitVersion>/)[1];
const lines = readFileSync(root + "CHANGELOG.md", "utf8").split(/\r?\n/);

// "## 0.11.0" or "## 0.11.0 (2026-10-22)"
const heading = new RegExp(`^## ${version.replace(/\./g, "\\.")}(\\s|$)`);
const start = lines.findIndex((line) => heading.test(line));
if (start < 0) {
  console.error(`CHANGELOG.md has no section for ${version}: rename Unreleased to "## ${version} (date)" first`);
  process.exit(1);
}
let end = lines.findIndex((line, i) => i > start && line.startsWith("## "));
if (end < 0) end = lines.length;
const section = lines.slice(start + 1, end);
if (!section.some((line) => line.trim())) {
  console.error(`CHANGELOG.md: the section for ${version} is empty`);
  process.exit(1);
}

const notes = `Windows x64 build of REDbridge ${version}. It is framework dependent, so it needs the .NET 10 runtime (\`winget install Microsoft.DotNet.Runtime.10\`).

Unzip it anywhere and point REDcode (\`redcode.bridgePath\`) or EDGEdeck (\`edgedeck.bridgePath\`) at \`REDbridge.exe\`, or let REDcode's Install REDbridge or EDGEdeck's Get REDbridge fetch it.

${section.join("\n").trim()}

Built with WolvenKit ${wolvenKit} from its NuGet packages. WolvenKit's source is https://github.com/WolvenKit/WolvenKit/tree/${wolvenKit} (GPL-3.0), and the bridge's source is this repository.`;

if (out) writeFileSync(out, notes + "\n", "utf8");
else console.log(notes);
