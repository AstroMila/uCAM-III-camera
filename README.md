# uCAM-III Camera Driver

A .NET 8 serial driver and interactive CLI for the [4D Systems uCAM-III](https://4dsystems.com.au/ucam-iii) camera module, developed for use aboard a **nano-satellite (CubeSat)**.

The project is split into two layers:

| Project | Purpose |
|---------|---------|
| **UcamIII.Protocol** | Reusable library – command builder, response parser, and high-level camera driver (`UcamCamera`) |
| **UcamIII.App** | Interactive command-line application for desktop testing |

> **Phase 1** (this repo) is a C# desktop prototype used to validate the full protocol against real hardware.  
> **Phase 2** will be a lightweight embedded C port targeting the flight computer.

---

## Features

- Full implementation of the uCAM-III 6-byte serial command protocol (all 11 commands)
- **JPEG capture** at 160×128, 320×240, 640×480
- **RAW capture** in Grayscale 8-bit, RGB565, and CrYCbY at 80×60, 160×120, 128×128, 128×96
- Packaged JPEG transfer with configurable packet size (up to 512 bytes)
- Contrast / Brightness / Exposure adjustment
- Light frequency filter (50 Hz / 60 Hz)
- Baud rate switching (2400 – 1 843 200)
- Sleep timeout configuration
- Automatic state-machine reset between captures (no disconnect/reconnect needed)
- Robust retry logic with progressive back-off on SYNC and GET_PICTURE
- PGM export for grayscale RAW images (viewable without special tools)
- Diagnostic logging with timestamps

## Hardware Setup

```
USB-to-TTL Adapter          uCAM-III
  TX  ──────────────────▶  RX
  RX  ◀──────────────────  TX
  GND ◀─────────────────▶  GND
  5V  ──────────────────▶  VCC
```

- **Voltage:** 5 V (do not use 3.3 V)
- **Default baud:** 115 200
- Cable: any USB-to-TTL serial adapter (FTDI, CP2102, CH340, etc.)

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- A serial port (physical or USB-to-TTL)

## Quick Start

```bash
# Clone
git clone https://github.com/AstroMila/uCAM-III-camera.git
cd uCAM-III-camera

# Build
dotnet build

# Run the interactive CLI
dotnet run --project UcamIII.App
```

## CLI Commands

```
ports                          List available serial ports
connect [COM#] [baud]          Connect and sync (default: auto-detect, 115200)
disconnect                     Close connection

jpeg [160|320|640]             Capture JPEG snapshot (default: 640x480)
raw [gray|rgb565|crycby] [80x60|160x120|128x128|128x96]
                               Capture RAW image (default: gray 160x120)

cbe [contrast] [brightness] [exposure]
                               Set image adjustment (each: min|low|normal|high|max or 0-4)
light [50|60]                  Set light frequency (Hz)
sleep [0-255]                  Set sleep timeout in seconds (0 = disabled)
baud <rate>                    Change baud rate
reset [full|state]             Software reset (full requires reconnect)

status                         Show connection info
help                           Show help
quit                           Exit
```

### Example Session

```
ucam> connect COM3
Connecting to COM3 at 115200 baud...
  [14:32:01.123] SYNC attempt 1/60...
  [14:32:01.250] Camera synced.

ucam> jpeg 640
Capturing JPEG at 640x480...
Saved: captures/ucam_20260227_143205_640x480.jpg (28,412 bytes)

ucam> cbe max max normal
Setting contrast=Max, brightness=Max, exposure=Normal...
Done.

ucam> raw gray 128x96
Capturing RAW (GrayScale8Bit, 128x96, expected 12288 bytes)...
Saved: captures/ucam_20260227_143210_GrayScale8Bit_128x96.raw (12,288 bytes)
  Also saved viewable PGM: captures/ucam_20260227_143210_GrayScale8Bit_128x96.pgm

ucam> quit
Disconnected.
```

## Project Structure

```
├── UcamIII.sln
├── global.json
├── UcamIII.Protocol/
│   ├── Enums.cs              # Command IDs, resolutions, image formats, error codes
│   ├── CommandBuilder.cs     # Builds 6-byte command packets
│   ├── CameraResponse.cs     # Parses 6-byte camera responses
│   ├── CameraException.cs    # Domain exception type
│   └── UcamCamera.cs         # High-level driver (connect, capture, configure)
├── UcamIII.App/
│   └── Program.cs            # Interactive CLI
└── captures/                 # Output directory (created at runtime)
```

## Protocol Overview

The uCAM-III uses a simple 6-byte command/response protocol over UART:

```
Byte 0: 0xAA (prefix)
Byte 1: Command ID
Bytes 2-5: Parameters (P1-P4)
```

**Capture flow (JPEG):**

```
Host              Camera
 │── SYNC ──────────▶│
 │◀──── ACK+SYNC ────│
 │── ACK ───────────▶│
 │── INITIAL ───────▶│   (set JPEG mode + resolution)
 │◀──── ACK ─────────│
 │── SET_PKG_SIZE ──▶│   (512 bytes)
 │◀──── ACK ─────────│
 │── SNAPSHOT ──────▶│   (compressed)
 │◀──── ACK ─────────│
 │   ... delay ...    │   (wait for compression)
 │── GET_PICTURE ───▶│
 │◀──── ACK ─────────│
 │◀──── DATA ────────│   (image size in header)
 │── ACK pkg 0 ────▶│
 │◀──── pkg 0 ───────│
 │── ACK pkg 1 ────▶│
 │◀──── pkg 1 ───────│
 │   ... repeat ...   │
 │── ACK (final) ──▶│
```

## Resolution Compatibility

| Resolution | JPEG | RAW (Gray/RGB565/CrYCbY) |
|------------|------|--------------------------|
| 80×60      | —    | ✓                        |
| 128×96     | —    | ✓                        |
| 128×128    | —    | ✓                        |
| 160×120    | —    | ✓                        |
| 160×128    | ✓    | —                        |
| 320×240    | ✓    | —                        |
| 640×480    | ✓    | —                        |

## Known Notes

- **128×96 RAW resolution** uses value `0x0B` (not `0x08` as sometimes listed in third-party code). Confirmed against the [nsstc-uae reference implementation](https://github.com/nsstc-uae/uCamIII-example).
- A **state-machine reset** (`RESET` type `0x01`) is sent before each capture to allow reliable repeat captures without reconnecting.
- After RAW capture, a final **ACK(DATA)** must be sent to return the camera to an idle state — required for subsequent captures.
- The **light frequency** setting (50/60 Hz) is an anti-flicker filter for indoor AC lighting and is irrelevant for space applications.

## References

- [uCAM-III Datasheet](https://4dsystems.com.au/ucam-iii) — 4D Systems official documentation
- [nsstc-uae/uCamIII-example](https://github.com/nsstc-uae/uCamIII-example) — C reference (National Space Science & Technology Center, UAE)
- [ScruffR/uCamIII](https://github.com/ScruffR/uCamIII) — C++ / Particle reference
- [kristianharge/uCamIII](https://github.com/kristianharge/uCamIII) — C / Raspberry Pi reference

## License

MIT
