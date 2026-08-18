# Streamer v0.2 — GStreamer portátil

Implementação isolada da próxima versão do streamer. A versão existente em `src/` e o OBS bundled não são alterados.

## Rodar

- Execute `StreamerV2.exe` sem argumentos para abrir a UI HTML escura hospedada pelo WebView2.
- A UI é English-only, lista janelas visíveis e monitores, guarda `HWND + PID` ou índice de monitor, salva `settings-v02.json` no próprio diretório e mantém `Start streaming`/`Stop stream` no mesmo processo.
- O áudio da janela é sempre isolado pelo processo escolhido; somente o modo de monitor inteiro usa o áudio global com Discord excluído. O ganho aparece como percentual na UI: 100% é normal e 200% é o padrão.
- A stream key fica salva localmente em `settings-v02.json`; a UI mostra o viewer link clicável, oferece `Copy link` e só libera a troca da key pelo botão `Change stream key`.
- A stream só existe enquanto o processo do app mantém o pipeline GStreamer vivo. Ao fechar o app, a UI cancela e aguarda o pipeline chegar a `NULL` antes de sair.
- O link de viewer é `https://b.siobud.com/<stream-key>`; o endpoint padrão é `https://b.siobud.com/api/whip`.

O ZIP final é organizado assim, sem DLLs espalhadas ao lado do executável:

```text
StreamerV2.exe
gstreamer/win-x64/       # GStreamer e plugins nativos
runtimes/win-x64/native/ # optional WebView2 loader emitted by some publish modes
WebView2/                # Fixed WebView2 runtime; no system install required
WebView2Data/             # Created on first run for this portable copy
docs/                    # XML de documentação, não necessário para rodar
```

O ZIP inclui o Fixed Version WebView2 Runtime em `WebView2/`. Nenhuma instalação do Microsoft Edge WebView2 é necessária; o app aponta explicitamente para essa cópia. `WebView2Data/` é criado ao abrir o app e guarda apenas o perfil/cache local dessa cópia portátil.

CLI para diagnóstico:

```powershell
StreamerV2.exe --list-windows
StreamerV2.exe --list-monitors
StreamerV2.exe --capture --title YouTube --duration 10 --output capture.mkv
StreamerV2.exe --capture --monitor-index 0 --audio-mode SystemExceptDiscord --duration 10 --output monitor.mkv
StreamerV2.exe --stream --title YouTube --token STREAM_KEY --duration 30
```

Opções de teste incluem `--encoder H264Nvenc|HevcNvenc|Av1Nvenc|H264X264`, `--rate-control Cbr|Vbr|Cqp|Crf`, `--scale None|Bilinear|Bicubic|Lanczos`, `--preset`, `--crf`, `--width`, `--height`, `--fps`, `--video-kbps`, `--audio-kbps`, `--audio-gain`, `--video-source Window|Monitor`, `--monitor-index`, `--audio-mode SelectedProcess|SystemExceptDiscord` e `--audio-source`.

## Pipeline efetivo

```text
Window/monitor -> d3d11screencapturesrc (WGC)
     -> d3d11convert + D3D11Memory quando possível
     -> NVENC H.264 ou download/videoconvert/videoscale + x264
     -> h264parse -> rtph264pay -> whipsink

Selected window -> wasapi2src (process loopback, include-process-tree)
Entire monitor -> wasapi2src (system loopback, exclude-process-tree com Discord como alvo)
    -> audioconvert/audioresample/volume
    -> Opus -> rtpopuspay -> whipsink
```

O caminho padrão é `H264Nvenc`, 720p30, 2 Mbps, áudio Opus 192 kbps, ganho 2x e scaling bilinear. O preset Lanczos força o caminho CPU de scaling; ele é intencionalmente opcional para não penalizar máquinas antigas.

O bundle atual implementa e testa H.264 NVENC, H.265/HEVC NVENC, AV1 NVENC e x264. A release portátil usa o runtime oficial GStreamer 1.28.6, que inclui `nvd3d11av1enc`, `nvautogpuav1enc` e `nvav1enc`; no PC com RTX 4070 SUPER, AV1 é selecionado por `nvd3d11av1enc` e foi capturado localmente sem mensagens de erro. H.265 usa `nvd3d11h265enc` diretamente com memória D3D11, `h265parse` e `rtph265pay`. Nunca há fallback automático para H.264, HEVC ou CPU. `CRF` é aplicado ao x264; NVENC H.264/HEVC/AV1 aceita CBR, VBR e CQP.

