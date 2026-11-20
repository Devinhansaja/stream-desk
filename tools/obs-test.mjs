// OBS control test against a FAKE OBS (obs-websocket v5, with password). Host needs an SDH_DATA_DIR with obs-auth.json
// {"enabled":true,"host":"127.0.0.1","port":47955,"password":"plain:pw123"}.   usage: node obs-test.mjs <pairing-file> <hostPort>
import { readFileSync } from 'node:fs';
import { startFakeObs } from './fake-obs.mjs';
const [,, pairFile, port] = process.argv;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const obs = await startFakeObs(47955, 'pw123');
const pairing = JSON.parse(readFileSync(pairFile, 'utf8'));
const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
const inbox = []; let last = null;
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === 'obs') last = m; inbox.push(m); };
await new Promise(r => ws.onopen = r);
ws.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId: 'obs-test-' + Math.random().toString(16).slice(2), deviceName: 'obs' }));
const until = async (pred, ms = 15000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (pred()) return true; await sleep(100); } return false; };
let id = 1; const act = async action => { const i = id++; const n = inbox.length; ws.send(JSON.stringify({ t: 'action', id: i, action })); await until(() => inbox.slice(n).some(m => m.id === i), 8000); return inbox.slice(n).find(m => m.id === i); };

check('host connects to OBS with the password challenge', await until(() => last && last.rpc === 'ok') && obs.st.identified >= 1, JSON.stringify(last && { rpc: last.rpc, status: last.status }));
check('scene list (in OBS order) and current scene', JSON.stringify(last.scenes) === '["Starting Soon","Gameplay","Chatting"]' && last.scene === 'Gameplay', JSON.stringify(last.scenes));
check('mixer lists only audio sources', JSON.stringify(last.inputs.map(i => i.name)) === '["Mic/Aux","Desktop Audio"]', JSON.stringify(last.inputs.map(i => i.name)));
check('volume fader mapping (1.0 -> 100%, 0.125 -> 50%)', last.inputs[0].vol === 1 && Math.abs(last.inputs[1].vol - 0.5) < 0.01, JSON.stringify(last.inputs.map(i => i.vol)));
check('nothing live at first', !last.streaming && !last.recording && !last.vcam);

let r = await act({ type: 'obs_scene', scene: 'Chatting' });
check('switch scene', r?.ok === true && await until(() => last.scene === 'Chatting'));
r = await act({ type: 'obs_stream_toggle' });
check('go live', r?.ok === true && await until(() => last.streaming === true));
check('stream timer shown', await until(() => last.streamTime === '00:01:23', 4000), last.streamTime);
r = await act({ type: 'obs_record_toggle' });
check('start recording', r?.ok === true && await until(() => last.recording === true));
r = await act({ type: 'obs_record_pause_toggle' });
check('pause recording', r?.ok === true && await until(() => last.paused === true));
await act({ type: 'obs_record_pause_toggle' });
check('resume recording', await until(() => last.paused === false));
r = await act({ type: 'obs_virtualcam_toggle' });
check('virtual camera on', r?.ok === true && await until(() => last.vcam === true));
r = await act({ type: 'obs_mute_toggle', input: 'Mic/Aux' });
check('mute a source', r?.ok === true && await until(() => last.inputs.find(i => i.name === 'Mic/Aux').mute === true));
r = await act({ type: 'obs_volume', input: 'Desktop Audio', value: 80 });
check('set volume (80% fader -> multiplier 0.512)', r?.ok === true && Math.abs(obs.st.inputs['Desktop Audio'].mul - 0.512) < 0.001 && await until(() => Math.abs(last.inputs.find(i => i.name === 'Desktop Audio').vol - 0.8) < 0.01), String(obs.st.inputs['Desktop Audio'].mul));
r = await act({ type: 'obs_mute_toggle', input: 'Logo' });
check('muting a non-audio source reports an error', r?.ok === false, r?.error);
await act({ type: 'obs_stream_toggle' }); await act({ type: 'obs_record_toggle' });
check('stop stream and recording', await until(() => !last.streaming && !last.recording && last.streamTime === ''));

// OBS quits
obs.close();
check('OBS closing is reported (waiting)', await until(() => last.rpc === 'waiting', 12000), last?.rpc);
console.log(`\n${pass} passed, ${fail} failed`);
ws.close(); process.exit(fail ? 1 : 0);
