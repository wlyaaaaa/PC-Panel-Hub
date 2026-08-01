# Startup Task

The recommended startup path is a Windows Scheduled Task:

- Task name: `TURZX SideScreen`
- Run level: `Highest`
- Trigger: user logon
- Action: `wscript.exe` -> `tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs`
- Default port: `COM7`
- Default refresh: `1000ms`

This is not a `SYSTEM` account task. It runs as the current interactive user with `Highest` run level, which is usually safer for COM ports, user-profile Python installs, RTSS/Afterburner, and other desktop telemetry tools.

The watchdog starts the render stack, listens for `Win32_PowerManagementEvent`, sends a black frame when Windows enters suspend, and restarts the stack after resume. It also listens for `Win32_ComputerShutdownEvent` and blanks the panel when Windows is shutting down or restarting. The black frame is a best-effort screen blanking fallback; the current public protocol path does not expose a real panel power-off command.

Metrics startup is health-based rather than process-name-based. If an old Python
process is still visible while its HTTP endpoint is already dead, the launcher
waits for port `18765` to release and starts a replacement. The watchdog allows
60 seconds for this startup recovery before evaluating stream heartbeats.

Install:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-startup-admin.ps1
```

The installer runs `scripts\check-runtime.ps1` first. It will not install the startup task if the local stock TURZX runtime files are missing.

Uninstall:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall-startup-admin.ps1
```

Check current state:

```powershell
Get-ScheduledTask | Where-Object { $_.TaskName -in @('TURZX SideScreen','TURZX WeatherFix','TURZX_88inch_AdminStart') } |
  Select-Object TaskName,State,@{Name='RunLevel';Expression={$_.Principal.RunLevel}}
```

The installer also creates shortcuts for manual recovery/start:

- Desktop: `TURZX SideScreen Start`
- Start Menu / All apps: `TURZX SideScreen`

The installer disables these old stock startup tasks if present:

- `TURZX WeatherFix`
- `TURZX_88inch_AdminStart`

It does not delete them, so rollback is still possible.

Create shortcuts again manually:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\create-desktop-shortcut.ps1
```
