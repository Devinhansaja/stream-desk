// Editor icon features in headless Edge: colorful emoji icons, "colorful" style for new keys, make-all-colorful, file icon, online icon sets.
// usage: node icons-ui-test.mjs <debugPort> <hostPort> <screenshot.png>
import { writeFileSync } from 'node:fs';
const [,, dbg, port, shotFile] = process.argv;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const t = await (await fetch(`http://127.0.0.1:${dbg}/json/new?about:blank`, { method: 'PUT' })).json();
const ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const pend = {}; const errors = [];
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pend[m.id]) { pend[m.id](m); delete pend[m.id]; } if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); };
const call = (method, params = {}) => new Promise(r => { const i = ++id; pend[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
await call('Runtime.enable'); await call('Page.enable');
await call('Emulation.setDeviceMetricsOverride', { width: 1400, height: 900, deviceScaleFactor: 1, mobile: false });
await call('Page.navigate', { url: `http://127.0.0.1:${port}/editor` });
const ev = async expr => (await call('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
const until = async (expr, ms = 12000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await ev(expr)) return true; await sleep(150); } return false; };
const btn = text => `[...document.querySelectorAll('#panel button')].find(b=>b.textContent.includes(${JSON.stringify(text)}))`;

check('editor loaded', await until("document.getElementById('saved') && document.getElementById('saved').textContent==='Loaded'"));
await ev("window.alert = () => {}; window.confirm = () => true; 0");
await ev("sel = 0; draw(); 0");
check('inspector offers a colorful emoji section', await until("[...document.querySelectorAll('#panel summary')].some(s=>s.textContent.includes('Colorful emoji'))"));
await ev("document.querySelector('#panel details').open = true; 0");
check('emoji grid is filled (graphemes kept whole)', (await ev("document.querySelectorAll('#panel .icons.emo div').length")) > 20 && (await ev("[...document.querySelectorAll('#panel .icons.emo div')].some(d=>d.textContent==='🎙️')")));
await ev("[...document.querySelectorAll('#panel .icons.emo div')].find(d=>d.textContent==='🔊').click(); 0");
check('clicking an emoji sets it as the key icon', await until("page().slots[0].icon==='t:🔊'") && (await ev("document.querySelector('#grid .key .ic').textContent")) === '🔊');

// style for new keys
await ev("document.getElementById('iconStyle').value='emoji'; document.getElementById('iconStyle').dispatchEvent(new Event('change')); 0");
await ev("page().slots[23] = null; sel = 23; addToFree({kind:'action', type:'mute_toggle'}); 0"); await sleep(300);
check('with "Colorful" a new Mute key gets an emoji icon', await until("page().slots.some(s=>s && s.action.type==='mute_toggle' && s.icon==='t:🔇')"));
await ev("L.iconStyle='classic'; 0");
const before = await ev("page().slots.filter(s=>s && !s.icon.startsWith('t:') && !s.image && !s.widget).length");
await ev("document.getElementById('colorizeAll').click(); 0"); await sleep(300);
const after = await ev("page().slots.filter(s=>s && !s.icon.startsWith('t:') && !s.image && !s.widget).length");
check('"Make all keys colorful" converts classic icons', before > 5 && after < before, `classic keys on page: ${before} -> ${after}`);

// icon of a real file on this PC
await ev("sel = 1; draw(); 0");
await ev("[...document.querySelectorAll('#panel input[placeholder*=\"game.exe\"]')][0].value='C:\\\\Windows\\\\System32\\\\notepad.exe'; 0");
await ev(`${btn("Use this file")}.click(); 0`);
check("\"Use this file's icon\" puts notepad's real icon on the key", await until("page().slots[1].image && page().slots[1].image.startsWith('data:image/png;base64,') && page().slots[1].image.length > 2000"));

// online icon sets (needs internet)
await ev("sel = 2; draw(); 0");
await ev("document.querySelector('#panel input[placeholder*=\"Search online\"]').value='heart'; 0");
const sets = await ev("[...document.querySelectorAll('#panel select option')].map(o=>o.textContent).filter(t=>/emoji|logos|Colorful|Gaming/.test(t)).join('|')");
check('online library offers creative icon sets', sets.includes('3D emoji') && sets.includes('Brand logos') && sets.includes('Colorful icons'), sets);
await ev("[...document.querySelectorAll('#panel select')].find(s=>[...s.options].some(o=>o.textContent==='3D emoji')).value='fluent-emoji'; 0");
await ev(`${btn("Search icons")}.click(); 0`);
const online = await until("document.querySelectorAll('#panel .icons:not(.emo) img').length > 3", 15000);
check('3D emoji search returns icons', online, online ? '' : '(is this PC online?)');
if (online) {
  await ev("document.querySelector('#panel .icons:not(.emo) img').click(); document.querySelector('#panel .icons:not(.emo) img').parentElement.click(); 0");
  check('clicking a found icon puts it on the key', await until("page().slots[2].image && page().slots[2].image.startsWith('data:image/png') && page().slots[2].image.length > 3000", 15000));
}
await ev("sel = 1; draw(); document.querySelector('#panel details').open = true; 0"); await sleep(400);
{ const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(shotFile, Buffer.from(s.result.data, 'base64')); }
check('no script errors in the editor', errors.length === 0, errors.join(' | '));
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
