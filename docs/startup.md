# Startup Task

The recommended startup path is a Windows Scheduled Task:

- Task name: `TURZX SideScreen`
- Run level: `Highest`
- Trigger: user logon
- Action: `wscript.exe` -> `tools\turzx_side_screen\StartSideScreenWatchdog-Hidden.vbs` -> `StartSideScreenWatchdog.ps1`
- Default port: `COM7`
- Default installer mode: Hybrid command-204 updates at **1 Hz** with verified
  command-200 recovery baselines. After every stream start or watchdog restart,
  hard serial-session rebuilds run at frames **60, 120, and 180**; the long-term
  rebuild remains every 900 frames. `-HybridRefresh:$false` is the explicit
  **3-second compatibility fallback**, not an automatic stability downgrade.
  The optional Alt helper is not enabled: this panel accepted 18 frames and then
  rejected Alt command 204 plus subsequent writes.

This is not a `SYSTEM` account task. It runs as the current interactive user with `Highest` run level, which is usually safer for COM ports, user-profile Python installs, RTSS/Afterburner, and other desktop telemetry tools.

Task Scheduler launches a GUI-subsystem `wscript.exe` parent adapter. The adapter
starts PowerShell with window style 0 and waits for the long-running watchdog, so
the task remains running without creating a visible console at interactive logon.

The watchdog starts the render stack and coordinates both auxiliary displays across
Windows power transitions. HS2 uses a preserve-current-mode safety policy: every
new startup and resume epoch keeps the controller mode that firmware and Windows
successfully enumerated. An existing Windows secondary controller (`17104897`) is
verified in place and is never demoted before its AD23/LED binding and overlay are
accepted. An existing native controller (`17104896`) is kept lit for a configurable
30-second stabilization window and then receives exactly one promotion request to
Windows secondary-display mode. If neither controller is present, no mode request
is sent. This prevents repeated Windows/GPU display-topology rebuilds.

Before any L-Connect request, the watchdog also proves that the previously bound
dedicated hub owns exactly one normal `A068` or `AD23` controller endpoint. A
missing endpoint or an exact `A108:EAEF` boot-ROM identity on hub port two enters
a read-only wait state: no mode command, service restart, hub reset, device
removal, or PnP scan runs. The cheap endpoint check continues at the normal retry
cadence, so a repaired or replaced module automatically resumes preserved-mode
L-Connect activation without restarting the watchdog.

For high-load resilience, the watchdog and stream run at `AboveNormal` priority and
the serial write worker runs at `Highest`, without using realtime process priority.
Full-frame writes are bounded to 10 seconds; hybrid delta writes are bounded to
900 ms. The first send failure causes the
worker to exit and reopen under the watchdog. Heartbeat timing checks also reject a
live-but-stalled process. Diagnostic preview encoding is asynchronous and never sits
on the COM7 send path.

Installed Hybrid startup follows the vendor 8.8-inch lifecycle: raw `0x2C` priming,
brightness restore, then two identical command-200 baselines before command 204 begins.
Frames 60, 120, and 180 repeat a single complete command-200 baseline on a rebuilt
session to recover a panel that missed the restarted host session. After that warmup,
every 900-frame boundary (about 15 minutes) performs the same bounded recovery. The
normal clock remains 1 Hz; each hard recovery causes one roughly 2.5-second pause
instead of making every clock tick three seconds long.
The watchdog verifies that this recovery cadence remains enabled and is actually reached.
This is still a host-side recovery strategy rather than device acknowledgement.
The installed default remains Hybrid 1 Hz. The verified command-200-only path is an
explicit 3-second compatibility fallback and is never selected automatically.

| Windows state | LIAN LI HS2 curved OLED | TURZX case panel |
| --- | --- | --- |
| Active | 保留开机已成功枚举的模式：已有 `17104897` 时原位验证 AD23+绑定并启动完整浮层；已有 `17104896` 时保持点亮，稳定 30 秒后仅一次尝试 Windows secondary-display；两者都没有时不发送模式切换 | stream running at the configured brightness (`170` by default) |
| Suspend | monitor mode, native offline clock enabled, normal screen output off | stream stopped, verified command `123` sets brightness to `0` |
| Shutdown/restart | monitor mode, offline clock disabled, screen output off | stream stopped, verified command `123` sets brightness to `0` |

The Windows window-preservation policies are deliberately deferred until that
secondary display has been verified. Once enabled, they cover the separate case
where the PC remains awake but the main monitor powers down and leaves the display
topology. Applications are minimized in place instead of being rearranged onto the
small HS2 display, and Windows restores their remembered monitor locations when the
main display reconnects.

