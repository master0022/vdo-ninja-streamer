# Plano de teste v0.2

## O que a implementação mede hoje

Cada execução retorna JSON com:

- `Started`, `Completed`, duração, erro e mensagens do bus;
- CPU média/pico do processo do streamer;
- memória de trabalho máxima;
- tempo de CPU do processo e quantidade de amostras.

Para arquivos locais, `gst-discoverer-1.0` confirma resolução, FPS, perfil H.264, canais e sample rate do Opus.

## Smoke tests reproduzíveis

```powershell
dotnet build .\v0.2-gstreamer\StreamerV2.csproj --nologo
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --list-windows
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --list-monitors
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --capture --title YouTube --duration 10 --output capture.mkv
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --capture --monitor-index 0 --audio-mode SystemExceptDiscord --duration 10 --output capture-monitor.mkv
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --capture --title YouTube --duration 8 --encoder H264X264 --rate-control Crf --crf 23 --preset ultrafast --scale Lanczos --output capture-x264.mkv
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --stream --title YouTube --duration 20 --token STREAM_KEY
```

Para a matriz de codecs, executar também:

```powershell
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --capture --title YouTube --duration 8 --encoder HevcNvenc --width 1280 --height 720 --fps 30 --video-kbps 2000 --audio-kbps 192 --audio-gain 2 --output capture-hevc.mkv
dotnet run --project .\v0.2-gstreamer\StreamerV2.csproj --no-build -- --capture --title YouTube --duration 3 --encoder Av1Nvenc --width 1280 --height 720 --fps 30 --video-kbps 2000 --output capture-av1.mkv
```

HEVC e AV1 precisam retornar `Started=true`, `Completed=true`, sem erro do bus, e o `gst-discoverer-1.0` precisa reportar o codec selecionado. A release portátil usa GStreamer 1.28.6, portanto deve expor `nvd3d11av1enc`, `nvautogpuav1enc` e `nvav1enc` no PC com NVIDIA compatível. Se uma instalação local não tiver uma factory AV1 NVIDIA, AV1 deve falhar antes de PLAYING com erro de plugin ausente, nunca gerar H.264 como fallback.

O teste de monitor inteiro deve retornar `Started=true`, `Completed=true`, conter vídeo e Opus estéreo 48 kHz no `gst-discoverer-1.0`, e o pipeline descrito deve usar `monitor-index` e `exclude-process-tree` com o PID-raiz do Discord. Se o Discord não estiver aberto, o modo deve falhar explicitamente em vez de capturar áudio sem a exclusão.

O teste de fechamento deve iniciar uma captura contra uma janela temporária e fechar essa janela durante a execução. O resultado esperado é `Completed=false`, erro explícito de janela fechada e processo finalizado sem filho GStreamer.

## Limites atuais

Ainda não há contadores de frames por estágio, p95/p99, NACK/PLI/jitter do receptor nem simulação de perda de rede. O transporte `whipsink` usado neste bundle não expõe FEC/RTX/adaptação de bitrate para a aplicação. Portanto, estes números não devem ser inventados a partir de “pareceu liso”.

Para a validação final no PC antigo, comparar pelo menos:

1. NVENC H.264, 720p30, 1–2 Mbps, bilinear;
2. x264 ultrafast/zerolatency, 720p30, CBR;
3. NVENC 720p60 apenas se o caso 30 FPS tiver margem;
4. jogo e navegador separadamente, com Discord tocando áudio, confirmando que o arquivo/receptor contém só o PID alvo.

Critério mínimo da primeira versão é não perder a sessão por sobrecarga: se a máquina não sustentar 60 FPS, reduzir FPS/bitrate de forma explícita e manter o pipeline vivo, em vez de acumular fila até stutter severo.
