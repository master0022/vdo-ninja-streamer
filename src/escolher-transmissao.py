import base64
import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import tkinter as tk
from pathlib import Path
from tkinter import messagebox, ttk
from ctypes import wintypes

import websocket


ROOT = Path(sys.executable).resolve().parent.parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent.parent
OBS_EXE = ROOT / "obs-portable" / "app" / "bin" / "64bit" / "obs64.exe"
OBS_CONFIG = ROOT / "obs-portable" / "app" / "config" / "obs-studio"
OBS_WS_CONFIG = OBS_CONFIG / "plugin_config" / "obs-websocket" / "config.json"
SCENE = "Scene"
PICKER_VIDEO = "PICKER - Captura"
PICKER_AUDIO = "PICKER - Audio"
PICKER_AUDIO_PREFIX = "PICKER - Audio"
OLD_GAME_SOURCE = "GAME - video + audio"
CANVAS_WIDTH = 3840.0
CANVAS_HEIGHT = 2160.0
DISCORD_EXECUTABLES = {
    "discord.exe",
    "discord",
    "discordcanary.exe",
    "discordcanary",
    "discordptb.exe",
    "discordptb",
    "discorddevelopment.exe",
    "discorddevelopment",
    "discordstable.exe",
    "discordstable",
    "discordlite.exe",
    "discordlite",
    "discordapp.exe",
    "discordapp",
    "discordhost.exe",
    "discordhost",
    "discordoverlay.exe",
    "discordoverlay",
    "discordgameoverlay.exe",
    "discordgameoverlay",
    "discordhook.exe",
    "discordhook",
    "discordupdater.exe",
    "discordupdater",
    "discordupdate.exe",
    "discordupdate",
}
DISCORD_NAME_MARKERS = {
    "discord",
    "discordapp",
    "discordptb",
    "discordcanary",
    "discorddevelopment",
    "discordstable",
    "discordlite",
    "discordhost",
    "discordoverlay",
    "discordgameoverlay",
    "discordhook",
    "discordupdater",
    "discordupdate",
}


def normalize_process_text(value):
    """Normalize names/paths so Discord variants cannot evade the filter."""
    return re.sub(r"[^a-z0-9]+", "", str(value or "").casefold())


DISCORD_NORMALIZED_EXECUTABLES = {
    normalize_process_text(value) for value in DISCORD_EXECUTABLES
}
DISCORD_NORMALIZED_MARKERS = {
    normalize_process_text(value) for value in DISCORD_NAME_MARKERS
}
GAME_EXECUTABLES = {
    "palworld-win64-shipping.exe",
    "manosaba.exe",
}

HIDDEN_WINDOW_EXES = {
    "applicationframehost.exe",
    "backgroundtaskhost.exe",
    "dwm.exe",
    "gamebar.exe",
    "gamebarftserver.exe",
    "lockapp.exe",
    "nvidia overlay.exe",
    "runtimebroker.exe",
    "searchhost.exe",
    "shellexperiencehost.exe",
    "startmenuexperiencehost.exe",
    "systemsettings.exe",
    "textinputhost.exe",
    "widgetservice.exe",
    "widgets.exe",
}
HIDDEN_WINDOW_CLASSES = {
    "progman",
    "shell_traywnd",
    "windows.ui.core.corewindow",
}
HIDDEN_WINDOW_TITLES = {
    "microsoft text input application",
    "nvidia geforce overlay",
    "program manager",
}


class ObsError(RuntimeError):
    pass


