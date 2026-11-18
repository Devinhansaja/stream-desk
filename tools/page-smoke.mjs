// usage: node cdp-shot.mjs <debugPort> <url> <out.png> <width> <height> [waitMs]
import { writeFileSync } from 'node:fs';
const [,, port, url, out, w, h, waitMs = '4000'] = process.argv;
const targets = await (await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent('about:blank')}`, { method: 'PUT' })).json();
const ws = new WebSocket(targets.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const pend = {}; const logs = [];
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pend[m.id]) { pend[m.id](m); delete pend[m.id]; }
  if (m.method === 'Runtime.exceptionThrown') logs.push('EXC ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text));
  if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') logs.push('ERR ' + m.params.args.map(a => a.value || a.description).join(' ')); };
const call = (method, params = {}) => new Promise(r => { const i = ++id; pend[i] = r; ws.send(JSON.stringify({ id: i, method, params })); });
await call('Runtime.enable'); await call('Page.enable');
await call('Emulation.setDeviceMetricsOverride', { width: +w, height: +h, deviceScaleFactor: 1, mobile: true });
await call('Page.navigate', { url });
await new Promise(r => setTimeout(r, +waitMs));
const ev = async expr => (await call('Runtime.evaluate', { expression: expr, returnByValue: true })).result?.result?.value;
console.log('keys:', await ev("document.querySelectorAll('.key:not(.empty)').length"), ' pages:', await ev("document.querySelectorAll('.page').length"),
  ' dot:', await ev("document.getElementById('dot')?.className"), ' bgChildren:', await ev("document.getElementById('bg')?.children.length"));
console.log('errors:', logs.length ? logs.join(' | ') : 'none');
const shot = await call('Page.captureScreenshot', { format: 'png' });
writeFileSync(out, Buffer.from(shot.result.data, 'base64'));
process.exit(0);
