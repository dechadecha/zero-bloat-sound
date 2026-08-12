namespace ZBS.Core.Remote;

/// <summary>Страница веб-пульта: вкладки Пульт/Плейлист/Радио/Подкасты, SVG-иконки, без зависимостей.</summary>
internal static class RemotePage
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no">
<title>ZBS Пульт</title>
<style>
  :root { --bg:#0B0B0F; --card:#15151C; --card2:#1C1C25; --text:#EDEDF2; --muted:#8A8A96; --accent:#00E676; --ink:#08130B; }
  * { margin:0; padding:0; box-sizing:border-box; -webkit-tap-highlight-color:transparent; }
  html,body { height:100%; }
  body { background:var(--bg); color:var(--text); font-family:system-ui,sans-serif;
         display:flex; flex-direction:column; padding:16px 14px 10px; gap:12px; }
  h1 { font-size:12px; letter-spacing:3px; color:var(--muted); font-weight:600; text-align:center; }
  .tabs { display:flex; gap:6px; justify-content:center; }
  .tab { background:var(--card); color:var(--muted); border:none; border-radius:18px;
         padding:8px 14px; font-size:13px; font-weight:600; cursor:pointer; }
  .tab.on { background:var(--accent); color:var(--ink); }
  .page { flex:1; overflow-y:auto; display:none; }
  .page.on { display:block; }
  /* ── Пульт ── */
  #now { display:flex; flex-direction:column; align-items:center; gap:16px; padding-top:12px; }
  .np { text-align:center; max-width:340px; }
  #title { font-size:20px; font-weight:700; margin-bottom:5px; word-break:break-word; }
  #sub { font-size:13px; color:var(--muted); }
  .row { display:flex; gap:16px; align-items:center; margin-top:6px; }
  .btn { background:var(--card); border:none; border-radius:50%; width:62px; height:62px;
         display:flex; align-items:center; justify-content:center; cursor:pointer; }
  .btn:active { background:var(--card2); }
  .btn svg { width:24px; height:24px; fill:var(--text); }
  #play { background:var(--accent); width:82px; height:82px; }
  #play svg { width:30px; height:30px; fill:var(--ink); }
  .sliderrow { width:min(320px,86vw); display:flex; align-items:center; gap:10px; }
  .sliderrow svg { width:18px; height:18px; fill:var(--muted); flex:none; }
  input[type=range] { flex:1; accent-color:var(--accent); height:26px; }
  #time { font-size:12px; color:var(--muted); }
  .hint { font-size:11px; color:var(--muted); text-align:center; margin-top:8px; }
  /* ── Списки ── */
  .item { background:var(--card); border-radius:10px; padding:10px 12px; margin:0 0 8px;
          font-size:14px; cursor:pointer; display:flex; gap:10px; align-items:center; }
  .item:active { background:var(--card2); }
  .item.cur { outline:1.5px solid var(--accent); }
  .item .t { flex:1; min-width:0; }
  .item .name { font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .item .d { font-size:11.5px; color:var(--muted); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .num { color:var(--muted); font-size:12px; width:26px; flex:none; text-align:right; }
  .sec { font-size:11px; letter-spacing:2px; color:var(--muted); font-weight:700; margin:12px 2px 8px; }
  .empty { color:var(--muted); font-size:13px; text-align:center; padding:30px 10px; }
</style>
</head>
<body>
  <h1>ZERO-BLOAT SOUND</h1>
  <div class="tabs">
    <button class="tab on" data-p="now">Пульт</button>
    <button class="tab" data-p="queue">Плейлист</button>
    <button class="tab" data-p="radio">Радио</button>
    <button class="tab" data-p="pods">Подкасты</button>
  </div>

  <div class="page on" id="p-now">
    <div id="now">
      <div class="np"><div id="title">—</div><div id="sub"></div></div>
      <div class="sliderrow">
        <svg viewBox="0 0 24 24"><path d="M12 20c4.4 0 8-3.6 8-8s-3.6-8-8-8-8 3.6-8 8 3.6 8 8 8zm0-14a6 6 0 1 1 0 12 6 6 0 0 1 0-12zm1 2h-2v5l4 2.4 1-1.6-3-1.8V8z"/></svg>
        <input id="pos" type="range" min="0" max="1000" value="0">
      </div>
      <div id="time"></div>
      <div class="row">
        <button class="btn" onclick="cmd('prev')"><svg viewBox="0 0 24 24"><path d="M6 5h2v14H6zM20 5v14l-10-7z"/></svg></button>
        <button class="btn" id="play" onclick="cmd('playpause')"><svg id="playico" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg></button>
        <button class="btn" onclick="cmd('next')"><svg viewBox="0 0 24 24"><path d="M16 5h2v14h-2zM4 5v14l10-7z"/></svg></button>
      </div>
      <div class="sliderrow">
        <svg viewBox="0 0 24 24"><path d="M3 9v6h4l5 5V4L7 9H3z"/></svg>
        <input id="vol" type="range" min="0" max="100" value="70">
        <svg viewBox="0 0 24 24"><path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1c2.9.9 5 3.5 5 6.7s-2.1 5.8-5 6.7v2.1c4-.9 7-4.5 7-8.8s-3-7.9-7-8.8z"/></svg>
      </div>
      <div class="hint">страница не грузится? — выключи VPN на этом устройстве</div>
    </div>
  </div>

  <div class="page" id="p-queue"><div id="queue-list" class="empty">загружаю…</div></div>
  <div class="page" id="p-radio"><div id="radio-list" class="empty">загружаю…</div></div>
  <div class="page" id="p-pods"><div id="pods-list" class="empty">загружаю…</div></div>

<script>
  const base = location.pathname.replace(/\/$/, '');
  let seeking = false, volTouched = 0, curPage = 'now', podsFeed = -1;

  function cmd(name, arg) {
    return fetch(`${base}/api/cmd?name=${name}${arg !== undefined ? '&arg=' + encodeURIComponent(arg) : ''}`).catch(() => {});
  }
  function fmt(s) {
    s = Math.max(0, Math.round(s));
    return `${Math.floor(s / 60)}:${('0' + s % 60).slice(-2)}`;
  }

  // ── вкладки ──
  document.querySelectorAll('.tab').forEach(t => t.onclick = () => {
    document.querySelectorAll('.tab').forEach(x => x.classList.toggle('on', x === t));
    curPage = t.dataset.p;
    document.querySelectorAll('.page').forEach(p => p.classList.toggle('on', p.id === 'p-' + curPage));
    if (curPage === 'queue') loadQueue();
    if (curPage === 'radio') loadRadio();
    if (curPage === 'pods') loadPods();
  });

  async function getList(what) {
    const r = await fetch(`${base}/api/list?what=${what}`);
    return r.json();
  }
  function el(html) { const d = document.createElement('div'); d.innerHTML = html; return d.firstElementChild; }

  // ── плейлист ──
  async function loadQueue() {
    const box = document.getElementById('queue-list');
    try {
      const data = await getList('queue');
      box.className = ''; box.innerHTML = '';
      if (!data.items.length) { box.className = 'empty'; box.textContent = 'плейлист пуст'; return; }
      data.items.forEach(it => {
        const d = el(`<div class="item${it.current ? ' cur' : ''}"><div class="num">${it.index + 1}</div>
          <div class="t"><div class="name"></div></div></div>`);
        d.querySelector('.name').textContent = it.name;
        d.onclick = () => cmd('play', it.index).then(() => setTimeout(loadQueue, 400));
        box.appendChild(d);
      });
      const cur = box.querySelector('.cur'); if (cur) cur.scrollIntoView({block: 'center'});
    } catch (e) { box.className = 'empty'; box.textContent = 'не загрузилось'; }
  }

  // ── радио ──
  async function loadRadio() {
    const box = document.getElementById('radio-list');
    try {
      const data = await getList('radio');
      box.className = ''; box.innerHTML = '';
      const add = (title, items, kind) => {
        if (!items.length) return;
        box.appendChild(el(`<div class="sec">${title}</div>`));
        items.forEach(it => {
          const d = el(`<div class="item${it.current ? ' cur' : ''}"><div class="t">
            <div class="name"></div><div class="d"></div></div></div>`);
          d.querySelector('.name').textContent = it.name;
          d.querySelector('.d').textContent = it.detail || '';
          d.onclick = () => cmd(kind, it.index).then(() => setTimeout(loadRadio, 600));
          box.appendChild(d);
        });
      };
      add('ИЗБРАННОЕ', data.favorites, 'radiofav');
      add('FM-ШКАЛА', data.fm, 'radiofm');
      if (!box.children.length) { box.className = 'empty'; box.textContent = 'нет станций — добавь избранное в плеере'; }
    } catch (e) { box.className = 'empty'; box.textContent = 'не загрузилось'; }
  }

  // ── подкасты ──
  async function loadPods() {
    const box = document.getElementById('pods-list');
    try {
      const data = await getList('podcasts');
      box.className = ''; box.innerHTML = '';
      if (!data.feeds.length) { box.className = 'empty'; box.textContent = 'нет подписок — добавь в плеере'; return; }
      box.appendChild(el('<div class="sec">ПОДКАСТЫ</div>'));
      data.feeds.forEach(f => {
        const d = el(`<div class="item${f.index === podsFeed ? ' cur' : ''}"><div class="t"><div class="name"></div></div></div>`);
        d.querySelector('.name').textContent = f.name;
        d.onclick = () => { podsFeed = f.index; loadEpisodes(f.index); };
        box.appendChild(d);
      });
      if (podsFeed >= 0) loadEpisodes(podsFeed, true);
    } catch (e) { box.className = 'empty'; box.textContent = 'не загрузилось'; }
  }
  async function loadEpisodes(feed, keep) {
    const box = document.getElementById('pods-list');
    const old = box.querySelectorAll('.ep, .ep-sec'); old.forEach(x => x.remove());
    try {
      const r = await fetch(`${base}/api/list?what=episodes&feed=${feed}`);
      const data = await r.json();
      box.appendChild(el('<div class="sec ep-sec">ВЫПУСКИ</div>'));
      data.items.forEach(it => {
        const d = el(`<div class="item ep"><div class="t"><div class="name"></div><div class="d"></div></div></div>`);
        d.querySelector('.name').textContent = it.name;
        d.querySelector('.d').textContent = it.detail || '';
        d.onclick = () => { cmd('podcast', feed + ':' + it.index); d.querySelector('.d').textContent = 'скачиваю и включаю…'; };
        box.appendChild(d);
      });
    } catch (e) {}
  }

  // ── статус ──
  async function tick() {
    if (curPage !== 'now') return;
    try {
      const r = await fetch(`${base}/api/status`);
      const st = await r.json();
      document.getElementById('title').textContent = st.title || '—';
      document.getElementById('sub').textContent = st.subtitle || '';
      document.getElementById('playico').innerHTML = st.playing
        ? '<path d="M7 5h4v14H7zM13 5h4v14h-4z"/>'
        : '<path d="M8 5v14l11-7z"/>';
      if (!seeking && st.duration > 0) {
        document.getElementById('pos').value = Math.round(st.position / st.duration * 1000);
        document.getElementById('time').textContent = `${fmt(st.position)} / ${fmt(st.duration)}`;
      } else if (st.duration <= 0) {
        document.getElementById('time').textContent = 'прямой эфир';
      }
      if (Date.now() - volTouched > 2000)
        document.getElementById('vol').value = Math.round((st.volume || 0) * 100);
    } catch (e) { document.getElementById('sub').textContent = 'нет связи с плеером…'; }
  }
  const pos = document.getElementById('pos');
  pos.addEventListener('pointerdown', () => seeking = true);
  pos.addEventListener('change', () => { cmd('seek', pos.value / 1000); seeking = false; });
  const vol = document.getElementById('vol');
  vol.addEventListener('input', () => { volTouched = Date.now(); cmd('volume', vol.value / 100); });
  setInterval(tick, 1000); tick();
</script>
</body>
</html>
""";
}
