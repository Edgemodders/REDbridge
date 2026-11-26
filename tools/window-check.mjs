// Checks the window.* methods (issue #22) against two Electron stand-ins: an owner window whose
// page is titled like a webview, and a framed target standing in for the game. They show on screen
// for about 8 seconds; the check presses Ctrl+Alt+Shift+F11 once (the release key it registers).
//   node tools/window-check.mjs <REDbridge.exe> <electron.exe>
// Any Electron works, such as REDnode's node_modules/electron/dist/electron.exe.
import { spawn, execFileSync } from 'node:child_process';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'redbridge-window-'));
const [bridgeExe, electronExe] = process.argv.slice(2);
let failures = 0;
const ok = (cond, what, extra = '') => {
  console.log(`${cond ? 'ok  ' : 'FAIL'}  ${what}${extra ? '  ' + extra : ''}`);
  if (!cond) failures++;
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function stand(role) {
  const control = path.join(scratch, `control-${role}.txt`);
  fs.writeFileSync(control, '');
  const env = { ...process.env, RB22_ROLE: role, RB22_CONTROL: control };
  delete env.ELECTRON_RUN_AS_NODE;
  const p = spawn(electronExe, [path.join(here, 'window-stand.cjs'), `--user-data-dir=${path.join(scratch, 'ud-' + role)}`], { env, stdio: ['pipe', 'pipe', 'ignore'] });
  return new Promise((resolve) => {
    p.stdout.on('data', (d) => {
      for (const line of String(d).split('\n')) {
        if (line.includes('"ready"')) resolve({ proc: p, pid: JSON.parse(line).pid, send: (m) => fs.appendFileSync(control, JSON.stringify(m) + '\n') });
      }
    });
  });
}

// JSON-RPC over stdio, Content-Length framed.
function bridge(args) {
  const p = spawn(bridgeExe, args, { stdio: ['pipe', 'pipe', 'pipe'] });
  let buf = Buffer.alloc(0);
  let next = 1;
  const waiting = new Map();
  const notes = [];
  const log = [];
  p.stderr.on('data', (d) => log.push(...String(d).split(/\r?\n/).filter(Boolean)));
  p.stdout.on('data', (d) => {
    buf = Buffer.concat([buf, d]);
    for (;;) {
      const end = buf.indexOf('\r\n\r\n');
      if (end < 0) return;
      const len = Number(/Content-Length: (\d+)/i.exec(buf.slice(0, end).toString())[1]);
      if (buf.length < end + 4 + len) return;
      const msg = JSON.parse(buf.slice(end + 4, end + 4 + len).toString());
      buf = buf.slice(end + 4 + len);
      if (msg.id && waiting.has(msg.id)) {
        waiting.get(msg.id)(msg);
        waiting.delete(msg.id);
      } else if (msg.method) notes.push(msg);
    }
  });
  const call = (method, params) => new Promise((resolve) => {
    const id = next++;
    waiting.set(id, resolve);
    const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
    p.stdin.write(`Content-Length: ${body.length}\r\n\r\n`);
    p.stdin.write(body);
  });
  return { proc: p, call, notes, log };
}

const owner = await stand('owner');
const target = await stand('target');
await sleep(800);
const cls = 'Chrome_WidgetWin_1';

// Off without --windows.
const off = bridge([]);
const refused = await off.call('window.find', { class: cls });
ok(refused.error && /--windows/.test(refused.error.message), 'window.find refused without --windows', refused.error?.message);
off.proc.stdin.end();

const b = bridge(['--windows']);
let found = (await b.call('window.find', { class: cls, pid: target.pid })).result;
ok(found && found.pid === target.pid && found.title === 'rb22-target', 'window.find by class and pid', JSON.stringify(found?.rect));
const framedClient = found && { w: found.clientWidth, h: found.clientHeight };
ok(found && (BigInt(found.style) & 0xC00000n) !== 0n, 'the target starts framed', found?.style);
ok((await b.call('window.find', { class: 'NoSuchClass_rb22' })).result === null, 'window.find of a missing class answers null');

const bad = await b.call('window.dock', { class: cls, pid: target.pid, owner: { pid: owner.pid, title: 'rb22-owner' }, webview: 'rb22-webview', releaseKey: 'Ctrl+Hyper+Q' });
ok(bad.error && /not a key/.test(bad.error.message), 'a bad release key is refused', bad.error?.message);

const docked = await b.call('window.dock', { class: cls, pid: target.pid, owner: { pid: owner.pid, title: 'rb22-owner' }, webview: 'rb22-webview', releaseKey: 'Ctrl+Alt+Shift+F11' });
let s = docked.result;
ok(s && s.docked, 'window.dock answers docked', docked.error?.message ?? '');
ok(s && s.releaseKeyRegistered, 'the release key is registered');
await sleep(400);
s = (await b.call('window.state')).result;
found = (await b.call('window.find', { class: cls, pid: target.pid })).result;
ok(s.webviewRect && s.webviewRect.width > 800, 'the webview is found by UI Automation', JSON.stringify(s.webviewRect));
ok(found && (BigInt(found.style) & 0xC00000n) === 0n && (BigInt(found.style) & 0x80000000n) !== 0n, 'the frame is off (a popup)', found?.style);
ok(found && found.rect.width === framedClient.w && found.rect.height === framedClient.h, 'the window keeps its client size', `${found?.rect.width}x${found?.rect.height} vs ${framedClient?.w}x${framedClient?.h}`);
ok(found && found.owner !== 0, 'the window is owned', String(found?.owner));
const wv = s.webviewRect;
const cx = wv.x + Math.floor((wv.width - found.rect.width) / 2);
const cy = wv.y + Math.floor((wv.height - found.rect.height) / 2);
ok(Math.abs(found.rect.x - cx) <= 1 && Math.abs(found.rect.y - cy) <= 1, 'centred in the bigger webview', `${found.rect.x},${found.rect.y} vs ${cx},${cy}`);
ok(s.shown, 'shown');

// The owner moves: the window follows.
owner.send({ move: [260, 200] });
await sleep(500);
s = (await b.call('window.state')).result;
found = (await b.call('window.find', { class: cls, pid: target.pid })).result;
const cx2 = s.webviewRect.x + Math.floor((s.webviewRect.width - found.rect.width) / 2);
ok(s.webviewRect.x !== wv.x && Math.abs(found.rect.x - cx2) <= 1, 'follows the webview when the owner moves', `webview ${s.webviewRect.x},${s.webviewRect.y}, window ${found.rect.x},${found.rect.y}`);

// The game moves itself: put back.
execFileSync('powershell', ['-NoProfile', '-Command', `Add-Type -Name W -Namespace X -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr a, int x, int y, int cx, int cy, uint f);'; [X.W]::SetWindowPos([IntPtr]${found.hwnd}, [IntPtr]::Zero, 5, 5, 0, 0, 0x15) | Out-Null`]);
await sleep(300);
const back = (await b.call('window.find', { class: cls, pid: target.pid })).result;
ok(back.rect.x === found.rect.x && back.rect.y === found.rect.y, 'a window moved away is put back', `${back.rect.x},${back.rect.y}`);

// The page changes title (another tab shown): hidden, then shown again.
const before = b.notes.length;
owner.send({ page: 'another tab' });
await sleep(900);
s = (await b.call('window.state')).result;
ok(!s.shown, 'hidden while the webview is not found');
ok(b.notes.slice(before).some((n) => n.method === 'window.shown' && n.params.shown === false), 'window.shown false sent');
owner.send({ page: 'rb22-webview' });
await sleep(1200);
s = (await b.call('window.state')).result;
ok(s.shown, 'shown again when the webview comes back');

// The release key.
const beforeKey = b.notes.length;
execFileSync('powershell', ['-NoProfile', '-Command', "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('^%+{F11}')"]);
await sleep(600);
const released = b.notes.slice(beforeKey).find((n) => n.method === 'window.released');
ok(!!released, 'window.released on the release key', JSON.stringify(released?.params ?? null));

// Undock: owner and region cleared, the frame put back around the same client area.
const at = (await b.call('window.find', { class: cls, pid: target.pid })).result;
const und = (await b.call('window.undock')).result;
ok(und && !und.docked && und.ended === 'undocked', 'window.undock', JSON.stringify(und?.ended));
await sleep(200);
const after = (await b.call('window.find', { class: cls, pid: target.pid })).result;
ok(after.owner === 0, 'owner cleared');
ok((BigInt(after.style) & 0xC00000n) !== 0n && after.clientWidth === framedClient.w && after.clientHeight === framedClient.h, 'the frame is back, with the same client size', `${after.style} ${after.clientWidth}x${after.clientHeight}`);
ok(after.rect.x <= at.rect.x && after.rect.y <= at.rect.y && after.rect.x + after.rect.width >= at.rect.x + at.rect.width, 'the frame goes round where the window was', `${JSON.stringify(after.rect)} round ${JSON.stringify(at.rect)}`);

// Dock again with an inset at the bottom: centred in the webview less the inset.
const inset = { bottom: 120 };
const badInset = await b.call('window.dock', { class: cls, pid: target.pid, owner: { pid: owner.pid, title: 'rb22-owner' }, webview: 'rb22-webview', inset: { left: -1 } });
ok(badInset.error && /inset/.test(badInset.error.message), 'a negative inset is refused', badInset.error?.message);
await b.call('window.dock', { class: cls, pid: target.pid, owner: { pid: owner.pid, title: 'rb22-owner' }, webview: 'rb22-webview', inset });
await sleep(400);
s = (await b.call('window.state')).result;
found = (await b.call('window.find', { class: cls, pid: target.pid })).result;
const iy = s.webviewRect.y + Math.floor((s.webviewRect.height - 120 - found.rect.height) / 2);
ok(s.inset?.bottom === 120 && Math.abs(found.rect.y - iy) <= 1, 'centred above the inset', `y ${found.rect.y} vs ${iy}, inset ${JSON.stringify(s.inset)}`);

// The target stops answering: its owner is cleared until it answers again.
const beforeHang = b.notes.length;
target.send({ hang: 9000 });
await sleep(7000);
s = (await b.call('window.state')).result;
found = (await b.call('window.find', { class: cls, pid: target.pid })).result;
ok(s.hung && found.owner === 0, 'a window that does not answer is hung, its owner cleared', `hung ${s.hung}, owner ${found.owner}`);
ok(b.notes.slice(beforeHang).some((n) => n.method === 'window.hung'), 'window.hung sent');
await sleep(3500);
s = (await b.call('window.state')).result;
found = (await b.call('window.find', { class: cls, pid: target.pid })).result;
ok(!s.hung && found.owner !== 0, 'answering again: owned again', `hung ${s.hung}, owner ${found.owner}`);
ok(b.notes.slice(beforeHang).some((n) => n.method === 'window.answering'), 'window.answering sent');

// The target closes: window.lost.
await sleep(300);
const beforeLost = b.notes.length;
target.send({ quit: true });
await sleep(1000);
const lost = b.notes.slice(beforeLost).find((n) => n.method === 'window.lost');
ok(lost && lost.params.reason === 'window', 'window.lost when the window closes', JSON.stringify(lost?.params ?? null));
s = (await b.call('window.state')).result;
ok(!s.docked && s.ended === 'window', 'state says the docking ended', JSON.stringify(s.ended));

owner.send({ quit: true });
b.proc.stdin.end();
await sleep(500);
console.log(b.log.filter((l) => l.includes('window')).join('\n'));
console.log(failures ? `\n${failures} failure(s)` : '\nAll passed');
process.exit(failures ? 1 : 0);
