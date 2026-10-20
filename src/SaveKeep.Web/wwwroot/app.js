'use strict';

// ---------------------------------------------------------------- helpers
const $ = (id) => document.getElementById(id);
function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'checked' || k === 'disabled' || k === 'value') el[k] = v;
    else el.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}
function clear(el, ...children) { el.replaceChildren(...children.flat().filter(c => c !== null && c !== undefined && c !== false)); }
function toast(msg) { const t = $('toast'); t.textContent = msg; t.classList.remove('hidden'); clearTimeout(toast.t); toast.t = setTimeout(() => t.classList.add('hidden'), 4000); }
const store = {
  get(k, d) { try { const v = localStorage.getItem('sk.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('sk.' + k, JSON.stringify(v)); } catch { /* private mode */ } },
};

async function api(path, opts = {}) {
  const init = { method: opts.method || 'GET', headers: { 'X-SaveKeep': '1' } };
  if (opts.form) init.body = opts.form;
  else if (opts.body !== undefined) { init.body = JSON.stringify(opts.body); init.headers['Content-Type'] = 'application/json'; }
  let res;
  try { res = await fetch('/api' + path, init); }
  catch { throw new Error('The SaveKeep service is not reachable.'); }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { error: text }; }
  if (!res.ok) throw new Error((data && (data.error || data.detail || data.title)) || res.statusText);
  return data;
}
async function busy(btn, fn) {
  const label = btn.textContent;
  btn.disabled = true; btn.textContent = '...';
  try { return await fn(); }
  catch (e) { toast(e.message); }
  finally { btn.disabled = false; btn.textContent = label; }
}
function fmtSize(n) { return n === null || n === undefined ? '-' : n >= 1048576 ? (n / 1048576).toFixed(2) + ' MB' : Math.round(n / 1024) + ' KB'; }
function when(t) { return t ? new Date(t).toLocaleString() : '-'; }
function storeToast(v, what) {
  if (!v) return;
  if (v.error) toast(v.error);
  else toast(v.added ? `${what} kept in Saves.` : `${what} was already in Saves.`);
}

// ---------------------------------------------------------------- tabs
document.querySelectorAll('#tabs button').forEach(b => b.addEventListener('click', () => showTab(b.dataset.tab)));
function showTab(name) {
  if (!$('tab-' + name)) name = 'saves';
  document.querySelectorAll('#tabs button').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.id === 'tab-' + name));
  store.set('tab', name);
  if (name === 'saves') loadVault();
  if (name === 'jksv') loadJksv();
  if (name === 'log') loadLog();
}

async function loadSummary() {
  try {
    const s = await api('/status');
    $('summary').textContent = `${s.saves} save${s.saves === 1 ? '' : 's'} · ${fmtSize(s.totalBytes)} · JKSV receiver ${s.webdav ? 'on' : 'off'}`;
    $('ftp-port').placeholder = String(s.ftpPort);
  } catch (e) { $('summary').textContent = e.message; }
}

