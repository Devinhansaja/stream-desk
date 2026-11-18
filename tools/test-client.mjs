// End-to-end protocol test against a running StreamDeck Host (stand-in for the phone).
// Usage: node test-client.mjs <pairing-file> [port]
// The pairing file is produced by:  StreamDeckHost.exe --tray --dev-pairing-file <file> --port <port>
import { readFileSync } from 'node:fs';

const pairing = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const port = Number(process.argv[3] ?? pairing.p);
const url = `ws://127.0.0.1:${port}/ws`;
const deviceId = 'test-device-' + Math.random().toString(16).slice(2);

let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { (ok ? pass++ : fail++); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };

function open() {
  return new Promise((res, rej) => {
    const ws = new WebSocket(url);
    ws.queue = []; ws.waiters = [];
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      const i = ws.waiters.findIndex(w => w.pred(m));
      if (i >= 0) ws.waiters.splice(i, 1)[0].res(m); else ws.queue.push(m);
    };
    ws.onopen = () => res(ws);
    ws.onerror = (e) => rej(new Error('ws error'));
  });
}
function wait(ws, pred, ms = 8000) {
  const i = ws.queue.findIndex(pred);
  if (i >= 0) return Promise.resolve(ws.queue.splice(i, 1)[0]);
  return new Promise((res, rej) => {
    const w = { pred, res };
    ws.waiters.push(w);
    setTimeout(() => rej(new Error('timeout')), ms);
  });
}
let nextId = 1;
async function rpc(ws, msg, type, ms = 8000) {
  const id = nextId++;
  ws.send(JSON.stringify({ ...msg, id }));
  return wait(ws, m => m.id === id && (!type || m.t === type), ms);
}

// 1. unauthenticated request must be refused
let ws = await open();
ws.send(JSON.stringify({ t: 'action', id: 1, action: { type: 'volume_up' } }));
let m = await wait(ws, x => x.t === 'error');
check('unauthenticated action refused', m.error === 'not_authenticated');
ws.close();

// 2. bad pairing secret refused
ws = await open();
ws.send(JSON.stringify({ t: 'pair', secret: 'deadbeef', deviceId, deviceName: 'Test' }));
m = await wait(ws, x => x.t === 'pair_fail');
check('bad pairing secret rejected', !!m);
ws.close();

// 3. real pairing
ws = await open();
ws.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId, deviceName: 'Test Phone' }));
m = await wait(ws, x => x.t === 'paired');
check('pairing with QR secret', !!m.token);
const token = m.token;
const st = await wait(ws, x => x.t === 'status');
check('status after pair', typeof st.volume === 'number', JSON.stringify(st));
ws.close();

// 4. secret is single-use
ws = await open();
ws.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId: deviceId + 'x', deviceName: 'Replay' }));
m = await wait(ws, x => x.t === 'pair_fail');
check('pairing secret is single-use', !!m);
ws.close();

// 5. auth with token, wrong token refused
ws = await open();
ws.send(JSON.stringify({ t: 'auth', deviceId, token: 'wrong' }));
m = await wait(ws, x => x.t === 'auth_fail');
check('wrong token refused', !!m);
ws.close();

ws = await open();
ws.send(JSON.stringify({ t: 'auth', deviceId, token }));
m = await wait(ws, x => x.t === 'auth_ok');
check('auth with stored token', !!m);
await wait(ws, x => x.t === 'status');

// 6. real actions
const before = (await rpc(ws, { t: 'status' }, 'status')).volume;
m = await rpc(ws, { t: 'action', action: { type: 'volume_set', value: 33 } }, 'result');
check('volume_set executes', m.ok === true, m.error ?? '');
let s2 = await rpc(ws, { t: 'status' }, 'status');
check('volume really changed to 33', s2.volume === 33, `got ${s2.volume}`);
m = await rpc(ws, { t: 'action', action: { type: 'volume_up', step: 10 } }, 'result');
s2 = await rpc(ws, { t: 'status' }, 'status');
check('volume_up +10 => 43', s2.volume === 43, `got ${s2.volume}`);
m = await rpc(ws, { t: 'action', action: { type: 'mute', value: true } }, 'result');
s2 = await rpc(ws, { t: 'status' }, 'status');
check('mute applied', s2.muted === true);
await rpc(ws, { t: 'action', action: { type: 'mute', value: false } }, 'result');
await rpc(ws, { t: 'action', action: { type: 'volume_set', value: before } }, 'result');

m = await rpc(ws, { t: 'action', action: { type: 'macro', steps: [{ type: 'volume_set', value: 20 }, { type: 'delay', ms: 100 }, { type: 'volume_set', value: before }] } }, 'result');
check('macro runs', m.ok === true, m.error ?? '');

m = await rpc(ws, { t: 'action', action: { type: 'shell', command: 'echo hi' } }, 'result');
check('shell disabled by default', m.ok === false && /disabled/i.test(m.error));
m = await rpc(ws, { t: 'action', action: { type: 'nonsense' } }, 'result');
check('unknown action reported', m.ok === false);
m = await rpc(ws, { t: 'action', action: { type: 'hotkey', keys: 'ctrl+bogus' } }, 'result');
check('bad hotkey reported', m.ok === false, m.error);

