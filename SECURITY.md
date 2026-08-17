# Security notes

This app is designed for private screen sharing between people who have the
viewer link. Treat that link as a secret. Anyone who has it may be able to
watch while the stream is active.

The local OBS WebSocket is configured on `127.0.0.1` with a generated password.
The supervisor owns the panel and OBS processes through a Windows Job Object;
closing the supervisor is intended to stop the stream and its child processes.

Please do not publish viewer links, OBS WebSocket passwords, local logs, or
`%LOCALAPPDATA%\VDO-Ninja-Streamer-Compiled\identity.json` in issues or pull
requests. To report a vulnerability, open a private GitHub security advisory
when the repository has GitHub Security Advisories enabled.