class Obs:
    def __init__(self):
        config = json.loads(OBS_WS_CONFIG.read_text(encoding="utf-8"))
        self.ws = websocket.create_connection(
            f"ws://127.0.0.1:{config['server_port']}", timeout=5
        )
        hello = json.loads(self.ws.recv())
        auth = hello.get("d", {}).get("authentication")
        identify = {"op": 1, "d": {"rpcVersion": 1}}
        if auth:
            secret = base64.b64encode(
                hashlib.sha256((config["server_password"] + auth["salt"]).encode()).digest()
            )
            identify["d"]["authentication"] = base64.b64encode(
                hashlib.sha256(secret + auth["challenge"].encode()).digest()
            ).decode()
        self.ws.send(json.dumps(identify))
        identified = json.loads(self.ws.recv())
        if identified.get("op") != 2:
            raise ObsError("O OBS recusou a conexão WebSocket.")

    def call(self, request_type, request_data=None):
        request_id = f"picker-{request_type}-{time.monotonic_ns()}"
        self.ws.send(
            json.dumps(
                {
                    "op": 6,
                    "d": {
                        "requestType": request_type,
                        "requestId": request_id,
                        "requestData": request_data or {},
                    },
                }
            )
        )
        while True:
            message = json.loads(self.ws.recv())
            if message.get("op") != 7:
                continue
            data = message.get("d", {})
            if data.get("requestId") != request_id:
                continue
            status = data.get("requestStatus", {})
            if not status.get("result"):
                raise ObsError(
                    f"{request_type} falhou ({status.get('code')}): {status.get('comment', '')}"
                )
            return data.get("responseData", {})

    def optional(self, request_type, request_data=None):
        try:
            return self.call(request_type, request_data)
        except ObsError:
            return None

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def connect_obs():
    try:
        return Obs()
    except Exception:
        if not OBS_EXE.exists():
            raise ObsError(f"OBS portátil não encontrado em {OBS_EXE}")
        # A forced panel shutdown intentionally leaves OBS' .sentinel marker.
        # Remove only this package's marker before a fresh, owned launch so
        # OBS cannot stop at its unclean-shutdown dialog.
        for pid in find_obs_process_ids():
            terminate_process(pid)
        sentinel = OBS_CONFIG / ".sentinel"
        if sentinel.is_dir():
            shutil.rmtree(sentinel, ignore_errors=True)
        elif sentinel.exists():
            sentinel.unlink()
        subprocess.Popen(
            [
                str(OBS_EXE),
                "--portable",
                "--minimize-to-tray",
                "--disable-updater",
            ],
            cwd=str(OBS_EXE.parent),
        )
        deadline = time.time() + 30
        while time.time() < deadline:
            try:
                return Obs()
            except Exception:
                time.sleep(0.5)
        raise ObsError("O OBS não ficou disponível em 30 segundos.")


def terminate_process(pid):
    if os.name != "nt":
        return
    kernel32 = ctypes.windll.kernel32
    process = kernel32.OpenProcess(0x0001, False, int(pid))
    if not process:
        return
    try:
        kernel32.TerminateProcess(process, 1)
    finally:
        kernel32.CloseHandle(process)


def find_obs_process_ids():
    """Return PIDs for this package's OBS executable.

    The panel uses these PIDs to put OBS in a Windows job object. That makes
    OBS die automatically when the panel process disappears, including a
    forced close or crash.
    """
    if os.name != "nt" or not OBS_EXE.exists():
        return []

    class ProcessEntry32W(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", wintypes.WCHAR * 260),
        ]

    kernel32 = ctypes.windll.kernel32
    snapshot = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        return []

    entry = ProcessEntry32W()
    entry.dwSize = ctypes.sizeof(entry)
    result = []
    target = os.path.normcase(os.path.abspath(str(OBS_EXE)))
    try:
        if not kernel32.Process32FirstW(snapshot, ctypes.byref(entry)):
            return []
        while True:
            if entry.szExeFile.lower() == OBS_EXE.name.lower():
                process = kernel32.OpenProcess(0x1000, False, entry.th32ProcessID)
                if process:
                    try:
                        size = wintypes.DWORD(32768)
                        buffer = ctypes.create_unicode_buffer(size.value)
                        if kernel32.QueryFullProcessImageNameW(
                            process, 0, buffer, ctypes.byref(size)
                        ):
                            actual = os.path.normcase(os.path.abspath(buffer.value))
                            if actual == target:
                                result.append(int(entry.th32ProcessID))
                    finally:
                        kernel32.CloseHandle(process)
            if not kernel32.Process32NextW(snapshot, ctypes.byref(entry)):
                break
    finally:
        kernel32.CloseHandle(snapshot)
    return result


def get_window_text(hwnd):
    length = ctypes.windll.user32.GetWindowTextLengthW(hwnd)
    if length <= 0:
        return ""
    buffer = ctypes.create_unicode_buffer(length + 1)
    ctypes.windll.user32.GetWindowTextW(hwnd, buffer, length + 1)
    return buffer.value.strip()


