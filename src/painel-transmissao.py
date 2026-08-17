import base64
import atexit
import ctypes
import importlib.util
import io
import json
import os
import secrets
import subprocess
import sys
import threading
import time
import urllib.parse
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from ctypes import wintypes

from PIL import Image, ImageGrab, ImageDraw


FROZEN = bool(getattr(sys, "frozen", False))
SUPERVISED = os.environ.get("VDO_NINJA_SUPERVISED") == "1"
if FROZEN:
    EXECUTABLE_DIR = Path(sys.executable).resolve().parent
    PACKAGE_DIR = Path(getattr(sys, "_MEIPASS", EXECUTABLE_DIR))
    ROOT = EXECUTABLE_DIR.parent
else:
    EXECUTABLE_DIR = Path(__file__).resolve().parent
    PACKAGE_DIR = EXECUTABLE_DIR
    ROOT = PACKAGE_DIR.parent
PORT = 8765
URL = f"http://127.0.0.1:{PORT}/"
IDENTITY_DIR = Path(
    os.environ.get(
        "VDO_NINJA_DATA_DIR",
        str(Path(os.environ.get("LOCALAPPDATA", Path.home())) / "VDO-Ninja-Streamer"),
    )
)
IDENTITY_FILE = IDENTITY_DIR / "identity.json"
LEGACY_SETTINGS_FILE = IDENTITY_DIR / "settings.json"
SETTINGS_FILE = Path(
    os.environ.get("VDO_NINJA_SETTINGS_FILE", str(ROOT / "settings.json"))
)
STATUS_SCRIPT = EXECUTABLE_DIR / ("status-transmissao.exe" if FROZEN else "status-transmissao.py")
ENCODER_OPTIONS = {
    "nvenc_h264": {
        "label": "NVIDIA NVENC H.264 (mais compatível)",
        "obs_value": "nvenc",
        "plugin": "obs-nvenc.dll",
    },
    "nvenc_hevc": {
        "label": "NVIDIA NVENC HEVC / H.265",
        "obs_value": "nvenc_hevc",
        "plugin": "obs-nvenc.dll",
    },
    "nvenc_av1": {
        "label": "NVIDIA NVENC AV1 (melhor eficiência)",
        "obs_value": "av1_nvenc",
        "plugin": "obs-nvenc.dll",
    },
    "qsv_h264": {
        "label": "Intel QuickSync H.264",
        "obs_value": "obs_qsv11",
        "plugin": "obs-qsv11.dll",
    },
    "x264": {
        "label": "x264 (CPU)",
        "obs_value": "obs_x264",
        "plugin": None,
    },
}
DEFAULT_SETTINGS = {
    "video_bitrate": 6000,
    "audio_bitrate": 160,
    "fps": 60,
    "output_width": 1920,
    "output_height": 1080,
    "encoder": "nvenc_h264",
}

PICKER_MODULE_PATH = PACKAGE_DIR / "escolher-transmissao.py"
MODULE_SPEC = importlib.util.spec_from_file_location("picker_backend", PICKER_MODULE_PATH)
PICKER = importlib.util.module_from_spec(MODULE_SPEC)
MODULE_SPEC.loader.exec_module(PICKER)


