# TURZX SideScreen

Self-hosted realtime dashboard for 480x1920 TURZX USB side screens, plus an
optional event-driven overlay for the LIAN LI HS2 2288x1048 curved OLED.

This project replaces the stock TURZX monitoring page with a custom local stack:

- Python metrics agents for CPU/GPU thermals, live average core clocks and voltages, FPS, weather, physical-disk I/O, network quality, foreground app, and process ranking.
- C# / GDI+ renderer for a dense 480x1920 dashboard.
- Two guarded COM7 modes: verified command-200 full frames at 3 seconds, and an explicit
  1Hz hybrid candidate that uses a vendor-shaped command-200 startup/recovery baseline
  plus bounded command-204 deltas. Host heartbeats never claim to prove physical pixels.
- Coordinated sleep/shutdown handling: HS2 uses its native offline clock during sleep, while the TURZX panel uses the verified hardware brightness-off command; both panels turn off for shutdown/restart.
- Bounded JSONL diagnostics and explicit source/error states.
- Windows Scheduled Task startup support with highest privilege.
- A separate click-through HS2 crystal overlay for glance, media lyrics,
  Steam sessions, phone status, operations, tasks, and actionable alerts.

The HS2 overlay is intentionally not another dense telemetry dashboard. Its
design, supported sources, setup, and explicit limitations are documented in
[docs/hs2-crystal-overlay.md](docs/hs2-crystal-overlay.md).

## Current Status

This is an early Windows-first project extracted from a working local setup. The protocol and UI are practical, not polished SDK abstractions yet.

Known assumptions:

- Display size: `480x1920`.
- Serial port: `COM7` by default.
- Runtime OS: Windows.
- Python 3.11+ recommended.
- .NET Framework compiler `csc.exe` is required for the renderer/stream binaries.
- Hardware metrics work best with NVIDIA NVML and LibreHardwareMonitor.
- FPS comes from the optional TimeAudit/PresentMon chain enabled with `TIMEAUDIT_DSN`; no RTSS integration is required.
- Optional TimeAudit FPS source is enabled with `TIMEAUDIT_DSN`; no database password is stored in this repository.
- A fresh all-zero FPS sample means “waiting for a game”, not a collection fault. Connecting, stale, and error states are shown separately.
- The displayed DPC value is Windows `Processor Information(_Total)\% DPC Time`, not a synthetic scheduler-delay measurement.
- Physical disks are merged across their drive letters. Volumes named `RECOVER`, virtual/RAM disks, and USB/removable media smaller than 32,000,000,000 bytes are excluded.

## Quick Start

First check local runtime dependencies:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-runtime.ps1
```

The public repository does not include stock TURZX binaries. Put these files next to the repository root before starting the COM stream:

- `RJCP.SerialPortStream.dll`
- `TURZX.exe` or `TURZX.weatherfix.metrics.exe`

Run directly:

```text
start-side-screen.cmd
```

Or from PowerShell:

```powershell
cd E:\Projects\Tools\TURZX-SideScreen
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1 -Port COM7 -IntervalMs 3000
```

Install startup task:

```text
install-startup.cmd
```

Or from elevated PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-startup-admin.ps1 -Port COM7 -IntervalMs 3000
```

The personal start/install wrappers enable the guarded 1Hz hybrid mode. Pass
`-HybridRefresh:$false` for the conservative command-200-only fallback. `-AltHelper`
is retained only for isolated protocol testing; live evidence rejects it for this panel.

Uninstall startup task:

```text
uninstall-startup.cmd
```

Or from elevated PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall-startup-admin.ps1
```

## Useful Commands

Run tests and render previews:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

Build a release zip:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Check startup state:

```powershell
Get-ScheduledTask | Where-Object { $_.TaskName -like '*TURZX*' } |
  Select-Object TaskName,State,@{Name='RunLevel';Expression={$_.Principal.RunLevel}}
```

## Runtime Logs

Generated files stay out of git:

- `tools\turzx_side_screen\out\stream\stream-last.png`
- `tools\turzx_side_screen\out\data-trust.jsonl`
- `tools\turzx_side_screen\out\side-screen-stack.log`
- `tools\turzx_side_screen\out\top-processes.json`

## Repository Layout

```text
scripts/                       public install/start/test/release wrappers
docs/                          public documentation
tools/turzx_side_screen/       metrics agent, renderer, streamer, tests
tools/turzx_weather_shim/      weather shim used by local weather requests
tools/hs2_crystal_overlay/     HS2 overlay, NetEase bridge, and tests
```

The original TURZX vendor binaries and local runtime folders are intentionally excluded from git.

## License

Repository source code is MIT licensed. Third-party/vendor binaries and TURZX stock application files are not part of the public source license and should not be committed.
