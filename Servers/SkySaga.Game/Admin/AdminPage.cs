namespace SkySaga.Game.Admin;

/// <summary>
/// The admin panel's single page. Kept as one self-contained string (no external CSS/JS/fonts)
/// so the game server can serve it without any static-file plumbing.
/// </summary>
/// <remarks>
/// The layout mirrors the in-game screen: the rucksack panel on the left (equipment row,
/// hotbar, and the 6x6 rucksack grid at slots 9-44 — see the slot map on
/// <see cref="Connection"/>), and a creative-mode item catalogue on the right that you drag
/// items from. The client's item icons live inside the .pc archives and have not been
/// extracted, so a tile shows the item's initials tinted by rarity instead.
/// </remarks>
internal static class AdminPage
{
    internal const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SkySaga admin — inventory</title>
<style>
  :root {
    --leather: #8a5a2b; --leather-dark: #6d4520; --leather-light: #a86f38;
    --slot: #7a4c24; --slot-edge: #5b3818; --ink: #f4e4c6;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 18px; background: #2b2118;
    font-family: "Trebuchet MS", Verdana, sans-serif; color: var(--ink);
    display: flex; gap: 18px; align-items: flex-start; flex-wrap: wrap;
  }
  .panel {
    background: linear-gradient(180deg, var(--leather-light), var(--leather));
    border: 4px solid var(--leather-dark); border-radius: 14px;
    box-shadow: 0 8px 24px #0008, inset 0 2px 0 #ffffff22; padding: 14px;
  }
  h2 { margin: 0 0 12px; text-align: center; letter-spacing: 3px; text-transform: uppercase;
       text-shadow: 0 2px 0 #0007; font-size: 20px; }
  .grid { display: grid; gap: 6px; }
  .rucksack { grid-template-columns: repeat(6, 56px); }
  .row { display: flex; gap: 6px; margin-bottom: 10px; flex-wrap: wrap; }
  .slot {
    width: 56px; height: 56px; border-radius: 10px;
    background: var(--slot); border: 2px solid var(--slot-edge);
    box-shadow: inset 0 3px 6px #0006; position: relative;
    display: flex; align-items: center; justify-content: center;
    font-size: 10px; text-align: center; overflow: hidden; cursor: pointer;
    user-select: none; padding: 2px; line-height: 1.1;
  }
  .slot.over { outline: 3px solid #ffd479; outline-offset: -3px; }
  .slot .n { position: absolute; top: 1px; left: 3px; font-size: 9px; opacity: .45; }
  .slot .c { position: absolute; bottom: 1px; right: 3px; font-size: 10px; font-weight: bold;
             text-shadow: 0 1px 0 #000; }
  .slot .label { word-break: break-word; font-size: 9px; }
  .filled { background: #caa268; color: #2b2118; border-color: #8a6a34; }
  .catalogue { flex: 1 1 460px; min-width: 380px; max-height: 88vh; display: flex; flex-direction: column; }
  .items { display: grid; grid-template-columns: repeat(auto-fill, minmax(84px, 1fr));
           gap: 6px; overflow-y: auto; padding-right: 6px; }
  .item { background: #5d3a1c; border: 2px solid #472c14; border-radius: 8px; padding: 6px 4px;
          font-size: 10px; text-align: center; cursor: grab; word-break: break-word; }
  .item:active { cursor: grabbing; }
  .item .pill { display: block; width: 26px; height: 26px; margin: 0 auto 4px; border-radius: 6px;
                line-height: 26px; font-weight: bold; color: #1c1209; }
  .icon { display: block; width: 34px; height: 34px; margin: 0 auto 3px; image-rendering: pixelated; }
  .slot .icon { width: 40px; height: 40px; margin: 0; }
  input, select, button {
    font: inherit; padding: 6px 8px; border-radius: 8px; border: 2px solid var(--leather-dark);
    background: #f4e4c6; color: #2b2118;
  }
  button { cursor: pointer; font-weight: bold; }
  .bar { display: flex; gap: 8px; margin-bottom: 10px; align-items: center; flex-wrap: wrap; }
  .status { font-size: 12px; min-height: 16px; opacity: .9; }
  .hint { font-size: 11px; opacity: .7; margin-top: 8px; }
  .off { color: #ffb4b4; }
</style>
</head>
<body>

<div class="panel">
  <h2>Rucksack</h2>

  <div class="row" id="equipment"></div>
  <div class="row" id="hotbar"></div>

  <div class="grid rucksack" id="rucksack"></div>

  <div class="hint">
    Drag an item from the catalogue onto a square. Click a filled square to clear it.
    <br><a href="/editor" style="color:#ffe6b0">world editor →</a>
  </div>
  <div class="status" id="status">connecting…</div>
</div>

<div class="panel catalogue">
  <h2>Items</h2>
  <div class="bar">
    <input id="search" placeholder="search…" style="flex:1 1 160px">
    <select id="category"><option value="">all categories</option></select>
    <label>count <input id="count" type="number" value="1" min="1" max="999" style="width:70px"></label>
  </div>
  <div class="items" id="items"></div>
</div>

<script>
// Slot map is the server's (see Connection.cs): 0-1 hands, 2 Head, 3 Torso, 4 Legs, 5 Arms,
// 6 hotbar, 7-8 unrendered, 9-44 rucksack (6x6).
const EQUIPMENT = [[0,'Hand L'],[1,'Hand R'],[2,'Head'],[3,'Torso'],[4,'Legs'],[5,'Arms']];
const HOTBAR = [[6,'Hotbar'],[7,'slot 7'],[8,'slot 8']];
const RUCKSACK_FIRST = 9, RUCKSACK_COUNT = 36;

const RARITY = { Common:'#cfcfcf', Uncommon:'#8fd18f', Rare:'#8fb6f0', Epic:'#c79ae8',
                 Legendary:'#f0c56b', Ultimate:'#f09a6b' };

let items = [], contents = {}, icons = new Set();

// Only ~37 items ship a 2D icon in the client (the game renders the rest from their 3D
// model), so anything without one keeps the lettered tile.
const iconFor = name => icons.has(name)
  ? `<img class="icon" src="/icons/${encodeURIComponent(name)}.png" alt="">`
  : null;

const el = (t, c) => { const e = document.createElement(t); if (c) e.className = c; return e; };
const status = m => document.getElementById('status').textContent = m;

function slotEl(index, label) {
  const s = el('div', 'slot');
  s.dataset.slot = index;
  s.innerHTML = `<span class="n">${index}</span><span class="label">${label}</span>`;

  s.addEventListener('dragover', e => { e.preventDefault(); s.classList.add('over'); });
  s.addEventListener('dragleave', () => s.classList.remove('over'));
  s.addEventListener('drop', e => {
    e.preventDefault(); s.classList.remove('over');
    give(e.dataTransfer.getData('text/plain'), index);
  });
  s.addEventListener('click', () => { if (contents[index]) give(null, index); });
  return s;
}

function build() {
  const eq = document.getElementById('equipment');
  EQUIPMENT.forEach(([i, l]) => eq.appendChild(slotEl(i, l)));
  const hb = document.getElementById('hotbar');
  HOTBAR.forEach(([i, l]) => hb.appendChild(slotEl(i, l)));
  const rs = document.getElementById('rucksack');
  for (let i = 0; i < RUCKSACK_COUNT; i++) rs.appendChild(slotEl(RUCKSACK_FIRST + i, ''));
}

function paint() {
  document.querySelectorAll('.slot').forEach(s => {
    const i = +s.dataset.slot, name = contents[i];
    const base = s.querySelector('.label');
    if (name) {
      s.classList.add('filled');
      const icon = iconFor(name);
      if (icon) { base.innerHTML = icon; s.title = name; }
      else { base.textContent = name; s.title = name; }
    } else {
      s.title = '';
      s.classList.remove('filled');
      const known = [...EQUIPMENT, ...HOTBAR].find(([n]) => n === i);
      base.textContent = known ? known[1] : '';
    }
  });
}

function initials(name) {
  return name.replace(/[^A-Za-z]/g, ' ').split(/(?=[A-Z])|\s+/)
             .filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase();
}

function renderItems() {
  const q = document.getElementById('search').value.toLowerCase();
  const cat = document.getElementById('category').value;
  const box = document.getElementById('items');
  box.innerHTML = '';

  items.filter(i => (!q || i.name.toLowerCase().includes(q)) && (!cat || i.subCategory === cat))
       .slice(0, 400)
       .forEach(i => {
    const d = el('div', 'item');
    d.draggable = true;
    const icon = iconFor(i.name);
    d.innerHTML = (icon || `<span class="pill" style="background:${RARITY[i.rarity] || '#cfcfcf'}">${initials(i.name)}</span>`) + i.name;
    d.addEventListener('dragstart', e => e.dataTransfer.setData('text/plain', i.name));
    d.title = [i.subCategory, i.rarity].filter(Boolean).join(' · ');
    box.appendChild(d);
  });
}

async function give(name, slot) {
  const count = Math.max(1, +document.getElementById('count').value || 1);
  const r = await fetch('/api/give', {
    method: 'POST',
    body: JSON.stringify({ name, count, slot })
  }).then(r => r.json()).catch(e => ({ ok: false, message: e.message }));
  status(r.message || '');
  refresh();
}

async function refresh() {
  try {
    const r = await fetch('/api/inventory').then(r => r.json());
    contents = r.slots || {};
    paint();
    if (!r.online) status('no player online — start the client and join a world');
  } catch (e) { status('server unreachable'); }
}

(async function init() {
  build();
  icons = new Set(await fetch('/api/icons').then(r => r.json()).catch(() => []));
  items = await fetch('/api/items').then(r => r.json());
  const cats = [...new Set(items.map(i => i.subCategory).filter(Boolean))].sort();
  const sel = document.getElementById('category');
  cats.forEach(c => { const o = el('option'); o.value = o.textContent = c; sel.appendChild(o); });
  renderItems();
  document.getElementById('search').addEventListener('input', renderItems);
  sel.addEventListener('change', renderItems);
  status(`${items.length} items loaded`);
  refresh();
  setInterval(refresh, 2000);
})();
</script>
</body>
</html>
""";
}
