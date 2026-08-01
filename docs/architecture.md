# Architecture

TURZX SideScreen is intentionally split into small local processes:

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
   - Fetches snapshots with a short timeout and reuses the last good snapshot if metrics are slow.
   - Renders 480x1920 bitmaps with `System.Drawing`.
   - Sends one full frame, then TURZX differential frames over COM7.

5. `StartSideScreenStack.ps1`
   - Starts/stops the full stack.
   - Used by both manual launch and the Windows startup task.
   - Treats a real HTTP response as metrics health. A stale process-table entry
     cannot suppress replacement; startup waits for port `18765` to release.

## Data Freshness

- Main screen refresh target: `1000ms` by default, using differential frames to reduce USB/HID interference with RGB control software.
- The header clock is rendered from local Beijing time in the C# renderer, not from the metrics snapshot cache.
- Metrics fetches are capped at a short timeout; stale hardware values are preferable to a visibly stalled screen.
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