// ---------------------------------------------------------------- save details
function pad(n, w) { return String(n).padStart(w, '0'); }
function idText(d) {
  const six = d.idFormat && d.idFormat.startsWith('6');
  return `ID ${six ? pad(d.id, 6) : pad(d.id, 5)} · SID ${six ? pad(d.secretId, 4) : pad(d.secretId, 5)}`;
}
function glanceText(d) {
  return [
    `₽${d.money.toLocaleString()}`,
    `${d.playTime} played`,
    d.badges !== undefined && d.badges !== null ? `${d.badges} badge${d.badges === 1 ? '' : 's'}` : null,
    d.dexCaught !== undefined && d.dexCaught !== null ? `Dex ${d.dexCaught} caught` : null,
    d.watts !== undefined && d.watts !== null ? `${d.watts.toLocaleString()} W` : null,
  ].filter(Boolean).join(' · ');
}
function partyText(d) {
  if (!d.party.length) return 'party empty';
  return d.party.map(p => `${p.shiny ? '★' : ''}${p.isEgg ? 'Egg' : p.speciesName} ${p.level}`).join(', ');
}
function detailsBlock(d) {
  return h('details', { class: 'save-details' }, h('summary', {}, 'Save details'),
    h('dl', { class: 'kv' },
      h('dt', {}, 'Trainer'), h('dd', {}, `${d.trainer} (${d.gender}, ${d.language}) · ${d.game}, Gen ${d.generation}`),
      h('dt', {}, 'IDs'), h('dd', {}, idText(d), h('div', { class: 'muted' }, `${d.idFormat} · raw TID ${d.tid16} / SID ${d.sid16}`)),
      h('dt', {}, 'Money'), h('dd', {}, `₽${d.money.toLocaleString()}`),
      h('dt', {}, 'Play time'), h('dd', {}, d.playTime),
      d.lastSaved ? [h('dt', {}, 'Last saved'), h('dd', {}, new Date(d.lastSaved).toLocaleString([], { timeZone: 'UTC' }), h('div', { class: 'muted' }, 'by the console\'s clock, which can be wrong'))] : null,
      d.badges !== undefined && d.badges !== null ? [h('dt', {}, 'Badges'), h('dd', {}, `${d.badges} of 8`)] : null,
      d.dexSeen !== undefined && d.dexSeen !== null ? [h('dt', {}, 'Pokédex'), h('dd', {}, `${d.dexSeen} seen · ${d.dexCaught} caught`)] : null,
      d.watts !== undefined && d.watts !== null ? [h('dt', {}, 'Watts'), h('dd', {}, d.watts.toLocaleString())] : null,
      h('dt', {}, 'Boxes'), h('dd', {}, `${d.boxPokemon} Pokémon in ${d.boxesUsed} of ${d.boxesUnlocked} boxes`)),
    h('table', { class: 'party' },
      h('tr', {}, h('th', {}, '#'), h('th', {}, 'Party'), h('th', {}, 'Lv'), h('th', {}, 'Item')),
      d.party.length ? d.party.map(p => h('tr', {},
        h('td', {}, p.slot),
        h('td', {}, h('span', { class: p.shiny ? 'shiny' : '' }, (p.shiny ? '★ ' : '') + (p.isEgg ? 'Egg' : p.speciesName)), p.gender ? ` (${p.gender})` : '',
          p.isNicknamed && !p.isEgg ? h('span', { class: 'muted' }, ` "${p.nickname}"`) : null),
        h('td', {}, p.level),
        h('td', {}, p.heldItem || '-'))) : h('tr', {}, h('td', { colspan: 4, class: 'muted' }, 'No Pokémon in the party'))));
}

