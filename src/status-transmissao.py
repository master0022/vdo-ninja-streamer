import json
import msvcrt
import os
import tkinter as tk
import urllib.request
import webbrowser
from pathlib import Path


API_URL = "http://127.0.0.1:8765/api/state"
STOP_URL = "http://127.0.0.1:8765/api/stop"
PANEL_URL = "http://127.0.0.1:8765/"
LOCK_FILE = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "VDO-Ninja-Streamer" / "status-window.lock"


def acquire_single_instance_lock():
    LOCK_FILE.parent.mkdir(parents=True, exist_ok=True)
    handle = LOCK_FILE.open("a+b")
    handle.seek(0)
    handle.write(b"0")
    handle.flush()
    handle.seek(0)
    try:
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        handle.close()
        return None
    return handle


class StatusWindow:
    def __init__(self, root):
        self.root = root
        self.root.title("VDO-Ninja — status")
        self.root.resizable(False, False)
        self.root.configure(bg="#11151f")
        self.root.attributes("-topmost", True)
        self.root.protocol("WM_DELETE_WINDOW", self.stop_and_close)
        self.red_icon = self.make_icon("#e34b60")
        self.green_icon = self.make_icon("#36c98f")
        self.root.iconphoto(True, self.red_icon)
        self.root.update_idletasks()
        x = max(10, self.root.winfo_screenwidth() - 326)
        self.root.geometry(f"300x136+{x}+34")

        self.circle = tk.Canvas(root, width=28, height=28, bg="#11151f", highlightthickness=0)
        self.circle.grid(row=0, column=0, rowspan=2, padx=(15, 10), pady=14)
        self.state_label = tk.Label(root, text="PARADO", bg="#11151f", fg="#ff8797", font=("Segoe UI", 12, "bold"))
        self.state_label.grid(row=0, column=1, sticky="sw", pady=(14, 0))
        self.source_label = tk.Label(root, text="Nenhuma transmissão ativa", bg="#11151f", fg="#d8deea", font=("Segoe UI", 9), anchor="w")
        self.source_label.grid(row=1, column=1, sticky="nw")
        self.detail_label = tk.Label(root, text="", bg="#11151f", fg="#9ba5b7", font=("Segoe UI", 8), anchor="w")
        self.detail_label.grid(row=2, column=0, columnspan=3, sticky="w", padx=15)
        self.open_button = tk.Button(root, text="Abrir painel", command=lambda: webbrowser.open(PANEL_URL), bg="#2b3342", fg="#ffffff", activebackground="#3b465b", activeforeground="#ffffff", relief="flat", borderwidth=0, padx=10, pady=4, font=("Segoe UI", 8, "bold"))
        self.open_button.grid(row=3, column=0, columnspan=3, pady=(8, 12))
        self.refresh()

    def stop_and_close(self):
        """Closing the visible status window is an explicit safe stop."""
        try:
            request = urllib.request.Request(
                STOP_URL,
                data=b"{}",
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            with urllib.request.urlopen(request, timeout=2):
                pass
        except Exception:
            # If the panel is already gone, its Windows job object has already
            # taken OBS down. The window should still be allowed to close.
            pass
        self.root.destroy()

    @staticmethod
    def make_icon(color):
        image = tk.PhotoImage(width=16, height=16)
        for y in range(16):
            for x in range(16):
                inside = (x - 7.5) ** 2 + (y - 7.5) ** 2 <= 49
                image.put(color if inside else "#11151f", (x, y))
        return image

    def set_circle(self, color):
        self.circle.delete("all")
        self.circle.create_oval(4, 4, 24, 24, fill=color, outline=color)

    def refresh(self):
        try:
            with urllib.request.urlopen(API_URL, timeout=0.8) as response:
                state = json.loads(response.read().decode("utf-8"))
            active = bool(state.get("stream_active"))
            if active:
                self.root.iconphoto(True, self.green_icon)
                self.set_circle("#36c98f")
                self.state_label.configure(text="TRANSMITINDO", fg="#8cf0c1")
                if state.get("source_mode") == "window":
                    source = state.get("source_title") or "Janela selecionada"
                    self.source_label.configure(text=source[:42])
                    self.detail_label.configure(text="Vigilando a janela — se fechar, a transmissão para")
                else:
                    self.source_label.configure(text="Tela inteira")
                    self.detail_label.configure(text="Áudio e vídeo ativos")
            else:
                self.root.iconphoto(True, self.red_icon)
                self.set_circle("#e34b60")
                self.state_label.configure(text="PARADO", fg="#ff8797")
                self.source_label.configure(text="Nenhuma transmissão ativa")
                message = state.get("message") or "Pronto para transmitir"
                self.detail_label.configure(text=message[:56])
        except Exception:
            self.root.iconphoto(True, self.red_icon)
            self.set_circle("#e34b60")
            self.state_label.configure(text="PAINEL OFFLINE", fg="#ff8797")
            self.source_label.configure(text="Abra o painel novamente")
            self.detail_label.configure(text="Não consegui consultar o OBS")
        self.root.after(1000, self.refresh)


def main():
    lock = acquire_single_instance_lock()
    if lock is None:
        return
    root = tk.Tk()
    StatusWindow(root)
    root.mainloop()
    lock.close()


if __name__ == "__main__":
    main()
