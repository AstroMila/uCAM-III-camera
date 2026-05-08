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
profile jpeg [160|320|640] [dwellMs]
                               Capture JPEG + save phase markers CSV for power profiling
profile raw [gray|rgb565|crycby] [80x60|160x120|128x128|128x96] [dwellMs]
                               Capture RAW + save phase markers CSV for power profiling
step ...                       Manual step-by-step protocol control for power testing

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

## Power Budget Measurement

This project includes a built-in profiling mode to help measure camera current/power for each protocol phase.
A concise lab-day checklist is provided at the end of this section.

### What profiling mode provides

- Captures an image as normal (`.jpg` or `.raw`)
- Saves a CSV timeline with timestamped phase markers in `captures/`
- Optional `dwellMs` delay between major steps so each phase is easier to isolate on power instruments

CSV columns:

- `timestamp_utc` (ISO-8601)
- `elapsed_ms` (time since first marker)
- `step` (protocol phase label)

### Recommended test setup

- Place a current measurement device in series with camera VCC (power analyzer, shunt + oscilloscope, or precision logger)
- Keep UART wiring as in Hardware Setup section
- Start logging current first, then run profiling command
- Correlate current trace with CSV `elapsed_ms` markers

### Typical profiling commands

```text
connect COM3 115200

# JPEG profile, no extra dwell
profile jpeg 640

# JPEG profile, hold ~500 ms between major phases
profile jpeg 640 500

# IMPORTANT: include max JPEG resolution in every campaign
# (640 = 640x480, highest supported JPEG size)

# RAW grayscale profile with dwell
profile raw gray 128x96 500

# RAW RGB565 profile
profile raw rgb565 160x120 300
```

### Typical phase markers

JPEG markers include steps such as:

- `jpeg:buffer_flushed`
- `jpeg:state_reset`
- `jpeg:initial_ack`
- `jpeg:set_package_size_ack`
- `jpeg:snapshot_ack`
- `jpeg:snapshot_settled`
- `jpeg:get_picture_ack`
- `jpeg:data_header size=...`
- `jpeg:transfer_start packages=...`
- `jpeg:transfer_done bytes=...`
- `jpeg:final_ack_sent`

RAW markers include:

- `raw:buffer_flushed`
- `raw:state_reset`
- `raw:initial_ack`
- `raw:snapshot_ack`
- `raw:snapshot_settled`
- `raw:get_picture_ack`
- `raw:data_header size=...`
- `raw:transfer_start`
- `raw:transfer_done bytes=...`
- `raw:final_ack_sent`

### Manual Step Mode (one phase at a time)

Use this mode when you need to hold the camera at specific protocol phases and measure power manually.

Core commands:

- `step help`
- `step flush`
- `step reset`
- `step wait <ms>`
- `step init jpeg <160|320|640>`
- `step init raw <gray|rgb565|crycby> <80x60|160x120|128x128|128x96>`
- `step pkg [size]`
- `step snapshot jpeg [skipFrames]`
- `step snapshot raw`
- `step getpic jpeg|raw`
- `step recv [count|all]` (JPEG: packages; RAW is auto-buffered during `step getpic raw`)
- `step finish` (sends final ACK and saves image)
- `step status`
- `step clear`

JPEG manual flow example:

```text
connect COM3 115200
step reset
step init jpeg 640
step pkg 512
step snapshot jpeg
step wait 1000
step getpic jpeg
step recv 1
step recv all
step finish
```

RAW manual flow example:

```text
connect COM3 115200
step reset
step init raw gray 128x96
step snapshot raw
step wait 1000
step getpic raw
step finish
```

Notes:

- `step finish` is required; it sends the final ACK that returns the camera to ready state.
- You can insert `step wait <ms>` between any commands to create stable measurement plateaus.
- For max JPEG power case, use `step init jpeg 640` (which is 640x480).
- RAW cannot be cleanly split into `getpic` and later `recv` because the camera starts streaming RAW bytes immediately after `GET_PICTURE`; the app now buffers RAW automatically during `step getpic raw`.

### Suggested power budget campaign

1. Baseline idle (connected, no capture)
2. JPEG at 160/320/640 (**always include 640x480 max resolution**)
3. RAW gray and RAW RGB565
4. `sleep 0` vs non-zero sleep timeout behavior
5. Repeat captures (`profile ...` multiple times) to confirm stable average and peak current

### Lab-Day Quick Checklist

Use this when running camera power measurements in the lab.

1. **Wiring and instrument**
  - Camera at 5V, shared GND with USB-TTL and measurement instrument
  - UART connected (TX↔RX crossed, GND common)
  - Current meter in series with camera VCC

2. **Start logging**
  - Start current/power logging on the instrument before camera commands
  - Note test ID/time in lab notes

3. **Connect and verify**
  - `connect COM3 115200`
  - `status`

4. **Run profile captures**
  - `profile jpeg 160 500`
  - `profile jpeg 320 500`
  - `profile jpeg 640 500` (**mandatory max JPEG test**)
  - `profile raw gray 128x96 500`
  - `profile raw rgb565 160x120 500`

5. **Collect artifacts**
  - Instrument export (current/power trace)
  - CLI CSV (`*_power.csv`)
  - Captured image (`.jpg` / `.raw` / `.pgm`)

6. **Validate markers**
  - Confirm CSV includes expected phase steps (`initial_ack`, `snapshot_ack`, `transfer_start`, `final_ack_sent`, `complete`)

7. **Repeatability**
  - Run each case at least 3 times
  - Report average and peak values per phase

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