def get_window_class(hwnd):
    buffer = ctypes.create_unicode_buffer(256)
    ctypes.windll.user32.GetClassNameW(hwnd, buffer, len(buffer))
    return buffer.value


def get_window_process_path(hwnd):
    process_id = wintypes.DWORD()
    ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
    if not process_id.value:
        return ""
    handle = ctypes.windll.kernel32.OpenProcess(0x1000 | 0x0400, False, process_id.value)
    if not handle:
        return ""
    try:
        size = wintypes.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(size.value)
        if ctypes.windll.kernel32.QueryFullProcessImageNameW(
            handle, 0, buffer, ctypes.byref(size)
        ):
            return buffer.value
    finally:
        ctypes.windll.kernel32.CloseHandle(handle)
    return ""


def get_window_executable(hwnd):
    process_path = get_window_process_path(hwnd)
    return Path(process_path).name if process_path else ""


def is_discord_window(window):
    """Identify Discord by executable, title, class, or full path.

    The path check intentionally catches both Program Files and Program Files
    (x86), while the normalization catches case, punctuation, and missing .exe.
    Generic names such as Update.exe are not blocked unless they also contain
    a Discord marker.
    """
    executable = normalize_process_text(window.get("exe", ""))
    combined = normalize_process_text(
        " ".join(
            (
                window.get("exe", ""),
                window.get("title", ""),
                window.get("class", ""),
                window.get("path", ""),
            )
        )
    )
    return (
        executable in DISCORD_NORMALIZED_EXECUTABLES
        or any(marker in combined for marker in DISCORD_NORMALIZED_MARKERS)
    )


def enumerate_windows():
    windows = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    @callback_type
    def callback(hwnd, _):
        user32 = ctypes.windll.user32
        if not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
            return True
        title = get_window_text(hwnd)
        if not title:
            return True
        window_class = get_window_class(hwnd)
        process_path = get_window_process_path(hwnd)
        executable = Path(process_path).name if process_path else ""
        if not window_class or not executable:
            return True
        if executable.lower() in {"obs64.exe", "python.exe", "pythonw.exe"}:
            return True
        if executable.lower() in HIDDEN_WINDOW_EXES:
            return True
        if window_class.lower() in HIDDEN_WINDOW_CLASSES:
            return True
        if title.lower() in HIDDEN_WINDOW_TITLES:
            return True
        windows.append(
            {
                "hwnd": int(hwnd),
                "title": title,
                "class": window_class,
                "exe": executable,
                "path": process_path,
                "spec": f"{title}:{window_class}:{executable}",
            }
        )
        return True

    ctypes.windll.user32.EnumWindows(callback, 0)
    windows.sort(key=lambda item: (item["title"].lower(), item["exe"].lower()))
    return windows


def enumerate_monitors():
    monitors = []
    display_device = type(
        "DISPLAY_DEVICEW",
        (ctypes.Structure,),
        {
            "_fields_": [
                ("cb", wintypes.DWORD),
                ("DeviceName", wintypes.WCHAR * 32),
                ("DeviceString", wintypes.WCHAR * 128),
                ("StateFlags", wintypes.DWORD),
                ("DeviceID", wintypes.WCHAR * 128),
                ("DeviceKey", wintypes.WCHAR * 128),
            ]
        },
    )
    index = 0
    while True:
        device = display_device()
        device.cb = ctypes.sizeof(device)
        if not ctypes.windll.user32.EnumDisplayDevicesW(None, index, ctypes.byref(device), 0):
            break
        if device.StateFlags & 1:
            monitors.append(
                {
                    "id": device.DeviceName,
                    "label": f"{device.DeviceName} — {device.DeviceString or 'monitor'}",
                }
            )
        index += 1
    return monitors or [{"id": "\\\\.\\DISPLAY1", "label": "Monitor principal"}]


