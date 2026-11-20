// REDbridge's keys.* (K4a) against REDnode's keys.mjs (PowerShell), in a scratch key folder, with
// test keys made here: never the player's own. Needs REDnode checked out beside this repo, or
// REDNODE_KEYS_MJS naming its tools/keys/keys.mjs.
//   node tools/keys-check.mjs <REDbridge.exe>
import { spawn } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { randomBytes } from 'node:crypto';

const exe = process.argv[2];
const dir = join(tmpdir(), `redbridge-keys-check-${process.pid}`);
rmSync(dir, { recursive: true, force: true });
mkdirSync(dir, { recursive: true });
process.env.EDGEMODDERS_KEYS_DIR = dir;
for (const v of ['OPENAI_API_KEY', 'GEMINI_API_KEY', 'ANTHROPIC_API_KEY', 'ELEVENLABS_API_KEY', 'TRIPO_API_KEY', 'MY_TOOL_API_KEY']) delete process.env[v];
const keys = await import(pathToFileURL(process.env.REDNODE_KEYS_MJS || resolve(import.meta.dirname, '..', '..', 'REDnode', 'tools', 'keys', 'keys.mjs')).href);

let fails = 0;
const ok = (cond, what) => {
  console.log(`${cond ? 'PASS' : 'FAIL'} ${what}`);
  if (!cond) fails++;
};
const secrets = [];
const testKey = (prefix) => {
  const k = prefix + randomBytes(18).toString('hex');
  secrets.push(k);
  return k;
};

function bridge(args, env = {}) {
  const p = spawn(exe, args, { env: { ...process.env, ...env }, stdio: ['pipe', 'pipe', 'pipe'] });
  let buf = Buffer.alloc(0), log = '', n = 0;
  const waiting = new Map();
  p.stderr.on('data', (d) => (log += d));
  p.stdout.on('data', (d) => {
    buf = Buffer.concat([buf, d]);
    for (;;) {
      const h = buf.indexOf('\r\n\r\n');
      if (h < 0) return;
      const len = +/Content-Length: (\d+)/i.exec(buf.subarray(0, h).toString())[1];
      if (buf.length < h + 4 + len) return;
      const msg = JSON.parse(buf.subarray(h + 4, h + 4 + len).toString());
      buf = buf.subarray(h + 4 + len);
      const w = waiting.get(msg.id);
      waiting.delete(msg.id);
      msg.error ? w.reject(new Error(msg.error.message)) : w.resolve(msg.result);
    }
  });
  return {
    call(method, params = {}) {
      const id = ++n;
      const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
      p.stdin.write(`Content-Length: ${body.length}\r\n\r\n`);
      p.stdin.write(body);
      return new Promise((resolve, reject) => waiting.set(id, { resolve, reject }));
    },
    async close() {
      p.stdin.end();
      await new Promise((r) => p.on('close', r));
      return log;
    },
  };
}

const refused = async (promise) => {
  try {
    await promise;
    return null;
  } catch (e) {
    return e.message;
  }
};

// 1. Off without --keys
{
  const b = bridge([]);
  const why = await refused(b.call('keys.get', { service: 'openai' }));
  ok(why && why.includes('--keys'), `without --keys, keys.get is refused: ${why}`);
  ok(!!(await refused(b.call('keys.list'))), 'without --keys, keys.list is refused');
  await b.close();
}

