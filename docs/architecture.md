# Architecture

PC Panel Hub is intentionally split into small local processes:

1. `turzx_weather_shim.py`
   - Provides short weather text for the dashboard.
   - Keeps weather API behavior isolated from screen rendering.

2. `metrics_agent.py`
   - Serves `GET http://127.0.0.1:18765/snapshot`.
   - Collects hardware, network, disk, weather, FPS, foreground app, and health data.
   - Reports CPU load from PDH `% Processor Time`, which is ordinary busy time and
     matches the user-facing Task Manager interpretation. Frequency-scaled
     `% Processor Utility` is intentionally not used as CPU load.
   - Adds `trust` scoring to every snapshot.
   - Writes data trust diagnostics to `out\data-trust.jsonl`.

3. `top_processes_helper.py`
   - Samples process CPU/RAM independently every 3 seconds.
   - Writes `out\top-processes.json`.
   - Prevents heavy process sampling from blocking the 1s main snapshot loop.

4. `TURZX.SideScreen.Stream.exe`
   - Fetches snapshots under one total deadline covering headers and the complete body,
     then reuses the last good snapshot if metrics are slow or stall mid-response.
   - Renders 480x1920 bitmaps with `System.Drawing`.
   - Keeps verified command `200` as the conservative 3-second mode.
   - Provides an explicit hybrid candidate for the required 1Hz clock: vendor-shaped
     priming/brightness/two-frame command-200 baseline, followed by bounded command-204
     deltas between recovery boundaries. Because command 204 has no device ACK, the
     default hybrid configuration rebuilds the serial session every 900 frames (about
     15 minutes at normal 1Hz cadence), repeats priming/brightness and the duplicate
     command-200 baseline, then restarts the command-204 sequence. This costs one brief
     recovery pause but reproduces the startup lifecycle that actually restores the panel;
     failures still exit and let the watchdog rebuild the process.
   - Runs at `AboveNormal` process/thread priority while the serial sender thread uses
     `Highest`; neither the process nor the sender uses realtime scheduling.
   - Writes the diagnostic PNG on a low-priority, single-flight background worker after
     the frame send. Slow PNG encoding or file replacement can be dropped, but cannot
     block the live panel stream.

5. `StartSideScreenStack.ps1`
   - Starts/stops the full stack.
   - Used by both manual launch and the Windows startup task.
   - Treats a real HTTP response as metrics health. A stale process-table entry
     cannot suppress replacement; startup waits for port `18765` to release.

## Data Freshness

- Main metrics sampling target: `1000ms`. Sources that are too expensive or inherently
  slower (notably top-process ranking and some TimeAudit data) keep their own `3s` cadence.
- Hybrid rendering is forced to `1000ms`; typical command-204 host writes are tens of
  milliseconds and have a dedicated `900ms` bound. Verified full-frame fallback remains
  `3000ms`, leaving COM7 idle headroom after the measured ~2.3s send.
- A full-frame send has a `10000ms` bound. The first timeout or send failure exits the
  worker so the watchdog can reopen the process and COM port instead of leaving a hung
  sender alive for minutes.
- Stream heartbeats include transport mode, per-frame transport, whether a send was attempted,
  its duration, the configured recovery interval, and the latest full-frame number. The
  watchdog also rejects send, frame, period, and overdue/mismatched recovery baselines, so
  a process that is alive but no longer meeting the host-side panel contract is not treated
  as healthy.
- The header clock is rendered from local Beijing time in the C# renderer, not from the metrics snapshot cache.
- A successful host write is not a device ACK. Physical 1Hz/freeze acceptance remains a
  visual hardware check until the vendor exposes a trustworthy panel-status response; the
  900-frame redraw limits time between host attempts, not proven physical outage duration.
- Metrics fetches use one short cancellation deadline for the complete HTTP response,
  including a stalled body, and cap the buffered payload at 4 MiB. Stale hardware values
  are preferable to a visibly stalled screen.
- Top process ranking refresh: `3s`.
- Weather refresh: cached and much slower.
- Data trust log write: throttled to avoid high-frequency disk writes.

## Public Repository Boundary

The public repo should include source code, docs, and scripts only.

Do not commit:

- Original TURZX vendor binaries.
- Local logs and generated previews.
- Device configs copied from a specific machine.
- Weather/API credentials.
- Large binary assets from the original package.
