/* applyBg(box, bg, dim): bg = {type:'image'|'video'|'preset', src|id} | legacy data-URL string | null */
function applyBg(box, bg, dim) {
  box.innerHTML = ''; box.classList.add('bgbox');
  if (!bg) return;
  if (typeof bg === 'string') bg = { type: 'image', src: bg };
  let n;
  if (bg.type === 'video') {
    n = document.createElement('video'); n.src = bg.src; n.muted = true; n.defaultMuted = true; n.loop = true; n.autoplay = true; n.playsInline = true;
    n.setAttribute('muted', ''); n.setAttribute('playsinline', ''); n.className = 'bgmedia';
    n.addEventListener('canplay', () => { const p = n.play(); if (p && p.catch) p.catch(() => {}); });
  } else if (bg.type === 'preset') { n = document.createElement('div'); n.className = 'bgp bgp-' + bg.id; }
  else { n = document.createElement('img'); n.src = bg.src; n.className = 'bgmedia'; }
  box.append(n);
  const d = document.createElement('div'); d.className = 'bgdim'; d.style.opacity = dim == null ? 0.25 : dim; box.append(d);
}
function setBgDim(box, dim) { const d = box.querySelector('.bgdim'); if (d) d.style.opacity = dim == null ? 0.25 : dim; }
