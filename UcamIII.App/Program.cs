using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using UcamIII.Protocol;

namespace UcamIII.App
{
    class Program
    {
        private static UcamCamera? _camera;
        private static string _outputDir = "captures";

        static void Main(string[] args)
        {
            Console.WriteLine("+===========================================+");
            Console.WriteLine("|  uCAM-III CubeSat Camera Controller       |");
            Console.WriteLine("|  Protocol v1.7 | Desktop Prototype        |");
            Console.WriteLine("+===========================================+");
            Console.WriteLine();

            Directory.CreateDirectory(_outputDir);
            RunCommandLoop();
        }

        static void RunCommandLoop()
        {
            PrintHelp();

            while (true)
            {
                Console.Write("\nucam> ");
                string? input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                string[] parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string cmd = parts[0].ToLowerInvariant();

                try
                {
                    switch (cmd)
                    {
                        case "ports": ListPorts(); break;
                        case "connect": CmdConnect(parts); break;
                        case "disconnect": CmdDisconnect(); break;
                        case "jpeg": CmdJpeg(parts); break;
                        case "raw": CmdRaw(parts); break;
                        case "profile": CmdProfile(parts); break;
                        case "cbe": CmdCbe(parts); break;
                        case "light": CmdLight(parts); break;
                        case "sleep": CmdSleep(parts); break;
                        case "baud": CmdBaud(parts); break;
                        case "reset": CmdReset(parts); break;
                        case "status": CmdStatus(); break;
                        case "help": PrintHelp(); break;
                        case "quit":
                        case "exit":
                            CmdDisconnect();
                            return;
                        default:
                            Console.WriteLine($"Unknown command: {cmd}. Type 'help' for commands.");
                            break;
                    }
                }
                catch (CameraException ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Camera error: {ex.Message}");
                    Console.ResetColor();
                }
                catch (TimeoutException)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Timeout: camera did not respond. Is it connected and powered?");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        // ── Commands ─────────────────────────────────────────────────

        static void ListPorts()
        {
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("No serial ports found.");
                return;
            }
            Console.WriteLine("Available serial ports:");
            foreach (string p in ports.OrderBy(p => p))
                Console.WriteLine($"  {p}");
        }

        static void CmdConnect(string[] parts)
        {
            if (_camera != null && _camera.IsSynced)
            {
                Console.WriteLine($"Already connected on {_camera.PortName}. Disconnect first.");
                return;
            }

            string portName;
            int baud = 115200;

            if (parts.Length < 2)
            {
                string[] ports = SerialPort.GetPortNames();
                if (ports.Length == 0)
                {
                    Console.WriteLine("No serial ports found. Connect a USB-to-TTL adapter.");
                    return;
                }
                if (ports.Length == 1)
                {
                    portName = ports[0];
                    Console.WriteLine($"Auto-selected: {portName}");
                }
                else
                {
                    Console.WriteLine("Available ports:");
                    for (int i = 0; i < ports.Length; i++)
                        Console.WriteLine($"  [{i + 1}] {ports[i]}");
                    Console.Write("Select port number: ");
                    string? sel = Console.ReadLine();
                    if (int.TryParse(sel, out int idx) && idx >= 1 && idx <= ports.Length)
                        portName = ports[idx - 1];
                    else
                    {
                        Console.WriteLine("Invalid selection.");
                        return;
                    }
                }
            }
            else
            {
                portName = parts[1].ToUpperInvariant();
                if (!portName.StartsWith("COM"))
                    portName = "COM" + portName;
            }

            if (parts.Length >= 3 && int.TryParse(parts[2], out int b))
                baud = b;

            Console.WriteLine($"Connecting to {portName} at {baud} baud...");

            _camera?.Dispose();
            _camera = new UcamCamera(portName, baud);
            _camera.Log += msg => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {msg}");

            _camera.Connect();
        }

        static void CmdDisconnect()
        {
            if (_camera == null)
            {
                Console.WriteLine("Not connected.");
                return;
            }
            _camera.Dispose();
            _camera = null;
            Console.WriteLine("Disconnected.");
        }

        static void CmdJpeg(string[] parts)
        {
            EnsureConnected();

            JpegResolution res = JpegResolution.Res640x480;
            if (parts.Length >= 2)
            {
                switch (parts[1])
                {
                    case "160": res = JpegResolution.Res160x128; break;
                    case "320": res = JpegResolution.Res320x240; break;
                    case "640": res = JpegResolution.Res640x480; break;
                    default:
                        throw new ArgumentException(
                            $"Unknown JPEG resolution '{parts[1]}'. Use: 160, 320, 640");
                }
            }

            Console.WriteLine($"Capturing JPEG at {ResolutionToString(res)}...");
            byte[] jpeg = _camera!.CaptureJpeg(res);

            string filename = $"ucam_{DateTime.Now:yyyyMMdd_HHmmss}_{ResolutionToString(res)}.jpg";
            string path = Path.Combine(_outputDir, filename);
            File.WriteAllBytes(path, jpeg);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Saved: {path} ({jpeg.Length:N0} bytes)");
            Console.ResetColor();
        }

        static void CmdRaw(string[] parts)
        {
            EnsureConnected();

            ImageFormat fmt = ImageFormat.GrayScale8Bit;
            RawResolution res = RawResolution.Res160x120;

            if (parts.Length >= 2)
            {
                switch (parts[1].ToLowerInvariant())
                {
                    case "gray":
                    case "grayscale": fmt = ImageFormat.GrayScale8Bit; break;
                    case "rgb565":
                    case "rgb": fmt = ImageFormat.ColorRgb565; break;
                    case "crycby":
                    case "yuv": fmt = ImageFormat.ColorCrYCbY; break;
                    default:
                        throw new ArgumentException(
                            $"Unknown format '{parts[1]}'. Use: gray, rgb565, crycby");
                }
            }

            if (parts.Length >= 3)
            {
                switch (parts[2].ToLowerInvariant())
                {
                    case "80x60":
                    case "80": res = RawResolution.Res80x60; break;
                    case "160x120":
                    case "160": res = RawResolution.Res160x120; break;
                    case "128x128": res = RawResolution.Res128x128; break;
                    case "128x96":
                    case "128": res = RawResolution.Res128x96; break;
                    default:
                        throw new ArgumentException(
                            $"Unknown resolution '{parts[2]}'. Use: 80x60, 160x120, 128x128, 128x96");
                }
            }

            int expectedSize = UcamCamera.GetRawImageSize(fmt, res);
            Console.WriteLine($"Capturing RAW ({fmt}, {RawResToString(res)}, expected {expectedSize} bytes)...");

            byte[] raw = _camera!.CaptureRaw(fmt, res);

            string filename = $"ucam_{DateTime.Now:yyyyMMdd_HHmmss}_{fmt}_{RawResToString(res)}.raw";
            string path = Path.Combine(_outputDir, filename);
            File.WriteAllBytes(path, raw);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Saved: {path} ({raw.Length:N0} bytes)");
            Console.ResetColor();

            // Also save a PGM for grayscale so it can be easily viewed
            if (fmt == ImageFormat.GrayScale8Bit)
            {
                GetRawDimensions(res, out int w, out int h);
                string pgmPath = Path.ChangeExtension(path, ".pgm");
                SavePgm(pgmPath, raw, w, h);
                Console.WriteLine($"  Also saved viewable PGM: {pgmPath}");
            }
        }

        static void CmdProfile(string[] parts)
        {
            EnsureConnected();

            if (parts.Length < 2)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  profile jpeg [160|320|640] [dwellMs]");
                Console.WriteLine("  profile raw [gray|rgb565|crycby] [80x60|160x120|128x128|128x96] [dwellMs]");
                return;
            }

            string mode = parts[1].ToLowerInvariant();
            if (mode == "jpeg")
            {
                JpegResolution res = JpegResolution.Res640x480;
                int dwellMs = 0;

                if (parts.Length >= 3)
                {
                    switch (parts[2])
                    {
                        case "160": res = JpegResolution.Res160x128; break;
                        case "320": res = JpegResolution.Res320x240; break;
                        case "640": res = JpegResolution.Res640x480; break;
                        default:
                            throw new ArgumentException(
                                $"Unknown JPEG resolution '{parts[2]}'. Use: 160, 320, 640");
                    }
                }

                if (parts.Length >= 4 && !int.TryParse(parts[3], out dwellMs))
                    throw new ArgumentException("dwellMs must be an integer >= 0");
                if (dwellMs < 0)
                    throw new ArgumentException("dwellMs must be >= 0");

                Console.WriteLine($"Profiling JPEG capture at {ResolutionToString(res)} (dwell={dwellMs}ms)...");
                UcamCamera.CaptureProfileResult profile = _camera!.CaptureJpegProfiled(res, 0, dwellMs);

                string baseName = $"ucam_profile_{DateTime.Now:yyyyMMdd_HHmmss}_jpeg_{ResolutionToString(res)}";
                string jpgPath = Path.Combine(_outputDir, baseName + ".jpg");
                string csvPath = Path.Combine(_outputDir, baseName + "_power.csv");

                File.WriteAllBytes(jpgPath, profile.ImageData);
                SaveProfileCsv(csvPath, profile.Points);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Saved image: {jpgPath} ({profile.ImageData.Length:N0} bytes)");
                Console.WriteLine($"Saved profile: {csvPath} ({profile.Points.Count} points)");
                Console.ResetColor();
                return;
            }

            if (mode == "raw")
            {
                ImageFormat fmt = ImageFormat.GrayScale8Bit;
                RawResolution res = RawResolution.Res160x120;
                int dwellMs = 0;

                if (parts.Length >= 3)
                {
                    switch (parts[2].ToLowerInvariant())
                    {
                        case "gray":
                        case "grayscale": fmt = ImageFormat.GrayScale8Bit; break;
                        case "rgb565":
                        case "rgb": fmt = ImageFormat.ColorRgb565; break;
                        case "crycby":
                        case "yuv": fmt = ImageFormat.ColorCrYCbY; break;
                        default:
                            throw new ArgumentException(
                                $"Unknown format '{parts[2]}'. Use: gray, rgb565, crycby");
                    }
                }

                if (parts.Length >= 4)
                {
                    switch (parts[3].ToLowerInvariant())
                    {
                        case "80x60":
                        case "80": res = RawResolution.Res80x60; break;
                        case "160x120":
                        case "160": res = RawResolution.Res160x120; break;
                        case "128x128": res = RawResolution.Res128x128; break;
                        case "128x96":
                        case "128": res = RawResolution.Res128x96; break;
                        default:
                            throw new ArgumentException(
                                $"Unknown resolution '{parts[3]}'. Use: 80x60, 160x120, 128x128, 128x96");
                    }
                }

                if (parts.Length >= 5 && !int.TryParse(parts[4], out dwellMs))
                    throw new ArgumentException("dwellMs must be an integer >= 0");
                if (dwellMs < 0)
                    throw new ArgumentException("dwellMs must be >= 0");

                Console.WriteLine($"Profiling RAW capture ({fmt}, {RawResToString(res)}, dwell={dwellMs}ms)...");
                UcamCamera.CaptureProfileResult profile = _camera!.CaptureRawProfiled(fmt, res, dwellMs);

                string baseName = $"ucam_profile_{DateTime.Now:yyyyMMdd_HHmmss}_raw_{fmt}_{RawResToString(res)}";
                string rawPath = Path.Combine(_outputDir, baseName + ".raw");
                string csvPath = Path.Combine(_outputDir, baseName + "_power.csv");

                File.WriteAllBytes(rawPath, profile.ImageData);
                SaveProfileCsv(csvPath, profile.Points);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Saved image: {rawPath} ({profile.ImageData.Length:N0} bytes)");
                Console.WriteLine($"Saved profile: {csvPath} ({profile.Points.Count} points)");
                Console.ResetColor();

                if (fmt == ImageFormat.GrayScale8Bit)
                {
                    GetRawDimensions(res, out int w, out int h);
                    string pgmPath = Path.ChangeExtension(rawPath, ".pgm");
                    SavePgm(pgmPath, profile.ImageData, w, h);
                    Console.WriteLine($"  Also saved viewable PGM: {pgmPath}");
                }
                return;
            }

            throw new ArgumentException("Use: profile jpeg ... | profile raw ...");
        }

