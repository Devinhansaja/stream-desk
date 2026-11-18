import { readFileSync } from 'node:fs';
const [,, pf, port] = process.argv; const p = JSON.parse(readFileSync(pf, 'utf8'));
const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`); const d = 'smoke-' + Math.random().toString(16).slice(2);
ws.onopen = () => ws.send(JSON.stringify({ t: 'pair', secret: p.s, deviceId: d, deviceName: 'smoke' }));
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.t === 'paired') { console.log(`${d}|${m.token}`); process.exit(0); } };