m = await rpc(ws, { t: 'action', action: { type: 'screenshot' } }, 'result');
check('screenshot', m.ok === true, m.message ?? m.error);

m = await rpc(ws, { t: 'apps', refresh: true }, 'apps', 60000);
check('app list', m.apps.length > 0, `${m.apps.length} apps, ${m.apps.filter(a => a.icon).length} with icons`);
console.log('   sample:', m.apps.slice(0, 5).map(a => `${a.name} [${a.kind}]`).join(', '));

m = await rpc(ws, { t: 'wallpapers' }, 'wallpapers');
check('wallpaper list', Array.isArray(m.items));


// 7. layout lives on the PC; editor API is PC-only; phones get live pushes
const base = `http://127.0.0.1:${port}`;
m = await rpc(ws, { t: 'layout_get' }, 'layout');
check('layout_get returns default layout', m.layout.profiles[0].pages[0].slots.filter(Boolean).length >= 10);
const lan = (await import('node:os')).networkInterfaces();
const lanIp = Object.values(lan).flat().find(i => i.family === 'IPv4' && !i.internal)?.address;
let r = await fetch(`${base}/deck`); check('/deck served to anyone', r.status === 200 && (await r.text()).includes('StreamDeck'));
r = await fetch(`${base}/editor`); check('/editor served on localhost', r.status === 200);
if (lanIp) { r = await fetch(`http://${lanIp}:${port}/editor`); check('/editor refused over LAN address', r.status === 403, `status ${r.status}`); r = await fetch(`http://${lanIp}:${port}/api/layout`); check('/api/layout refused over LAN address', r.status === 403); }
const lay = m.layout; lay.profiles[0].pages[0].slots[15] = { id: 'x1', label: 'Test key', icon: 'bolt', image: null, color1: '#ffffff', color2: '#000000', glow: 0.5, confirm: false, action: { type: 'lock' } };
r = await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(lay), headers: { 'Content-Type': 'application/json' } }); check('PUT layout without editor header refused', r.status === 403);
r = await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(lay), headers: { 'Content-Type': 'application/json', 'X-SD-Editor': '1', Origin: 'http://evil.example' } }); check('PUT layout from foreign origin refused', r.status === 403);
const pushed = wait(ws, x => x.t === 'layout' && !x.id);
r = await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(lay), headers: { 'Content-Type': 'application/json', 'X-SD-Editor': '1' } }); check('PUT layout from editor ok', r.status === 200);
m = await pushed; check('phone receives live layout push', m.layout.profiles[0].pages[0].slots[15]?.label === 'Test key');
const bad = JSON.parse(JSON.stringify(lay)); bad.profiles[0].pages[0].slots.pop();
r = await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(bad), headers: { 'Content-Type': 'application/json', 'X-SD-Editor': '1' } }); check('invalid layout rejected', r.status === 400);
r = await fetch(`${base}/assets/icons.json`); check('icon map served', (await r.json()).volume_up > 0);

// 8. live backgrounds: upload on the PC, stream to phones (with range support), restricted to the editor
const gif = Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64');
const hdr = { 'X-SD-Editor': '1' };
r = await fetch(`${base}/api/upload-bg?name=test.gif`, { method: 'POST', body: gif }); check('upload without editor header refused', r.status === 403);
if (lanIp) { r = await fetch(`http://${lanIp}:${port}/api/upload-bg?name=test.gif`, { method: 'POST', body: gif, headers: hdr }); check('upload over LAN address refused', r.status === 403); }
r = await fetch(`${base}/api/upload-bg?name=evil.exe`, { method: 'POST', body: gif, headers: hdr }); check('upload of .exe refused', r.status === 400);
r = await fetch(`${base}/api/upload-bg?name=test.gif`, { method: 'POST', body: gif, headers: hdr }); const up = await r.json();
check('GIF upload ok', r.status === 200 && up.src.startsWith('/media/') && up.type === 'image', JSON.stringify(up));
r = await fetch(`${base}${up.src}`); check('media served to anyone (phones)', r.status === 200 && r.headers.get('content-type') === 'image/gif');
r = await fetch(`${base}${up.src}`, { headers: { Range: 'bytes=0-9' } }); check('media supports range requests (video seeking)', r.status === 206);
r = await fetch(`${base}/media/..%2F..%2Fsettings.json`); check('media path traversal blocked', r.status === 404);
const lay2 = (await (await fetch(`${base}/api/layout`)).json()); lay2.background = { type: up.type, src: up.src }; lay2.bgDim = 0.4;
const pushed2 = wait(ws, x => x.t === 'layout' && !x.id);
r = await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(lay2), headers: { 'Content-Type': 'application/json', ...hdr } });
m = await pushed2; check('background reaches phones in the layout push', m.layout.background?.src === up.src && m.layout.bgDim === 0.4);
r = await fetch(`${base}/assets/bg.js`); check('bg.js served', r.status === 200);
ws.close();
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);


