// Game stats + app-profile test. usage: node game-test.mjs <pairing-file> <hostPort>
import { readFileSync } from 'node:fs';
const [,, pairFile, port] = process.argv;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const pairing = JSON.parse(readFileSync(pairFile, 'utf8'));
const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
const inbox = [];
ws.onmessage = e => { inbox.push(JSON.parse(e.data)); };
await new Promise(r => ws.onopen = r);
ws.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId: 'game-test-' + Math.random().toString(16).slice(2), deviceName: 'game' }));
const until = async (pred, ms = 10000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (pred()) return true; await sleep(100); } return false; };
await until(() => inbox.some(m => m.t === 'auth_ok' || m.t === 'paired'));
ws.send(JSON.stringify({ t: 'layout_get', id: 1 }));
await until(() => inbox.some(m => m.t === 'layout'));
const lay = inbox.find(m => m.t === 'layout').layout;
const pages = lay.profiles[0].pages;
check('layout has the Game stats page', pages.some(p => p.kind === 'gamestats'));
const auto = pages.filter(p => p.autoApp);
check('layout has Adobe pages with autoApp', ['photoshop', 'afterfx', 'illustrator'].every(a => auto.some(p => p.autoApp.includes(a))) && auto.some(p => p.autoApp.includes('premiere')), auto.map(p => p.name + ':' + p.autoApp).join(' | '));
check('Photoshop page has 24 keys with hotkey actions', (auto.find(p => p.name === 'Photoshop')?.slots.filter(Boolean).length === 24));

ws.send(JSON.stringify({ t: 'stats_sub', on: true }));
check('stats snapshot arrives', await until(() => inbox.some(m => m.t === 'game')));
await sleep(3500);
const g = inbox.filter(m => m.t === 'game').pop();
console.log(JSON.stringify(g).slice(0, 600));
check('CPU number present', typeof g.cpu === 'number');
check('RAM used/total in GB', g.ramTotal > 1 && g.ramUsed > 0 && g.ramUsed <= g.ramTotal, `${g.ramUsed}/${g.ramTotal}`);
check('history sparkline arrays grow', Array.isArray(g.hist?.cpu) && g.hist.cpu.length >= 2, String(g.hist?.cpu?.length));
check('updates keep coming (about 1/s)', inbox.filter(m => m.t === 'game').length >= 3, String(inbox.filter(m => m.t === 'game').length));
check('fps helper status reported', typeof g.fpsHelper === 'string', g.fpsHelper);
check('GPU memory/usage readable on this PC', g.vramUsed != null && g.gpu != null, `gpu ${g.gpu}% vram ${g.vramUsed}`);

ws.send(JSON.stringify({ t: 'stats_sub', on: false }));
await sleep(1500); const n0 = inbox.filter(m => m.t === 'game').length; await sleep(2500);
check('no more stats after unsubscribing', inbox.filter(m => m.t === 'game').length === n0);

ws.send(JSON.stringify({ t: 'fg_get' }));
check('foreground app query answered', await until(() => inbox.some(m => m.t === 'fg')));
console.log(`\n${pass} passed, ${fail} failed`);
ws.close(); process.exit(fail ? 1 : 0);