On resume, HS2 keeps whichever valid controller mode is already present. Only a
native controller enters the stabilization window before one Windows-secondary
promotion; an existing verified secondary controller remains secondary.
The TURZX brightness is restored before streaming starts. Moving HS2 out of the
Windows display topology before suspend also keeps desktop windows from being
stranded on the small display when the main monitor disconnects or powers down.

The HS2 policy never changes the physical primary display or the Sunshine MTT1337
virtual display mode, resolution, refresh rate, HDR, scaling, or capture target.

For the OLED Curved model, [LIAN LI requires](https://lian-li.com/product/hs2-oled-curved/)
the AIO USB lead to connect directly to a motherboard USB 2.0 9-pin header, or
to LIAN LI's supported EDGE HUB. The bundled 1-to-2
USB hub lead is for non-LCD devices and is not a supported OLED path. If the
motherboard USB supply is insufficient, use the supplied SATA auxiliary-power
lead shown in the [AIO manual](https://drive.google.com/file/d/100nRyDLIbXY8mkVBAG5gv92xSe4A7tpN/view?usp=sharing).
Power the PC off and unplug it before moving an
internal USB header; the magnetic OLED cap is the only hot-swappable part.

HS2 transitions use the local L-Connect service on `127.0.0.1:11021`. The
service can take several seconds to re-enumerate the controller while switching
between desktop and monitor modes, so the watchdog polls for the new controller
mode instead of assuming the switch is immediate. L-Connect failures are logged
and isolated so they do not prevent the TURZX panel from being turned off.

After an unclean restart, the watchdog keeps `desired active`, `native active`,
and `secondary verified` as separate states. Native Active uses L-Connect read-back
only and never requires an AD23 device. The later secondary phase requires both
the L-Connect mode read-back and two consecutive healthy AD23 Windows-display
samples with the same saved hub/display/LED binding. If that promotion fails, the
watchdog immediately asks for native Active again, stops any stale overlay, and
holds that bright native state for the remainder of the watchdog process. It does
not retry the Secondary transition on a timer, restart a USB hub, remove a device,
or run a PnP scan. The sole service-level exception is one L-Connect restart after
the 120-second startup grace when the previously bound 8091 hub has a physically
healthy native A068 endpoint or AD23 endpoint but the controller API is empty;
that recovery waits for the controller read-back and never clears the one-attempt
Secondary marker. A new normal startup or resume creates a new epoch and gets one
fresh promotion attempt. Once both
layers are verified, any overlay process that survived the display outage is
recycled once so it binds to the newly enumerated display geometry before
notifications resume.

Correcting an unsupported inline-hub installation changes the Windows instance
id of the same 8091 hub. When the old bound hub disappears, the watchdog may
provisionally follow exactly one complete replacement topology only: one healthy
8091 hub, one controller endpoint on internal port two, and one healthy 8051 LED
endpoint on internal port three. Multiple or partial candidates fail closed. A
new binding is not persisted until secondary mode returns with the AD23 composite
and MI_00 display interface healthy on two consecutive samples.

The long-running watchdog is the only resume owner. Its WMI power subscription
coalesces suspend/resume handling with the live process and COM ownership. The
installer no longer registers the former `TURZX SideScreen Resume` event task and
disables an existing legacy copy during upgrade. The retained compatibility
script is non-destructive: it cannot stop the watchdog, restart a USB device, run
PnP, or replace a running owner; at most it asks Task Scheduler to start the main
task when that task is absent from the running state and its registered mode still
matches.

TURZX brightness control uses the same RJCP serial path as frame streaming.
The watchdog releases the stream's COM-port ownership before sending the power
command. A black frame remains as an explicit fallback only when the hardware
brightness command fails; it is not the normal sleep/shutdown path.

Metrics startup is health-based rather than process-name-based. If an old Python
process is still visible while its HTTP endpoint is already dead, the launcher
waits for port `18765` to release and starts a replacement. The watchdog allows
60 seconds for this startup recovery before evaluating stream heartbeats.

Repeated child exits or heartbeat stalls now open a bounded 30-second circuit:
the watchdog proves that the previous `TURZX.SideScreen.Stream` process released
COM, cools down, and then starts one new stack instead of exiting successfully
and leaving the panel frozen. The scheduled task retains a long restart budget
for an actual worker fault. Sleep and shutdown still execute the HS2 power policy
even if old-stream exit proof or the TURZX brightness fallback fails; normal
startup remains fail-closed and will not create a second COM writer until the old
one is gone.

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
