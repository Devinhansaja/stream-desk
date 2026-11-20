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
await ev("window.__sent=[]; ws.send=m=>{window.__sent.push(m)}; 0");
await ev(`(()=>{ const tr=document.querySelectorAll('.obw .mx .mt')[1]; const r=tr.getBoundingClientRect();
  const e=(t,x)=>new PointerEvent(t,{bubbles:true,clientX:x,clientY:r.top+r.height/2,pointerId:1,isPrimary:true});
  tr.dispatchEvent(e('pointerdown', r.left+r.width*0.2)); tr.dispatchEvent(e('pointermove', r.left+r.width*0.75)); tr.dispatchEvent(e('pointerup', r.left+r.width*0.75)); })()`);
const sent = JSON.parse('[' + (await ev("window.__sent.join(',')")) + ']').filter(m => m.t === 'action' && m.action.type === 'obs_volume');
check('dragging a fader sends obs_volume for that source', sent.length >= 1 && sent.every(m => m.action.input === 'Desktop Audio') && sent[sent.length - 1].action.value >= 70, JSON.stringify(sent.map(m => m.action.value)));
{ const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(shotFile, Buffer.from(s.result.data, 'base64')); }
check('no script errors on the phone page', errors.length === 0, errors.join(' | '));
console.log(`\n${pass} passed, ${fail} failed`);
obs.close(); process.exit(fail ? 1 : 0);