        static void CmdCbe(string[] parts)
        {
            EnsureConnected();

            CameraLevel c = CameraLevel.Normal, b = CameraLevel.Normal, e = CameraLevel.Normal;
            if (parts.Length >= 2) c = ParseLevel(parts[1], "contrast");
            if (parts.Length >= 3) b = ParseLevel(parts[2], "brightness");
            if (parts.Length >= 4) e = ParseLevel(parts[3], "exposure");

            Console.WriteLine($"Setting contrast={c}, brightness={b}, exposure={e}...");
            _camera!.SetContrastBrightnessExposure(c, b, e);
            Console.WriteLine("Done.");
        }

        static void CmdLight(string[] parts)
        {
            EnsureConnected();

            LightFrequency freq = LightFrequency.Hz50;
            if (parts.Length >= 2)
            {
                switch (parts[1])
                {
                    case "50": freq = LightFrequency.Hz50; break;
                    case "60": freq = LightFrequency.Hz60; break;
                    default: throw new ArgumentException("Use: light 50 | light 60");
                }
            }

            Console.WriteLine($"Setting light frequency to {(freq == LightFrequency.Hz50 ? "50" : "60")}Hz...");
            _camera!.SetLightFrequency(freq);
            Console.WriteLine("Done.");
        }

        static void CmdSleep(string[] parts)
        {
            EnsureConnected();

            byte timeout = 0;
            if (parts.Length >= 2 && !byte.TryParse(parts[1], out timeout))
                throw new ArgumentException("Sleep timeout must be 0-255.");

            string desc = timeout == 0 ? "disabled" : $"{timeout}s";
            Console.WriteLine($"Setting sleep timeout: {desc}...");
            _camera!.SetSleepTimeout(timeout);
            Console.WriteLine("Done.");
        }

