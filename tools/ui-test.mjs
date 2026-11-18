// UI behaviour test (headless Edge via the DevTools protocol): sliders, folders, hidden titles, spans, editor actions.
// usage: node ui-test.mjs <debugPort> <hostPort> <deviceId> <token> <outPrefix>
import { writeFileSync } from 'node:fs';
const [,, dbg, port, dev, tok, outPrefix] = process.argv;
const base = `http://127.0.0.1:${port}`;
let pass = 0, fail = 0;
const check = (name, ok, extra = '') => { ok ? pass++ : fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name} ${extra}`); };

// ---- layout under test
const L = await (await fetch(`${base}/api/layout`)).json();
const home = L.profiles[0].pages[0];
const btn = (id, label, extra = {}) => ({ id, label, icon: 'bolt', image: null, color1: '#1e90ff', color2: '#7c4dff', glow: .6, confirm: false, action: { type: 'none' }, ...extra });
const folderPage = { id: 'fold1', name: 'Tools', cols: 6, rows: 4, hidden: true, slots: Array(24).fill(null) };
folderPage.slots[0] = btn('back1', 'Back', { icon: 'arrow_back', action: { type: 'back' } });
folderPage.slots[1] = btn('inf1', 'InsideKey');
home.slots = Array(24).fill(null);
home.slots[0] = btn('s1', 'Vol', { widget: 'slider:volume', rowSpan: 2, icon: 'volume_up' });
home.slots[1] = btn('s2', 'Bri', { widget: 'slider:brightness', rowSpan: 2, icon: 'wb_sunny' });
home.slots[2] = btn('f1', 'Tools', { icon: 'folder', action: { type: 'folder', pageId: 'fold1' } });
home.slots[3] = btn('n1', 'NoTitle', { hideLabel: true });
home.slots[4] = btn('n2', 'WithTitle');
L.profiles[0].pages.push(folderPage); L.showLabels = true; L.bgDim = 0.2;
let r = await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(L), headers: { 'Content-Type': 'application/json', 'X-SD-Editor': '1' } });
check('layout with sliders/folder/spans accepted', r.status === 200);

// ---- tiny CDP client
async function openPage(url, w, h) {
  const t = await (await fetch(`http://127.0.0.1:${dbg}/json/new?about:blank`, { method: 'PUT' })).json();
  const ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise(res => ws.onopen = res);
  let id = 0; const pend = {}; const errors = [];
  ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pend[m.id]) { pend[m.id](m); delete pend[m.id]; }
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); };
  const call = (method, params = {}) => new Promise(res => { const i = ++id; pend[i] = res; ws.send(JSON.stringify({ id: i, method, params })); });
  await call('Runtime.enable'); await call('Page.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile: true });
  await call('Page.navigate', { url }); await new Promise(res => setTimeout(res, 4500));
  const ev = async expr => (await call('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })).result?.result?.value;
  const shot = async name => { const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${outPrefix}${name}.png`, Buffer.from(s.result.data, 'base64')); };
  return { ev, shot, errors };
}
const sleep = ms => new Promise(res => setTimeout(res, ms));

// ================= phone deck =================
const deck = await openPage(`${base}/deck#d=${dev}&t=${tok}`, 412, 700);
check('deck connected', (await deck.ev("document.getElementById('dot').className")) === 'on');
check('covered cells are not rendered (spans)', (await deck.ev("document.querySelectorAll('.page')[0].children.length")) === 24 - 2);
check('two sliders present', (await deck.ev("document.querySelectorAll('.key.slider').length")) === 2);
check('folder page hidden from pager', (await deck.ev("document.querySelectorAll('.page').length")) === L.profiles[0].pages.filter(p => !p.hidden).length);
check('title hidden on one key only (4 of 5 keys show a title)', (await deck.ev("document.querySelectorAll('.page')[0].querySelectorAll('.lb').length")) === 4);
// slider sends volume_set while dragging (intercept the socket so the real volume is untouched)
await deck.ev("window.__sent=[]; const o=ws.send.bind(ws); ws.send=m=>{window.__sent.push(m); }; 0");
await deck.ev(`(()=>{ const k=document.querySelector('.key.slider[data-sl=volume]'); const r=k.getBoundingClientRect();
  const ev=(t,y)=>new PointerEvent(t,{bubbles:true,clientX:r.left+r.width/2,clientY:y,pointerId:1,isPrimary:true});
  k.dispatchEvent(ev('pointerdown', r.bottom-r.height*0.25)); k.dispatchEvent(ev('pointermove', r.top+r.height*0.1)); k.dispatchEvent(ev('pointerup', r.top+r.height*0.1)); })()`);
const sent = JSON.parse('[' + (await deck.ev("window.__sent.join(',')")) + ']').filter(m => m.t === 'action');
check('dragging the slider sends volume_set', sent.length >= 1 && sent.every(m => m.action.type === 'volume_set'), JSON.stringify(sent.map(m => m.action.value)));
check('dragging up gives a high value', sent.length > 0 && sent[sent.length - 1].action.value >= 85);
await deck.shot('deck_home');
// folder open / back
await deck.ev("document.querySelector('.key .lb') && [...document.querySelectorAll('.key')].find(k=>k.querySelector('.lb') && k.querySelector('.lb').textContent==='Tools').click(); 0"); await sleep(400);
check('folder opens its own page (with Back key)', (await deck.ev("[...document.querySelectorAll('.key .lb')].map(l=>l.textContent).join('|')")) === 'Back|InsideKey');
await deck.shot('deck_folder');
await deck.ev("[...document.querySelectorAll('.key')].find(k=>k.querySelector('.lb') && k.querySelector('.lb').textContent==='Back').click(); 0"); await sleep(400);
check('Back returns to the main pages (6 pages, sliders visible)', (await deck.ev("document.querySelectorAll('.page').length")) === L.profiles[0].pages.filter(p => !p.hidden).length && (await deck.ev("document.querySelectorAll('.key.slider').length")) === 2);
check('deck: no script errors', deck.errors.length === 0, deck.errors.join(' | '));

// global "show titles" off
L.showLabels = false; await fetch(`${base}/api/layout`, { method: 'PUT', body: JSON.stringify(L), headers: { 'Content-Type': 'application/json', 'X-SD-Editor': '1' } }); await sleep(800);
check('titles can be hidden for all keys', (await deck.ev("document.querySelectorAll('.key .lb').length")) === 0);
await deck.shot('deck_notitles');

// ================= PC editor =================
const ed = await openPage(`${base}/editor`, 1300, 780);
check('editor loaded', (await ed.ev("document.getElementById('saved').textContent")) === 'Loaded');
check('editor hides covered cells', (await ed.ev("document.querySelectorAll('#grid .key').length")) === 22);
check('folder tab shown as a folder pill', (await ed.ev("[...document.querySelectorAll('#pageTabs .tab')].some(t=>t.textContent.includes('Tools'))")) === true);
const before = await ed.ev("prof().pages.length");
await ed.ev("sel = 10; addToFree({kind:'action', type:'folder'}); 0"); await sleep(300);
check('dropping Folder creates a hidden page and a folder key', (await ed.ev("prof().pages.length")) === before + 1 && (await ed.ev("prof().pages[prof().pages.length-1].hidden")) === true && (await ed.ev("page().slots.some(s=>s && s.action.type==='folder')")) === true);
await ed.ev("sel = 12; addToFree({kind:'tile', i: WIDGETS.findIndex(w=>w[0]==='slider:brightness')}); 0"); await sleep(300);
check('slider tile starts 2 cells tall', (await ed.ev("page().slots[12] && page().slots[12].rowSpan")) === 2);
check('size change refused when another key is in the way', (await ed.ev("trySpan(page(), 4, 1, 2, true)")) === false);
await ed.shot('editor');
check('editor: no script errors', ed.errors.length === 0, ed.errors.join(' | '));

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
