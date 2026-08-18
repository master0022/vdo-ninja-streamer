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
  --bg: #0c0f16; --panel: #151a24; --panel-2: #1a2130; --line: #293244;
  --text: #edf2ff; --muted: #8d99ad; --accent: #7c6cff; --accent-2: #9a8eff;
  --green: #49d18a; --red: #ff6e7e; --amber: #f4ba69; --shadow: 0 18px 55px #0008;
}
* { box-sizing: border-box; }
body { margin: 0; background: radial-gradient(circle at 78% -10%, #25204d 0, transparent 38%), var(--bg); color: var(--text); font: 14px/1.4 "Segoe UI", Inter, system-ui, sans-serif; }
button, input, select { font: inherit; }
button { cursor: pointer; }
.shell { max-width: 1250px; min-width: 900px; margin: 0 auto; padding: 24px 28px 20px; }
.top { display: flex; align-items: center; justify-content: space-between; margin-bottom: 22px; }
.brand { display: flex; align-items: center; gap: 13px; }
.mark { width: 38px; height: 38px; display: grid; place-items: center; border-radius: 12px; color: #fff; background: linear-gradient(145deg, #8173ff, #4231ae); box-shadow: 0 8px 25px #5b4de955; font-weight: 800; letter-spacing: -1px; }
.brand h1 { margin: 0; font-size: 18px; letter-spacing: .04em; }
.brand p { margin: 2px 0 0; color: var(--muted); font-size: 12px; }
.status { display: inline-flex; align-items: center; gap: 8px; padding: 7px 12px; border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-size: 11px; font-weight: 800; letter-spacing: .09em; }
.status .dot { width: 8px; height: 8px; border-radius: 50%; background: #667085; box-shadow: 0 0 0 4px #66708518; }
.status.live { color: var(--green); border-color: #327b5b; }.status.live .dot { background: var(--green); box-shadow: 0 0 0 4px #49d18a22; }
.status.starting { color: var(--amber); border-color: #806234; }.status.starting .dot { background: var(--amber); }
.status.error { color: var(--red); border-color: #833946; }.status.error .dot { background: var(--red); }
.layout { display: grid; grid-template-columns: minmax(0, 1.28fr) minmax(320px, .72fr); gap: 16px; align-items: start; }
.stack { display: grid; gap: 16px; }
.card { background: linear-gradient(145deg, #171d29, #121720); border: 1px solid var(--line); border-radius: 16px; box-shadow: var(--shadow); padding: 19px; }
.card h2 { margin: 0 0 3px; font-size: 13px; letter-spacing: .11em; text-transform: uppercase; color: #c7d0e4; }
.sub { color: var(--muted); font-size: 12px; margin: 0 0 16px; }
.source-head { display: flex; justify-content: space-between; align-items: start; gap: 12px; }
.window-picker { display: grid; grid-template-columns: 1fr auto; gap: 9px; align-items: center; }
select, input[type=text], input[type=number] { width: 100%; color: var(--text); background: #0e131d; border: 1px solid #303b4e; border-radius: 9px; padding: 10px 11px; outline: none; }
input[readonly] { color: #b8c1d4; background: #121824; cursor: default; }
select:focus, input:focus { border-color: var(--accent); box-shadow: 0 0 0 3px #7c6cff22; }
select { appearance: none; background-image: linear-gradient(45deg, transparent 50%, #9ca8bd 50%), linear-gradient(135deg, #9ca8bd 50%, transparent 50%); background-position: calc(100% - 16px) 15px, calc(100% - 11px) 15px; background-size: 5px 5px; background-repeat: no-repeat; padding-right: 30px; }
.window-meta { display: flex; gap: 8px; margin-top: 10px; color: var(--muted); font-size: 11px; }
.window-meta span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.window-meta .pill { padding: 3px 7px; border-radius: 5px; background: #252d3d; color: #cbd4e8; }
[hidden] { display: none !important; }
.btn { border: 1px solid #364159; border-radius: 9px; padding: 9px 12px; color: #dce4f7; background: #202838; transition: .15s ease; }
.btn:hover:not(:disabled) { border-color: #6f63d4; background: #2b3150; transform: translateY(-1px); }.btn:disabled { cursor: default; opacity: .45; }
.btn.primary { background: linear-gradient(135deg, #7568ee, #5242c5); border-color: #8d83ff; color: #fff; font-weight: 700; }.btn.primary:hover:not(:disabled) { background: linear-gradient(135deg, #897dff, #6351e1); }
.btn.danger { background: #49202c; border-color: #8e354a; color: #ffb4be; }.btn.danger:hover:not(:disabled) { background: #652738; }
.btn.ghost { padding: 8px 10px; background: transparent; color: var(--muted); }
.key-row { display: grid; grid-template-columns: 1fr auto; gap: 8px; }.key-row input { letter-spacing: .03em; }
.link-box { display: grid; grid-template-columns: 1fr auto; gap: 8px; margin-top: 9px; align-items: center; }
.link { display: block; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; color: #aaa2ff; background: #100f22; border: 1px solid #3f3975; border-radius: 9px; padding: 10px 11px; text-decoration: none; }
.link.empty { color: #626c80; border-color: #303746; }.link:hover:not(.empty) { border-color: var(--accent-2); }
.hint { margin: 8px 0 0; color: var(--muted); font-size: 11px; }
.grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 11px; }.field { min-width: 0; }.field label { display: block; color: var(--muted); font-size: 11px; margin: 0 0 6px; cursor: help; }.field.wide { grid-column: span 2; }
.presets { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }.preset { text-align: left; min-height: 55px; }.preset strong { display: block; font-size: 12px; }.preset small { display: block; color: var(--muted); margin-top: 3px; font-size: 10px; }.preset-note { margin: 13px 0 8px; padding-top: 13px; border-top: 1px solid #293244; color: var(--muted); font-size: 11px; }
.actions { display: flex; flex-wrap: wrap; gap: 8px; }.actions .btn.primary { flex: 1; min-width: 155px; }.actions .btn.danger { min-width: 130px; }
.transport { display: flex; align-items: center; gap: 9px; color: var(--muted); padding: 11px 12px; background: #10151f; border: 1px solid #252e3d; border-radius: 10px; font-size: 11px; }.transport b { color: #d8def0; font-weight: 600; }
.log { min-height: 133px; max-height: 190px; overflow: auto; padding: 12px; background: #090c12; border: 1px solid #232b39; border-radius: 10px; color: #96a4ba; font: 11px/1.55 "Cascadia Mono", Consolas, monospace; white-space: pre-wrap; }
.log .error { color: #ff8997; }.log .ok { color: #63d99d; }
.footer { display: flex; justify-content: space-between; color: #667286; font-size: 11px; margin-top: 15px; }.footer a { color: #9d96ff; text-decoration: none; }
@media (max-width: 1000px) { .shell { min-width: 0; padding: 18px; }.layout { grid-template-columns: 1fr; }.grid { grid-template-columns: repeat(4, 1fr); } }
</style>
</head>
<body>
<main class="shell">
  <header class="top">
    <div class="brand"><div class="mark">S/</div><div><h1>NIGHTSHIFT STREAMER</h1><p>Window or monitor capture · selectable audio · WHIP</p></div></div>
    <div id="status" class="status"><span class="dot"></span><span id="statusText">IDLE</span></div>
  </header>
  <div class="layout">
    <section class="stack">
      <article class="card">
        <div class="source-head"><div><h2>Video source</h2><p class="sub">Capture one application window or an entire monitor.</p></div><button id="refresh" class="btn ghost" title="Refresh the visible application and monitor lists.">↻ Refresh</button></div>
        <div class="grid">
          <div class="field"><label title="Application window captures only the selected app video and its process audio. Entire monitor captures the full display and system audio except Discord.">Capture mode</label><select id="videoSource" title="Choose between one application window and a full monitor."><option value="Window">Application window</option><option value="Monitor">Entire monitor</option></select></div>
          <div id="windowSource" class="field wide"><label title="Only this window is captured. Its audio is isolated by process tree.">Window</label><select id="window" title="Choose the application or game to capture."></select></div>
          <div id="monitorSource" class="field wide" hidden><label title="Monitor capture includes the entire selected display. Its audio uses the system mix with Discord excluded.">Monitor</label><select id="monitorIndex" title="Choose which physical monitor to capture."></select></div>
        </div>
        <div class="window-meta"><span id="windowTitle">No video source selected</span><span id="windowPid" class="pill">—</span></div>
        <div class="field" style="margin-top:12px"><label title="This follows the capture mode automatically: selected process for a window, system audio excluding Discord for a monitor.">Audio source</label><select id="audioSource" title="Audio is tied to the video source to prevent accidentally sharing unrelated desktop audio."><option value="SelectedProcess">Selected app audio</option><option value="SystemExceptDiscord">Entire PC audio (except Discord)</option></select><p id="audioNote" class="hint">Captures the selected window's process tree.</p></div>
      </article>
      <article class="card">
        <h2>Stream link</h2><p class="sub">This public link is saved locally with your settings. Change the stream key only when you need a new link.</p>
        <div class="key-row"><input id="streamKey" type="text" placeholder="e.g. thales-main" autocomplete="off" readonly title="This is the public stream slug and the WHIP bearer token. It is saved locally, not treated as a hidden password."><button id="changeKey" class="btn" title="Unlock the stream key so you can replace the saved public link.">Change stream key</button></div>
        <div class="link-box"><a id="viewerLink" class="link empty" href="#" title="Click to open the viewer link.">Set a stream key to create the viewer link</a><button id="copyLink" class="btn" title="Copy the viewer link.">Copy link</button></div>
        <p id="keyHint" class="hint">Saved in settings-v02.json. Use “Change stream key” to replace it.</p>
      </article>
      <article class="card">
        <h2>Video and audio</h2><p class="sub">Use the tooltips on each field for recommended values. Gain is percentage-based: 100% is normal volume and the default is 200%.</p>
        <div class="grid">
          <div class="field"><label title="H.264 NVENC is the safest browser choice; HEVC/AV1 can be more efficient but require compatible viewers and hardware. x264 uses CPU.">Encoder</label><select id="encoder" title="Choose the hardware or CPU video encoder."></select><p id="encoderNote" class="hint"></p></div>
          <div class="field"><label title="CBR is recommended for live WebRTC. VBR varies bitrate, CQP targets constant quality on NVENC, and CRF is intended for x264.">Rate control</label><select id="rateControl" title="CBR is the predictable live-stream option."><option>Cbr</option><option>Vbr</option><option>Cqp</option><option>Crf</option></select></div>
          <div class="field"><label title="None is lightest, Bilinear is the budget option, Bicubic is a quality/performance balance, and Lanczos is sharpest but heavier on CPU.">Scaling</label><select id="scale" title="Choose the downscaling filter."><option>None</option><option>Bilinear</option><option>Bicubic</option><option>Lanczos</option></select></div>
          <div class="field"><label title="NVENC: p1 is fastest/lightest, p2-p3 are still low-load, p4 is a quality/latency balance, and p5-p7 trade more GPU time for quality. low-latency is recommended for older GPUs. x264: ultrafast or superfast for weak CPUs, veryfast as a starting point.">Encoder preset</label><input id="encoderPreset" type="text" placeholder="p1 / ultrafast" title="NVENC: p1-p3 for performance, p4 for balance, p5-p7 for quality. Use low-latency on older GPUs. x264: ultrafast, superfast, veryfast."></div>
          <div class="field"><label title="Output width in pixels. Common shortcuts are 1920 for 1080p, 1280 for 720p, and 854 for 480p.">Width</label><input id="width" type="number" min="160" max="4096" step="16" title="Output width in pixels."></div>
          <div class="field"><label title="Output height in pixels. Common shortcuts are 1080, 720, and 480.">Height</label><input id="height" type="number" min="64" max="4096" step="16" title="Output height in pixels."></div>
          <div class="field"><label title="30 FPS is safer for older hardware and networks; use 60 FPS only when the capture/encoder has headroom.">FPS</label><input id="fps" type="number" min="1" max="120" title="Frames per second. 30 is the safe starting point; 60 needs more headroom."></div>
          <div class="field"><label title="Video bitrate in kilobits per second. 720p30 commonly uses 1500-2500; 720p60 2500-4000; 1080p60 4500-7000.">Video kbps</label><input id="videoKbps" type="number" min="100" max="100000" step="100" title="Target video bitrate in kilobits per second."></div>
          <div class="field"><label title="Opus audio bitrate. 128-192 kbps is a good quality range; 192 kbps is the default and does not reduce video bitrate.">Audio kbps</label><input id="audioKbps" type="number" min="32" max="512" step="16" title="Opus audio bitrate in kilobits per second."></div>
          <div class="field"><label title="Percentage gain: 100% is unchanged, 200% doubles the captured audio, and 50% halves it. Viewers can always turn it down.">Audio boost (%)</label><input id="audioGain" type="number" min="10" max="400" step="10" title="100% is normal volume; 200% is the default boost."></div>
          <div class="field"><label title="Keyframe interval for WebRTC. 1 second is recommended for fast recovery and seeking.">Keyframe seconds</label><input id="keyframeSeconds" type="number" min="1" max="10" title="Recommended: 1 second for live WebRTC."></div>
          <div class="field"><label title="B-frames improve compression but can add reordering latency. 0 is recommended for WebRTC and older hardware.">B-frames</label><input id="bFrames" type="number" min="0" max="4" title="Recommended: 0 for WebRTC playback."></div>
          <div class="field"><label title="CRF is x264 quality control: lower is higher quality and larger bitrate. 20-28 is a practical range; NVENC does not use CRF.">CRF / quality</label><input id="crf" type="number" min="0" max="51" title="x264 only: 23 is a practical starting point."></div>
          <div class="field wide"><label title="WHIP ingest endpoint. Leave the default unless you are running another compatible server.">WHIP endpoint</label><input id="endpoint" type="text" title="WHIP ingest URL."></div>
        </div>
      </article>
    </section>
    <aside class="stack">
      <article class="card"><h2>Quick presets</h2><p class="sub">Resolution shortcuts change only width and height. Profiles change a few related video settings; everything remains editable.</p>
        <div class="presets"><button class="btn preset" data-resolution="1080" title="Set only the output resolution to 1920×1080."><strong>1080p</strong><small>1920 × 1080 only</small></button><button class="btn preset" data-resolution="720" title="Set only the output resolution to 1280×720."><strong>720p</strong><small>1280 × 720 only</small></button><button class="btn preset" data-resolution="480" title="Set only the output resolution to 854×480."><strong>480p</strong><small>854 × 480 only</small></button></div>
        <p class="preset-note">Stream profiles</p>
        <div class="presets"><button class="btn preset" data-preset="performance" title="Low-load 720p30 profile for older PCs."><strong>Performance</strong><small>720p30 · 1.5 Mbps · Bilinear</small></button><button class="btn preset" data-preset="balanced" title="General-purpose 720p60 profile."><strong>Balanced</strong><small>720p60 · 3.5 Mbps · Bicubic</small></button><button class="btn preset" data-preset="quality" title="Sharp 4K-to-720p downscale profile."><strong>4K Downscale</strong><small>720p60 · 5 Mbps · Lanczos</small></button></div>
      </article>
      <article class="card"><h2>Transport</h2><p class="sub">The pipeline stays inside this app. No detached OBS or FFmpeg process.</p><div class="transport"><span>●</span><b>WHIP / RTP H.264 · H.265 · Opus</b><span>fixed bitrate · short queues</span></div><p class="hint">AV1 is shown only when the bundled runtime exposes a hardware AV1 encoder. No CPU fallback is used.</p></article>
      <article class="card"><h2>Actions</h2><p class="sub">Closing this window waits for the stream pipeline to stop.</p><div class="actions"><button id="save" class="btn">Save config</button><button id="capture" class="btn">Local test · 10s</button><button id="start" class="btn primary">Start streaming</button><button id="stop" class="btn danger" disabled>Stop stream</button></div></article>
      <article class="card"><h2>Activity</h2><div id="log" class="log"></div></article>
    </aside>
  </div>
  <footer class="footer"><span>Streamer v0.2 · portable build</span><a href="#" id="docs">GStreamer / Broadcast Box docs ↗</a></footer>
</main>
<script>
const $ = id => document.getElementById(id);
let current = { running: false, windows: [], monitors: [], settings: {} };
let keyEditing = false;
const post = (type, extra = {}) => window.chrome.webview.postMessage({ type, ...extra });
const val = id => $(id).value;
const set = (id, value) => { if (value !== undefined && value !== null) $(id).value = value; };
function settings() { return { endpoint: val('endpoint'), streamKey: val('streamKey'), encoder: val('encoder'), rateControl: val('rateControl'), scale: val('scale'), encoderPreset: val('encoderPreset'), width: Number(val('width')), height: Number(val('height')), fps: Number(val('fps')), videoKbps: Number(val('videoKbps')), audioKbps: Number(val('audioKbps')), audioGain: Number(val('audioGain')) / 100, keyframeSeconds: Number(val('keyframeSeconds')), bFrames: Number(val('bFrames')), crf: Number(val('crf')), videoSource: val('videoSource'), monitorIndex: Number(val('monitorIndex')), audioSource: val('audioSource') }; }
function link() { const key = val('streamKey').trim(); const a = $('viewerLink'); if (!key) { a.textContent = 'Set a stream key to create the viewer link'; a.classList.add('empty'); a.href = '#'; return ''; } const url = 'https://b.siobud.com/' + encodeURIComponent(key); a.textContent = url; a.classList.remove('empty'); a.href = '#'; return url; }
function setKeyEditing(editing) { keyEditing = editing; $('streamKey').readOnly = !editing; $('changeKey').textContent = editing ? 'Save stream key' : 'Change stream key'; $('changeKey').classList.toggle('primary', editing); $('keyHint').textContent = editing ? 'Enter the new public link slug, then save it. This updates the persisted settings.' : 'Saved in settings-v02.json. Use “Change stream key” to replace it.'; if (editing) { $('streamKey').focus(); $('streamKey').select(); } }
function saveKey() { if (current.running) { log('Stop the stream before changing its key.', 'error'); return; } const key = val('streamKey').trim(); if (!key) { log('Stream key cannot be empty.', 'error'); return; } post('save-key', { streamKey: key }); }
function renderWindows() { const select = $('window'); const old = current.selectedHwnd; select.innerHTML = ''; current.windows.forEach(w => { const opt = document.createElement('option'); opt.value = w.hwnd; opt.textContent = w.title + '  ·  ' + w.process; select.appendChild(opt); }); if (old && [...select.options].some(o => o.value === old)) select.value = old; updateWindowMeta(); }
function renderMonitors() { const select = $('monitorIndex'); const old = current.selectedMonitorIndex ?? current.settings.monitorIndex; select.innerHTML = ''; current.monitors.forEach(m => { const opt = document.createElement('option'); opt.value = m.index; opt.textContent = m.title; select.appendChild(opt); }); if ([...select.options].some(o => String(o.value) === String(old))) select.value = old; }
function updateWindowMeta() { if (val('videoSource') === 'Monitor') { const m = current.monitors.find(x => String(x.index) === String(val('monitorIndex'))); $('windowTitle').textContent = m ? m.title : 'No monitor selected'; $('windowPid').textContent = 'FULL SCREEN'; return; } const o = $('window').selectedOptions[0]; if (!o) { $('windowTitle').textContent = 'No window selected'; $('windowPid').textContent = 'PID —'; return; } const w = current.windows.find(x => x.hwnd === o.value); $('windowTitle').textContent = w ? w.title : 'No window selected'; $('windowPid').textContent = w ? 'PID ' + w.pid : 'PID —'; }
function updateSourceMode() { const monitor = val('videoSource') === 'Monitor'; $('audioSource').value = monitor ? 'SystemExceptDiscord' : 'SelectedProcess'; $('audioSource').disabled = true; $('windowSource').hidden = monitor; $('monitorSource').hidden = !monitor; $('audioNote').textContent = monitor ? 'Captures system audio while excluding the Discord process tree.' : 'Captures only the selected window\'s process tree.'; if (monitor && ![...$('monitorIndex').options].some(o => String(o.value) === String(val('monitorIndex'))) && $('monitorIndex').options.length) $('monitorIndex').selectedIndex = 0; updateWindowMeta(); }
function renderEncoders(options) { const select = $('encoder'); const selected = select.value || current.settings.encoder; select.innerHTML = ''; (options || []).forEach(o => { const option = document.createElement('option'); option.value = o.value; option.textContent = o.label + (o.available ? '' : '  · runtime check'); option.title = o.reason || ''; select.appendChild(option); }); if ([...select.options].some(o => o.value === selected)) select.value = selected; const chosen = options && options.find(o => o.value === select.value); $('encoderNote').textContent = chosen ? chosen.reason : ''; }
function renderSettings(s) { Object.entries(s).forEach(([k, v]) => { const id = k; if ($(id)) set(id, id === 'audioGain' ? Number(v) * 100 : v); }); link(); const chosen = (current.encoders || []).find(o => o.value === val('encoder')); $('encoderNote').textContent = chosen ? chosen.reason : ''; }
function renderState(s) { current = s; renderEncoders(s.encoders || []); renderWindows(); renderMonitors(); renderSettings(s.settings || {}); setKeyEditing(false); $('changeKey').disabled = s.running; updateSourceMode(); $('start').disabled = s.running; $('capture').disabled = s.running; $('stop').disabled = !s.running; if (s.status) status(s.status, s.running ? 'live' : 'idle'); }
function status(text, tone) { $('statusText').textContent = text; $('status').className = 'status ' + (tone || ''); }
function log(text, tone = '') { const row = document.createElement('div'); row.textContent = '[' + new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'}) + '] ' + text; if (tone) row.className = tone; $('log').appendChild(row); $('log').scrollTop = $('log').scrollHeight; }
function applyResolution(name) { const resolutions = { '1080':[1920,1080], '720':[1280,720], '480':[854,480] }; const r = resolutions[name]; if (!r) return; set('width', r[0]); set('height', r[1]); log('Resolution set to ' + name + 'p. Other settings were unchanged.'); }
function preset(name) { const s = settings(); if (name === 'performance') Object.assign(s, { encoder:'H264Nvenc', rateControl:'Cbr', scale:'Bilinear', width:1280, height:720, fps:30, videoKbps:1500, audioKbps:192, audioGain:2, encoderPreset:'low-latency', keyframeSeconds:1, bFrames:0 }); if (name === 'balanced') Object.assign(s, { encoder:'H264Nvenc', rateControl:'Cbr', scale:'Bicubic', width:1280, height:720, fps:60, videoKbps:3500, audioKbps:192, audioGain:2, encoderPreset:'p1', keyframeSeconds:1, bFrames:0 }); if (name === 'quality') Object.assign(s, { encoder:'H264Nvenc', rateControl:'Cbr', scale:'Lanczos', width:1280, height:720, fps:60, videoKbps:5000, audioKbps:192, audioGain:2, encoderPreset:'p1', keyframeSeconds:1, bFrames:0 }); renderSettings(s); updateSourceMode(); log('Profile applied: ' + name + '.'); }
$('streamKey').addEventListener('input', link); $('streamKey').addEventListener('keydown', e => { if (e.key === 'Enter') saveKey(); if (e.key === 'Escape') { renderSettings(current.settings || {}); setKeyEditing(false); } }); $('window').addEventListener('change', updateWindowMeta); $('monitorIndex').addEventListener('change', updateWindowMeta); $('videoSource').addEventListener('change', updateSourceMode); $('audioSource').addEventListener('change', updateSourceMode);
const sourcePayload = () => val('videoSource') === 'Monitor' ? { monitorIndex: Number(val('monitorIndex')) } : { hwnd: $('window').value };
$('refresh').onclick = () => post('refresh'); $('save').onclick = () => post('save', { settings: settings() }); $('start').onclick = () => post('start', { ...sourcePayload(), settings: settings() }); $('capture').onclick = () => post('capture', { ...sourcePayload(), settings: settings() }); $('stop').onclick = () => post('stop');
$('changeKey').onclick = () => keyEditing ? saveKey() : setKeyEditing(true); $('copyLink').onclick = () => { const url = link(); if (url) { post('copy', { text: url }); log('Viewer link copied.', 'ok'); } };
$('viewerLink').onclick = e => { e.preventDefault(); const url = link(); if (url) post('open', { url }); }; $('docs').onclick = e => { e.preventDefault(); post('open', { url:'https://github.com/Glimesh/broadcast-box' }); };
document.querySelectorAll('[data-preset]').forEach(b => b.onclick = () => preset(b.dataset.preset)); document.querySelectorAll('[data-resolution]').forEach(b => b.onclick = () => applyResolution(b.dataset.resolution));
window.chrome.webview.addEventListener('message', e => { const m = e.data; if (m.type === 'state') renderState(m); if (m.type === 'status') status(m.text, m.tone); if (m.type === 'log') log(m.text, m.text.startsWith('Error') ? 'error' : ''); });
post('ready');
</script>
</body>
</html>
""";
}