        static void CmdBaud(string[] parts)
        {
            EnsureConnected();

            if (parts.Length < 2 || !int.TryParse(parts[1], out int baud))
            {
                Console.WriteLine("Usage: baud <rate>");
                Console.WriteLine("Rates: 2400, 4800, 9600, 19200, 38400, 57600, 115200,");
                Console.WriteLine("       153600, 230400, 460800, 921600, 1228800, 1843200");
                return;
            }

            Console.WriteLine($"Changing baud rate to {baud}...");
            _camera!.SetBaudRate(baud);
            Console.WriteLine($"Baud rate set to {baud}.");
        }

        static void CmdReset(string[] parts)
        {
            EnsureConnected();

            ResetType type = ResetType.Full;
            if (parts.Length >= 2 && parts[1].ToLowerInvariant() == "state")
                type = ResetType.StateMachineOnly;

            Console.WriteLine($"Resetting camera ({type})...");
            _camera!.SoftReset(type);

            if (type == ResetType.Full)
                Console.WriteLine("Camera reset. Use 'connect' to re-sync.");
        }

        static void CmdStatus()
        {
            if (_camera == null)
            {
                Console.WriteLine("Not connected.");
                return;
            }
            Console.WriteLine($"Port:       {_camera.PortName}");
            Console.WriteLine($"Synced:     {_camera.IsSynced}");
            Console.WriteLine($"Pkg size:   {_camera.PackageSize}");
            Console.WriteLine($"Output dir: {Path.GetFullPath(_outputDir)}");
        }

