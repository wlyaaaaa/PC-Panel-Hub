# Release Process

Build a source release zip:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Default output:

```text
dist\PC-Panel-Hub-source.zip
```

The release package intentionally includes:

- `README.md`
- `AGENTS.md`
- `LICENSE`
- `docs\`
- `scripts\`
- `tools\turzx_side_screen\` source files
- `tools\turzx_weather_shim\` source files

It intentionally excludes:

- `tools\**\out\`
- logs and cache files
- original TURZX vendor binaries
- local device configs
- generated PNG previews

Before publishing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

Clean-clone users must provide local stock TURZX runtime files themselves:

- `RJCP.SerialPortStream.dll`
- `TURZX.exe` or `TURZX.weatherfix.metrics.exe`

This avoids redistributing vendor binaries in the public repository.
