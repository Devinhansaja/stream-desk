// Phone-side Discord page test: auto-opens on joining a call, shows who speaks, buttons work, returns after the call.
// Needs: host with SDH_DISCORD_PIPE=discord-ipc-test9 + seeded discord-auth.json, and headless Edge with --remote-debugging-port.
// usage: node discord-ui-test.mjs <debugPort> <hostPort> <pairing-file> <screenshot.png>
import net from 'node:net';
import { readFileSync, writeFileSync } from 'node:fs';
const [,, dbg, port, pairFile, shotFile] = process.argv;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ---- fake Discord
const received = []; let sock = null, voice = { mute: false, deaf: false }, channel = null;
const users = [
  { user: { id: '111111111111111111', username: 'me', global_name: 'Me', avatar: null }, voice_state: { self_mute: false, self_deaf: false }, nick: null },
  { user: { id: '222222222222222222', username: 'friend', global_name: 'Friend', avatar: null }, voice_state: { self_mute: true, self_deaf: false }, nick: 'Buddy' },
];
const frame = (op, o) => { const p = Buffer.from(JSON.stringify(o)); const b = Buffer.alloc(8 + p.length); b.writeInt32LE(op, 0); b.writeInt32LE(p.length, 4); p.copy(b, 8); return b; };
const dispatch = (evt, data) => sock.write(frame(1, { cmd: 'DISPATCH', evt, data }));
const server = net.createServer(s => { sock = s; let buf = Buffer.alloc(0);
  s.on('data', d => { buf = Buffer.concat([buf, d]);
    while (buf.length >= 8) { const op = buf.readInt32LE(0), len = buf.readInt32LE(4); if (buf.length < 8 + len) break;
      const msg = JSON.parse(buf.subarray(8, 8 + len).toString()); buf = buf.subarray(8 + len);
      if (op === 0) { s.write(frame(1, { cmd: 'DISPATCH', evt: 'READY', data: {} })); continue; }
      received.push(msg); const reply = data => s.write(frame(1, { cmd: msg.cmd, data, evt: null, nonce: msg.nonce }));
      switch (msg.cmd) {
        case 'AUTHENTICATE': reply({ user: { id: '111111111111111111', username: 'me', global_name: 'Me' } }); break;
        case 'GET_VOICE_SETTINGS': reply({ ...voice }); break;
        case 'GET_SELECTED_VOICE_CHANNEL': reply(channel ? { id: channel, name: 'General', voice_states: users } : null); break;
        case 'SET_VOICE_SETTINGS': Object.assign(voice, msg.args); reply({ ...voice }); dispatch('VOICE_SETTINGS_UPDATE', { ...voice }); break;
        case 'SELECT_VOICE_CHANNEL': channel = msg.args.channel_id; reply(null); dispatch('VOICE_CHANNEL_SELECT', { channel_id: channel }); break;
        default: reply(null); } } });
  s.on('error', () => {}); });
await new Promise(r => server.listen('\\\\.\\pipe\\discord-ipc-test9', r));

// ---- pair a phone to get credentials for the page
const pairing = JSON.parse(readFileSync(pairFile, 'utf8'));
const cred = await new Promise(res => { const w = new WebSocket(`ws://127.0.0.1:${port}/ws`); const dev = 'dui-' + Math.random().toString(16).slice(2);
  w.onopen = () => w.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId: dev, deviceName: 'dui' }));
  w.onmessage = e => { const m = JSON.parse(e.data); if (m.t === 'paired') { res({ dev, tok: m.token }); w.close(); } }; });

// ---- CDP
const t = await (await fetch(`http://127.0.0.1:${dbg}/json/new?about:blank`, { method: 'PUT' })).json();
const ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const pend = {}; const errors = [];
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pend[m.id]) { pend[m.id](m); delete pend[m.id]; } if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); };
const call = (method, params = {}) => new Promise(r => { const i = ++id; pend[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
await call('Runtime.enable'); await call('Page.enable');
await call('Emulation.setDeviceMetricsOverride', { width: 412, height: 700, deviceScaleFactor: 1, mobile: true });
await call('Page.navigate', { url: `http://127.0.0.1:${port}/deck#d=${cred.dev}&t=${cred.tok}` });
const ev = async expr => (await call('Runtime.evaluate', { expression: expr, returnByValue: true })).result?.result?.value;
const until = async (expr, ms = 12000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await ev(expr)) return true; await sleep(150); } return false; };

check('deck connected and the Discord page exists in the pager', await until("document.getElementById('dot').className==='on' && shownPages.some(p=>p.kind==='discord')"));
const dIdx = await ev("shownPages.findIndex(p=>p.kind==='discord')");
check('starts on the first page (not in a call)', (await ev('curPage')) === 0);
check('Discord page says it is connected but not in a call', await until("discord.rpc==='ok' && document.querySelector('.dcw .dh span').textContent==='Not in a call'"));

// join a call -> phone jumps to the Discord page
channel = 'chan1'; dispatch('VOICE_CHANNEL_SELECT', { channel_id: 'chan1' });
check('joining a call opens the Discord page automatically', await until(`curPage===${dIdx}`));
check('channel and participants shown', await until("document.querySelector('.dcw .dh b').textContent.includes('General') && document.querySelectorAll('.dcw .du').length===2"));
check('muted friend is marked', (await ev("document.querySelector('.du.muted .dn').textContent")) === 'Buddy');
dispatch('SPEAKING_START', { user_id: '222222222222222222' });
check('speaking participant is highlighted', await until("document.querySelectorAll('.du.speaking').length===1"));
{ const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(shotFile, Buffer.from(s.result.data, 'base64')); }

// buttons
await ev("document.querySelector('.dcw [data-a=discord_mute_toggle]').click(); 0");
check('Mute button reaches Discord and turns red', await until("document.querySelector('.dcw [data-a=discord_mute_toggle]').classList.contains('on')") && received.some(m => m.cmd === 'SET_VOICE_SETTINGS' && m.args.mute === true));
check('button label switches to Unmute', (await ev("document.querySelector('.dcw [data-a=discord_mute_toggle]').textContent")).includes('Unmute'));

// leave -> phone goes back
await ev("document.querySelector('.dcw .leave').click(); 0");
check('Leave button disconnects and the deck returns to the previous page', await until('curPage===0') && received.some(m => m.cmd === 'SELECT_VOICE_CHANNEL' && m.args.channel_id === null));

// swiping away during a call is respected
channel = 'chan1'; dispatch('VOICE_CHANNEL_SELECT', { channel_id: 'chan1' });
await until(`curPage===${dIdx}`); await sleep(300);
await ev("document.getElementById('pages').scrollLeft = 0; 0"); await sleep(600);
dispatch('SPEAKING_START', { user_id: '111111111111111111' }); await sleep(600);
check('swiping away during a call is not overridden', (await ev('curPage')) === 0);
channel = null; dispatch('VOICE_CHANNEL_SELECT', { channel_id: null }); await sleep(600);
channel = 'chan1'; dispatch('VOICE_CHANNEL_SELECT', { channel_id: 'chan1' });
check('the next call opens the page again', await until(`curPage===${dIdx}`));
check('no script errors on the phone page', errors.length === 0, errors.join(' | '));

console.log(`\n${pass} passed, ${fail} failed`);
server.close(); process.exit(fail ? 1 : 0);