def fit_source(obs, item_id):
    video = obs.optional("GetVideoSettings") or {}
    canvas_width = float(video.get("baseWidth", CANVAS_WIDTH))
    canvas_height = float(video.get("baseHeight", CANVAS_HEIGHT))
    for _ in range(20):
        transform = obs.call(
            "GetSceneItemTransform", {"sceneName": SCENE, "sceneItemId": item_id}
        ).get("sceneItemTransform", {})
        if transform.get("sourceWidth", 0) > 0 and transform.get("sourceHeight", 0) > 0:
            break
        time.sleep(0.25)
    obs.call(
        "SetSceneItemTransform",
        {
            "sceneName": SCENE,
            "sceneItemId": item_id,
            "sceneItemTransform": {
                "alignment": 5,
                "positionX": 0.0,
                "positionY": 0.0,
                "rotation": 0.0,
                "scaleX": 1.0,
                "scaleY": 1.0,
                "cropLeft": 0,
                "cropRight": 0,
                "cropTop": 0,
                "cropBottom": 0,
                "boundsType": "OBS_BOUNDS_SCALE_INNER",
                "boundsAlignment": 5,
                "boundsWidth": canvas_width,
                "boundsHeight": canvas_height,
                "cropToBounds": False,
            },
        },
    )


def item_id(obs, source_name):
    return obs.call(
        "GetSceneItemId", {"sceneName": SCENE, "sourceName": source_name}
    )["sceneItemId"]


def set_enabled(obs, source_name, enabled):
    current = obs.optional("GetSceneItemId", {"sceneName": SCENE, "sourceName": source_name})
    if current:
        obs.call(
            "SetSceneItemEnabled",
            {
                "sceneName": SCENE,
                "sceneItemId": current["sceneItemId"],
                "sceneItemEnabled": enabled,
            },
        )


def remove_input(obs, source_name):
    if obs.optional("GetInputSettings", {"inputName": source_name}) is None:
        return
    obs.call("RemoveInput", {"inputName": source_name})
    for _ in range(20):
        if obs.optional("GetInputSettings", {"inputName": source_name}) is None:
            return
        time.sleep(0.1)


def remove_picker_audio_inputs(obs):
    inputs = obs.call("GetInputList").get("inputs", [])
    for current in inputs:
        name = current.get("inputName", "")
        if name == PICKER_AUDIO or name.startswith(PICKER_AUDIO_PREFIX + " -"):
            remove_input(obs, name)


def likely_game_window(window):
    executable = window.get("exe", "").lower()
    window_class = window.get("class", "").lower()
    title = window.get("title", "").lower()
    return (
        executable in GAME_EXECUTABLES
        or executable.endswith("-win64-shipping.exe")
        or executable.endswith("-win64-test.exe")
        or "shipping.exe" in executable
        or window_class in {"unrealwindow", "unitywndclass"}
        or title in {"pal", "palworld"}
    )


def capture_settings_for_window(window):
    if likely_game_window(window):
        return "game_capture", {
            "capture_mode": "window",
            "window": window["spec"],
            "priority": 2,
            "capture_audio": True,
            "capture_cursor": False,
            "capture_overlays": False,
            "anti_cheat_hook": True,
            "hook_rate": 1,
            "limit_framerate": False,
            "allow_transparency": False,
            "premultiplied_alpha": False,
            "sli_compatibility": False,
            "rgb10a2_space": "srgb",
        }
    return "window_capture", {
        "window": window["spec"],
        "method": 0,
        "priority": 1,
        "cursor": False,
        "client_area": True,
        "compatibility": False,
        "force_sdr": False,
        "capture_audio": True,
    }


def configure_audio_without_discord(obs, preferred_window=None):
    remove_picker_audio_inputs(obs)
    candidates = {}
    windows = ([preferred_window] if preferred_window else []) + enumerate_windows()
    for window in windows:
        if not window:
            continue
        executable = window.get("exe", "").lower()
        if not executable or is_discord_window(window):
            continue
        candidates.setdefault(executable, window)

    created = []
    for executable, window in candidates.items():
        stem = re.sub(r"[^a-zA-Z0-9_.-]+", "_", Path(executable).stem)
        source_name = f"{PICKER_AUDIO_PREFIX} - {stem}"[:90]
        ensure_input(
            obs,
            source_name,
            "wasapi_process_output_capture",
            {"window": window["spec"], "priority": 1},
        )
        set_enabled(obs, source_name, True)
        created.append(source_name)
    return created