// ---------------------------------------------------------------- saves
const sourceLabel = { 'upload': 'uploaded', 'ftp-fetch': 'copied over FTP', 'jksv-webdav': 'JKSV upload' };
async function loadVault() {
  try { renderVault(await api('/vault')); } catch (e) { toast(e.message); }
  loadSummary();
}
function renderVault(v) {
  const count = v.games.reduce((n, g) => n + g.entries.length, 0);
  $('vault-meta').textContent = `${count} save${count === 1 ? '' : 's'} · ${fmtSize(v.totalBytes)} of ${fmtSize(v.maxTotalBytes)} · keeps ${v.keepPerGame} per game`;
  const limit = $('vault-limit');
  limit.classList.toggle('hidden', !v.overLimit);
  limit.textContent = v.overLimit ? 'Over the limit: the extra saves are pinned (or the newest of their game), so they stay until you unpin or delete them.' : '';
  if (!v.games.length) { clear($('vault'), h('p', { class: 'muted' }, 'No saves yet. Copy one over FTP, turn on the JKSV receiver, or upload a file on the Inspect tab.')); return; }
  clear($('vault'), v.games.map(g => h('div', {},
    h('h3', {}, `Pokémon ${g.edition}`, h('span', { class: 'badge' }, g.platform === 'Switch' ? 'JKSV' : g.platform === '3DS' ? 'Checkpoint' : g.platform)),
    h('div', { class: 'table-wrap' }, h('table', { class: 'vault' },
      h('tr', {}, h('th', {}, 'Stored'), h('th', {}, 'Trainer'), h('th', {}, 'Pokémon'), h('th', {}, 'Source'), h('th', {}, 'Download')),
      g.entries.map(vaultRow))))));
}
function vaultRow(e) {
  const base = `/api/vault/${encodeURIComponent(e.game)}/${encodeURIComponent(e.id)}`;
  const t = e.trainer;
  return h('tr', {},
    h('td', {}, when(e.archivedAt),
      e.pinned ? h('div', {}, h('span', { class: 'badge ok' }, 'pinned'), e.pinNote ? h('span', { class: 'muted' }, ' ' + e.pinNote) : null) : null,
      h('div', { class: 'muted' }, `${e.saveType} · ${fmtSize(e.size)}`),
      e.lastSeenAt ? h('div', { class: 'muted' }, `seen again ${when(e.lastSeenAt)}`) : null),
    h('td', {}, t.name,
      h('div', { class: 'muted' }, e.details ? idText(e.details) : `ID ${t.displayTid}`),
      e.details ? h('div', { class: 'muted' }, glanceText(e.details)) : null,
      e.details ? detailsBlock(e.details) : (e.detailsError ? h('div', { class: 'muted' }, 'Details unavailable: ' + e.detailsError) : null)),
    h('td', {}, `${e.boxPokemon} in boxes`, h('div', { class: 'muted' }, e.details ? partyText(e.details) : `party ${e.partyCount}`)),
    h('td', {}, e.sources.map(x => h('span', { class: 'badge' }, sourceLabel[x] || x)),
      e.sourceDetail ? h('div', { class: 'muted' }, e.sourceDetail) : null),
    h('td', {}, h('div', { class: 'vault-actions' },
      h('a', { class: 'button primary', href: base + '/main', download: 'main' }, 'main'),
      e.platform === 'Switch' && e.hasOriginalZip ? h('a', { class: 'button', href: base + '/original.zip', download: '' }, 'Original zip') : null,
      e.platform === 'Switch' ? h('a', { class: 'button', href: base + '/jksv.zip', download: '' }, 'main-only zip') : null,
      h('button', { onclick: (ev) => busy(ev.target, () => togglePin(e)) }, e.pinned ? 'Unpin' : 'Pin'),
      h('button', { onclick: (ev) => busy(ev.target, () => deleteSave(e)) }, 'Delete')),
      h('details', {}, h('summary', {}, 'SHA-256'), h('code', {}, e.sha256))));
}
async function togglePin(e) {
  const path = `/vault/${encodeURIComponent(e.game)}/${encodeURIComponent(e.id)}/pin`;
  if (e.pinned) { renderVault(await api(path, { method: 'POST', body: { pinned: false } })); toast('Unpinned.'); return; }
  const note = prompt('Pin this save so it is never removed automatically. Note (optional), e.g. "before trading":', '');
  if (note === null) return;
  renderVault(await api(path, { method: 'POST', body: { pinned: true, note } }));
  toast('Pinned.');
}
async function deleteSave(e) {
  if (!confirm(`Delete the ${e.edition} save of ${e.trainer.name} stored ${when(e.archivedAt)}? This can't be undone.`)) return;
  let confirmPinned = false;
  if (e.pinned) {
    if (!confirm('This save is pinned' + (e.pinNote ? ` (${e.pinNote})` : '') + '. Delete it anyway?')) return;
    confirmPinned = true;
  }
  renderVault(await api(`/vault/${encodeURIComponent(e.game)}/${encodeURIComponent(e.id)}/delete`, { method: 'POST', body: { confirm: true, confirmPinned } }));
  toast('Deleted.');
  loadSummary();
}

