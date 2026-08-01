# Startup Task

The recommended startup path is a Windows Scheduled Task:

- Task name: `TURZX SideScreen`
- Run level: `Highest`
- Trigger: user logon
- Action: `wscript.exe` -> `tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs`
- Default port: `COM7`
- Default refresh: `1000ms`

This is not a `SYSTEM` account task. It runs as the current interactive user with `Highest` run level, which is usually safer for COM ports, user-profile Python installs, RTSS/Afterburner, and other desktop telemetry tools.

The watchdog starts the render stack and coordinates both auxiliary displays across
Windows power transitions:

| Windows state | LIAN LI HS2 curved OLED | TURZX case panel |
| --- | --- | --- |
| Active | Windows secondary-display mode, screen on, offline clock armed | stream running at the configured brightness (`170` by default) |
| Suspend | monitor mode, native offline clock enabled, normal screen output off | stream stopped, verified command `123` sets brightness to `0` |
| Shutdown/restart | monitor mode, offline clock disabled, screen output off | stream stopped, verified command `123` sets brightness to `0` |

On resume, the HS2 screen is turned on before it is returned to Windows
secondary-display mode, and the TURZX brightness is restored before streaming
starts. Moving HS2 out of the Windows display topology before suspend also keeps
desktop windows from being stranded on the small display when the main monitor
disconnects or powers down.

HS2 transitions use the local L-Connect service on `127.0.0.1:11021`. The
service can take several seconds to re-enumerate the controller while switching
between desktop and monitor modes, so the watchdog polls for the new controller
mode instead of assuming the switch is immediate. L-Connect failures are logged
and isolated so they do not prevent the TURZX panel from being turned off.

TURZX brightness control uses the same RJCP serial path as frame streaming.
The watchdog releases the stream's COM-port ownership before sending the power
command. A black frame remains as an explicit fallback only when the hardware
brightness command fails; it is not the normal sleep/shutdown path.

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