def ensure_input(obs, source_name, input_kind, settings):
    current = obs.optional("GetInputSettings", {"inputName": source_name})
    if current and current.get("inputKind") != input_kind:
        obs.call("RemoveInput", {"inputName": source_name})
        for _ in range(20):
            if obs.optional("GetInputSettings", {"inputName": source_name}) is None:
                break
            time.sleep(0.1)
        current = None
    if current:
        obs.call(
            "SetInputSettings",
            {"inputName": source_name, "inputSettings": settings, "overlay": True},
        )
    else:
        payload = {
            "sceneName": SCENE,
            "inputName": source_name,
            "inputKind": input_kind,
            "inputSettings": settings,
            "sceneItemEnabled": True,
        }
        for attempt in range(5):
            try:
                obs.call("CreateInput", payload)
                break
            except ObsError:
                if attempt == 4:
                    raise
                time.sleep(0.25)
    return item_id(obs, source_name)


def start_if_needed(obs):
    status = obs.call("GetStreamStatus")
    if not status.get("outputActive"):
        obs.call("StartStream")


class Picker:
    def __init__(self, root):
        self.root = root
        self.root.title("TRANSMITIR ESSA PORRA")
        self.root.geometry("760x610")
        self.root.minsize(650, 480)
        self.root.configure(bg="#15151b")
        self.windows = []
        self.monitors = enumerate_monitors()
        self.obs = None
        self.selected = None
        self.status = tk.StringVar(value="Abrindo o OBS...")
        self.monitor = tk.StringVar(value=self.monitors[0]["label"])
        self.screen_audio = tk.BooleanVar(value=True)
        self.build_ui()
        self.refresh()
        self.root.after(100, self.connect)

    def build_ui(self):
        title = tk.Label(
            self.root,
            text="TRANSMITIR ESSA PORRA",
            font=("Segoe UI", 20, "bold"),
            fg="#ffcf33",
            bg="#15151b",
        )
        title.pack(pady=(16, 2))
        subtitle = tk.Label(
            self.root,
            text="Escolhe uma janela ou manda a tela inteira. O OBS faz o resto.",
            font=("Segoe UI", 10),
            fg="#c5c5d0",
            bg="#15151b",
        )
        subtitle.pack(pady=(0, 12))

        controls = tk.Frame(self.root, bg="#15151b")
        controls.pack(fill="x", padx=16)
        tk.Button(
            controls,
            text="ATUALIZAR JANELAS",
            command=self.refresh,
            bg="#30303d",
            fg="white",
            activebackground="#45455a",
            activeforeground="white",
            relief="flat",
            padx=12,
            pady=8,
        ).pack(side="left")
        tk.Label(
            controls,
            text="  Monitor:",
            fg="#c5c5d0",
            bg="#15151b",
        ).pack(side="left", padx=(12, 4))
        self.monitor_combo = ttk.Combobox(
            controls,
            textvariable=self.monitor,
            values=[monitor["label"] for monitor in self.monitors],
            state="readonly",
            width=28,
        )
        self.monitor_combo.pack(side="left")

        frame = tk.Frame(self.root, bg="#15151b")
        frame.pack(fill="both", expand=True, padx=16, pady=12)
        self.listbox = tk.Listbox(
            frame,
            bg="#20202a",
            fg="#f1f1f5",
            selectbackground="#7655d6",
            selectforeground="white",
            borderwidth=0,
            highlightthickness=1,
            highlightbackground="#3b3b4b",
            font=("Consolas", 10),
            activestyle="none",
        )
        scrollbar = ttk.Scrollbar(frame, orient="vertical", command=self.listbox.yview)
        self.listbox.configure(yscrollcommand=scrollbar.set)
        self.listbox.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        self.listbox.bind("<Double-Button-1>", lambda _event: self.use_window())

        bottom = tk.Frame(self.root, bg="#15151b")
        bottom.pack(fill="x", padx=16, pady=(0, 10))
        tk.Checkbutton(
            bottom,
            text="Na tela inteira, capturar também o áudio da janela selecionada",
            variable=self.screen_audio,
            fg="#d4d4df",
            bg="#15151b",
            activebackground="#15151b",
            activeforeground="white",
            selectcolor="#20202a",
        ).pack(anchor="w", pady=(0, 8))
        buttons = tk.Frame(bottom, bg="#15151b")
        buttons.pack(fill="x")
        tk.Button(
            buttons,
            text="TRANSMITIR JANELA SELECIONADA",
            command=self.use_window,
            bg="#38a169",
            fg="white",
            activebackground="#48bb78",
            activeforeground="white",
            relief="flat",
            padx=12,
            pady=10,
        ).pack(side="left", fill="x", expand=True, padx=(0, 5))
        tk.Button(
            buttons,
            text="TRANSMITIR TELA INTEIRA",
            command=self.use_screen,
            bg="#d97706",
            fg="white",
            activebackground="#f59e0b",
            activeforeground="white",
            relief="flat",
            padx=12,
            pady=10,
        ).pack(side="left", fill="x", expand=True, padx=(5, 0))
        tk.Label(
            self.root,
            textvariable=self.status,
            anchor="w",
            fg="#9ee6b5",
            bg="#15151b",
            font=("Segoe UI", 9),
        ).pack(fill="x", padx=16, pady=(0, 12))

    def connect(self):
        try:
            self.obs = connect_obs()
            self.status.set("OBS conectado. Escolhe a janela e aperta um botão.")
        except Exception as exc:
            self.status.set(f"Erro: {exc}")

    def refresh(self):
        self.windows = enumerate_windows()
        self.listbox.delete(0, tk.END)
        for window in self.windows:
            self.listbox.insert(
                tk.END,
                f"{window['title'][:70]}  —  {window['exe']} [{window['class']}]",
            )
        self.status.set(f"{len(self.windows)} janelas encontradas.")

    def selected_window(self):
        selection = self.listbox.curselection()
        if not selection:
            raise ObsError("Seleciona uma janela primeiro.")
        return self.windows[selection[0]]

    def selected_monitor(self):
        label = self.monitor.get()
        return next((m for m in self.monitors if m["label"] == label), self.monitors[0])

    def prepare(self):
        if self.obs is None:
            self.obs = connect_obs()
        self.obs.call("SetCurrentProgramScene", {"sceneName": SCENE})
        set_enabled(self.obs, OLD_GAME_SOURCE, False)

    def use_window(self):
        try:
            window = self.selected_window()
            self.prepare()
            self.status.set(f"Capturando {window['title']}...")
            self.root.update_idletasks()
            remove_picker_audio_inputs(self.obs)
            input_kind, input_settings = capture_settings_for_window(window)
            item = ensure_input(self.obs, PICKER_VIDEO, input_kind, input_settings)
            set_enabled(self.obs, PICKER_VIDEO, True)
            set_enabled(self.obs, PICKER_AUDIO, False)
            fit_source(self.obs, item)
            start_if_needed(self.obs)
            capture_label = "Game Capture" if input_kind == "game_capture" else "Window Capture"
            self.status.set(f"AO VIVO: {window['title']} ({capture_label}) + áudio do processo. Headset preservado.")
        except Exception as exc:
            self.status.set(f"Erro: {exc}")
            messagebox.showerror("Deu ruim", str(exc), parent=self.root)

    def use_screen(self):
        try:
            self.prepare()
            monitor = self.selected_monitor()
            self.status.set(f"Capturando {monitor['label']}...")
            self.root.update_idletasks()
            item = ensure_input(
                self.obs,
                PICKER_VIDEO,
                "monitor_capture",
                {
                    "monitor_id": monitor["id"],
                    "method": 0,
                    "monitor_wgc": 0,
                    "capture_cursor": True,
                    "force_sdr": False,
                },
            )
            set_enabled(self.obs, PICKER_VIDEO, True)
            fit_source(self.obs, item)
            selected = self.listbox.curselection()
            if self.screen_audio.get():
                preferred = self.windows[selected[0]] if selected else None
                audio_sources = configure_audio_without_discord(self.obs, preferred)
                audio_note = f" + áudio de apps, exceto Discord ({len(audio_sources)} fontes)"
            else:
                remove_picker_audio_inputs(self.obs)
                audio_note = " + sem áudio"
            start_if_needed(self.obs)
            self.status.set(f"AO VIVO: tela inteira{audio_note}. Headset preservado.")
        except Exception as exc:
            self.status.set(f"Erro: {exc}")
            messagebox.showerror("Deu ruim", str(exc), parent=self.root)


def main():
    if os.name != "nt":
        print("Este seletor foi feito para Windows.", file=sys.stderr)
        return 1
    root = tk.Tk()
    Picker(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
