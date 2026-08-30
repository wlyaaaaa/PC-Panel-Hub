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
- `tools\hs2_crystal_overlay\` buildable source, tests, scripts, and app-owned icons

It intentionally excludes:

- `tools\**\out\`
- logs and cache files
- original TURZX vendor binaries
- all machine and launch JSON files; only the public-safe
  `tools\turzx_side_screen\config.example.json` is included
- generated `out`, `bin`, `obj`, and `AppPackages` trees
- generated PNG previews

Before publishing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

The focused public-package check builds and expands a fresh ZIP, verifies the HS2
source manifest, and rejects binaries, generated trees, JSON configuration,
embedded weather locations, and stale RTSS design labels:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-public-release.ps1
```

The weather shim intentionally has no built-in location. Copy
`tools\turzx_side_screen\config.example.json` to the ignored
`config.json` and fill the private `weather.latitude` and
`weather.longitude`; the weather launcher uses that file automatically.
Alternatively set `TURZX_WEATHER_CONFIG` to another private JSON file, or
inject both `TURZX_WEATHER_LATITUDE` and
`TURZX_WEATHER_LONGITUDE`. Optional display fields are `id`,
`name`, `adm2`, `adm1`, `country`,
`timezone`, and `utc_offset`.

The one-time vendor URL patch helper likewise reads `old_geo`, `new_geo`,
`old_now`, and `new_now` from the private JSON path named by
`TURZX_WEATHER_URL_PATCH_CONFIG`; it does not carry account-specific endpoints in
the public source or echo those URLs on patch failures.

Clean-clone users must provide local stock TURZX runtime files themselves:

- `RJCP.SerialPortStream.dll`
- `TURZX.exe` or `TURZX.weatherfix.metrics.exe`

This avoids redistributing vendor binaries in the public repository.