const b = bridge(['--keys']);
// 2. A key PowerShell wrote, read by .NET
const ps = testKey('sk-test-');
keys.setKey('openai', ps);
let r = await b.call('keys.get', { service: 'openai', tool: 'K4a check' });
ok(r.key === ps && r.source === 'file', 'REDbridge reads a key written by keys.mjs (PowerShell)');
// 3. A key .NET wrote, read by PowerShell
const net = testKey('gem-test-');
const info = await b.call('keys.set', { service: 'gemini', key: net });
ok(info.set && info.source === 'file' && info.last4 === net.slice(-4) && !('key' in info), 'keys.set answers info with last4, never the key');
ok(keys.getKey('gemini') === net, 'keys.mjs (PowerShell) reads a key written by REDbridge');
// 4. Refusals
ok((await refused(b.call('keys.set', { service: 'openai', key: 'nope-' + randomBytes(8).toString('hex') })))?.includes('start with sk-'), 'an OpenAI key without sk- is refused');
ok((await refused(b.call('keys.set', { service: 'gemini', key: 'has space' })))?.includes('no spaces'), 'a key with a space is refused');
ok((await refused(b.call('keys.get', { service: 'Bad_ID' })))?.includes('service id'), 'a bad service id is refused');
// 5. list, with a service of another id
keys.setKey('my-tool', testKey('mt-'));
const list = await b.call('keys.list');
const ids = list.map((x) => x.service);
ok(['anthropic', 'gemini', 'openai', 'elevenlabs', 'tripo', 'my-tool'].every((x) => ids.includes(x)), `keys.list has the known services and my-tool: ${ids.join(', ')}`);
ok(!JSON.stringify(list).includes(ps) && !JSON.stringify(list).includes(net), 'keys.list holds no key');
ok(list.find((x) => x.service === 'my-tool').env === 'MY_TOOL_API_KEY', 'another id gets its variable name');
// 6. used-by, shared with keys.mjs
const usedBy = keys.keyInfo('openai').usedBy.map((u) => u.tool);
ok(usedBy.includes('K4a check'), `keys.mjs sees REDbridge's used-by record: ${usedBy.join(', ')}`);
keys.getKey('openai', { tool: 'keys.mjs side' });
const viaBridge = (await b.call('keys.info', { service: 'openai' })).usedBy.map((u) => u.tool);
ok(viaBridge.includes('keys.mjs side') && viaBridge.includes('K4a check'), `REDbridge sees keys.mjs's record too: ${viaBridge.join(', ')}`);
// 7. Unreadable file
writeFileSync(join(dir, 'tripo.txt'), '01000000d08c9ddf0115d1118c7a00c04fc297eb' + '00'.repeat(40) + '\r\n');
r = await b.call('keys.get', { service: 'tripo' });
ok(r.key === null && r.unreadable === true, 'a file this user cannot decrypt counts as unset, and says unreadable');
ok((await b.call('keys.info', { service: 'tripo' })).unreadable === true, 'keys.info says unreadable too');
// 8. The file changes under REDbridge
const ps2 = testKey('sk-test-');
keys.setKey('openai', ps2);
r = await b.call('keys.get', { service: 'openai' });
ok(r.key === ps2, 'a key changed by another tool is read again, not the cached one');
// 9. Delete
ok((await b.call('keys.delete', { service: 'gemini' })) === true && !keys.hasKey('gemini'), 'keys.delete removes the file for every tool');
ok((await b.call('keys.delete', { service: 'gemini' })) === false, 'a second delete answers false');
r = await b.call('keys.get', { service: 'gemini' });
ok(r.key === null && r.source === null && !r.unreadable, 'a deleted key reads as unset');
const log = await b.close();
ok(!secrets.some((s) => log.includes(s)), 'no key in REDbridge\'s log');
ok(log.includes('Keys: gemini set') && log.includes('Keys: gemini removed'), 'the log names the changes');

// 10. The environment variable wins
{
  const envKey = testKey('sk-env-');
  const e = bridge(['--keys'], { OPENAI_API_KEY: envKey });
  r = await e.call('keys.get', { service: 'openai' });
  ok(r.key === envKey && r.source === 'env', 'OPENAI_API_KEY wins over the file');
  const i = await e.call('keys.info', { service: 'openai' });
  ok(i.source === 'env' && i.last4 === envKey.slice(-4), 'keys.info says env');
  await e.close();
}
rmSync(dir, { recursive: true, force: true });
console.log(fails ? `${fails} FAILED` : 'all passed');
process.exit(fails ? 1 : 0);
