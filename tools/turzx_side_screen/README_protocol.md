# TURZX side screen protocol notes

Scope: v1 reusable protocol layer for the 480x1920 TURZX side screen.

Verified from `tools\turzx_protocol_probe\TurzxProtocolProbe.cs` and local black-box converter samples:

- Full frame size: `480 * 1920 * 4 = 3686400` bytes.
- Device pixel order: row-major `B, G, R, A`.
- Full-frame send default used by the successful probe path:
  - Send one 250-byte command packet with command `200`.
  - Command packet bytes:
    - `[0]`: command.
    - `[1..2]`: magic `EF 69`.
    - `[3..6]`: declared payload length, big-endian.
    - `[7]`: extra byte.
    - `[8..9]`: reserved zero.
    - `[10..]`: optional command payload, max 240 bytes.
  - Then write raw frame bytes in chunks of up to `24900` bytes.
- Alternate full-frame command `202` exists in the vendor driver path, but v1 defaults to command `200`.
- Panel brightness uses the vendor command `123`:
  - declared payload length: `1`;
  - extra byte: `0`;
  - payload byte `[10]`: brightness `0..255`;
  - brightness `0` turns the panel/backlight off, and the watchdog restores the configured active value before streaming resumes.
- `RJCP.SerialPortStream` is required for COM open/write. The ordinary `.NET SerialPort` probe path timed out.

Not implemented in the reusable v1 protocol layer:

- Command `204` differential/partial refresh. `TurzxProtocolProbe.cs` did not verify a
  reusable payload contract, so `WriteDifferentialFrame` intentionally throws
  `NotSupportedException`. The separate device-specific HybridRefresh wrapper is an
  explicit local candidate derived from the vendor assembly; it is bounded, reversible,
  and must not be presented as a verified public protocol or a device ACK.

Safe test command:

```powershell
powershell -ExecutionPolicy Bypass -File tools\turzx_side_screen\TestProtocolEncoding.ps1
```

The test compiles and runs pure logic only; it does not open `COM7` or any serial port.

Build and inspect a brightness packet without opening the serial port:

```powershell
powershell -ExecutionPolicy Bypass -File tools\turzx_side_screen\SetTurzxBrightness.ps1 -Brightness 0 -DryRun
```

The live form opens the configured COM port, so the frame stream must be stopped
first. The watchdog performs that ownership handoff automatically.
