# VDO-Ninja Streamer

A small Windows-only launcher around OBS and VDO-Ninja-style WHIP streaming.
It gives the user a single portable app where they can choose an open window,
a game, or an entire monitor, then share the generated viewer link.

The supervisor and the panel are intentionally tied together: closing the
supervisor is the safe stop action and also terminates the child OBS process.
The panel captures game windows with Game Capture when possible, downscales to
the configured output resolution, and can capture application audio while
excluding known Discord process variants.

## Using a release

1. Download the latest Windows ZIP from the repository's **Releases** page.
2. Extract it to a normal folder.
3. For `v0.2.0` and newer, run `StreamerV2.exe`; older `v0.1.x` packages use `TRANSMITIR.cmd`.
4. Open the panel, choose a window or monitor, and share the viewer link.
5. Close the app when finished; that stops the stream pipeline.

## v0.2.0 — GStreamer portable pipeline

The `v0.2.0` release is the new self-contained GStreamer implementation in
`v0.2-gstreamer/`. It bundles fixed WebView2 and GStreamer runtimes, so users
do not need to install .NET, Python, WebView2, OBS, or GStreamer. It supports
application-window capture, full-monitor capture, hardware H.264, HEVC and
AV1 where exposed by the GPU/runtime, x264, and system audio with the Discord
process tree excluded.

The panel exposes the available OBS video encoders from the bundled runtime,
including NVENC H.264, NVENC HEVC, NVENC AV1, QuickSync, and x264 when present.
H.264 is the safest choice for browser compatibility. Stream settings are
written atomically to `settings.json` beside the portable app. If that folder
is not writable, the app falls back to `%LOCALAPPDATA%\VDO-Ninja-Streamer`.

In automatic mode, 1280×720 output uses a matching canvas, Bicubic scaling, and
NVENC P4 to reduce render and encoder pressure on older GPUs. The panel also
offers an explicit Lanczos option for 4K-to-720p text sharpness, plus Bicubic
and no-filter modes for machines whose source is already 1080p or 720p.
For 720p, Auto and Bicubic keep the OBS canvas at the output size to reduce GPU
work on older machines. The audio gain control affects only captured audio sent
to viewers and can boost it up to 200% without changing headset volume.
The Budget performance profile fixes a low-load 720p30 path with no scaling
filter, the fastest NVENC preset available, limited game capture framerate, and
only the selected app's audio when sharing the whole screen. It is the default
profile for new installs: video uses 2 Mbps while audio stays at high-quality
192 kbps Opus, and the default captured-audio gain is 200%.
The optional WebRTC x264 profile switches OBS to Advanced output with CRF 23,
1-second keyframes, veryfast, High, fastdecode, and `bframes=0`; it is disabled
by default so AV1/NVENC behavior remains unchanged.

The release is portable and does not require a Python or .NET installation.

## Building locally

Requirements:

- Windows x64
- Python 3.13 or newer with `PyInstaller`, `Pillow`, and `websocket-client`
- .NET 10 SDK
- PowerShell 7 or Windows PowerShell

Install the Python build dependencies, then run:

```powershell
python -m pip install pyinstaller Pillow websocket-client
& .\packaging\build-release.ps1 -ArtifactVersion local
```

The script downloads the pinned OBS portable ZIP, verifies its SHA-256 hash,
builds the Python panel and C# supervisor, creates a clean OBS configuration,
and writes both a runnable folder and a ZIP under `release\`.

The OBS version is pinned in `packaging\build-release.ps1` so a release is
reproducible. Update that version and checksum deliberately when upgrading.

## Making a GitHub release

The workflow in `.github/workflows/release.yml` runs on a version tag. After
creating the repository and pushing the default branch:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

For the new pipeline, use `v0.2.0`:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

GitHub Actions selects the matching builder and attaches the portable ZIP to
the release automatically. The workflow also has a manual **Run workflow**
option that accepts a tag name.

## Repository layout

```text
src/                       Python panel, picker, and status window
Program.cs                 Windows Job Object supervisor
TRANSMITIR.cmd             Portable launcher
packaging/build-release.ps1 Reproducible Windows package build
v0.2-gstreamer/             GStreamer/WebView2 portable v0.2 pipeline
packaging/obs-config/      Sanitized OBS profile templates
.github/workflows/         Automatic tagged releases
```

The repository deliberately does not commit compiled binaries, personal OBS
logs, viewer tokens, or the large portable OBS runtime. Those are assembled
only for a release ZIP.

## License

The project's own source is MIT-licensed. The release also bundles OBS Studio;
see `THIRD-PARTY-NOTICES.md` and the notices included by OBS.
