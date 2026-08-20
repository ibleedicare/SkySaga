namespace SkySaga.Game.Admin;

/// <summary>
/// The admin panel's world editor: a three.js view of the live world that writes edits back
/// to the running game.
/// </summary>
/// <remarks>
/// Chunks come from <c>/api/world/chunk</c> exactly as the server sees them — generated
/// terrain with players' edits applied — and every click POSTs to <c>/api/world/voxel</c>,
/// which records the edit and pushes a PartialChunkEditsSync to the connected client. So this
/// is a live editor rather than the export-a-file-and-restart workflow.
///
/// three.js is loaded from a CDN, so the page needs internet access; everything else is
/// inline. Meshing only emits faces that border air, which keeps a 4x4 chunk world (half a
/// million voxels) to a few thousand triangles.
/// </remarks>
internal static class EditorPage
{
    internal const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SkySaga admin — world editor</title>
<style>
  :root { --leather:#8a5a2b; --leather-dark:#6d4520; --ink:#f4e4c6; }
  * { box-sizing: border-box; }
  html, body { margin:0; height:100%; overflow:hidden;
               font-family:"Trebuchet MS",Verdana,sans-serif; background:#2b2118; color:var(--ink); }
  #view { position:absolute; inset:0; }
  .panel { position:absolute; background:linear-gradient(180deg,#a86f38,var(--leather));
           border:3px solid var(--leather-dark); border-radius:12px; padding:10px;
           box-shadow:0 6px 20px #0008; }
  #palette { top:12px; left:12px; width:250px; max-height:88vh; overflow:auto; }
  #blockSearch { width:100%; margin-bottom:6px; padding:4px 6px; border-radius:6px;
                 border:1px solid #0006; background:#f4e4c6; color:#2b2118; font:inherit; }
  .tag { font-size:9px; padding:0 4px; border-radius:4px; background:#00000055; vertical-align:middle; }
  .tag.warn { background:#a3341f; }
  #hud { top:12px; right:12px; width:250px; font-size:12px; line-height:1.5; }
  h2 { margin:0 0 8px; font-size:15px; letter-spacing:2px; text-transform:uppercase;
       text-shadow:0 2px 0 #0007; }
  .block { display:flex; align-items:center; gap:7px; padding:3px 5px; border-radius:6px;
           cursor:pointer; font-size:11px; }
  .block:hover { background:#00000022; }
  .block.sel { background:#ffd47955; outline:2px solid #ffd479; }
  .sw { width:16px; height:16px; border-radius:3px; border:1px solid #0006; flex:none; }
  .muted { opacity:.75; }
  a { color:#ffe6b0; }
  kbd { background:#00000033; border-radius:4px; padding:1px 5px; font-family:monospace; }
  #status { margin-top:6px; min-height:16px; }
</style>
</head>
<body>
<div id="view"></div>

<div class="panel" id="palette">
  <h2>Blocks</h2>
  <input id="blockSearch" placeholder="search… (try: deposit)">
  <label class="muted"><input type="checkbox" id="onlyPlaceable"> only what a player can place</label>
  <div id="blocks"></div>
</div>

<div class="panel" id="hud">
  <h2>World editor</h2>
  <div id="info" class="muted">loading…</div>
  <div style="margin-top:8px">
    <kbd>middle drag</kbd> orbit · <kbd>shift+middle</kbd> pan · <kbd>wheel</kbd> zoom<br>
    <kbd>left click</kbd> place · <kbd>right click</kbd> dig
  </div>
  <div id="status"></div>
  <div style="margin-top:8px"><a href="/">← inventory panel</a></div>
</div>

<script src="https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.min.js"></script>
<script>
const AIR = 255;
let world, blocks = [], selected = 0;
const chunks = new Map();          // "x,y,z" -> { data, mesh }
const status = m => document.getElementById('status').textContent = m;

// Colour per block, derived from its name so the palette stays readable without textures.
function colourOf(b) {
  const n = b.name.toLowerCase();
  if (n.includes('gold')) return 0xE0B341;
  if (n.includes('iron') || n.includes('lead')) return 0x9BA0A6;
  if (n.includes('copper') || n.includes('rose')) return 0xC1743B;
  if (n.includes('water')) return 0x3E7FD1;
  if (n.includes('sand')) return 0xE3CE8B;
  if (n.includes('dirt')) return 0x8A5A34;
  if (n.includes('snow') || n.includes('ice')) return 0xDDEBF5;
  if (n.includes('wood') || n.includes('thatch')) return 0xA9763F;
  if (n.includes('leaf') || n.includes('cactus')) return 0x5C8C3A;
  if (n.includes('gravel')) return 0x8C8681;
  if (n.includes('clay')) return 0xB4705A;
  if (n.includes('bedrock')) return 0x3A3A3A;
  return 0x8B8B8B;                                   // stones and everything else
}

// ---------------------------------------------------------------- three.js
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x8fc7ea);
const camera = new THREE.PerspectiveCamera(60, innerWidth / innerHeight, 0.1, 4000);
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
document.getElementById('view').appendChild(renderer.domElement);

scene.add(new THREE.HemisphereLight(0xffffff, 0x666644, 1.1));
const sun = new THREE.DirectionalLight(0xffffff, 0.7);
sun.position.set(120, 200, 90);
scene.add(sun);

addEventListener('resize', () => {
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(innerWidth, innerHeight);
});

// Blender-style navigation, so the left button is free for editing:
//   middle drag = orbit, shift + middle = pan, wheel = zoom.
// Left click places the selected block, right click digs.
const target = new THREE.Vector3();
let dist = 140, yaw = 0.7, pitch = 0.9, orbiting = false, panning = false, lx = 0, ly = 0;

function applyCamera() {
  camera.position.set(
    target.x + dist * Math.sin(pitch) * Math.cos(yaw),
    target.y + dist * Math.cos(pitch),
    target.z + dist * Math.sin(pitch) * Math.sin(yaw));
  camera.lookAt(target);
}

renderer.domElement.addEventListener('contextmenu', e => e.preventDefault());

renderer.domElement.addEventListener('pointerdown', e => {
  if (e.button === 1) {                       // middle
    e.preventDefault();
    orbiting = !e.shiftKey;
    panning = e.shiftKey;
    lx = e.clientX; ly = e.clientY;
  }
});

addEventListener('pointerup', () => { orbiting = panning = false; });

addEventListener('pointermove', e => {
  if (!orbiting && !panning) { hover(e); return; }
  const dx = e.clientX - lx, dy = e.clientY - ly; lx = e.clientX; ly = e.clientY;
  if (orbiting) {
    yaw -= dx * 0.005;
    pitch = Math.min(Math.PI - 0.05, Math.max(0.05, pitch - dy * 0.005));
  } else {
    const right = new THREE.Vector3().setFromMatrixColumn(camera.matrix, 0);
    const up = new THREE.Vector3().setFromMatrixColumn(camera.matrix, 1);
    target.addScaledVector(right, -dx * dist * 0.0012).addScaledVector(up, dy * dist * 0.0012);
  }
  applyCamera();
});

renderer.domElement.addEventListener('wheel', e => {
  dist = Math.min(1200, Math.max(6, dist * (1 + Math.sign(e.deltaY) * 0.12)));
  applyCamera();
}, { passive: true });

// ---------------------------------------------------------------- meshing
// Only faces that border air are emitted, so a solid world costs nothing to draw.
const FACES = [
  { dir: [ 1, 0, 0], corners: [[1,0,0],[1,1,0],[1,1,1],[1,0,1]] },
  { dir: [-1, 0, 0], corners: [[0,0,1],[0,1,1],[0,1,0],[0,0,0]] },
  { dir: [ 0, 1, 0], corners: [[0,1,0],[0,1,1],[1,1,1],[1,1,0]] },
  { dir: [ 0,-1, 0], corners: [[0,0,1],[0,0,0],[1,0,0],[1,0,1]] },
  { dir: [ 0, 0, 1], corners: [[1,0,1],[1,1,1],[0,1,1],[0,0,1]] },
  { dir: [ 0, 0,-1], corners: [[0,0,0],[0,1,0],[1,1,0],[1,0,0]] },
];

function buildChunk(key) {
  const c = chunks.get(key);
  if (!c) return;
  if (c.mesh) { scene.remove(c.mesh); c.mesh.geometry.dispose(); c.mesh = null; }

  const S = world.chunkSize, { data, cx, cy, cz } = c;
  const at = (x, y, z) => (x < 0 || y < 0 || z < 0 || x >= S || y >= S || z >= S)
    ? AIR : data[y * S * S + z * S + x];

  const pos = [], norm = [], col = [], idx = [];
  const tint = new THREE.Color();

  for (let y = 0; y < S; y++) for (let z = 0; z < S; z++) for (let x = 0; x < S; x++) {
    const m = at(x, y, z);
    if (m === AIR) continue;
    tint.setHex(colourOf(blocks[m] || { name: '' }));
    for (const f of FACES) {
      if (at(x + f.dir[0], y + f.dir[1], z + f.dir[2]) !== AIR) continue;
      const start = pos.length / 3;
      for (const cn of f.corners) {
        pos.push(x + cn[0] + cx * S, y + cn[1] + cy * S, z + cn[2] + cz * S);
        norm.push(...f.dir);
        col.push(tint.r, tint.g, tint.b);
      }
      idx.push(start, start + 1, start + 2, start, start + 2, start + 3);
    }
  }

  if (!idx.length) return;
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(norm, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  g.setIndex(idx);
  c.mesh = new THREE.Mesh(g, new THREE.MeshLambertMaterial({ vertexColors: true }));
  c.mesh.userData.key = key;
  scene.add(c.mesh);
}

// ---------------------------------------------------------------- data
async function loadChunk(cx, cy, cz) {
  const r = await fetch(`/api/world/chunk?x=${cx}&y=${cy}&z=${cz}`).then(r => r.json());
  const bin = atob(r.voxels), data = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) data[i] = bin.charCodeAt(i);
  const key = `${cx},${cy},${cz}`;
  chunks.set(key, { data, cx, cy, cz, mesh: null });
  buildChunk(key);
}

function voxelAt(wx, wy, wz) {
  const S = world.chunkSize;
  const key = `${Math.floor(wx / S)},${Math.floor(wy / S)},${Math.floor(wz / S)}`;
  const c = chunks.get(key);
  if (!c) return null;
  return { c, i: (wy - c.cy * S) * S * S + (wz - c.cz * S) * S + (wx - c.cx * S) };
}

async function setVoxel(wx, wy, wz, material) {
  const hit = voxelAt(wx, wy, wz);
  if (!hit) return status('outside the loaded world');
  const r = await fetch('/api/world/voxel', {
    method: 'POST', body: JSON.stringify({ x: wx, y: wy, z: wz, material })
  }).then(r => r.json()).catch(e => ({ ok: false, message: e.message }));
  status(r.message || '');
  if (!r.ok) return;
  hit.c.data[hit.i] = material;                  // keep the view in step with the server
  buildChunk(`${hit.c.cx},${hit.c.cy},${hit.c.cz}`);
}

// ---------------------------------------------------------------- picking
const ray = new THREE.Raycaster();

// A wireframe box showing the voxel under the cursor, so it is obvious what a click hits.
const marker = new THREE.LineSegments(
  new THREE.EdgesGeometry(new THREE.BoxGeometry(1.02, 1.02, 1.02)),
  new THREE.LineBasicMaterial({ color: 0xffd479 }));
marker.visible = false;
scene.add(marker);

function pick(e) {
  ray.setFromCamera(new THREE.Vector2(
    (e.clientX / innerWidth) * 2 - 1, -(e.clientY / innerHeight) * 2 + 1), camera);

  const meshes = [...chunks.values()].map(c => c.mesh).filter(Boolean);
  const hits = ray.intersectObjects(meshes);

  if (!hits.length) return null;

  const h = hits[0];
  // Step just inside the face for the block that was hit, just outside for its neighbour.
  const inside = h.point.clone().addScaledVector(h.face.normal, -0.5);
  const outside = h.point.clone().addScaledVector(h.face.normal, 0.5);

  return {
    dig: [Math.floor(inside.x), Math.floor(inside.y), Math.floor(inside.z)],
    place: [Math.floor(outside.x), Math.floor(outside.y), Math.floor(outside.z)]
  };
}

function hover(e) {
  const p = pick(e);
  marker.visible = !!p;
  if (p) marker.position.set(p.dig[0] + 0.5, p.dig[1] + 0.5, p.dig[2] + 0.5);
}

renderer.domElement.addEventListener('pointerdown', e => {
  if (e.button !== 0 && e.button !== 2) return;

  const p = pick(e);
  if (!p) return status('nothing under the cursor');

  const [x, y, z] = e.button === 2 ? p.dig : p.place;
  setVoxel(x, y, z, e.button === 2 ? AIR : selected);
});

// ---------------------------------------------------------------- palette
function renderPalette() {
  const box = document.getElementById('blocks');
  box.innerHTML = '';
  const q = (document.getElementById('blockSearch').value || '').toLowerCase();
  const onlyPlaceable = document.getElementById('onlyPlaceable').checked;

  blocks.filter(Boolean)
        .filter(b => !onlyPlaceable || b.placeable)
        .filter(b => !q || b.name.toLowerCase().includes(q) || (b.resource || '').toLowerCase().includes(q))
        .forEach(b => {
    const d = document.createElement('div');
    d.className = 'block' + (b.id === selected ? ' sel' : '');

    // The editor writes voxel ids straight into the world, so it can place blocks no player
    // ever could — ore deposits especially. Flag the two traps: blocks the client does not
    // draw, and blocks unobtainable through the rucksack.
    const tags = [];
    if (!b.rendered) tags.push('<span class="tag warn">invisible</span>');
    if (!b.placeable) tags.push('<span class="tag">editor only</span>');

    d.innerHTML = `<span class="sw" style="background:#${colourOf(b).toString(16).padStart(6,'0')}"></span>`
      + `<span>${b.name} ${tags.join('')}<br><span class="muted">${b.id}${b.resource ? ' · drops ' + b.resource : ' · drops nothing'}</span></span>`;
    d.onclick = () => { selected = b.id; renderPalette(); };
    box.appendChild(d);
  });
}

(async function init() {
  world = await fetch('/api/world/info').then(r => r.json());
  const list = await fetch('/api/world/blocks').then(r => r.json());
  list.forEach(b => blocks[b.id] = b);
  selected = (list.find(b => b.name === 'Dirt') || list[0]).id;
  renderPalette();
  document.getElementById('blockSearch').addEventListener('input', renderPalette);
  document.getElementById('onlyPlaceable').addEventListener('change', renderPalette);

  target.set(world.spawn.x, world.spawn.y, world.spawn.z);
  dist = world.sizeChunks * world.chunkSize * 1.1;
  applyCamera();

  const n = world.sizeChunks;
  document.getElementById('info').textContent = `loading ${n * n} chunks…`;
  for (let cx = 0; cx < n; cx++) for (let cz = 0; cz < n; cz++) await loadChunk(cx, 0, cz);

  document.getElementById('info').innerHTML =
    `world ${n}x${n} chunks of ${world.chunkSize}³ · seed ${world.seed}<br>`
    + `spawn ${world.spawn.x}, ${world.spawn.y}, ${world.spawn.z}`;
  status('edits apply to the running game');
})();

(function loop() { requestAnimationFrame(loop); renderer.render(scene, camera); })();
</script>
</body>
</html>
""";
}