HTML = r"""<!doctype html>
<html lang="pt-BR">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Escolher transmissão</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #0b0d12;
      --panel: #121620;
      --panel-2: #181d29;
      --line: #293143;
      --text: #f4f6fb;
      --muted: #9ba5b7;
      --purple: #8b6cff;
      --purple-2: #6d4ff0;
      --green: #36c98f;
      --orange: #f0a04b;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: radial-gradient(circle at 15% 0%, #1b2040 0, transparent 38%), var(--bg);
      color: var(--text);
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
    }
    button, input { font: inherit; }
    .shell { width: min(1180px, calc(100% - 40px)); margin: 0 auto; padding: 28px 0 48px; }
    .topbar { display: flex; align-items: center; justify-content: space-between; gap: 18px; margin-bottom: 24px; }
    .brand { display: flex; align-items: center; gap: 13px; }
    .brand-mark { width: 42px; height: 42px; display: grid; place-items: center; border-radius: 13px; background: linear-gradient(135deg, var(--purple), #b98dff); font-size: 21px; box-shadow: 0 8px 28px #6d4ff033; }
    h1 { margin: 0; font-size: clamp(23px, 3vw, 31px); letter-spacing: -.04em; }
    .subtitle { margin: 4px 0 0; color: var(--muted); font-size: 14px; }
    .live-pill { display: flex; align-items: center; gap: 8px; color: #bdf5da; background: #17372e; border: 1px solid #28684f; border-radius: 999px; padding: 8px 12px; font-size: 13px; white-space: nowrap; }
    .live-pill.inactive { color: #ffb6c1; background: #2b2027; border-color: #6d3443; }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--green); box-shadow: 0 0 12px var(--green); }
    .live-pill.inactive .dot { background: #e34b60; box-shadow: 0 0 12px #e34b60; }
    .share-panel { display: grid; grid-template-columns: 1fr auto; gap: 20px; align-items: center; background: linear-gradient(115deg, #181a31, #151c28); border: 1px solid #303757; border-radius: 18px; padding: 18px 20px; margin-bottom: 24px; box-shadow: 0 20px 55px #00000026; }
    .eyebrow { color: #a99cff; text-transform: uppercase; letter-spacing: .12em; font-size: 11px; font-weight: 700; margin-bottom: 6px; }
    .link { color: #fff; font-size: 15px; word-break: break-all; }
    .share-actions { display: flex; gap: 9px; }
    .button { border: 0; border-radius: 10px; color: #fff; cursor: pointer; padding: 10px 14px; font-weight: 700; transition: transform .15s, filter .15s; }
    .button:hover { transform: translateY(-1px); filter: brightness(1.1); }
    .button.primary { background: linear-gradient(135deg, var(--purple), var(--purple-2)); }
    .button.neutral { background: #2b3342; }
    .button.green { background: #168a62; }
    .button.orange { background: #a7611d; }
    .button.danger { background: linear-gradient(135deg, #a53d50, #7d293b); border: 1px solid #cf6072; box-shadow: 0 8px 22px #8f344533; }
    .button.danger:hover { filter: brightness(1.16); }
    .button.stop-disabled { color: #9ba5b7; background: #202631; border: 1px solid #30394a; box-shadow: none; cursor: default; }
    .share-actions { flex-wrap: wrap; justify-content: flex-end; }
    .settings-panel { background: var(--panel); border: 1px solid var(--line); border-radius: 16px; padding: 17px 19px; margin-bottom: 24px; }
    .settings-head { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
    .settings-body { border-top: 1px solid var(--line); margin-top: 16px; padding-top: 16px; }
    .settings-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
    .field { display: grid; gap: 7px; color: #dce2ee; font-size: 13px; font-weight: 700; }
    .field small { color: var(--muted); font-weight: 500; }
    .field input, .field select { width: 100%; background: #0d1119; color: #fff; border: 1px solid var(--line); border-radius: 9px; padding: 10px 11px; outline: none; }
    .field input:focus, .field select:focus { border-color: var(--purple); }
    .settings-footer { display: flex; align-items: center; justify-content: space-between; gap: 15px; margin-top: 15px; }
    .settings-footer .hint { margin: 0; }
    .settings-dirty { color: #f4c06b; font-size: 12px; margin-top: 4px; }
    .tabs { display: flex; gap: 5px; border-bottom: 1px solid var(--line); margin-bottom: 18px; }
    .tab { border: 0; color: var(--muted); background: transparent; padding: 11px 14px; cursor: pointer; border-bottom: 2px solid transparent; }
    .tab.active { color: #fff; border-color: var(--purple); }
    .toolbar { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 15px; }
    .toolbar-actions { display: flex; align-items: center; gap: 9px; }
    .button:disabled { opacity: .45; cursor: not-allowed; transform: none; filter: none; }
    .section-title { font-size: 17px; font-weight: 750; }
    .search { width: min(320px, 45vw); background: var(--panel); color: #fff; border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; outline: none; }
    .search:focus { border-color: var(--purple); }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 15px; }
    .window-card { text-align: left; border: 1px solid var(--line); border-radius: 15px; padding: 0; overflow: hidden; background: var(--panel); color: #fff; cursor: pointer; transition: transform .15s, border-color .15s, box-shadow .15s; }
    .window-card:hover, .window-card.selected { transform: translateY(-2px); border-color: #8069f8; box-shadow: 0 12px 28px #0000003a; }
    .window-card.selected { outline: 2px solid #8069f866; }
    .preview { aspect-ratio: 16 / 9; display: grid; place-items: center; overflow: hidden; background: linear-gradient(135deg, #232a3d, #151923); }
    .preview img { width: 100%; height: 100%; object-fit: cover; display: block; }
    .fallback { font-size: 38px; color: #929cff; font-weight: 800; }
    .card-body { padding: 12px 13px 13px; }
    .window-title { font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .window-meta { color: var(--muted); font-size: 12px; margin-top: 5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .card-action { margin-top: 11px; color: #b8abff; font-size: 12px; font-weight: 700; }
    .screen-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(290px, 1fr)); gap: 15px; }
    .screen-card { background: var(--panel); border: 1px solid var(--line); border-radius: 15px; padding: 17px; }
    .screen-icon { height: 150px; border-radius: 10px; display: grid; place-items: center; background: linear-gradient(135deg, #252a4b, #171b27); color: #b7aaff; font-size: 54px; margin-bottom: 13px; }
    .screen-name { font-weight: 750; }
    .screen-meta { color: var(--muted); font-size: 12px; margin: 4px 0 14px; }
    .audio-option { display: flex; gap: 9px; align-items: flex-start; color: #c4ccda; font-size: 13px; margin: 0 0 14px; }
    .audio-option input { accent-color: var(--purple); margin-top: 3px; }
    .empty { border: 1px dashed #3a4356; color: var(--muted); border-radius: 14px; padding: 32px; text-align: center; grid-column: 1 / -1; }
    .status { min-height: 22px; color: #b7c0d0; font-size: 13px; margin-top: 20px; }
    .status.ok { color: #9ce8c5; }
    .status.error { color: #ffadad; }
    .hint { color: var(--muted); font-size: 12px; margin-top: 6px; }
    @media (max-width: 680px) { .shell { width: min(100% - 24px, 1180px); padding-top: 17px; } .topbar, .share-panel { grid-template-columns: 1fr; display: grid; } .live-pill { justify-self: start; } .share-actions { flex-wrap: wrap; } .search { width: 100%; } .toolbar { align-items: stretch; flex-direction: column; } }
  </style>
</head>
<body>
  <main class="shell">
    <header class="topbar">
      <div class="brand"><div class="brand-mark">▶</div><div><h1>Escolher transmissão</h1><p class="subtitle">Selecione uma janela ou a tela inteira.</p></div></div>
      <div class="live-pill inactive" id="livePill"><span class="dot"></span><span id="liveText">OBS pronto</span></div>
    </header>
    <section class="share-panel">
      <div><div class="eyebrow">Seu link de espectador</div><div class="link" id="viewerLink">gerando link...</div><div class="hint">Este link é individual para este usuário do Windows.</div></div>
      <div class="share-actions"><button class="button primary" id="copyButton">Copiar link</button><button class="button neutral" id="refreshButton">Atualizar</button><button class="button stop-disabled" id="stopButton" disabled>■ Não está transmitindo</button></div>
    </section>
    <section class="settings-panel">
      <div class="settings-head"><div><div class="eyebrow">Qualidade</div><div class="section-title">Ajustes de transmissão</div><div class="hint">Bitrate maior melhora a imagem, mas usa mais upload. Digite 7 para usar 7 Mbps (7000 kbps).</div></div><button class="button neutral" id="settingsToggle" type="button">Mostrar ajustes</button></div>
      <div class="settings-body" id="settingsBody" hidden>
        <div class="settings-grid">
          <label class="field">Vídeo <small>Mbps</small><input id="videoBitrate" type="number" min="1" max="20" step="0.5" inputmode="decimal"></label>
          <label class="field">Áudio <small>kbps</small><input id="audioBitrate" type="number" min="64" max="320" step="16" inputmode="numeric"></label>
          <label class="field">Quadros <small>por segundo</small><select id="fps"><option value="60">60 FPS</option><option value="30">30 FPS</option></select></label>
          <label class="field">Saída <small>resolução</small><select id="resolution"><option value="1920x1080">1920 × 1080</option><option value="1280x720">1280 × 720</option></select></label>
          <label class="field">Encoder <small>vídeo</small><select id="encoder"></select></label>
        </div>
        <div class="settings-footer"><div><div class="hint">Salvar configura a qualidade independentemente da janela/tela escolhida.</div><div class="hint" id="settingsFileHint">As configurações serão salvas localmente.</div><div class="settings-dirty" id="settingsDirty" hidden>Há alterações ainda não salvas.</div></div><button class="button primary" id="saveSettings" type="button">Salvar ajustes</button></div>
      </div>
    </section>
    <nav class="tabs"><button class="tab active" data-tab="windows">Janelas abertas</button><button class="tab" data-tab="screen">Tela inteira</button></nav>
    <section id="windowsPanel"><div class="toolbar"><div><div class="section-title">O que você quer mostrar?</div><div class="hint">Clique em um card para selecionar.</div></div><div class="toolbar-actions"><input class="search" id="search" placeholder="Filtrar janelas..." autocomplete="off"><button class="button green" id="shareWindowButton" disabled>Transmitir janela selecionada</button></div></div><div class="grid" id="windowGrid"></div></section>
    <section id="screenPanel" hidden><div class="toolbar"><div><div class="section-title">Escolha o monitor</div><div class="hint">O áudio dos aplicativos é capturado sem Discord; selecione o jogo na aba de janelas para priorizá-lo.</div></div></div><div class="screen-grid" id="screenGrid"></div></section>
    <div class="status" id="status"></div>
  </main>
  <script>
    let state = { windows: [], monitors: [], selected: null, settings: {} };
    let activeTab = 'windows';
    let settingsDirty = false;
    const $ = (id) => document.getElementById(id);
    const escapeHtml = (value) => String(value).replace(/[&<>'"]/g, (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
    function setStatus(text, kind='') { const el = $('status'); el.textContent = text || ''; el.className = 'status ' + kind; }
    function syncEncoderOptions() {
      const select = $('encoder');
      const selected = (state.settings || {}).encoder || select.value;
      const options = state.encoders || [];
      select.innerHTML = options.map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.label)}</option>`).join('');
      if (selected && options.some(item => item.id === selected)) select.value = selected;
    }
    function syncSettingsFields() {
      const settings = state.settings || {};
      syncEncoderOptions();
      if (settings.video_bitrate) {
        const mbps = Number(settings.video_bitrate) / 1000;
        $('videoBitrate').value = Number.isInteger(mbps) ? String(mbps) : mbps.toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
      }
      if (settings.audio_bitrate) $('audioBitrate').value = settings.audio_bitrate;
      if (settings.fps) $('fps').value = settings.fps;
      if (settings.output_width && settings.output_height) $('resolution').value = `${settings.output_width}x${settings.output_height}`;
      if (settings.encoder) $('encoder').value = settings.encoder;
    }
    function readSettingsFields() {
      const [output_width, output_height] = $('resolution').value.split('x').map(Number);
      return {
        video_bitrate: Math.round(Number($('videoBitrate').value) * 1000),
        audio_bitrate: Number($('audioBitrate').value),
        fps: Number($('fps').value),
        output_width,
        output_height,
        encoder: $('encoder').value,
      };
    }
    function refreshSettingsDirty() {
      const current = readSettingsFields();
      const saved = state.settings || {};
      const numericFields = ['video_bitrate', 'audio_bitrate', 'fps', 'output_width', 'output_height'];
      settingsDirty = numericFields.some(name => !Number.isFinite(current[name]) || Number(saved[name]) !== current[name]) || current.encoder !== saved.encoder;
      $('settingsDirty').hidden = !settingsDirty;
    }
    function render(syncSettings = false) {
      $('viewerLink').textContent = state.viewer_url || 'gerando link...';
      const active = Boolean(state.stream_active);
      $('liveText').textContent = active ? 'Transmitindo agora' : 'OBS pronto';
      $('livePill').classList.toggle('inactive', !active);
      $('stopButton').disabled = !active;
      $('stopButton').textContent = active ? '■ Parar transmissão' : '■ Não está transmitindo';
      $('stopButton').className = active ? 'button danger' : 'button stop-disabled';
      $('settingsFileHint').textContent = state.settings_file ? `Arquivo local: ${state.settings_file}` : 'As configurações serão salvas localmente.';
      if (syncSettings) syncSettingsFields();
      refreshSettingsDirty();
      const q = ($('search').value || '').toLowerCase();
      const items = state.windows.filter(w => (w.title + ' ' + w.exe).toLowerCase().includes(q));
      $('shareWindowButton').disabled = !state.selected;
      $('windowGrid').innerHTML = items.length ? items.map(w => `<button class="window-card ${state.selected === w.hwnd ? 'selected' : ''}" data-hwnd="${w.hwnd}"><div class="preview">${w.preview ? `<img src="${w.preview}" alt="">` : `<div class="fallback">${escapeHtml((w.exe || '?')[0].toUpperCase())}</div>`}</div><div class="card-body"><div class="window-title">${escapeHtml(w.title)}</div><div class="window-meta">${escapeHtml(w.exe)} · ${escapeHtml(w.class_name)}</div><div class="card-action">Selecionar esta janela →</div></div></button>`).join('') : '<div class="empty">Nenhuma janela encontrada. Abra o jogo/aplicativo e clique em Atualizar.</div>';
      document.querySelectorAll('.window-card').forEach(card => card.addEventListener('click', () => { state.selected = Number(card.dataset.hwnd); render(); }));
      $('screenGrid').innerHTML = state.monitors.length ? state.monitors.map(m => `<article class="screen-card"><div class="screen-icon">▣</div><div class="screen-name">${escapeHtml(m.label)}</div><div class="screen-meta">Tela capturada em alta resolução</div><label class="audio-option"><input type="checkbox" class="screen-audio" checked> Áudio dos aplicativos, exceto Discord</label><button class="button orange screen-button" data-monitor="${encodeURIComponent(m.id)}">Transmitir esta tela</button></article>`).join('') : '<div class="empty">Nenhum monitor encontrado.</div>';
      document.querySelectorAll('.screen-button').forEach(button => button.addEventListener('click', () => { const audio = button.parentElement.querySelector('.screen-audio').checked; shareScreen(decodeURIComponent(button.dataset.monitor), audio); }));
    }
    async function load() { try { const response = await fetch('/api/state'); state = await response.json(); settingsDirty = false; $('settingsDirty').hidden = true; render(true); } catch (e) { setStatus('Não consegui falar com o helper local.', 'error'); } }
    async function shareWindow() { if (!state.selected) { setStatus('Selecione uma janela primeiro.', 'error'); return; } await share('/api/share', { mode: 'window', hwnd: state.selected }); }
    async function shareScreen(monitor_id, audio) { await share('/api/share', { mode: 'screen', monitor_id, audio, hwnd: audio ? state.selected : null }); }
    async function stopStream() { await share('/api/stop', {}); }
    async function saveSettings() {
      const [output_width, output_height] = $('resolution').value.split('x').map(Number);
      const video_mbps = Number($('videoBitrate').value);
      if (!Number.isFinite(video_mbps) || video_mbps < 1 || video_mbps > 20) {
        setStatus('O vídeo deve ficar entre 1 e 20 Mbps. Exemplo: 7 = 7000 kbps.', 'error');
        return;
      }
      await share('/api/settings', {
        video_bitrate: Math.round(video_mbps * 1000),
        audio_bitrate: Number($('audioBitrate').value),
        fps: Number($('fps').value),
        output_width,
        output_height,
        encoder: $('encoder').value,
      }, true);
    }
    async function share(path, payload, syncSettings = false) { setStatus(path === '/api/settings' ? 'Salvando ajustes no OBS...' : 'Preparando o OBS...', ''); try { const response = await fetch(path, { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) }); const data = await response.json(); if (!response.ok) throw new Error(data.error || 'Falha desconhecida'); state = data; if (syncSettings) { settingsDirty = false; $('settingsDirty').hidden = true; } render(syncSettings); setStatus(data.message || 'Transmissão atualizada.', 'ok'); } catch (e) { setStatus(e.message, 'error'); } }
    $('windowGrid').addEventListener('dblclick', () => shareWindow());
    $('shareWindowButton').addEventListener('click', shareWindow);
    $('copyButton').addEventListener('click', async () => { try { await navigator.clipboard.writeText(state.viewer_url); $('copyButton').textContent = 'Copiado'; setTimeout(() => $('copyButton').textContent = 'Copiar link', 1400); } catch (_) { setStatus('Selecione e copie o link manualmente.', 'error'); } });
    $('refreshButton').addEventListener('click', load);
    $('stopButton').addEventListener('click', stopStream);
    $('settingsToggle').addEventListener('click', () => { const body = $('settingsBody'); body.hidden = !body.hidden; $('settingsToggle').textContent = body.hidden ? 'Mostrar ajustes' : 'Ocultar ajustes'; });
    $('saveSettings').addEventListener('click', saveSettings);
    ['videoBitrate', 'audioBitrate', 'fps', 'resolution'].forEach(id => $(id).addEventListener('input', refreshSettingsDirty));
    $('encoder').addEventListener('change', refreshSettingsDirty);
    $('search').addEventListener('input', render);
    document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', () => { activeTab = tab.dataset.tab; document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t === tab)); $('windowsPanel').hidden = activeTab !== 'windows'; $('screenPanel').hidden = activeTab !== 'screen'; if (activeTab === 'screen') render(); }));
    load();
  </script>
</body>
</html>"""