        // ── Helpers ──────────────────────────────────────────────────

        static void EnsureConnected()
        {
            if (_camera == null || !_camera.IsSynced)
                throw new CameraException("Not connected. Use 'connect' first.");
        }

        static CameraLevel ParseLevel(string s, string name)
        {
            if (int.TryParse(s, out int v) && v >= 0 && v <= 4)
                return (CameraLevel)v;
            switch (s.ToLowerInvariant())
            {
                case "min": return CameraLevel.Min;
                case "low": return CameraLevel.Low;
                case "normal": return CameraLevel.Normal;
                case "high": return CameraLevel.High;
                case "max": return CameraLevel.Max;
            }
            throw new ArgumentException($"{name} must be min|low|normal|high|max or 0-4");
        }

        static string ResolutionToString(JpegResolution res)
        {
            switch (res)
            {
                case JpegResolution.Res160x128: return "160x128";
                case JpegResolution.Res320x240: return "320x240";
                case JpegResolution.Res640x480: return "640x480";
                default: return res.ToString();
            }
        }

        static string RawResToString(RawResolution res)
        {
            switch (res)
            {
                case RawResolution.Res80x60: return "80x60";
                case RawResolution.Res160x120: return "160x120";
                case RawResolution.Res128x128: return "128x128";
                case RawResolution.Res128x96: return "128x96";
                default: return res.ToString();
            }
        }

