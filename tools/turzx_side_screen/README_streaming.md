# TURZX Side Screen Streaming Investigation

Scope: static/reflection investigation for the 480x1920 side screen refresh path. This document intentionally does not define an unverified differential protocol.

Safety constraints used during this investigation:

- No Drive upload.
- No COM7 send, no serial open/write from the new inspection script.
- No changes to `metrics_agent` or renderer files.
- New investigation artifact: `tools\turzx_side_screen\InspectTurzxProtocol.ps1`.

## Current Streaming Paths

The conservative path remains full-frame only:

1. `StartVideoStream.ps1` builds and runs `TURZX.SideScreen.Stream.exe`.
2. `TURZX.SideScreen.Stream.cs` renders a 480x1920 bitmap for each loop.
3. `TURZX.SideScreen.TurzxHelperSender.SendBitmap(...)` loads `TURZX.weatherfix.metrics.exe` when present, otherwise `TURZX.exe`.
4. Reflection calls `趵.蛳(Bitmap, 4, 480)` to produce frame data.
5. Reflection creates `駟(devCode)`, opens the `釃` serial helper field `韘`, and calls `駟.詔(byte[], int, false)`.
6. The observed helper message is `OK frameBytes=3686400`.

The explicit HybridRefresh path is a device-specific candidate for the required 1Hz
clock. It does not redefine command 204 as a verified public protocol. It uses the
field-tested default delta helper, a vendor-shaped priming/brightness/double-command-200
baseline, one persistent serial writer, a 900ms delta timeout, and process exit/reopen
on the first failure. Startup keeps the vendor-shaped double baseline. During normal
operation, one command-200 redraw is attempted every 900 frames (about 15 minutes at
1Hz) without repeating priming or the duplicate startup frame, limiting the interruption
to one normal full-frame transfer.

Hybrid heartbeat success proves only that the local write returned within its bound.
The current path has no device ACK and cannot prove that the OLED decoded or scanned
the frame; physical acceptance therefore remains separate.

The full frame size is:

```text
480 * 1920 * 4 = 3,686,400 bytes
```

## Reflection Findings

`TURZX.weatherfix.metrics.exe` exposes these relevant obfuscated types:

- `趵`: bitmap/frame conversion helpers. The helper path uses `蛳(Bitmap, int, int) -> byte[]`.
- `駟`: serial/display driver. Relevant methods include:
  - `詔(byte[], int, bool) -> void`: full-frame send path.
  - `裺(byte[], byte[], long, bool) -> int`: suspected differential path.
  - field `韘: 釃`: serial helper.
- `釃`: RJCP serial wrapper and packet writer. Relevant methods include:
  - `詔(string) -> bool`: open a port.
  - `込() -> void`: close.
  - `詔(int, int, byte[], byte) -> void`: command packet writer.
  - `詔(byte[], int) -> void`: frame/body writer.
- `陾`: USB/207LCD path. It has `豛(byte[], bool) -> bool` and LibUsb fields, but this is not the current COM streaming path.
- `뛮`: base driver class with the same broad send/diff method surface.

`TURZX.exe` can enumerate the same key runtime types via normal reflection. Raw Cecil metadata for `TURZX.exe` shows only a protected-looking `x1400.*` outer layout, so readable IL evidence below is taken from `TURZX.weatherfix.metrics.exe`. Treat the patched executable as the practical reflection target used by the current helper.

## Command Evidence

Readable IL in `TURZX.weatherfix.metrics.exe` shows:

- `駟.詔(byte[], int, bool)` sends command `200` when the alternate-frame flag is false, or command `202` when it is true, then writes the body bytes.
- `釃.詔(int command, int declaredLength, byte[] payload, byte extra)` builds a 250-byte command packet:
  - byte `0`: command.
  - bytes `1..2`: magic `0xEF 0x69` (`61289`).
  - bytes `3..6`: declared length, big-endian.
  - byte `7`: extra.
  - bytes `10..`: optional payload.
- `釃.詔(byte[], int)` writes body data as repeated 250-byte serial writes with a 249-byte payload budget and sleeps 1 ms around each 24,900 payload bytes.
- `駟.裺(byte[], byte[], long, bool)` contains a command `204` send:
  - It calls proprietary frame-delta helpers on `趵` with a `65000` threshold.
  - It writes command `204` through `釃.詔(204, length, payload, 0)`.
  - It also manipulates sequence/length bytes and the `0xEF69` marker, but the exact payload layout is not reliably reconstructable from static IL alone.

Conclusion: command `204` exists and the private wrapper now mirrors the vendor
`A3=false` packet shape closely enough for a guarded local hardware candidate. It is
still not a safe public protocol contract: helper choice is firmware-dependent and no
device ACK, rectangle API, or pixel-level health signal has been found.

## Measured Throughput

Existing local log evidence from `tools\turzx_side_screen\out\video-stream.log`:

```text
samples=93
frameBytes=3686400
min=2328ms
max=2391ms
avg=2351.4ms
```

The default stream interval is `3000ms`, but sending one full frame already consumes about `2.35s`. That leaves only about `0.65s` idle time per 3-second loop.

At the measured average:

```text
3,686,400 bytes / 2.351s = about 1.57 MB/s payload
```

A 0.5-second full-frame animation target would require:

```text
3,686,400 bytes / 1s = about 3.69 MB/s payload
```

That is roughly 4.7x the observed payload rate, before accounting for 250-byte packet overhead, 1 ms periodic sleeps, serial open/close overhead, and device-side consume time. Therefore the current full-screen refresh path cannot support smooth 0.5-second animation.

## Strategy

Conservative mode:

- Keep the device path full-screen and low-frequency.
- Treat `3000ms` as a realistic default for stable display.
- Avoid UI animations that depend on sub-second device refresh.
- Coalesce metric changes and skip sends when rendered content is materially unchanged.

Hybrid candidate:

- Render/fetch at 1 second and send command-204 deltas on one persistent COM session.
- Establish the baseline with the vendor 8.8-inch priming and two command-200 frames.
- Use the default delta helper. Static 1.15 firmware evidence suggested Alt, but the
  physical panel rejected the 19th Alt delta and then rejected all writes until recovery.
- Abort the whole child after a 900ms delta timeout; never retry concurrently on the
  possibly blocked session.
- Attempt one host-side full redraw every 900 frames without reopening the serial session,
  and keep command-204 sequence numbers monotonic across that redraw. This is a recovery
  attempt, not proof that the physical panel accepted it.

Future protocol work:

- Add an application-level card dirty API above the renderer/sender boundary.
- Track dirty cards/rectangles in memory, but keep the wire format as verified full-frame until command `204` is captured and validated.
- Use dirty state to reduce render work, coalesce frequent metric changes, and prepare a clean future adapter for differential sending.
- Keep a minimum send interval and avoid per-card animation promises unless a real differential path is proven.

Future validation path:

- Capture official vendor traffic for controlled one-card or small-region changes.
- Compare captured `204` packets against `駟.裺(...)` static evidence.
- Validate on hardware only after the payload layout is understood.
- Only then implement a differential sender behind a feature flag with full-frame fallback.

## Inspection Command

Run the static/reflection inspection without opening COM ports:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\turzx_side_screen\InspectTurzxProtocol.ps1
```

The script prints reflection summaries, constant hits, and key IL windows. It does not instantiate `駟`, does not call `釃.詔(string)`, and does not write serial data.