def load_identity():
    IDENTITY_DIR.mkdir(parents=True, exist_ok=True)
    if IDENTITY_FILE.exists():
        try:
            data = json.loads(IDENTITY_FILE.read_text(encoding="utf-8"))
            if data.get("stream_key"):
                return data["stream_key"]
        except Exception:
            pass
    key = secrets.token_hex(16)
    IDENTITY_FILE.write_text(json.dumps({"stream_key": key}, indent=2), encoding="utf-8")
    return key


def preview_for_window(window):
    try:
        # Capturar pelo HWND evita que janelas sobrepostas recebam a mesma
        # região do desktop, especialmente em monitores com DPI diferente.
        image = ImageGrab.grab(window=window["hwnd"])
        if image.width < 80 or image.height < 60:
            return ""
        image.thumbnail((640, 360), Image.Resampling.LANCZOS)
        canvas = Image.new("RGB", (640, 360), (25, 29, 40))
        x = (canvas.width - image.width) // 2
        y = (canvas.height - image.height) // 2
        canvas.paste(image.convert("RGB"), (x, y))
        output = io.BytesIO()
        canvas.save(output, format="JPEG", quality=72, optimize=True)
        return "data:image/jpeg;base64," + base64.b64encode(output.getvalue()).decode()
    except Exception:
        return ""


