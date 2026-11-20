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
check('sources of the live scene (top layer first)', JSON.stringify(last.items.map(i => i.name)) === '["Overlay","Webcam","Game Capture"]' && last.itemsScene === 'Gameplay', JSON.stringify(last.items.map(i => i.name)));
check('locked state is known', last.items[0].locked === true && last.items[1].locked === false);
check('transitions list and current transition', JSON.stringify(last.transitions) === '["Cut","Fade","Slide"]' && last.transition === 'Fade' && last.transitionMs === 300);
check('studio mode and replay buffer start off', last.studio === false && last.replay === false);

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

// ---- full scene control
check('sources follow the live scene', last.itemsScene === 'Chatting' && JSON.stringify(last.items.map(i => i.name)) === '["Webcam"]', JSON.stringify(last.items.map(i => i.name)));
r = await act({ type: 'obs_item_enabled', scene: 'Chatting', id: 1, value: false });
check('hide a source', r?.ok === true && await until(() => last.items[0].enabled === false));
r = await act({ type: 'obs_item_locked', scene: 'Chatting', id: 1, value: true });
check('lock a source', r?.ok === true && await until(() => last.items[0].locked === true));
r = await act({ type: 'obs_source_toggle', source: 'Webcam' });
check('show/hide a source by name (for keys)', r?.ok === true && await until(() => last.items[0].enabled === true));
obs.addScene('Be Right Back');
check('a scene created in OBS appears on the phone automatically', await until(() => last.scenes.includes('Be Right Back')), JSON.stringify(last.scenes));
r = await act({ type: 'obs_scene', scene: 'Be Right Back' });
check('the new scene can be switched to; it has no sources yet', r?.ok === true && await until(() => last.scene === 'Be Right Back' && last.items.length === 0 && last.itemsScene === 'Be Right Back'));
obs.st.items['Be Right Back'].push({ id: 7, name: 'BRB Text', enabled: true, locked: false }); obs.bcast('SceneItemCreated', { sceneName: 'Be Right Back', sceneItemId: 7 });
check('a source added in OBS shows up in that scene', await until(() => last.items.length === 1 && last.items[0].name === 'BRB Text'));
r = await act({ type: 'obs_set_transition', name: 'Slide' });
check('change transition', r?.ok === true && await until(() => last.transition === 'Slide'));
r = await act({ type: 'obs_transition_duration', ms: 750 });
check('change transition duration', r?.ok === true && await until(() => last.transitionMs === 750));
r = await act({ type: 'obs_studio_toggle' });
check('studio mode on (preview scene known)', r?.ok === true && await until(() => last.studio === true && last.preview === 'Starting Soon'), last.preview);
check('in studio mode the sources shown are the preview scene\'s', await until(() => last.itemsScene === 'Starting Soon' && last.items.length === 1 && last.items[0].name === 'Banner'));
r = await act({ type: 'obs_preview_scene', scene: 'Gameplay' });
check('pick another preview scene', r?.ok === true && await until(() => last.preview === 'Gameplay' && last.items.length === 3));
r = await act({ type: 'obs_transition' });
check('transition makes the preview go live', r?.ok === true && await until(() => last.scene === 'Gameplay'));
r = await act({ type: 'obs_studio_toggle' });
check('studio mode off: sources follow the live scene again', await until(() => last.studio === false && last.itemsScene === last.scene));
r = await act({ type: 'obs_replay_toggle' });
check('replay buffer on', r?.ok === true && await until(() => last.replay === true));
r = await act({ type: 'obs_replay_save' });
check('save replay', r?.ok === true && obs.st.replaySaved === 1);

// OBS quits
obs.close();
check('OBS closing is reported (waiting)', await until(() => last.rpc === 'waiting', 12000), last?.rpc);
console.log(`\n${pass} passed, ${fail} failed`);
ws.close(); process.exit(fail ? 1 : 0);