Para montar uma release portátil do zero, use `.build-portable.ps1`; ele baixa o runtime oficial GStreamer, o coloca em `gstreamer/win-x64` e inclui o WebView2 fixo no ZIP. O pacote não usa o runtime GStreamer 1.24.3 apenas por ele vir junto do binding C#.

O transporte usa `whipsink` com RTP H.264/H.265/AV1 + Opus explícito. O `whipclientsink` mais novo seria interessante para controle adaptativo, mas apresentou falha de negociação com o bundle Windows 1.24.3 usado nesta versão. Por isso a v0.2 mantém bitrate fixo e filas curtas/leaky; FEC/RTX/adaptação ficam reservados para uma próxima troca de transporte, não são prometidos pelos checkboxes atuais.

## Segurança e áudio

- A janela alvo é validada continuamente; fechamento, minimização, EOS ou erro da fonte encerram o pipeline.
- Para uma janela, o áudio usa loopback por PID e inclui a árvore do processo escolhido.
- Para um monitor inteiro, o áudio usa loopback global com `exclude-process-tree`, apontando para o processo-raiz mais antigo do Discord. Assim jogos, navegador, música e outros apps entram, mas o Discord e seus subprocessos ficam fora. O Discord precisa estar aberto antes de iniciar.
- A captura de monitor usa o índice zero-based exibido por `--list-monitors`; se o monitor desaparecer durante a execução, o pipeline encerra por segurança.
- Nenhum OBS, `gst-launch` ou FFmpeg é iniciado como processo separado.
- Configuração é salva atomicamente em JSON no diretório portátil.

## Verificações já executadas

- Build C# net8.0-windows: 0 warnings, 0 errors.
- Janela YouTube encontrada via Win32: HWND `0x20588`, PID `10268`.
- Captura local: 1280×720, 30 FPS, H.264, Opus estéreo 48 kHz, sem mensagens de erro.
- Captura local: 1280×720, 30 FPS, H.265 Main, Opus estéreo, sem mensagens de erro.
- Captura local: 1280×720, 30 FPS, AV1 NVENC em RTX 4070 SUPER, sem mensagens de erro.
- Captura x264: Lanczos + CRF 23, H.264 Constrained Baseline + Opus, sem erro.
- WHIP público H.265: sessão criada e encerrada após duração controlada, `Started=true`, `Completed=true`, `BusMessages=0`, CPU médio abaixo de 1% no teste local.
- Fechamento de janela durante a captura: encerramento em cerca de 3 s, mensagem de segurança e nenhum processo GStreamer órfão.

## Links primários

- [GStreamer no Windows](https://gstreamer.freedesktop.org/documentation/installing/on-windows.html)
- [Captura D3D11/WGC](https://gstreamer.freedesktop.org/documentation/d3d11/d3d11screencapturesrc.html)
- [Conversão D3D11](https://gstreamer.freedesktop.org/documentation/d3d11/d3d11convert.html)
- [NVENC H.264 D3D11](https://gstreamer.freedesktop.org/documentation/nvcodec/nvd3d11h264enc.html)
- [NVENC H.265 D3D11](https://gstreamer.freedesktop.org/documentation/nvcodec/nvd3d11h265enc.html)
- [NVENC AV1 D3D11](https://gstreamer.freedesktop.org/documentation/nvcodec/nvd3d11av1enc.html)
- [RTP H.265 payloader](https://gstreamer.freedesktop.org/documentation/rtp/rtph265pay.html)
- [RTP AV1 payloader](https://gstreamer.freedesktop.org/documentation/rtp/rtpav1pay.html)
- [Áudio WASAPI por processo](https://gstreamer.freedesktop.org/documentation/wasapi2/wasapi2src.html)
- [WHIP sink](https://gstreamer.freedesktop.org/documentation/webrtchttp/whipsink.html)
- [Broadcast Box](https://github.com/Glimesh/broadcast-box)
