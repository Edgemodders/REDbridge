// Stops a release whose README does not say it: the repo's README.md must name the version in
// its status line ("**Status: 0.56.1 released on 2026-11-03** ..."; any word after the version,
// such as "released" or "preview"), and its status badge, when it has one, must name it too
// (the README's <img alt> and the SVG's aria-label, "status: 0.56.1 released"). The release
// workflow runs it before it builds, as it checks the changelog, so a README never stays on an
// old release again (REDnode's said 0.38.1 through four releases, found on 2026-11-03).
//
//   node tools/release/readme-check.mjs [--version <v>] [--readme README.md] [--badge <svg>]
//
// The version is VERSION's, or package.json's when there is no VERSION file. The badge is the
// file the README's <img> for status.svg names, unless --badge says another. The same file in
// every Edgemodders repo: copy it as it is (from REDnode's tools/release/).
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const args = process.argv.slice(2);
const option = (name) => {
  const at = args.indexOf(name);
  return at >= 0 ? args[at + 1] : undefined;
};
const root = process.cwd();

function versionOf() {
  const given = option('--version');
  if (given) return given.replace(/^v/, '');
  if (existsSync(join(root, 'VERSION'))) return readFileSync(join(root, 'VERSION'), 'utf8').trim();
  if (existsSync(join(root, 'package.json'))) return JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')).version;
  throw new Error('No version: give --version, or run where VERSION or package.json is');
}

const version = versionOf();
const readmePath = resolve(root, option('--readme') ?? 'README.md');
const readme = readFileSync(readmePath, 'utf8');
const problems = [];
// A version as written: 0.56.1, v0.56.1, 1.140.3, 0.27.0-beta.1.
const VERSION_AT = /^v?(\d+(?:\.\d+)+(?:[-+][\w.]+)?)/;
const named = (text) => new RegExp(`(^|[^\\w.])v?${version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![\\w.]*\\d)`).test(text);

// The status line: "Status:" (bold or not) followed by the version. Images are left out, since a
// badge's alt text says "status: ..." too.
const text = readme.replace(/<img[^>]*>/gi, '');
const status = /status:?\**\s*([^\n]*)/i.exec(text);
const said = status && VERSION_AT.exec(status[1].replace(/^\**\s*/, ''))?.[1];
if (!status) problems.push(`${readmePath} has no status line ("**Status: ${version} released on <date>** ..."): add one`);
else if (said !== version) problems.push(`${readmePath}'s status line says "${status[1].slice(0, 60).trim()}": make it name ${version} ("**Status: ${version} released on <date>** ...")`);

// The status badge, when the README shows one.
const img = /<img[^>]*src="([^"]*badges\/status\.svg)"[^>]*>/i.exec(readme);
const badgePath = option('--badge') ? resolve(root, option('--badge')) : img ? resolve(dirname(readmePath), img[1]) : null;
if (img) {
  const alt = /alt="([^"]*)"/i.exec(img[0])?.[1] ?? '';
  if (!named(alt)) problems.push(`${readmePath}'s status badge <img> says alt "${alt}": make it name ${version} ("status: ${version} released")`);
}
if (badgePath) {
  if (!existsSync(badgePath)) problems.push(`${badgePath} is missing`);
  else {
    const label = /aria-label="([^"]*)"/i.exec(readFileSync(badgePath, 'utf8'))?.[1] ?? '';
    if (!named(label)) problems.push(`${badgePath} says "${label}": draw it again naming ${version} (the repo's badge tool, such as tools/brand/badges.py)`);
  }
}

if (problems.length) {
  for (const p of problems) console.error(`README check: ${p}`);
  console.error(`The README must say ${version} before the release run. Change it, commit, and run again.`);
  process.exit(1);
}
console.log(`README check: ${version} in the status line${badgePath ? ' and the badge' : ''}`);