class JobObjectBasicLimitInformation(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_longlong),
        ("PerJobUserTimeLimit", ctypes.c_longlong),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class JobObjectIoCounters(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_ulonglong),
        ("WriteOperationCount", ctypes.c_ulonglong),
        ("OtherOperationCount", ctypes.c_ulonglong),
        ("ReadTransferCount", ctypes.c_ulonglong),
        ("WriteTransferCount", ctypes.c_ulonglong),
        ("OtherTransferCount", ctypes.c_ulonglong),
    ]


class JobObjectExtendedLimitInformation(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", JobObjectBasicLimitInformation),
        ("IoInfo", JobObjectIoCounters),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class ProcessGuard:
    """Own OBS through a Windows job object.

    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is the hard safety net: if this panel
    is killed, crashes, or is closed by Task Manager, Windows closes OBS too.
    The normal shutdown path still sends StopStream first so the server gets a
    clean disconnect.
    """

    JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
    PROCESS_SET_QUOTA = 0x0100
    PROCESS_TERMINATE = 0x0001
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

    def __init__(self):
        self.handle = None
        self.closed = False
        if os.name != "nt":
            return
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        kernel32.SetInformationJobObject.argtypes = [
            wintypes.HANDLE,
            wintypes.INT,
            ctypes.c_void_p,
            wintypes.DWORD,
        ]
        kernel32.SetInformationJobObject.restype = wintypes.BOOL
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.restype = wintypes.BOOL
        self.kernel32 = kernel32
        self.handle = kernel32.CreateJobObjectW(None, None)
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "Não consegui criar a proteção do OBS.")
        limits = JobObjectExtendedLimitInformation()
        limits.BasicLimitInformation.LimitFlags = self.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not kernel32.SetInformationJobObject(
            self.handle,
            self.JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
            ctypes.byref(limits),
            ctypes.sizeof(limits),
        ):
            kernel32.CloseHandle(self.handle)
            self.handle = None
            raise OSError(ctypes.get_last_error(), "Não consegui ativar a proteção do OBS.")

    def attach(self, pid):
        if self.handle is None or self.closed:
            return False
        access = (
            self.PROCESS_SET_QUOTA
            | self.PROCESS_TERMINATE
            | self.PROCESS_QUERY_LIMITED_INFORMATION
        )
        process = self.kernel32.OpenProcess(access, False, int(pid))
        if not process:
            return False
        try:
            return bool(self.kernel32.AssignProcessToJobObject(self.handle, process))
        finally:
            self.kernel32.CloseHandle(process)

    def close(self):
        if self.closed:
            return
        self.closed = True
        if self.handle is not None:
            self.kernel32.CloseHandle(self.handle)
            self.handle = None


