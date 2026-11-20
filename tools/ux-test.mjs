// Editor UX test (headless Edge via CDP): live phone preview, accordion setup panel, guided empty state, shortcuts.
// usage: node ux-test.mjs <debugPort> <hostPort> <screenshotPrefix>
import { writeFileSync } from 'node:fs';
const [,, dbg, port, shotPrefix] = process.argv;
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
const shot = async name => { const s = await call('Page.captureScreenshot', { format: 'png' }); writeFileSync(`${shotPrefix}${name}.png`, Buffer.from(s.result.data, 'base64')); };
const key = (k, extra = {}) => ev(`document.dispatchEvent(new KeyboardEvent('keydown', ${JSON.stringify({ key: k, bubbles: true, ...extra })})); 0`);
const prev = expr => ev(`(()=>{ const d = document.getElementById('phoneFrame').contentDocument; return ${expr}; })()`);

check('editor loaded', await until("document.getElementById('saved') && document.getElementById('saved').textContent==='Loaded'"));
await ev("window.alert = () => {}; window.confirm = () => true; 0");
check('setup panel is an accordion with 4 sections', (await ev("document.querySelectorAll('details.sec').length")) === 4 && (await ev("document.querySelectorAll('details.sec[open]').length")) >= 2);
check('Profile & Page sections start open, Keys & Background are tidy (closed)', (await ev("[...document.querySelectorAll('details.sec')].map(d=>d.open).join()")) === 'true,true,false,false');

// ---- phone preview
check('phone preview is ready', await until("phone.ready === true"));
check('preview shows the same keys as the editor grid', await until(`(()=>{ const d = document.getElementById('phoneFrame').contentDocument; const p = d.querySelector('.page'); return p && p.querySelectorAll('.key:not(.empty)').length === document.querySelectorAll('#grid .key:not(.empty)').length && p.querySelectorAll('.key:not(.empty)').length > 10; })()`));
check('preview keys are not dimmed (looks like a live phone)', (await prev("d.querySelectorAll('.key.off').length")) === 0);
await shot('editor_default');
// change a key in the editor -> preview follows
await ev("sel = 0; draw(); page().slots[0].label = 'ZZTOP'; L.showLabels = true; changed(); 0");
check('editing a key updates the preview', await until(`(()=>{ const d = document.getElementById('phoneFrame').contentDocument; return [...d.querySelectorAll('.key .lb')].some(x=>x.textContent==='ZZTOP'); })()`));
// page switching follows
const pagesCount = await ev("prof().pages.filter(p=>!p.hidden).length");
await ev("gi = 1; sel = -1; draw(); 0"); await sleep(600);
check('switching the editor page scrolls the preview to that page', (await prev("Math.round(d.getElementById('pages').scrollLeft / Math.max(1, d.getElementById('pages').clientWidth))")) === 1);
// folder pages show as the opened folder
await ev("gi = 0; sel = 0; draw(); 0");
await ev("page().slots[23] = null; sel = 23; addToFree({kind:'action', type:'folder'}); 0"); await sleep(200);
await ev("gi = prof().pages.length - 1; sel = -1; draw(); 0"); await sleep(700);
check('a folder page is previewed as an opened folder', await until(`(()=>{ const d = document.getElementById('phoneFrame').contentDocument; return [...d.querySelectorAll('.key .lb')].some(x=>x.textContent==='Back') || d.querySelectorAll('.page').length === 1; })()`));
await ev("gi = 0; sel = -1; draw(); 0");
// toggle
await ev("document.getElementById('togglePhone').click(); 0");
check('phone preview can be hidden', (await ev("document.getElementById('phoneBox').style.display")) === 'none');
await ev("document.getElementById('togglePhone').click(); 0");
check('...and shown again', (await ev("document.getElementById('phoneBox').style.display")) === '');

// ---- inspector
check('nothing selected: friendly guide instead of a blank panel', (await ev("document.querySelector('#panel .empty-card h3').textContent")).includes('Build your deck'));
await ev("sel = 3; draw(); 0");
const heads = await ev("[...document.querySelectorAll('#panel .sect')].map(s=>s.textContent.replace(/^\\S+\\s+/,'')).join('|')");
check('selected key: settings grouped under clear headings', heads.includes('Appearance') && heads.includes('What it does on your PC') && heads.includes('Behavior'), heads);
await shot('editor_selected');
// ---- shortcuts
await ev("sel = 4; draw(); 0");
const n0 = await ev("page().slots.filter(Boolean).length");
await key('Delete');
check('Delete removes the selected key', (await ev("page().slots[4]")) === null && (await ev("page().slots.filter(Boolean).length")) === n0 - 1);
await ev("sel = 3; draw(); 0"); await key('d', { ctrlKey: true });
check('Ctrl+D duplicates the key into a free slot', (await ev("page().slots.filter(Boolean).length")) === n0 && (await ev("sel")) !== 3);
await ev("sel = 8; draw(); 0"); await key('ArrowRight');
check('arrow keys move the selection', (await ev("sel")) === 9);
await key('ArrowDown'); check('...down a row (6 columns)', (await ev("sel")) === 15);
await key('Escape'); check('Escape deselects', (await ev("sel")) === -1);
await ev("showToast('Test message'); 0");
check('toast notifications work', (await ev("document.getElementById('toast').classList.contains('show')")) === true);
check('no script errors', errors.length === 0, errors.join(' | '));
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
