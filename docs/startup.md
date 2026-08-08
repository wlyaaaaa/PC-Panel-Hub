# Startup Task

The recommended startup path is a Windows Scheduled Task:

- Task name: `TURZX SideScreen`
- Run level: `Highest`
- Trigger: user logon
- Action: hidden `powershell.exe` -> `tools\turzx_side_screen\StartSideScreenWatchdog.ps1`
- Default port: `COM7`
- Installed personal mode: explicit hybrid refresh (`204` deltas at `1000ms`);
  command-200-only fallback remains `3000ms`. The optional Alt helper is not enabled:
  this panel accepted 18 frames and then rejected Alt command 204 plus subsequent writes.

This is not a `SYSTEM` account task. It runs as the current interactive user with `Highest` run level, which is usually safer for COM ports, user-profile Python installs, RTSS/Afterburner, and other desktop telemetry tools.

Task Scheduler launches the watchdog process directly. This keeps task state and
restart/stop behavior attached to the real long-running process instead of an
intermediate script host that can leave an orphaned watchdog behind.

The watchdog starts the render stack and coordinates both auxiliary displays across
Windows power transitions. At startup it also enables the Windows multi-monitor
policies that remember window locations and minimize windows when a monitor is
disconnected:

For high-load resilience, the watchdog and stream run at `AboveNormal` priority and
the serial write worker runs at `Highest`, without using realtime process priority.
Full-frame writes are bounded to 10 seconds; hybrid delta writes are bounded to
900 ms. The first send failure causes the
worker to exit and reopen under the watchdog. Heartbeat timing checks also reject a
live-but-stalled process. Diagnostic preview encoding is asynchronous and never sits
on the COM7 send path.

Hybrid startup/recovery follows the vendor 8.8-inch lifecycle: raw `0x2C` priming,
brightness restore, then two identical command-200 baselines before command 204 begins.
No periodic full baseline is enabled by default because each pair costs about 4.8 seconds
and would visibly stop a seconds clock. If command 204 is not reliable on the physical
panel, disable HybridRefresh and use the 3-second verified full-frame mode.

| Windows state | LIAN LI HS2 curved OLED | TURZX case panel |
| --- | --- | --- |
| Active | Windows secondary-display mode, screen on, offline clock armed | stream running at the configured brightness (`170` by default) |
| Suspend | monitor mode, native offline clock enabled, normal screen output off | stream stopped, verified command `123` sets brightness to `0` |
| Shutdown/restart | monitor mode, offline clock disabled, screen output off | stream stopped, verified command `123` sets brightness to `0` |

The native Windows window-preservation policies also cover the separate case where
the PC remains awake but the main monitor powers down and leaves the display
topology. Applications are minimized in place instead of being rearranged onto the
small HS2 display, and Windows restores their remembered monitor locations when the
main display reconnects.

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

After an unclean restart, the watchdog also keeps `desired active` separate from
`verified active`. It retries the read-back-verified Active request every 15
seconds and does not launch the HS2 overlay until verification succeeds. If the
HS2 USB display is present but L-Connect has not rebound it, the watchdog restarts
only `LConnectService` once for that failure streak. If Windows instead reports
the exact dedicated `VID_1A86&PID_8091` HS2 hub with its port-2 descriptor child
in Code 43, recovery additionally requires the exact hub identity previously
learned from a healthy HS2 display plus its LIAN LI LED sibling. It is then
limited to one precise hub restart and, only if still needed, removal/rescan of
that exact failed child. Missing or ambiguous binding fails closed; it never
resets a root hub or the whole USB tree. A continuing descriptor failure is
logged as a hardware cold-power boundary and subsequent probes use a slower
retry interval instead of writing every few seconds.

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
