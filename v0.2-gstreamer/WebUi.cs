namespace StreamerV2;

internal static class WebUi
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="color-scheme" content="dark">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Streamer v0.2</title>
<style>
:root {
  --bg: #0c0f16; --panel: #151a24; --line: #293244; --text: #edf2ff; --muted: #8d99ad;
  --accent: #7c6cff; --accent-2: #9a8eff; --green: #49d18a; --red: #ff6e7e; --amber: #f4ba69;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.4 "Segoe UI", system-ui, sans-serif; }
button, input, select { font: inherit; }
button { cursor: pointer; }
[hidden] { display: none !important; }
.shell { max-width: 640px; margin: 0 auto; padding: 20px 18px 24px; }
.top { display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px; }
.brand { display: flex; align-items: center; gap: 11px; }
.mark { width: 34px; height: 34px; display: grid; place-items: center; border-radius: 10px; color: #fff; background: linear-gradient(145deg, #8173ff, #4231ae); font-weight: 800; letter-spacing: -1px; }
.brand h1 { margin: 0; font-size: 16px; letter-spacing: .04em; }
.status { display: inline-flex; align-items: center; gap: 8px; padding: 6px 11px; border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-size: 11px; font-weight: 800; letter-spacing: .09em; }
.status .dot { width: 8px; height: 8px; border-radius: 50%; background: #667085; }
.status.live { color: var(--green); border-color: #327b5b; } .status.live .dot { background: var(--green); }
.status.starting { color: var(--amber); border-color: #806234; } .status.starting .dot { background: var(--amber); }
.status.error { color: var(--red); border-color: #833946; } .status.error .dot { background: var(--red); }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 14px; padding: 16px; margin-bottom: 12px; }
.card h2 { margin: 0 0 10px; font-size: 12px; letter-spacing: .11em; text-transform: uppercase; color: #c7d0e4; }
.row { display: grid; grid-template-columns: 170px 1fr; gap: 8px; }
.hint { margin: 8px 0 0; color: var(--muted); font-size: 12px; }
select, input[type=text], input[type=number] { width: 100%; color: var(--text); background: #0e131d; border: 1px solid #303b4e; border-radius: 9px; padding: 9px 10px; outline: none; }
input[readonly] { color: #b8c1d4; background: #121824; }
select:focus, input:focus { border-color: var(--accent); }
.btn { border: 1px solid #364159; border-radius: 9px; padding: 9px 12px; color: #dce4f7; background: #202838; }
.btn:hover:not(:disabled) { border-color: #6f63d4; background: #2b3150; } .btn:disabled { cursor: default; opacity: .45; }
.btn.primary { background: linear-gradient(135deg, #7568ee, #5242c5); border-color: #8d83ff; color: #fff; font-weight: 700; }
.btn.danger { background: #49202c; border-color: #8e354a; color: #ffb4be; font-weight: 700; }
.modes { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.mode { text-align: left; padding: 13px 14px; border: 1px solid #364159; border-radius: 12px; background: #111723; color: var(--text); }
.mode strong { display: block; font-size: 15px; }
.mode small { display: block; color: var(--muted); margin-top: 4px; font-size: 12px; }
.mode.selected { border-color: var(--accent); background: #1b1940; box-shadow: 0 0 0 2px #7c6cff33; }
.mode:disabled { opacity: .5; cursor: default; }
.live { margin-top: 12px; padding: 10px 12px; border-radius: 10px; background: #0f1520; border: 1px solid #252e3d; font-size: 12px; color: var(--muted); }
.live b { color: #dfe6f7; font-weight: 600; }
.live .why { display: block; margin-top: 3px; color: var(--amber); }
.key-row, .link-box { display: grid; grid-template-columns: 1fr auto; gap: 8px; }
.link-box { margin-top: 8px; }
.link { display: block; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; color: #aaa2ff; background: #100f22; border: 1px solid #3f3975; border-radius: 9px; padding: 9px 10px; text-decoration: none; }
.link.empty { color: #626c80; border-color: #303746; }
.go { display: grid; grid-template-columns: 1fr; gap: 8px; margin-bottom: 12px; }
.go .btn { padding: 13px; font-size: 15px; }
details.card { padding: 0; }
details.card > summary { list-style: none; cursor: pointer; padding: 14px 16px; font-size: 12px; letter-spacing: .11em; text-transform: uppercase; color: #c7d0e4; }
details.card > summary::-webkit-details-marker { display: none; }
details.card > summary::before { content: "▸ "; color: var(--muted); }
details.card[open] > summary::before { content: "▾ "; }
.advanced { padding: 0 16px 16px; }
.toggle { display: flex; gap: 10px; align-items: flex-start; padding: 10px 12px; border: 1px solid #303b4e; border-radius: 10px; background: #111723; margin-bottom: 12px; }
.toggle input { margin-top: 3px; }
.toggle small { display: block; color: var(--muted); font-size: 12px; }
.grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }
.field { min-width: 0; } .field.wide { grid-column: span 3; }
.field label { display: block; color: var(--muted); font-size: 11px; margin: 0 0 5px; }
fieldset { border: 0; padding: 0; margin: 0; } fieldset:disabled { opacity: .45; }
.sub { margin: 14px 0 8px; color: var(--muted); font-size: 11px; letter-spacing: .08em; text-transform: uppercase; }
.log { min-height: 90px; max-height: 170px; overflow: auto; padding: 10px; background: #090c12; border: 1px solid #232b39; border-radius: 10px; color: #96a4ba; font: 11px/1.55 Consolas, monospace; white-space: pre-wrap; }
.log .error { color: #ff8997; } .log .ok { color: #63d99d; }
.adv-actions { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; }
@media (max-width: 520px) { .row, .modes { grid-template-columns: 1fr; } .grid { grid-template-columns: 1fr 1fr; } .field.wide { grid-column: span 2; } }
</style>
</head>
<body>
<main class="shell">
  <header class="top">
    <div class="brand"><div class="mark">S/</div><h1>NIGHTSHIFT STREAMER</h1></div>
    <div id="status" class="status"><span class="dot"></span><span id="statusText">IDLE</span></div>
  </header>

  <section class="card">
    <h2>What to share</h2>
    <div class="row">
      <select id="videoSource" title="One app window shares only that app's video and sound. Entire monitor shares the whole screen and PC sound (Discord excluded).">
        <option value="Window">One app window</option><option value="Monitor">Entire monitor</option>
      </select>
      <div style="display:grid;grid-template-columns:1fr auto;gap:8px">
        <select id="window" title="The app or game to share."></select>
        <select id="monitorIndex" title="The monitor to share." hidden></select>
        <button id="refresh" class="btn" title="Refresh the list of windows and monitors.">↻</button>
      </div>
    </div>
    <p id="audioNote" class="hint">Sound: only this app.</p>
  </section>

  <section class="card">
    <h2>Quality</h2>
    <div class="modes">
      <button class="mode" data-mode="Sharp" title="Sends the screen at its own resolution, up to 4K. Best for text, code and slides. Lowers itself automatically if the PC or connection can't keep up.">
        <strong>Sharp screen</strong><small>Up to 4K · text, code, slides</small></button>
      <button class="mode" data-mode="Performance720" title="Sends 720p. Lightest option: best for games and older PCs. Lowers itself further if needed.">
        <strong>Performance</strong><small>720p · games, older PCs</small></button>
    </div>
    <div id="live" class="live">The app picks the best encoder for this PC and adjusts quality while streaming.</div>
  </section>

  <section class="card">
    <h2>Viewer link</h2>
    <div class="key-row"><input id="streamKey" type="text" placeholder="e.g. thales-main" autocomplete="off" readonly title="The name in your viewer link."><button id="changeKey" class="btn">Change</button></div>
    <div class="link-box"><a id="viewerLink" class="link empty" href="#">Set a stream key to create the viewer link</a><button id="copyLink" class="btn">Copy link</button></div>
  </section>

  <div class="go">
    <button id="start" class="btn primary">Start streaming</button>
    <button id="stop" class="btn danger" hidden>Stop streaming</button>
  </div>

  <details id="advancedBox" class="card">
    <summary>Advanced</summary>
    <div class="advanced">
      <label class="toggle"><input id="autoQuality" type="checkbox">
        <span>Automatic quality (recommended)<small>Chooses encoder, resolution, FPS and bitrate and keeps adjusting them to this PC and connection. Turn off to use the manual values below.</small></span></label>
      <div class="grid">
        <div class="field wide"><label>Encoder</label><select id="encoder" title="Automatic tries NVIDIA, AMD, Intel and Windows hardware encoders before the CPU."></select><p id="encoderNote" class="hint"></p></div>
      </div>
      <fieldset id="manual">
        <p class="sub">Manual video</p>
        <div class="grid">
          <div class="field"><label>Width</label><input id="width" type="number" min="160" max="4096" step="16"></div>
          <div class="field"><label>Height</label><input id="height" type="number" min="64" max="4096" step="16"></div>
          <div class="field"><label>FPS</label><input id="fps" type="number" min="1" max="120"></div>
          <div class="field"><label>Video kbps</label><input id="videoKbps" type="number" min="100" max="100000" step="100"></div>
          <div class="field"><label>Rate control</label><select id="rateControl"><option>Cbr</option><option>Vbr</option><option>Cqp</option><option>Crf</option></select></div>
          <div class="field"><label>Scaling</label><select id="scale"><option>None</option><option>Bilinear</option><option>Bicubic</option><option>Lanczos</option></select></div>
          <div class="field"><label title="NVENC: p1 (fastest) to p7. x264: ultrafast, superfast, veryfast.">Encoder preset</label><input id="encoderPreset" type="text" placeholder="p1 / ultrafast"></div>
          <div class="field"><label>Keyframe seconds</label><input id="keyframeSeconds" type="number" min="1" max="10"></div>
          <div class="field"><label>B-frames</label><input id="bFrames" type="number" min="0" max="4"></div>
          <div class="field"><label>CRF (x264)</label><input id="crf" type="number" min="0" max="51"></div>
        </div>
      </fieldset>
      <p class="sub">Audio and server</p>
      <div class="grid">
        <div class="field"><label title="100% is unchanged; 200% doubles the captured sound.">Audio boost (%)</label><input id="audioGain" type="number" min="10" max="400" step="10"></div>
        <div class="field"><label>Audio kbps</label><input id="audioKbps" type="number" min="32" max="512" step="16"></div>
        <div class="field wide"><label>WHIP endpoint</label><input id="endpoint" type="text"></div>
      </div>
      <div class="adv-actions"><button id="save" class="btn">Save settings</button><button id="capture" class="btn" title="Records 10 seconds to a local file to check capture and sound without going live.">Local test · 10 s</button></div>
      <p class="sub">Activity</p>
      <div id="log" class="log"></div>
    </div>
  </details>
</main>
<script>
const $ = id => document.getElementById(id);
let current = { running: false, windows: [], monitors: [], settings: {}, encoders: [] };
let mode = 'Performance720';
let keyEditing = false;
const post = (type, extra = {}) => window.chrome.webview.postMessage({ type, ...extra });
const val = id => $(id).value;
const set = (id, value) => { if (value !== undefined && value !== null && $(id)) $(id).value = value; };
function settings() {
  return { endpoint: val('endpoint'), streamKey: val('streamKey'), encoder: val('encoder'), rateControl: val('rateControl'), scale: val('scale'),
    encoderPreset: val('encoderPreset'), width: Number(val('width')), height: Number(val('height')), fps: Number(val('fps')), videoKbps: Number(val('videoKbps')),
    audioKbps: Number(val('audioKbps')), audioGain: Number(val('audioGain')) / 100, keyframeSeconds: Number(val('keyframeSeconds')), bFrames: Number(val('bFrames')),
    crf: Number(val('crf')), videoSource: val('videoSource'), monitorIndex: Number(val('monitorIndex')), mode, autoQuality: $('autoQuality').checked };
}
function link() { const key = val('streamKey').trim(); const a = $('viewerLink'); if (!key) { a.textContent = 'Set a stream key to create the viewer link'; a.classList.add('empty'); return ''; } const url = 'https://b.siobud.com/' + encodeURIComponent(key); a.textContent = url; a.classList.remove('empty'); return url; }
function setKeyEditing(editing) { keyEditing = editing; $('streamKey').readOnly = !editing; $('changeKey').textContent = editing ? 'Save' : 'Change'; $('changeKey').classList.toggle('primary', editing); if (editing) { $('streamKey').focus(); $('streamKey').select(); } }
function saveKey() { if (current.running) { log('Stop the stream before changing its key.', 'error'); return; } const key = val('streamKey').trim(); if (!key) { log('Stream key cannot be empty.', 'error'); return; } post('save-key', { streamKey: key }); }
function fill(selectId, items, value, label, selected) { const s = $(selectId); s.innerHTML = ''; items.forEach(i => { const o = document.createElement('option'); o.value = value(i); o.textContent = label(i); s.appendChild(o); }); if (selected !== undefined && selected !== null && [...s.options].some(o => o.value === String(selected))) s.value = String(selected); }
function setMode(m) { mode = m; document.querySelectorAll('[data-mode]').forEach(b => b.classList.toggle('selected', b.dataset.mode === m)); }
function updateSource() { const monitor = val('videoSource') === 'Monitor'; $('window').hidden = monitor; $('monitorIndex').hidden = !monitor; $('audioNote').textContent = monitor ? 'Sound: everything on this PC except Discord.' : 'Sound: only this app.'; }
function updateManual() { const auto = $('autoQuality').checked; $('manual').disabled = auto; document.querySelectorAll('[data-mode]').forEach(b => b.disabled = !auto || current.running); }
function updateEncoderNote() { const chosen = (current.encoders || []).find(o => o.value === val('encoder')); $('encoderNote').textContent = chosen ? chosen.reason : 'Checking this PC’s encoders…'; }
function renderSettings(s) {
  Object.entries(s).forEach(([k, v]) => set(k, k === 'audioGain' ? Math.round(Number(v) * 100) : v));
  $('autoQuality').checked = s.autoQuality !== false; setMode(s.mode || 'Performance720'); link(); updateManual();
}
function renderState(s) {
  current = s;
  fill('encoder', s.encoders || [], o => o.value, o => o.label + (o.available ? '' : ' · not available'), s.settings.encoder);
  [...$('encoder').options].forEach((o, i) => o.disabled = !(s.encoders[i] || {}).available);
  fill('window', s.windows, w => w.hwnd, w => w.title + '  ·  ' + w.process, s.selectedHwnd);
  fill('monitorIndex', s.monitors, m => m.index, m => m.title, s.selectedMonitorIndex ?? s.settings.monitorIndex);
  renderSettings(s.settings || {}); setKeyEditing(false); updateSource(); updateEncoderNote();
  $('changeKey').disabled = s.running; $('start').hidden = s.running; $('stop').hidden = !s.running; $('capture').disabled = s.running;
  $('videoSource').disabled = s.running; $('window').disabled = s.running; $('monitorIndex').disabled = s.running;
  if (!s.running) $('live').textContent = 'The app picks the best encoder for this PC and adjusts quality while streaming.';
  if (s.status) status(s.status, s.status === 'LIVE' ? 'live' : s.running ? 'starting' : 'idle');
}
function renderLive(m) {
  const mbps = (m.bitrateKbps / 1000).toFixed(1);
  $('live').innerHTML = '';
  const line = document.createElement('div'); line.innerHTML = 'Sending <b></b> · <b></b> · <b></b>';
  const parts = line.querySelectorAll('b'); parts[0].textContent = m.width + '×' + m.height + ' ' + m.framesPerSecond + ' fps'; parts[1].textContent = mbps + ' Mbps'; parts[2].textContent = m.encoder;
  $('live').appendChild(line);
  if (m.adjustment) { const why = document.createElement('span'); why.className = 'why'; why.textContent = 'Adjusted: ' + m.adjustment; $('live').appendChild(why); }
}
function status(text, tone) { $('statusText').textContent = text; $('status').className = 'status ' + (tone || ''); }
function log(text, tone = '') { const row = document.createElement('div'); row.textContent = '[' + new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'}) + '] ' + text; if (tone) row.className = tone; $('log').appendChild(row); $('log').scrollTop = $('log').scrollHeight; }
const sourcePayload = () => val('videoSource') === 'Monitor' ? { monitorIndex: Number(val('monitorIndex')) } : { hwnd: $('window').value };
document.querySelectorAll('[data-mode]').forEach(b => b.onclick = () => { setMode(b.dataset.mode); post('save', { settings: settings(), quiet: true }); });
$('videoSource').onchange = updateSource; $('autoQuality').onchange = updateManual; $('encoder').onchange = updateEncoderNote;
$('streamKey').addEventListener('input', link); $('streamKey').addEventListener('keydown', e => { if (e.key === 'Enter') saveKey(); if (e.key === 'Escape') { renderSettings(current.settings || {}); setKeyEditing(false); } });
$('refresh').onclick = () => post('refresh'); $('save').onclick = () => post('save', { settings: settings() });
$('start').onclick = () => post('start', { ...sourcePayload(), settings: settings() }); $('capture').onclick = () => post('capture', { ...sourcePayload(), settings: settings() }); $('stop').onclick = () => post('stop');
$('changeKey').onclick = () => keyEditing ? saveKey() : setKeyEditing(true);
$('copyLink').onclick = () => { const url = link(); if (url) { post('copy', { text: url }); log('Viewer link copied.', 'ok'); } };
$('viewerLink').onclick = e => { e.preventDefault(); const url = link(); if (url) post('open', { url }); };
window.chrome.webview.addEventListener('message', e => { const m = e.data; if (m.type === 'state') renderState(m); if (m.type === 'status') status(m.text, m.tone); if (m.type === 'log') log(m.text, m.text.startsWith('Error') ? 'error' : ''); if (m.type === 'live') renderLive(m); });
post('ready');
</script>
</body>
</html>
""";
}
