// Phone-side OBS control center test (headless Edge via CDP) against the fake OBS.
// usage: node obs-ui-test.mjs <debugPort> <hostPort> <pairing-file> <screenshot.png>
import { readFileSync, writeFileSync } from 'node:fs';
import { startFakeObs } from './fake-obs.mjs';
const [,, dbg, port, pairFile, shotFile] = process.argv;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const obs = await startFakeObs(47955, 'pw123');
const pairing = JSON.parse(readFileSync(pairFile, 'utf8'));
const cred = await new Promise(res => { const w = new WebSocket(`ws://127.0.0.1:${port}/ws`); const dev = 'oui-' + Math.random().toString(16).slice(2);
  w.onopen = () => w.send(JSON.stringify({ t: 'pair', secret: pairing.s, deviceId: dev, deviceName: 'oui' }));
  w.onmessage = e => { const m = JSON.parse(e.data); if (m.t === 'paired') { res({ dev, tok: m.token }); w.close(); } }; });
const t = await (await fetch(`http://127.0.0.1:${dbg}/json/new?about:blank`, { method: 'PUT' })).json();
const ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const pend = {}; const errors = [];
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pend[m.id]) { pend[m.id](m); delete pend[m.id]; } if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); };
const call = (method, params = {}) => new Promise(r => { const i = ++id; pend[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
await call('Runtime.enable'); await call('Page.enable');
await call('Emulation.setDeviceMetricsOverride', { width: 412, height: 760, deviceScaleFactor: 1, mobile: true });
await call('Page.navigate', { url: `http://127.0.0.1:${port}/deck#d=${cred.dev}&t=${cred.tok}` });
const ev = async expr => (await call('Runtime.evaluate', { expression: expr, returnByValue: true })).result?.result?.value;
const until = async (expr, ms = 12000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await ev(expr)) return true; await sleep(150); } return false; };

check('deck connected and the OBS page exists in the pager', await until("document.getElementById('dot').className==='on' && shownPages.some(p=>p.kind==='obs')"));
const oIdx = await ev("shownPages.findIndex(p=>p.kind==='obs')");
await ev(`document.getElementById('pages').scrollLeft = ${oIdx} * document.getElementById('pages').clientWidth; 0`);
check('OBS page shows connected + current scene', await until("document.querySelector('.obw .os').textContent.includes('Connected') && document.querySelector('.obw .os').textContent.includes('Gameplay')"));
check('3 scenes, current one highlighted', await until("document.querySelectorAll('.obw .sc').length===3 && document.querySelector('.obw .sc.cur').textContent==='Gameplay'"));
check('2 mixer rows (audio sources only)', (await ev("document.querySelectorAll('.obw .mx').length")) === 2);

await ev("[...document.querySelectorAll('.obw .sc')].find(x=>x.textContent==='Chatting').click(); 0");
check('tapping a scene switches OBS and highlights it', await until("document.querySelector('.obw .sc.cur').textContent==='Chatting'") && obs.st.scene === 'Chatting');
await ev("document.querySelector('.obw [data-a=obs_stream_toggle]').click(); 0");
check('Go live turns red and shows the timer', await until("document.querySelector('.obw [data-a=obs_stream_toggle]').classList.contains('on') && document.querySelector('.obw [data-a=obs_stream_toggle] small').textContent.includes('00:01')"));
await ev("document.querySelector('.obw [data-a=obs_record_toggle]').click(); 0");
check('Record starts and the Pause button appears', await until("document.querySelector('.obw [data-a=obs_record_toggle]').classList.contains('on') && document.querySelector('.obw [data-a=obs_record_pause_toggle]').style.display===''"));
await ev("document.querySelector('.obw [data-a=obs_virtualcam_toggle]').click(); 0");
check('Virtual cam toggles', await until("document.querySelector('.obw [data-a=obs_virtualcam_toggle]').classList.contains('on')"));
await ev("document.querySelector('.obw .mx .mm').click(); 0");
check('mute button mutes the source and dims the row', await until("document.querySelector('.obw .mx').classList.contains('muted')") && obs.st.inputs['Mic/Aux'].mute === true);
// mixer fader drag (the socket is intercepted so we can read what is sent)
await ev("window.__sent=[]; window.__orig=ws.send.bind(ws); ws.send=m=>{window.__sent.push(m)}; 0");
await ev(`(()=>{ const tr=document.querySelectorAll('.obw .mx .mt')[1]; const r=tr.getBoundingClientRect();
  const e=(t,x)=>new PointerEvent(t,{bubbles:true,clientX:x,clientY:r.top+r.height/2,pointerId:1,isPrimary:true});
  tr.dispatchEvent(e('pointerdown', r.left+r.width*0.2)); tr.dispatchEvent(e('pointermove', r.left+r.width*0.75)); tr.dispatchEvent(e('pointerup', r.left+r.width*0.75)); })()`);
const sent = JSON.parse('[' + (await ev("window.__sent.join(',')")) + ']').filter(m => m.t === 'action' && m.action.type === 'obs_volume');
await ev("ws.send = window.__orig; 0");
check('dragging a fader sends obs_volume for that source', sent.length >= 1 && sent.every(m => m.action.input === 'Desktop Audio') && sent[sent.length - 1].action.value >= 70, JSON.stringify(sent.map(m => m.action.value)));
// ---- full scene control on the phone
const click = sel => ev(`document.querySelector(${JSON.stringify(sel)}).click(); 0`);
check('sources of the live scene are listed', await until("document.querySelectorAll('.obw .oit .it').length===1 && document.querySelector('.obw .oit .in').textContent==='Webcam'"));
await click('.obw .oit .ie');
check('eye button hides the source in OBS and greys the row', await until("document.querySelector('.obw .oit .it').classList.contains('hid')") && obs.st.items['Chatting'][0].enabled === false);
await click('.obw .oit .il');
check('lock button locks it', await until("document.querySelector('.obw .oit .il').classList.contains('on')") && obs.st.items['Chatting'][0].locked === true);
await click('.obw [data-a=obs_studio_toggle]');
check('Studio mode: button lights up, Transition button appears, live scene is marked', await until("document.querySelector('.obw [data-a=obs_studio_toggle]').classList.contains('on') && document.querySelector('.obw [data-a=obs_transition]').style.display==='' && document.querySelector('.obw .sc.cur').dataset.b==='LIVE'"));
await ev("[...document.querySelectorAll('.obw .sc')].find(x=>x.textContent==='Gameplay').click(); 0");
check('tapping a scene in Studio mode selects it as preview and lists ITS sources', await until("document.querySelector('.obw .sc.prev') && document.querySelector('.obw .sc.prev').textContent==='Gameplay' && document.querySelectorAll('.obw .oit .it').length===3"));
await click('.obw [data-a=obs_transition]');
check('Transition button sends the preview live', await until("document.querySelector('.obw .sc.cur').textContent==='Gameplay'") && obs.st.scene === 'Gameplay');
check('transitions are listed with the current one marked', (await ev("[...document.querySelectorAll('.obw .tc')].map(x=>x.textContent).join('|')")) === 'Cut|Fade|Slide' && (await ev("document.querySelector('.obw .tc.cur').textContent")) === 'Fade');
await ev("[...document.querySelectorAll('.obw .tc')].find(x=>x.textContent==='Slide').click(); 0");
check('picking a transition changes it in OBS', await until("document.querySelector('.obw .tc.cur').textContent==='Slide'") && obs.st.transition === 'Slide');
await click('.obw [data-a=obs_studio_toggle]');
await click('.obw [data-a=obs_replay_toggle]');
check('replay buffer toggles and the Save replay button appears', await until("document.querySelector('.obw [data-a=obs_replay_toggle]').classList.contains('on') && document.querySelector('.obw [data-a=obs_replay_save]').style.display===''"));
obs.addScene('Be Right Back');
check('a scene created in OBS appears as a new tile', await until("document.querySelectorAll('.obw .sc').length===4"));
await ev("[...document.querySelectorAll('.obw .sc')].find(x=>x.textContent==='Be Right Back').click(); 0");
check('switching to the new (empty) scene shows its empty source list', await until("document.querySelector('.obw .oempty') && document.querySelector('.obw .osrc').textContent.includes('Be Right Back')"));
{ const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(shotFile, Buffer.from(s.result.data, 'base64')); }
await ev("document.querySelector('.obw').scrollTop = 99999; 0"); await sleep(300);
{ const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(shotFile.replace('.png', '_bottom.png'), Buffer.from(s.result.data, 'base64')); }
check('no script errors on the phone page', errors.length === 0, errors.join(' | '));
console.log(`\n${pass} passed, ${fail} failed`);
obs.close(); process.exit(fail ? 1 : 0);