// ---------------------------------------------------------------- FTP
$('ftp-host').value = store.get('ftpHost', '');
$('ftp-port').value = store.get('ftpPort', '');
$('ftp-trainer').value = store.get('ftpTrainer', '');
async function loadGames() {
  const games = await api('/games');
  clear($('ftp-game'), games.map(g => h('option', { value: g.id }, `${g.edition} (${g.platform}, ${g.tool})`)));
  $('ftp-game').value = store.get('ftpGame', games[0].id);
}
$('btn-ftp-find').addEventListener('click', (e) => busy(e.target, async () => {
  const host = $('ftp-host').value.trim(), port = parseInt($('ftp-port').value, 10) || null, game = $('ftp-game').value, trainer = $('ftp-trainer').value.trim();
  store.set('ftpHost', host); store.set('ftpPort', $('ftp-port').value); store.set('ftpGame', game); store.set('ftpTrainer', trainer);
  if (!host) throw new Error('Enter the console\'s IP address.');
  renderListing(await api('/backup/find', { method: 'POST', body: { host, port, game, trainer: trainer || null } }));
}));
function renderListing(l) {
  const el = $('ftp-listing');
  if (!l) { clear(el); return; }
  const head = h('p', { class: 'muted' }, `${l.tool} backups of Pokémon ${l.game} on ${l.host}:${l.port}` + (l.trainer ? ` · looking for ${l.trainer}` : '') + ` · searched ${new Date(l.at).toLocaleTimeString()}` +
    (l.previousSearchAt ? ` · previous search ${new Date(l.previousSearchAt).toLocaleTimeString()}` : ' · first search for this game'));
  if (l.problem) { clear(el, head, h('div', { class: 'banner warn' }, l.problem)); return; }
  let picked = l.suggestedId;
  const rows = l.candidates.map(c => h('tr', {},
    h('td', { class: 'pick' }, h('input', { type: 'radio', name: 'ftp-pick', value: c.id, checked: c.id === l.suggestedId, onchange: () => { picked = c.id; } })),
    h('td', {}, c.name, c.id === l.suggestedId ? h('span', { class: 'badge ok' }, 'suggested') : null,
      c.isNew ? h('span', { class: 'badge warn' }, 'new since last search') : null,
      c.matchesTrainer ? h('span', { class: 'badge' }, 'name matches') : null,
      h('div', { class: 'muted' }, c.remotePath)),
    h('td', {}, c.firstSeenAt && !c.isNew ? when(c.firstSeenAt) : (c.isNew ? 'just now' : '-')),
    h('td', {}, c.namedAt || '-', c.modifiedAt ? h('div', { class: 'muted' }, 'file ' + when(c.modifiedAt)) : null),
    h('td', {}, fmtSize(c.size), h('div', { class: 'muted' }, c.isZip ? 'zip' : 'folder'))));
  clear(el, head,
    h('div', { class: 'table-wrap' }, h('table', {}, h('tr', {}, h('th', {}), h('th', {}, 'Backup'), h('th', {}, 'First seen'), h('th', {}, 'Time in name (console clock)'), h('th', {}, 'Size')), rows)),
    l.notes && l.notes.length ? h('ul', { class: 'plain muted' }, l.notes.map(n => h('li', {}, n))) : null,
    h('div', { class: 'row' }, h('button', { class: 'primary', onclick: (e) => busy(e.target, async () => {
      if (!picked) throw new Error('Choose a backup.');
      const r = await api('/backup/fetch', { method: 'POST', body: { candidateId: picked } });
      storeToast(r.vault, `${r.trainer.name}'s save`);
      loadVault();
    }) }, 'Copy into Saves')));
}

