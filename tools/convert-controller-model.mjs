// Converts a controller .obj into the compact format the deck's Controller page loads (kept on YOUR PC only, never in git).
// usage: node convert-controller-model.mjs <model.obj> [outDir]      default outDir = %APPDATA%\StreamDeckHost\controller
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
const [,, objPath, outArg] = process.argv;
const outDir = outArg || (process.env.APPDATA + '\\StreamDeckHost\\controller');
const P = [], N = [], tris = [];       // positions, normals, [vp0,vn0,vp1,vn1,vp2,vn2]
for (const l of readFileSync(objPath, 'utf8').split('\n')) {
  if (l.startsWith('v ')) { const p = l.split(' '); P.push([+p[1], +p[2], +p[3]]); }
  else if (l.startsWith('vn ')) { const p = l.split(' '); N.push([+p[1], +p[2], +p[3]]); }
  else if (l.startsWith('f ')) {
    const c = l.trim().split(/\s+/).slice(1).map(t => { const a = t.split('/'); return [parseInt(a[0]) - 1, a[2] ? parseInt(a[2]) - 1 : -1]; });
    for (let i = 1; i + 1 < c.length; i++) tris.push([c[0], c[i], c[i + 1]]);
  }
}
// connected parts (union-find on positions)
const par = P.map((_, i) => i); const find = x => { while (par[x] !== x) { par[x] = par[par[x]]; x = par[x]; } return x; }; const uni = (a, b) => { a = find(a); b = find(b); if (a !== b) par[a] = b; };
for (const t of tris) { uni(t[0][0], t[1][0]); uni(t[0][0], t[2][0]); }
const partOf = new Map(); const faces = []; // faces[partIdx] = tris
for (const t of tris) { const r = find(t[0][0]); if (!partOf.has(r)) { partOf.set(r, faces.length); faces.push([]); } faces[partOf.get(r)].push(t); }
const order = faces.map((f, i) => i).sort((a, b) => faces[b].length - faces[a].length);   // biggest part first
// bounding box
const mn = [1e9, 1e9, 1e9], mx = [-1e9, -1e9, -1e9]; P.forEach(v => v.forEach((c, i) => { mn[i] = Math.min(mn[i], c); mx[i] = Math.max(mx[i], c); }));
const ext = mx.map((x, i) => Math.max(1e-6, x - mn[i]));
// unique (position, normal) vertices, indices grouped by part
const vmap = new Map(); const pos = [], nor = []; const index = []; const parts = [];
for (const pi of order) {
  const start = index.length; const mnp = [1e9, 1e9, 1e9], mxp = [-1e9, -1e9, -1e9];
  for (const t of faces[pi]) for (const [vp, vn] of t) {
    const key = vp + ':' + vn; let id = vmap.get(key);
    if (id === undefined) {
      id = pos.length / 3; vmap.set(key, id); const p = P[vp];
      for (let k = 0; k < 3; k++) pos.push(Math.round(((p[k] - mn[k]) / ext[k]) * 65535 - 32768));
      const n = vn >= 0 ? N[vn] : [0, 1, 0]; for (let k = 0; k < 3; k++) nor.push(Math.max(-127, Math.min(127, Math.round(n[k] * 127))));
      nor.push(0);
    }
    index.push(id); const p = P[vp]; for (let k = 0; k < 3; k++) { mnp[k] = Math.min(mnp[k], p[k]); mxp[k] = Math.max(mxp[k], p[k]); }
  }
  parts.push({ start, count: index.length - start, min: mnp.map(x => +x.toFixed(3)), max: mxp.map(x => +x.toFixed(3)) });
}
const nv = pos.length / 3;
const posB = Buffer.from(new Int16Array(pos).buffer), norB = Buffer.from(new Int8Array(nor).buffer), idxB = Buffer.from(new Uint32Array(index).buffer);
mkdirSync(outDir, { recursive: true });
const pad = Buffer.alloc((4 - ((posB.length + norB.length) % 4)) % 4);
writeFileSync(outDir + '\\model.bin', Buffer.concat([posB, norB, pad, idxB]));
// Which piece is which button. Pieces are numbered biggest-first, so these numbers belong to the Xbox One style model "xbox_con".
// Orientation of that model: the face points to +Y, the controller's up is +X and its right is +Z.
const keys = {
  a: [6], b: [5], x: [7], y: [8], lb: [13], rb: [14], lt: [20], rt: [21], back: [16], start: [12], guide: [11, 22],
  l3: [4, 25, 33, 34, 35], r3: [3, 24, 30, 31, 32], dpad: [2],
};
const statics = { ring: [9, 10] };
writeFileSync(outDir + '\\model.json', JSON.stringify({ version: 1, vertices: nv, indices: index.length, idxOffset: posB.length + norB.length + pad.length, min: mn, max: mx, up: 'x', right: 'z', face: 'y', keys, statics, parts }));
console.log(`parts ${parts.length}, vertices ${nv}, triangles ${index.length / 3}, file ${((posB.length + norB.length + idxB.length) / 1024).toFixed(0)} KB -> ${outDir}`);