class PanelApp:
    def __init__(self):
        self.lock = threading.RLock()
        self.stream_key = load_identity()
        self.viewer_url = f"https://b.siobud.com/{self.stream_key}"
        self.process_guard = ProcessGuard()
        self.guarded_obs_pids = set()
        self.obs = None
        self.saved_settings = self.load_settings_file()
        self.active_window_hwnd = None
        self.source_mode = None
        self.source_title = ""
        self.last_message = ""
        self.status_process = None
        self.shutting_down = False
        self.monitor_thread = threading.Thread(target=self.monitor_window_loop, daemon=True)
        self.monitor_thread.start()

    def windows(self):
        result = []
        for window in PICKER.enumerate_windows():
            result.append(
                {
                    "hwnd": window["hwnd"],
                    "title": window["title"],
                    "exe": window["exe"],
                    "class_name": window["class"],
                    "spec": window["spec"],
                    "preview": preview_for_window(window),
                }
            )
        return result

    def attach_obs_processes(self):
        pids = PICKER.find_obs_process_ids()
        if SUPERVISED:
            if not pids:
                raise RuntimeError(
                    "O supervisor não encontrou o OBS protegido. A transmissão foi bloqueada por segurança."
                )
            self.guarded_obs_pids.update(pids)
            return
        attached = {pid for pid in pids if self.process_guard.attach(pid)}
        if not attached:
            raise RuntimeError(
                "Não consegui vincular o OBS ao painel. A transmissão foi bloqueada por segurança."
            )
        self.guarded_obs_pids.update(attached)

    def get_obs(self):
        if self.obs is None:
            obs = PICKER.connect_obs()
            try:
                self.attach_obs_processes()
            except Exception:
                obs.close()
                raise
            self.obs = obs
        return self.obs

    @staticmethod
    def encoder_id_from_obs(value):
        for encoder_id, option in ENCODER_OPTIONS.items():
            if option["obs_value"] == value:
                return encoder_id
        return DEFAULT_SETTINGS["encoder"]

    @staticmethod
    def available_encoders():
        plugin_dir = PICKER.ROOT / "obs-portable" / "app" / "obs-plugins" / "64bit"
        available = []
        for encoder_id, option in ENCODER_OPTIONS.items():
            if option["plugin"] is None or (plugin_dir / option["plugin"]).exists():
                available.append({"id": encoder_id, "label": option["label"]})
        return available

    def load_settings_file(self):
        candidates = [SETTINGS_FILE]
        if LEGACY_SETTINGS_FILE != SETTINGS_FILE:
            candidates.append(LEGACY_SETTINGS_FILE)
        for path in candidates:
            if not path.exists():
                continue
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                self.settings_file = SETTINGS_FILE
                return PanelApp.validate_settings(data)
            except Exception:
                continue
        return None

    @staticmethod
    def settings_match(left, right):
        return all(left.get(name) == right.get(name) for name in DEFAULT_SETTINGS)

    def persist_settings(self, settings):
        content = json.dumps(settings, indent=2, ensure_ascii=False) + "\n"
        errors = []
        for path in (SETTINGS_FILE, LEGACY_SETTINGS_FILE):
            try:
                path.parent.mkdir(parents=True, exist_ok=True)
                temporary = path.with_name(f".{path.name}.tmp")
                temporary.write_text(content, encoding="utf-8")
                temporary.replace(path)
                self.settings_file = path
                return
            except OSError as error:
                errors.append(f"{path}: {error}")
        raise OSError("Não consegui salvar settings.json. " + " | ".join(errors))

    def current_state(self, message=""):
        if message:
            self.last_message = message
        active = False
        obs = None
        try:
            obs = self.get_obs()
            active = bool(obs.call("GetStreamStatus").get("outputActive"))
        except Exception:
            pass
        settings = dict(self.saved_settings or DEFAULT_SETTINGS)
        if obs is not None and self.saved_settings is None:
            settings = self.current_settings(obs)
            self.saved_settings = settings
            self.persist_settings(settings)
        return {
            "viewer_url": self.viewer_url,
            "stream_active": active,
            "settings": settings,
            "settings_file": str(getattr(self, "settings_file", SETTINGS_FILE)),
            "encoders": self.available_encoders(),
            "source_mode": self.source_mode,
            "source_title": self.source_title,
            "watch_window": self.active_window_hwnd is not None,
            "windows": self.windows(),
            "monitors": PICKER.enumerate_monitors(),
            "message": self.last_message,
        }

    @staticmethod
    def profile_value(obs, category, name, fallback):
        try:
            value = obs.call(
                "GetProfileParameter",
                {"parameterCategory": category, "parameterName": name},
            ).get("parameterValue")
            return fallback if value in (None, "") else value
        except Exception:
            return fallback

    @staticmethod
    def canvas_for_settings(settings):
        if settings["output_width"] <= 1280 and settings["output_height"] <= 720:
            return settings["output_width"], settings["output_height"]
        return 3840, 2160

    @staticmethod
    def scale_type_for_settings(settings):
        return "bicubic" if settings["output_width"] <= 1280 and settings["output_height"] <= 720 else "lanczos"

    @staticmethod
    def nvenc_preset_for_settings(settings):
        if settings["encoder"] in {"nvenc_h264", "nvenc_hevc", "nvenc_av1"}:
            return "p4" if settings["output_width"] <= 1280 and settings["output_height"] <= 720 else "p5"
        return None

    @classmethod
    def tuning_matches(cls, obs, settings):
        expected_width, expected_height = cls.canvas_for_settings(settings)
        try:
            video = obs.call("GetVideoSettings")
            if int(video.get("baseWidth", 0)) != expected_width or int(video.get("baseHeight", 0)) != expected_height:
                return False
        except Exception:
            return False
        if cls.profile_value(obs, "Video", "ScaleType", "") != cls.scale_type_for_settings(settings):
            return False
        expected_preset = cls.nvenc_preset_for_settings(settings)
        if expected_preset and cls.profile_value(obs, "SimpleOutput", "NVENCPreset2", "") != expected_preset:
            return False
        return True

    def current_settings(self, obs):
        settings = dict(DEFAULT_SETTINGS)
        try:
            video = obs.call("GetVideoSettings")
            settings["output_width"] = int(video.get("outputWidth", settings["output_width"]))
            settings["output_height"] = int(video.get("outputHeight", settings["output_height"]))
            numerator = int(video.get("fpsNumerator", 60))
            denominator = max(1, int(video.get("fpsDenominator", 1)))
            settings["fps"] = 60 if numerator / denominator >= 45 else 30
        except Exception:
            pass
        try:
            settings["video_bitrate"] = int(
                self.profile_value(obs, "SimpleOutput", "VBitrate", settings["video_bitrate"])
            )
        except (TypeError, ValueError):
            pass
        try:
            settings["audio_bitrate"] = int(
                self.profile_value(obs, "SimpleOutput", "ABitrate", settings["audio_bitrate"])
            )
        except (TypeError, ValueError):
            pass
        current_encoder = self.profile_value(
            obs,
            "SimpleOutput",
            "StreamEncoder",
            ENCODER_OPTIONS[DEFAULT_SETTINGS["encoder"]]["obs_value"],
        )
        settings["encoder"] = self.encoder_id_from_obs(current_encoder)
        return settings

    def apply_settings_to_obs(self, obs, settings):
        canvas_width, canvas_height = self.canvas_for_settings(settings)
        obs.call(
            "SetProfileParameter",
            {
                "parameterCategory": "SimpleOutput",
                "parameterName": "StreamEncoder",
                "parameterValue": ENCODER_OPTIONS[settings["encoder"]]["obs_value"],
            },
        )
        obs.call(
            "SetProfileParameter",
            {
                "parameterCategory": "SimpleOutput",
                "parameterName": "VBitrate",
                "parameterValue": str(settings["video_bitrate"]),
            },
        )
        obs.call(
            "SetProfileParameter",
            {
                "parameterCategory": "SimpleOutput",
                "parameterName": "ABitrate",
                "parameterValue": str(settings["audio_bitrate"]),
            },
        )
        obs.call(
            "SetVideoSettings",
            {
                "baseWidth": canvas_width,
                "baseHeight": canvas_height,
                "outputWidth": settings["output_width"],
                "outputHeight": settings["output_height"],
                "fpsNumerator": settings["fps"],
                "fpsDenominator": 1,
            },
        )
        obs.call(
            "SetProfileParameter",
            {
                "parameterCategory": "Video",
                "parameterName": "ScaleType",
                "parameterValue": self.scale_type_for_settings(settings),
            },
        )
        expected_preset = self.nvenc_preset_for_settings(settings)
        if expected_preset:
            obs.call(
                "SetProfileParameter",
                {
                    "parameterCategory": "SimpleOutput",
                    "parameterName": "NVENCPreset2",
                    "parameterValue": expected_preset,
                },
            )

    def ensure_saved_settings(self, obs):
        if self.saved_settings is None:
            self.saved_settings = self.current_settings(obs)
            self.persist_settings(self.saved_settings)
            self.apply_settings_to_obs(obs, self.saved_settings)
            return
        actual = self.current_settings(obs)
        if self.settings_match(actual, self.saved_settings) and self.tuning_matches(obs, self.saved_settings):
            return
        was_active = self.stop_output(obs)
        try:
            self.apply_settings_to_obs(obs, self.saved_settings)
        except Exception:
            if was_active:
                obs.call("StartStream")
                self.wait_for_stream(obs)
            raise
        if was_active:
            obs.call("StartStream")
            self.wait_for_stream(obs)

    def ensure_status_window(self):
        if SUPERVISED:
            return
        if not STATUS_SCRIPT.exists():
            return
        try:
            if getattr(self, "status_process", None) is not None and self.status_process.poll() is None:
                return
            command = [str(STATUS_SCRIPT)] if FROZEN else [sys.executable, str(STATUS_SCRIPT)]
            self.status_process = subprocess.Popen(
                command,
                cwd=str(EXECUTABLE_DIR),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            if self.process_guard.handle is not None:
                self.process_guard.attach(self.status_process.pid)
        except Exception:
            self.status_process = None

    @staticmethod
    def source_window_exists(hwnd):
        return any(int(window["hwnd"]) == int(hwnd) for window in PICKER.enumerate_windows())

    def monitor_window_loop(self):
        while True:
            time.sleep(5)
            with self.lock:
                if self.shutting_down:
                    return
                hwnd = self.active_window_hwnd
                if hwnd is None:
                    continue
                try:
                    obs = self.get_obs()
                    if not obs.call("GetStreamStatus").get("outputActive"):
                        self.active_window_hwnd = None
                        continue
                    if self.source_window_exists(hwnd):
                        continue
                    title = self.source_title or "A janela transmitida"
                    self.stop_output(obs)
                    self.active_window_hwnd = None
                    self.source_mode = None
                    self.source_title = ""
                    self.last_message = f"{title} foi fechada. Transmissão parada automaticamente."
                    self.ensure_status_window()
                except Exception:
                    continue

    def configure_service(self, obs):
        current = obs.call("GetStreamServiceSettings")
        settings = current.get("streamServiceSettings", {})
        if settings.get("bearer_token") == self.stream_key:
            return
        self.stop_output(obs)
        obs.call(
            "SetStreamServiceSettings",
            {
                "streamServiceType": "whip_custom",
                "streamServiceSettings": {
                    "server": "https://b.siobud.com/api/whip",
                    "bearer_token": self.stream_key,
                },
            },
        )

    @staticmethod
    def stop_output(obs):
        was_active = bool(obs.call("GetStreamStatus").get("outputActive"))
        if was_active:
            obs.call("StopStream")
            for _ in range(30):
                time.sleep(0.1)
                if not obs.call("GetStreamStatus").get("outputActive"):
                    break
        return was_active

    @staticmethod
    def validate_settings(payload):
        def number(name, minimum, maximum):
            value = payload.get(name)
            if isinstance(value, bool):
                raise ValueError(f"{name} inválido.")
            try:
                value = int(value)
            except (TypeError, ValueError):
                raise ValueError(f"{name} inválido.")
            if not minimum <= value <= maximum:
                raise ValueError(f"{name} deve ficar entre {minimum} e {maximum}.")
            return value

        settings = {
            "video_bitrate": number("video_bitrate", 1000, 20000),
            "audio_bitrate": number("audio_bitrate", 64, 320),
            "fps": number("fps", 30, 60),
            "output_width": number("output_width", 1, 4096),
            "output_height": number("output_height", 1, 4096),
            "encoder": payload.get("encoder", DEFAULT_SETTINGS["encoder"]),
        }
        if settings["encoder"] not in ENCODER_OPTIONS:
            raise ValueError("Encoder inválido.")
        if settings["encoder"] not in {item["id"] for item in PanelApp.available_encoders()}:
            raise ValueError("Esse encoder não está disponível nesta instalação do OBS.")
        if settings["fps"] not in {30, 60}:
            raise ValueError("FPS disponível: 30 ou 60.")
        if (settings["output_width"], settings["output_height"]) not in {
            (1920, 1080),
            (1280, 720),
        }:
            raise ValueError("Resolução disponível: 1920×1080 ou 1280×720.")
        return settings

    def apply_settings(self, payload):
        with self.lock:
            settings = self.validate_settings(payload)
            obs = self.get_obs()
            was_active = self.stop_output(obs)
            try:
                self.apply_settings_to_obs(obs, settings)
            except Exception:
                if was_active:
                    obs.call("StartStream")
                    self.wait_for_stream(obs)
                raise
            if was_active:
                obs.call("StartStream")
                self.wait_for_stream(obs)
            self.saved_settings = settings
            self.persist_settings(settings)
            message = (
                f"Ajustes salvos: {settings['video_bitrate']} kbps de vídeo, "
                f"{settings['audio_bitrate']} kbps de áudio, {settings['fps']} FPS, "
                f"{settings['output_width']}×{settings['output_height']}, "
                f"{ENCODER_OPTIONS[settings['encoder']]['label']}. "
                f"Arquivo: {self.settings_file}."
            )
            if was_active:
                message += " A transmissão foi reiniciada."
            return self.current_state(message)

    def find_window(self, hwnd):
        for window in PICKER.enumerate_windows():
            if window["hwnd"] == int(hwnd):
                return window
        raise ValueError("Essa janela fechou ou ficou indisponível. Clique em Atualizar.")

    def wait_for_stream(self, obs):
        for _ in range(20):
            if obs.call("GetStreamStatus").get("outputActive"):
                return
            time.sleep(0.15)

    def share_window(self, hwnd):
        with self.lock:
            obs = self.get_obs()
            window = self.find_window(hwnd)
            self.ensure_saved_settings(obs)
            self.configure_service(obs)
            obs.call("SetCurrentProgramScene", {"sceneName": PICKER.SCENE})
            PICKER.set_enabled(obs, PICKER.OLD_GAME_SOURCE, False)
            PICKER.remove_picker_audio_inputs(obs)
            input_kind, input_settings = PICKER.capture_settings_for_window(window)
            item = PICKER.ensure_input(obs, PICKER.PICKER_VIDEO, input_kind, input_settings)
            PICKER.set_enabled(obs, PICKER.PICKER_VIDEO, True)
            PICKER.fit_source(obs, item)
            PICKER.start_if_needed(obs)
            self.wait_for_stream(obs)
            self.active_window_hwnd = int(window["hwnd"])
            self.source_mode = "window"
            self.source_title = window["title"]
            self.ensure_status_window()
            capture_label = "Game Capture" if input_kind == "game_capture" else "Window Capture"
            return self.current_state(f"Transmitindo a janela: {window['title']} ({capture_label})")

    def share_screen(self, monitor_id, audio, hwnd):
        with self.lock:
            obs = self.get_obs()
            self.ensure_saved_settings(obs)
            self.configure_service(obs)
            obs.call("SetCurrentProgramScene", {"sceneName": PICKER.SCENE})
            PICKER.set_enabled(obs, PICKER.OLD_GAME_SOURCE, False)
            item = PICKER.ensure_input(
                obs,
                PICKER.PICKER_VIDEO,
                "monitor_capture",
                {
                    "monitor_id": monitor_id,
                    "method": 0,
                    "monitor_wgc": 0,
                    "capture_cursor": True,
                    "force_sdr": False,
                },
            )
            PICKER.set_enabled(obs, PICKER.PICKER_VIDEO, True)
            PICKER.fit_source(obs, item)
            audio_note = "sem áudio"
            if audio:
                preferred = self.find_window(hwnd) if hwnd else None
                audio_sources = PICKER.configure_audio_without_discord(obs, preferred)
                audio_note = f"áudio de apps, exceto Discord ({len(audio_sources)} fontes)"
            else:
                PICKER.remove_picker_audio_inputs(obs)
            PICKER.start_if_needed(obs)
            self.wait_for_stream(obs)
            self.active_window_hwnd = None
            self.source_mode = "screen"
            self.source_title = monitor_id
            self.ensure_status_window()
            return self.current_state(f"Transmitindo a tela inteira · {audio_note}")

    def stop_stream(self):
        with self.lock:
            obs = self.get_obs()
            was_active = self.stop_output(obs)
            self.active_window_hwnd = None
            self.source_mode = None
            self.source_title = ""
            return self.current_state("Transmissão parada." if was_active else "A transmissão já estava parada.")

    def shutdown(self):
        with self.lock:
            if self.shutting_down:
                return
            self.shutting_down = True
            try:
                if self.obs is not None:
                    try:
                        self.stop_output(self.obs)
                    except Exception:
                        pass
                    try:
                        self.obs.close()
                    except Exception:
                        pass
                    self.obs = None
            finally:
                self.process_guard.close()


APP = PanelApp()
atexit.register(APP.shutdown)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, _format, *_args):
        return

    def send_json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/api/state"):
            try:
                with APP.lock:
                    self.send_json(APP.current_state())
            except Exception as exc:
                self.send_json({"error": str(exc)}, 500)
            return
        body = HTML.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        if self.path not in {"/api/share", "/api/stop", "/api/settings"}:
            self.send_json({"error": "Rota desconhecida"}, 404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            if self.path == "/api/stop":
                result = APP.stop_stream()
            elif self.path == "/api/settings":
                result = APP.apply_settings(payload)
            elif payload.get("mode") == "window":
                result = APP.share_window(payload["hwnd"])
            elif payload.get("mode") == "screen":
                result = APP.share_screen(
                    payload["monitor_id"], bool(payload.get("audio")), payload.get("hwnd")
                )
            else:
                raise ValueError("Modo de transmissão inválido.")
            self.send_json(result)
        except Exception as exc:
            self.send_json({"error": str(exc)}, 500)


def main():
    server = None
    try:
        server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    except OSError:
        webbrowser.open(URL)
        return
    threading.Thread(target=server.serve_forever, daemon=True).start()
    webbrowser.open(URL)
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        pass
    finally:
        server.shutdown()
        APP.shutdown()


if __name__ == "__main__":
    main()
