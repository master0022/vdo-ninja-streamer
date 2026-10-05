# Streamer v0.2 — GStreamer portátil

Implementação isolada da próxima versão do streamer. A versão existente em `src/` e o OBS bundled não são alterados.

## Rodar

- Execute `StreamerV2.exe` sem argumentos para abrir a UI HTML escura hospedada pelo WebView2.
- A UI é English-only. A tela principal tem só o essencial: o que compartilhar (janela ou monitor), a qualidade (`Sharp screen` até 4K ou `Performance` 720p), o link e `Start`/`Stop`. Encoder, valores manuais, áudio, endpoint, teste local e log ficam em `Advanced`.
- Por padrão a qualidade é automática: o app escolhe o encoder, a resolução, o FPS e o bitrate e continua ajustando durante a transmissão (ver "Qualidade automática"). Configurações salvas por versões antigas migram para o modo automático; a stream key, o endpoint, a fonte e o áudio são mantidos.
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
StreamerV2.exe --list-encoders
StreamerV2.exe --capture --title YouTube --duration 10 --output capture.mkv
StreamerV2.exe --capture --monitor-index 0 --audio-mode SystemExceptDiscord --duration 10 --output monitor.mkv
StreamerV2.exe --stream --title YouTube --token STREAM_KEY --duration 30
```

No modo CLI, `--stream` e `--capture` imprimem a cada segundo uma linha `[trace]` com FPS codificado, CPU do sistema e perda, além de cada ajuste automático. `--mode Sharp|Performance720` escolhe o modo; `--auto-quality false` (ou passar `--width`/`--video-kbps`) usa valores fixos. Opções de teste incluem `--encoder Auto|H264Nvenc|H264Amf|H264QuickSync|H264MediaFoundation|H264X264|HevcNvenc|Av1Nvenc`, `--rate-control Cbr|Vbr|Cqp|Crf`, `--scale None|Bilinear|Bicubic|Lanczos`, `--preset`, `--crf`, `--width`, `--height`, `--fps`, `--video-kbps`, `--audio-kbps`, `--audio-gain`, `--video-source Window|Monitor`, `--monitor-index`, `--audio-mode SelectedProcess|SystemExceptDiscord` e `--audio-source`.

## Pipeline efetivo

```text
Window/monitor -> d3d11screencapturesrc (WGC)
     -> d3d11convert -> capsfilter (resolução/FPS do degrau atual, D3D11Memory)
     -> queue (2 frames, descarta frames inteiros se o encoder atrasar)
     -> NVENC / AMF / QSV / Media Foundation (sem cópia para a CPU)
        ou d3d11download/videoconvert/videoscale -> x264
     -> h264parse -> rtph264pay -> queue (300 ms) -> whipsink

Selected window -> wasapi2src (process loopback, include-process-tree)
Entire monitor -> wasapi2src (system loopback, exclude-process-tree com Discord como alvo)
    -> audioconvert/audioresample/volume
    -> Opus -> rtpopuspay -> queue (300 ms) -> whipsink
```

## Qualidade automática

Encoder: em `Automatic`, o app tenta H.264 nesta ordem e fica com o primeiro que produzir vídeo: NVIDIA NVENC (`nvd3d11h264enc`) → AMD AMF (`amfh264enc`) → Intel Quick Sync (`qsvh264enc`) → Windows Media Foundation (`mfh264enc`) → x264 (CPU). O GStreamer só registra um encoder de hardware quando a GPU e o driver existem; se um deles abre mas não gera frames em 8 s (driver antigo, limite de sessões), o próximo é usado. Se um encoder de hardware falha acima de 1080p, ele é tentado de novo em 1080p antes do fallback. HEVC e AV1 só são usados quando escolhidos manualmente, porque nem todo navegador decodifica.

Escada de qualidade: `Sharp` começa na resolução nativa da fonte (até 3840×2160, bitrate até 9 Mbps); `Performance` começa em 720p30 (~2,2 Mbps). Os degraus mantêm a proporção da janela, nunca aumentam a resolução e descem até 360p20. Com x264, o início é 720p (540p em CPUs de até 4 threads).

Controle (`AdaptiveController.cs`), avaliado a cada segundo:

- Processamento: se o encoder fica abaixo de 85% do FPS alvo ou a CPU do sistema passa de 92% por 3 s, desce um degrau (resolução/FPS). A mudança renegocia os caps com o pipeline rodando; a sessão WHIP e os espectadores não caem.
- Rede: a perda é calculada pelos contadores RTCP (pacotes perdidos ÷ pacotes enviados desde a última leitura) e comparada com a base da própria conexão. Perda acima da base reduz o bitrate; congestionamento sustentado também reduz a resolução. O Broadcast Box não preenche os campos de RTT, então o RTT só é usado quando vem diferente de zero.
- Subida: após 25 s sem pressão, sobe um degrau. Um degrau que falha logo depois de subir não é tentado de novo por 5 minutos.
- Queda da conexão: reconecta automaticamente com espera crescente (até 8 tentativas seguidas).

Desligar `Automatic quality` em `Advanced` volta ao comportamento manual (largura, altura, FPS, bitrate, preset, CRF e scaling fixos).

## Transporte

O transporte usa `whipsink` com RTP H.264/H.265/AV1 + Opus explícito. O `whipclientsink` funciona no GStreamer 1.28 e tem controle de congestionamento próprio (GCC), mas só configura NVENC em modo CUDA, QSV, x264 e openh264. Isso deixaria placas AMD no x264 e copiaria cada frame da GPU para a memória principal, por isso a adaptação é feita pelo app.

A fila antes do `whipsink` é limitada por tempo (300 ms), não por número de pacotes. Um frame vira vários pacotes RTP, e um keyframe, dezenas: a fila antiga de 2 pacotes "leaky" descartava pedaços de frames (no x264 a 2 Mbps, cerca de 70% dos pacotes de vídeo), e quem assistia via imagem corrompida até o próximo keyframe.

Para montar uma release portátil do zero, use `build-portable.ps1`. Ele baixa o runtime oficial GStreamer 1.28.6 e copia só os 24 plugins que o app usa, junto com as DLLs de que eles dependem (lidas das tabelas de import), para `gstreamer/win-x64`; depois inclui o WebView2 fixo no ZIP. Ao usar um elemento novo no código, inclua o plugin dele em `$requiredPlugins`.

O cache de plugins do GStreamer fica em `%LOCALAPPDATA%\StreamerV2\gstreamer-registry`, um arquivo por pasta de instalação. Só a primeira abertura de cada cópia varre os plugins.

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
