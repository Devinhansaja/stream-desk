// Tests the Discord call feature against a FAKE Discord (speaks Discord's local IPC protocol on a named pipe).
// Host must run with SDH_DISCORD_PIPE=discord-ipc-test9 and an SDH_DATA_DIR containing a discord-auth.json with plain tokens.
// usage: node discord-test.mjs <pairing-file> <hostPort>
import net from 'node:net';
import { readFileSync } from 'node:fs';
const [,, pairFile, port] = process.argv;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ------------------------------------------------------------------ fake Discord
const received = [];            // commands the host sent
let sock = null, voice = { mute: false, deaf: false }, channel = null;
const users = [
  { user: { id: '111111111111111111', username: 'me', global_name: 'Me', avatar: 'abc' }, voice_state: { mute: false, deaf: false, self_mute: false, self_deaf: false }, nick: null },
  { user: { id: '222222222222222222', username: 'friend', global_name: 'Friend', avatar: null }, voice_state: { mute: false, deaf: false, self_mute: true, self_deaf: false }, nick: 'Buddy' },
];
const frame = (op, obj) => { const p = Buffer.from(JSON.stringify(obj)); const b = Buffer.alloc(8 + p.length); b.writeInt32LE(op, 0); b.writeInt32LE(p.length, 4); p.copy(b, 8); return b; };
const dispatch = (evt, data) => sock.write(frame(1, { cmd: 'DISPATCH', evt, data }));
const server = net.createServer(s => {
  sock = s; let buf = Buffer.alloc(0);
  s.on('data', d => {
    buf = Buffer.concat([buf, d]);
    while (buf.length >= 8) {
      const op = buf.readInt32LE(0), len = buf.readInt32LE(4); if (buf.length < 8 + len) break;
      const msg = JSON.parse(buf.subarray(8, 8 + len).toString()); buf = buf.subarray(8 + len);
      if (op === 0) { s.write(frame(1, { cmd: 'DISPATCH', evt: 'READY', data: { v: 1 } })); continue; }
      received.push(msg);
      const reply = data => s.write(frame(1, { cmd: msg.cmd, data, evt: null, nonce: msg.nonce }));
      switch (msg.cmd) {
        case 'AUTHENTICATE': reply({ user: { id: '111111111111111111', username: 'me', global_name: 'Me' }, scopes: [] }); break;
        case 'GET_VOICE_SETTINGS': reply({ ...voice }); break;
        case 'GET_SELECTED_VOICE_CHANNEL': reply(channel ? { id: channel, name: 'General', voice_states: users } : null); break;
        case 'SET_VOICE_SETTINGS': Object.assign(voice, msg.args); reply({ ...voice }); dispatch('VOICE_SETTINGS_UPDATE', { ...voice }); break;
        case 'SELECT_VOICE_CHANNEL': channel = msg.args.channel_id; reply(null); dispatch('VOICE_CHANNEL_SELECT', { channel_id: channel, guild_id: null }); break;
        default: reply(null);   // SUBSCRIBE / UNSUBSCRIBE
      }
    }
  });
  s.on('error', () => {});
});
await new Promise(r => server.listen('\\\\.\\pipe\\discord-ipc-test9', r));

// ------------------------------------------------------------------ phone (WebSocket) side
const pairing = JSON.parse(readFileSync(pairFile, 'utf8'));
const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
const inbox = []; let last = null;
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === 'discord') last = m; inbox.push(m); };
await new Promise(r => ws.onopen = r);
ws.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId: 'disc-test-' + Math.random().toString(16).slice(2), deviceName: 'disc' }));
const until = async (pred, ms = 15000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (pred()) return true; await sleep(100); } return false; };
let id = 1; const act = async type => { const i = id++; const n = inbox.length; ws.send(JSON.stringify({ t: 'action', id: i, action: { type } })); await until(() => inbox.slice(n).some(m => m.id === i), 8000); return inbox.slice(n).find(m => m.id === i); };

check('host connects to (fake) Discord and authenticates', await until(() => last && last.rpc === 'ok'), JSON.stringify(last && { rpc: last.rpc, status: last.status }));
check('knows who I am', last?.me === 'Me');
check('not in a call at first', last && last.inCall === false);
check('host asked Discord to authenticate with the stored token', received.some(m => m.cmd === 'AUTHENTICATE' && m.args.access_token === 'tok'));

// join a call
channel = 'chan1'; dispatch('VOICE_CHANNEL_SELECT', { channel_id: 'chan1', guild_id: 'g' });
check('joining a call is pushed to the phone with participants', await until(() => last && last.inCall && last.users.length === 2), JSON.stringify(last && last.users?.map(u => u.name)));
check('channel name and nick are used', last.channel === 'General' && last.users.some(u => u.name === 'Buddy'));
check('self-muted friend shows as muted', last.users.find(u => u.id === '222222222222222222').mute === true);
check('host subscribed to speaking events for the channel', received.some(m => m.cmd === 'SUBSCRIBE' && m.evt === 'SPEAKING_START' && m.args?.channel_id === 'chan1'));

// speaking indicator
dispatch('SPEAKING_START', { user_id: '222222222222222222' });
check('speaking start reaches the phone', await until(() => last.users.find(u => u.id === '222222222222222222')?.speaking === true));
dispatch('SPEAKING_STOP', { user_id: '222222222222222222' });
check('speaking stop reaches the phone', await until(() => last.users.find(u => u.id === '222222222222222222')?.speaking === false));

// someone joins / leaves
dispatch('VOICE_STATE_CREATE', { user: { id: '333333333333333333', username: 'newbie', global_name: 'Newbie', avatar: null }, voice_state: { mute: false, deaf: false, self_mute: false, self_deaf: false } });
check('new participant appears', await until(() => last.users.length === 3));
dispatch('VOICE_STATE_DELETE', { user: { id: '333333333333333333' } });
check('participant who left disappears', await until(() => last.users.length === 2));

// controls from the phone
let r = await act('discord_mute_toggle');
check('mute key works (SET_VOICE_SETTINGS mute:true sent)', r?.ok === true && received.some(m => m.cmd === 'SET_VOICE_SETTINGS' && m.args.mute === true));
check('phone sees mute state from Discord', await until(() => last.mute === true));
r = await act('discord_mute_toggle');
check('second press unmutes', await until(() => last.mute === false));
r = await act('discord_deafen_toggle');
check('deafen key works', r?.ok === true && await until(() => last.deaf === true));
await act('discord_deafen_toggle');
r = await act('discord_disconnect');
check('leave key disconnects (SELECT_VOICE_CHANNEL null)', r?.ok === true && received.some(m => m.cmd === 'SELECT_VOICE_CHANNEL' && m.args.channel_id === null));
check('call end clears the phone state', await until(() => last.inCall === false && last.users.length === 0));

// Discord quits -> host reports it and keeps running
sock.destroy();
check('Discord closing is reported (waiting)', await until(() => last.rpc === 'waiting', 12000), last?.rpc);

console.log(`\n${pass} passed, ${fail} failed`);
ws.close(); server.close(); process.exit(fail ? 1 : 0);