// ---------------------------------------------------------------- JKSV receiver
async function loadJksv() {
  try { renderJksv(await api('/webdav')); } catch (e) { toast(e.message); }
}
function renderJksv(w) {
  const pill = $('jksv-state');
  pill.className = 'pill ' + (w.lockedOut ? 'bad' : w.enabled ? 'ready' : 'off');
  pill.textContent = w.lockedOut ? 'Paused: wrong logins' : w.enabled ? 'On' : 'Off';
  clear($('jksv-status'), w.enabled
    ? h('dl', { class: 'kv' },
        h('dt', {}, 'Address'), h('dd', {}, h('code', {}, `${w.origin}/${w.basepath}/`)),
        h('dt', {}, 'User'), h('dd', {}, h('code', {}, w.username), h('span', { class: 'muted' }, ` · since ${when(w.createdAt)} · password not shown again`)),
        h('dt', {}, 'Largest upload'), h('dd', {}, fmtSize(w.maxUploadBytes)))
    : h('p', { class: 'muted' }, 'Off. JKSV uploads are refused until you turn this on.'),
    w.originWarning ? h('div', { class: 'banner warn' }, w.originWarning) : null);
  clear($('jksv-actions'),
    h('button', { class: w.enabled ? '' : 'primary', onclick: (ev) => busy(ev.target, () => jksvLogin(w.enabled)) }, w.enabled ? 'New login' : 'Turn on'),
    w.enabled ? h('button', { onclick: (ev) => busy(ev.target, jksvOff) }, 'Turn off') : null,
    h('button', { onclick: (ev) => busy(ev.target, loadJksv) }, 'Refresh list'));
  const outcome = { added: ['ok', 'kept'], duplicate: ['', 'already kept'], refused: ['warn', 'refused'] };
  clear($('jksv-uploads'), w.uploads.length
    ? h('div', { class: 'table-wrap' }, h('table', {},
        h('tr', {}, h('th', {}, 'When'), h('th', {}, 'File'), h('th', {}, 'Result')),
        w.uploads.map(u => h('tr', {},
          h('td', {}, when(u.at), h('div', { class: 'muted' }, u.remote || '')),
          h('td', {}, h('code', {}, u.path), h('div', { class: 'muted' }, fmtSize(u.size))),
          h('td', {}, h('span', { class: 'badge ' + (outcome[u.outcome] || ['', ''])[0] }, (outcome[u.outcome] || ['', u.outcome])[1]),
            u.edition ? ` ${u.edition} · ${u.trainer}` : '', h('div', { class: 'muted' }, u.message))))))
    : h('p', { class: 'muted' }, 'No uploads yet.'));
}
async function jksvLogin(replace) {
  if (replace && !confirm('Make a new login? The webdav.json on the Switch stops working until you replace it with the new one.')) return;
  const r = await api('/webdav/login', { method: 'POST', body: { confirmReplace: replace } });
  renderJksv(r.status);
  const ta = h('textarea', { class: 'jksv-json', readonly: 'readonly', rows: 6, spellcheck: 'false' });
  ta.value = r.webdavJson;
  clear($('jksv-login'),
    h('div', { class: 'banner good' }, h('strong', {}, 'Copy this now'), 'Save it as sd:/config/JKSV/webdav.json on the Switch. The password is shown only this once.'),
    ta,
    h('div', { class: 'row' },
      h('button', { class: 'primary', onclick: async () => { try { await navigator.clipboard.writeText(r.webdavJson); toast('Copied.'); } catch { ta.select(); toast('Select the text and copy it.'); } } }, 'Copy'),
      h('a', { class: 'button', href: URL.createObjectURL(new Blob([r.webdavJson + '\n'], { type: 'application/json' })), download: 'webdav.json' }, 'Download webdav.json'),
      h('button', { onclick: () => { clear($('jksv-login')); $('jksv-login').classList.add('hidden'); } }, 'Done')));
  $('jksv-login').classList.remove('hidden');
  loadSummary();
}
async function jksvOff() {
  if (!confirm('Turn the receiver off? JKSV uploads are refused until you turn it on again with a new login.')) return;
  clear($('jksv-login')); $('jksv-login').classList.add('hidden');
  renderJksv(await api('/webdav/off', { method: 'POST', body: { confirm: true } }));
  toast('Receiver off.');
  loadSummary();
}