        static void GetRawDimensions(RawResolution res, out int w, out int h)
        {
            switch (res)
            {
                case RawResolution.Res80x60: w = 80; h = 60; return;
                case RawResolution.Res160x120: w = 160; h = 120; return;
                case RawResolution.Res128x128: w = 128; h = 128; return;
                case RawResolution.Res128x96: w = 128; h = 96; return;
                default: w = 0; h = 0; return;
            }
        }

        /// <summary>Save 8-bit grayscale data as a PGM (Portable Gray Map) file.</summary>
        static void SavePgm(string path, byte[] data, int width, int height)
        {
            using (var fs = File.Create(path))
            using (var writer = new StreamWriter(fs))
            {
                writer.WriteLine("P5");
                writer.WriteLine($"{width} {height}");
                writer.WriteLine("255");
                writer.Flush();
                fs.Write(data, 0, Math.Min(data.Length, width * height));
            }
        }

        static void SaveProfileCsv(string path, System.Collections.Generic.IReadOnlyList<UcamCamera.PowerProfilePoint> points)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("timestamp_utc,elapsed_ms,step");
                if (points.Count == 0)
                    return;

                DateTimeOffset t0 = points[0].TimestampUtc;
                foreach (UcamCamera.PowerProfilePoint p in points)
                {
                    double elapsedMs = (p.TimestampUtc - t0).TotalMilliseconds;
                    string step = p.Step.Replace(",", ";");
                    writer.WriteLine($"{p.TimestampUtc:O},{elapsedMs:F3},{step}");
                }
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"
Commands:
  ports                     List available serial ports
  connect [COM#] [baud]     Connect and sync (default: auto-detect, 115200)
  disconnect                Close connection

  jpeg [160|320|640]        Capture JPEG snapshot (default: 640x480)
  raw [gray|rgb565|crycby] [80x60|160x120|128x128|128x96]
                            Capture RAW image (default: gray 160x120)
    profile jpeg [160|320|640] [dwellMs]
                                                        Capture JPEG + save power-phase CSV markers
    profile raw [gray|rgb565|crycby] [80x60|160x120|128x128|128x96] [dwellMs]
                                                        Capture RAW + save power-phase CSV markers

  cbe [contrast] [bright] [exposure]
                            Set contrast/brightness/exposure (each 0-4, 2=normal)
  light [50|60]             Set light frequency (Hz)
  sleep [0-255]             Set sleep timeout (seconds, 0=disabled)
  baud <rate>               Change baud rate
  reset [full|state]        Software reset (default: full)

  status                    Show connection info
  help                      Show this help
  quit                      Exit
");
        }
    }
}