// ---------------------------------------------------------------- inspect
const inspected = { boxes: [], box: 0 };
$('btn-inspect').addEventListener('click', (e) => busy(e.target, async () => {
  const f = $('inspect-file').files[0];
  if (!f) throw new Error('Choose a save file.');
  const form = new FormData(); form.append('save', f); form.append('keep', $('inspect-keep').checked ? '1' : '0');
  const r = await api('/inspect', { method: 'POST', form });
  storeToast(r.vault, 'This save');
  const s = r.summary, t = s.trainer, d = s.details;
  clear($('inspect-summary'), h('dl', { class: 'kv' },
    h('dt', {}, 'File'), h('dd', {}, `${s.fileName} · ${s.size.toLocaleString()} bytes`),
    h('dt', {}, 'SHA-256'), h('dd', {}, h('code', {}, s.sha256)),
    h('dt', {}, 'Save'), h('dd', {}, `${s.saveType} · ${s.game} · Gen ${s.generation}`),
    h('dt', {}, 'Trainer'), h('dd', {}, `${t.name} · ${idText(d)}`, h('div', { class: 'muted' }, glanceText(d))),
    h('dt', {}, 'Party'), h('dd', {}, partyText(d)),
    h('dt', {}, 'Boxes'), h('dd', {}, `${s.occupied} Pokémon · ${s.boxesUnlocked}/${s.boxCount} boxes unlocked`),
    h('dt', {}, 'Checksums'), h('dd', {}, s.checksumsValid ? 'valid' : 'INVALID')),
    detailsBlock(d));
  inspected.boxes = r.boxes; inspected.box = 0;
  $('inspect-boxes-card').classList.toggle('hidden', !r.boxes.length);
  renderBoxes();
  loadVault();
}));
function cellFor(s) {
  if (!s.valid) return h('div', { class: 'cell bad' }, h('div', { class: 'n' }, '!'), 'unreadable');
  if (s.empty) return h('div', { class: 'cell empty' }, h('div', { class: 'n' }, '-'), `slot ${s.slot}`);
  return h('div', { class: 'cell', title: `${s.nickname} · OT ${s.ot}` },
    h('div', { class: 'n' + (s.shiny ? ' shiny' : '') }, (s.shiny ? '★ ' : '') + (s.egg ? 'Egg' : s.speciesName)), `Lv ${s.level}`);
}
function renderBoxes() {
  const n = inspected.boxes.length;
  if (!n) return;
  const sel = $('ibox-select');
  if (sel.options.length !== n) clear(sel, inspected.boxes.map((b, i) => h('option', { value: i }, `Box ${b.box}`)));
  sel.value = String(inspected.box);
  clear($('ibox-grid'), inspected.boxes[inspected.box].slots.map(cellFor));
}
$('ibox-select').addEventListener('change', () => { inspected.box = +$('ibox-select').value; renderBoxes(); });
$('ibox-prev').addEventListener('click', () => { const n = inspected.boxes.length; if (n) { inspected.box = (inspected.box + n - 1) % n; renderBoxes(); } });
$('ibox-next').addEventListener('click', () => { const n = inspected.boxes.length; if (n) { inspected.box = (inspected.box + 1) % n; renderBoxes(); } });

// ---------------------------------------------------------------- log
async function loadLog() {
  try {
    const audit = await api('/audit?n=300');
    clear($('audit'), audit.length ? audit.map(a => h('div', {}, `${when(a.At)} · ${a.Event} · ${a.Message}`)) : h('p', { class: 'muted' }, 'Nothing logged yet.'));
  } catch (e) { toast(e.message); }
}
$('btn-log-refresh').addEventListener('click', (e) => busy(e.target, loadLog));

// ---------------------------------------------------------------- start
(async function init() {
  try {
    await loadGames();
    const b = await api('/backup');
    renderListing(b.listing);
  } catch (e) { toast(e.message); }
  showTab(store.get('tab', 'saves'));
})();
