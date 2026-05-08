using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace UcamIII.Protocol
{
    /// <summary>
    /// High-level driver for the uCAM-III serial camera.
    /// Handles connection synchronisation, image capture (JPEG and RAW),
    /// and camera configuration over a serial port.
    /// </summary>
    public class UcamCamera : IDisposable
    {
        public readonly struct PowerProfilePoint
        {
            public DateTimeOffset TimestampUtc { get; }
            public string Step { get; }

            public PowerProfilePoint(DateTimeOffset timestampUtc, string step)
            {
                TimestampUtc = timestampUtc;
                Step = step;
            }
        }

        public sealed class CaptureProfileResult
        {
            public byte[] ImageData { get; }
            public IReadOnlyList<PowerProfilePoint> Points { get; }

            public CaptureProfileResult(byte[] imageData, IReadOnlyList<PowerProfilePoint> points)
            {
                ImageData = imageData;
                Points = points;
            }
        }

        public readonly struct JpegTransferPackage
        {
            public int RequestedPackageId { get; }
            public int ReceivedPackageId { get; }
            public int DataSize { get; }
            public bool ChecksumOk { get; }
            public byte[] Payload { get; }

            public JpegTransferPackage(int requestedPackageId, int receivedPackageId, int dataSize, bool checksumOk, byte[] payload)
            {
                RequestedPackageId = requestedPackageId;
                ReceivedPackageId = receivedPackageId;
                DataSize = dataSize;
                ChecksumOk = checksumOk;
                Payload = payload;
            }
        }

        private readonly SerialPort _port;
        private bool _synced;
        private bool _disposed;

        /// <summary>Fired for diagnostic/progress messages.</summary>
        public event Action<string>? Log;

        /// <summary>Package size for JPEG transfer (max 512).</summary>
        public ushort PackageSize { get; set; } = 512;

        public bool IsSynced => _synced;
        public string PortName => _port.PortName;

        public UcamCamera(string portName, int baudRate = 115200)
        {
            _port = new SerialPort(portName)
            {
                BaudRate = baudRate,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                ReadTimeout = 1000,
                WriteTimeout = 1000,
            };
        }

        // ── Connection ───────────────────────────────────────────────

        /// <summary>
        /// Opens the serial port and performs the SYNC handshake.
        /// Per the datasheet: send SYNC up to 60 times with progressive delay
        /// (start 5ms, +1ms each retry). After receiving ACK+SYNC from the camera,
        /// reply with ACK to finalise. Then wait for AGC/AEC stabilisation.
        /// </summary>
        public void Connect(int maxRetries = 60, int stabilisationMs = 2000)
        {
            if (!_port.IsOpen)
                _port.Open();

            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            byte[] syncCmd = CommandBuilder.Sync();
            bool gotAck = false;

            for (int i = 0; i < maxRetries; i++)
            {
                int delayMs = 5 + i; // progressive delay per datasheet
                LogMessage($"SYNC attempt {i + 1}/{maxRetries} (delay {delayMs}ms)...");

                Send(syncCmd);
                Thread.Sleep(delayMs);

                // Try to read ACK for SYNC
                CameraResponse? response = TryReadResponse(200);
                if (response == null) continue;

                if (response.Value.IsAck && response.Value.AckedCommandId == CommandId.Sync)
                {
                    gotAck = true;
                    LogMessage("  Got ACK for SYNC");

                    // Camera should also send a SYNC back
                    CameraResponse? syncBack = TryReadResponse(200);
                    if (syncBack != null && syncBack.Value.IsSync)
                    {
                        LogMessage("  Got SYNC from camera");
                    }

                    break;
                }
                else if (response.Value.IsSync)
                {
                    // Sometimes the SYNC comes before the ACK
                    CameraResponse? ackBack = TryReadResponse(200);
                    if (ackBack != null && ackBack.Value.IsAck)
                    {
                        gotAck = true;
                        LogMessage("  Got SYNC+ACK from camera (reversed order)");
                        break;
                    }
                }
            }

            if (!gotAck)
            {
                throw new CameraException(
                    $"Failed to synchronise after {maxRetries} attempts. " +
                    "Check wiring, power supply, and baud rate.");
            }

            // Send ACK to finalise synchronisation
            Send(CommandBuilder.Ack(CommandId.Sync));
            _synced = true;

            // Wait for AGC/AEC stabilisation (1-2 seconds per datasheet)
            LogMessage($"Waiting {stabilisationMs}ms for camera AGC/AEC stabilisation...");
            Thread.Sleep(stabilisationMs);

            LogMessage("Camera synchronised and ready.");
        }

        /// <summary>Disconnect and close the serial port.</summary>
        public void Disconnect()
        {
            _synced = false;
            if (_port.IsOpen)
            {
                try { _port.DiscardInBuffer(); } catch { }
                try { _port.DiscardOutBuffer(); } catch { }
                _port.Close();
            }
        }

        // ── JPEG Capture ─────────────────────────────────────────────

        /// <summary>
        /// Capture a JPEG snapshot at the given resolution and return the image bytes.
        /// Protocol flow (validated against nsstc-uae, ScruffR, and kristianharge references):
        ///   1. Reset state machine (for repeat captures)
        ///   2. INITIAL (JPEG, resolution)
        ///   3. SET PACKAGE SIZE
        ///   4. SNAPSHOT (compressed) + 1s delay
        ///   5. GET PICTURE (with retry)
        ///   6. Receive DATA response with image length
        ///   7. Receive packages, ACK each one
        ///   8. Send final ACK with package ID 0xF0F0
        /// </summary>
        public byte[] CaptureJpeg(JpegResolution resolution = JpegResolution.Res640x480, ushort skipFrames = 0)
            => CaptureJpegProfiled(resolution, skipFrames, 0).ImageData;

        /// <summary>
        /// Capture JPEG and return timestamped phase markers for power profiling.
        /// </summary>
        public CaptureProfileResult CaptureJpegProfiled(
            JpegResolution resolution = JpegResolution.Res640x480,
            ushort skipFrames = 0,
            int dwellMsBetweenSteps = 0)
        {
            var profile = new List<PowerProfilePoint>();

            void Mark(string step)
            {
                profile.Add(new PowerProfilePoint(DateTimeOffset.UtcNow, step));
                LogMessage($"[PWR] {step}");
            }

            void Dwell()
            {
                if (dwellMsBetweenSteps > 0)
                    Thread.Sleep(dwellMsBetweenSteps);
            }

            Mark("jpeg:start");
            EnsureSynced();
            FlushSerialBuffer();
            Mark("jpeg:buffer_flushed");
            Dwell();

            // 1. Reset state machine so repeated captures work
            //    Without this, the camera stays in "picture sent" state
            //    and subsequent INITIAL/SNAPSHOT commands fail.
            ResetStateMachine();
            Mark("jpeg:state_reset");
            Dwell();

            // 2. INITIAL – configure JPEG mode
            SendAndExpectAck(
                CommandBuilder.Initial(ImageFormat.Jpeg, jpegRes: resolution),
                CommandId.Initial, "INITIAL (JPEG)");
            Mark("jpeg:initial_ack");
            Dwell();

            // 3. SET PACKAGE SIZE
            SendAndExpectAck(
                CommandBuilder.SetPackageSize(PackageSize),
                CommandId.SetPackageSize, "SET PACKAGE SIZE");
            Mark("jpeg:set_package_size_ack");
            Dwell();

            // 4. SNAPSHOT – capture a frame
            //    Per nsstc-uae: sleep(1) after snapshot before GET_PICTURE
            SendAndExpectAck(
                CommandBuilder.Snapshot(SnapshotType.Compressed, skipFrames),
                CommandId.Snapshot, "SNAPSHOT");
            Mark("jpeg:snapshot_ack");
            Thread.Sleep(1000);
            Mark("jpeg:snapshot_settled");
            Dwell();

            // 5. GET PICTURE with retry (per nsstc-uae reference)
            CameraResponse ack = default;
            bool gotAck = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Send(CommandBuilder.GetPicture(PictureType.Snapshot));
                Thread.Sleep(100);

                ack = ReadResponseWithTimeout(5000);
                if (ack.IsAck && ack.AckedCommandId == CommandId.GetPicture)
                {
                    gotAck = true;
                    break;
                }
                LogMessage($"  GET PICTURE attempt {attempt + 1} got: {ack}, retrying...");
                Thread.Sleep(100);
            }

            if (!gotAck)
            {
                if (ack.IsNak)
                    throw new CameraException($"GET PICTURE rejected: {ack.NakErrorCode}", ack.NakErrorCode);
                throw new CameraException($"Expected ACK for GET PICTURE after retries, got: {ack}");
            }
            LogMessage("  GET PICTURE acknowledged");
            Mark("jpeg:get_picture_ack");

            // 6. Receive DATA response
            CameraResponse data = ReadResponse();
            if (!data.IsData)
                throw new CameraException($"Expected DATA response, got: {data}");

            int imageSize = data.ImageDataLength;
            LogMessage($"  Image size: {imageSize} bytes (type: {data.ResponseDataType})");
            Mark($"jpeg:data_header size={imageSize}");
            Dwell();

            // 7. Receive JPEG packages
            int dataPerPackage = PackageSize - 6; // 6 bytes overhead: 2 ID + 2 DataSize + 2 Verify
            int totalPackages = (imageSize + dataPerPackage - 1) / dataPerPackage;
            Mark($"jpeg:transfer_start packages={totalPackages}");

            using (var imageStream = new MemoryStream(imageSize))
            {
                for (int pkgId = 0; pkgId < totalPackages; pkgId++)
                {
                    // Request this package by sending ACK with package ID
                    Send(CommandBuilder.Ack(0x00, 0x00, (ushort)pkgId));

                    // Read the full package
                    byte[] package = ReadPackage(PackageSize);

                    // Parse package header
                    int receivedId = package[0] | (package[1] << 8);
                    int dataSize = package[2] | (package[3] << 8);

                    if (receivedId != pkgId)
                    {
                        LogMessage($"  WARNING: Expected package {pkgId}, got {receivedId}");
                    }

                    // Verify checksum: low byte of sum of all bytes except the last 2
                    int checksumCalc = 0;
                    for (int i = 0; i < 4 + dataSize; i++)
                        checksumCalc += package[i];

                    byte expectedVerify = (byte)(checksumCalc & 0xFF);
                    byte actualVerify = package[4 + dataSize];

                    if (expectedVerify != actualVerify)
                    {
                        LogMessage($"  WARNING: Package {pkgId} checksum mismatch: expected 0x{expectedVerify:X2}, got 0x{actualVerify:X2}");
                    }

                    // Extract image data (after 4-byte header)
                    imageStream.Write(package, 4, dataSize);

                    if ((pkgId + 1) % 50 == 0 || pkgId == totalPackages - 1)
                        LogMessage($"  Package {pkgId + 1}/{totalPackages} received ({imageStream.Length}/{imageSize} bytes)");
                }
                Mark($"jpeg:transfer_done bytes={imageStream.Length}");

                // 7. Send final ACK with F0F0 to end transfer
                Send(CommandBuilder.Ack(0x00, 0x00, 0xF0F0));
                Mark("jpeg:final_ack_sent");
                LogMessage($"JPEG capture complete: {imageStream.Length} bytes");
                Mark("jpeg:complete");

                return new CaptureProfileResult(imageStream.ToArray(), profile);
            }
        }

        // ── RAW Capture ──────────────────────────────────────────────

        /// <summary>
        /// Capture a RAW image. RAW data is sent as a continuous byte stream (no packages).
        /// </summary>
        public byte[] CaptureRaw(ImageFormat format, RawResolution resolution)
            => CaptureRawProfiled(format, resolution, 0).ImageData;

        /// <summary>
        /// Capture RAW image and return timestamped phase markers for power profiling.
        /// </summary>
        public CaptureProfileResult CaptureRawProfiled(ImageFormat format, RawResolution resolution, int dwellMsBetweenSteps = 0)
        {
            var profile = new List<PowerProfilePoint>();

            void Mark(string step)
            {
                profile.Add(new PowerProfilePoint(DateTimeOffset.UtcNow, step));
                LogMessage($"[PWR] {step}");
            }

            void Dwell()
            {
                if (dwellMsBetweenSteps > 0)
                    Thread.Sleep(dwellMsBetweenSteps);
            }

            Mark("raw:start");
            EnsureSynced();
            FlushSerialBuffer();
            Mark("raw:buffer_flushed");
            Dwell();

            if (format == ImageFormat.Jpeg)
                throw new ArgumentException("Use CaptureJpeg() for JPEG format.");

            // Reset state machine for repeat captures
            ResetStateMachine();
            Mark("raw:state_reset");
            Dwell();

            // INITIAL – configure RAW mode
            SendAndExpectAck(
                CommandBuilder.Initial(format, rawRes: resolution),
                CommandId.Initial, "INITIAL (RAW)");
            Mark("raw:initial_ack");
            Dwell();

            // SNAPSHOT (uncompressed for RAW) + delay
            // Per nsstc-uae reference: snapshot with param1=0x01 for uncompressed
            SendAndExpectAck(
                CommandBuilder.Snapshot(SnapshotType.Uncompressed),
                CommandId.Snapshot, "SNAPSHOT (RAW)");
            Mark("raw:snapshot_ack");
            Thread.Sleep(1000);
            Mark("raw:snapshot_settled");
            Dwell();

            // GET PICTURE (RAW) with retry
            CameraResponse ack = default;
            bool gotAck = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Send(CommandBuilder.GetPicture(PictureType.Raw));
                Thread.Sleep(100);

                ack = ReadResponseWithTimeout(5000);
                if (ack.IsAck && ack.AckedCommandId == CommandId.GetPicture)
                {
                    gotAck = true;
                    break;
                }
                LogMessage($"  GET PICTURE (RAW) attempt {attempt + 1} got: {ack}, retrying...");
                Thread.Sleep(100);
            }

            if (!gotAck)
                throw new CameraException($"Expected ACK for GET PICTURE (RAW), got: {ack}");
            Mark("raw:get_picture_ack");

            // Receive DATA response with size
            CameraResponse data = ReadResponse();
            if (!data.IsData)
                throw new CameraException($"Expected DATA response, got: {data}");

            int imageSize = data.ImageDataLength;
            LogMessage($"  RAW image size: {imageSize} bytes");
            Mark($"raw:data_header size={imageSize}");
            Dwell();

            // Read continuous stream
            Mark("raw:transfer_start");
            byte[] imageData = ReadBytes(imageSize);
            Mark($"raw:transfer_done bytes={imageData.Length}");

            // Send final ACK to tell camera RAW transfer is complete.
            // Per nsstc-uae: ACK(DATA, 0x00, packageId=0x0001)
            // Per ScruffR:  sendCmd(ACK, DATA, 0x00, 0x01, 0x00)
            // Without this, the camera stays in "sending data" state
            // and subsequent captures fail.
            Send(CommandBuilder.Ack(CommandId.Data, 0x00, 0x0001));
            Mark("raw:final_ack_sent");
            LogMessage($"RAW capture complete: {imageData.Length} bytes");
            Mark("raw:complete");

            return new CaptureProfileResult(imageData, profile);
        }

        /// <summary>
        /// Calculate expected RAW image byte count based on format and resolution.
        /// </summary>
        public static int GetRawImageSize(ImageFormat format, RawResolution resolution)
        {
            int bpp;
            switch (format)
            {
                case ImageFormat.GrayScale8Bit: bpp = 1; break;
                case ImageFormat.ColorCrYCbY: bpp = 2; break;
                case ImageFormat.ColorRgb565: bpp = 2; break;
                default: throw new ArgumentException($"Not a RAW format: {format}");
            }

            int w, h;
            switch (resolution)
            {
                case RawResolution.Res80x60: w = 80; h = 60; break;
                case RawResolution.Res160x120: w = 160; h = 120; break;
                case RawResolution.Res128x128: w = 128; h = 128; break;
                case RawResolution.Res128x96: w = 128; h = 96; break;
                default: throw new ArgumentException($"Unknown resolution: {resolution}");
            }

            return w * h * bpp;
        }

        // ── Manual Step Control ─────────────────────────────────────

        public void FlushInputBuffer()
        {
            FlushSerialBuffer();
        }

        public void ResetStateMachineOnly()
        {
            EnsureSynced();
            ResetStateMachine();
        }

        public void InitializeJpeg(JpegResolution resolution)
        {
            EnsureSynced();
            SendAndExpectAck(
                CommandBuilder.Initial(ImageFormat.Jpeg, jpegRes: resolution),
                CommandId.Initial, "INITIAL (JPEG)");
        }

        public void InitializeRaw(ImageFormat format, RawResolution resolution)
        {
            EnsureSynced();
            if (format == ImageFormat.Jpeg)
                throw new ArgumentException("Use InitializeJpeg() for JPEG mode.");

            SendAndExpectAck(
                CommandBuilder.Initial(format, rawRes: resolution),
                CommandId.Initial, "INITIAL (RAW)");
        }

        public void ConfigurePackageSize(ushort size = 512)
        {
            EnsureSynced();
            PackageSize = size;
            SendAndExpectAck(
                CommandBuilder.SetPackageSize(size),
                CommandId.SetPackageSize, "SET PACKAGE SIZE");
        }

        public void SnapshotCompressed(ushort skipFrames = 0)
        {
            EnsureSynced();
            SendAndExpectAck(
                CommandBuilder.Snapshot(SnapshotType.Compressed, skipFrames),
                CommandId.Snapshot, "SNAPSHOT");
        }

        public void SnapshotUncompressed()
        {
            EnsureSynced();
            SendAndExpectAck(
                CommandBuilder.Snapshot(SnapshotType.Uncompressed),
                CommandId.Snapshot, "SNAPSHOT (RAW)");
        }

        public CameraResponse BeginPictureTransfer(PictureType type, int retries = 5, int ackTimeoutMs = 5000)
        {
            EnsureSynced();

            CameraResponse ack = default;
            bool gotAck = false;
            for (int attempt = 0; attempt < retries; attempt++)
            {
                Send(CommandBuilder.GetPicture(type));
                Thread.Sleep(100);

                ack = ReadResponseWithTimeout(ackTimeoutMs);
                if (ack.IsAck && ack.AckedCommandId == CommandId.GetPicture)
                {
                    gotAck = true;
                    break;
                }

                LogMessage($"  GET PICTURE ({type}) attempt {attempt + 1} got: {ack}, retrying...");
                Thread.Sleep(100);
            }

            if (!gotAck)
            {
                if (ack.IsNak)
                    throw new CameraException($"GET PICTURE rejected: {ack.NakErrorCode}", ack.NakErrorCode);
                throw new CameraException($"Expected ACK for GET PICTURE, got: {ack}");
            }

            LogMessage($"  GET PICTURE ({type}) acknowledged");

            CameraResponse data = ReadResponse();
            if (!data.IsData)
                throw new CameraException($"Expected DATA response, got: {data}");

            LogMessage($"  DATA header: type={data.ResponseDataType}, size={data.ImageDataLength} bytes");
            return data;
        }

        public JpegTransferPackage ReadJpegTransferPackage(ushort packageId)
        {
            EnsureSynced();

            Send(CommandBuilder.Ack(0x00, 0x00, packageId));
            byte[] package = ReadPackage(PackageSize);

            int receivedId = package[0] | (package[1] << 8);
            int dataSize = package[2] | (package[3] << 8);

            int checksumCalc = 0;
            for (int i = 0; i < 4 + dataSize; i++)
                checksumCalc += package[i];

            byte expectedVerify = (byte)(checksumCalc & 0xFF);
            byte actualVerify = package[4 + dataSize];
            bool checksumOk = expectedVerify == actualVerify;

            byte[] payload = new byte[dataSize];
            Array.Copy(package, 4, payload, 0, dataSize);

            if (!checksumOk)
                LogMessage($"  WARNING: Package {receivedId} checksum mismatch: expected 0x{expectedVerify:X2}, got 0x{actualVerify:X2}");

            return new JpegTransferPackage(packageId, receivedId, dataSize, checksumOk, payload);
        }

        public byte[] ReadRawTransferChunk(int byteCount)
        {
            EnsureSynced();
            if (byteCount < 0)
                throw new ArgumentException("byteCount must be >= 0");
            return ReadBytes(byteCount);
        }

        public void FinishJpegTransfer()
        {
            EnsureSynced();
            Send(CommandBuilder.Ack(0x00, 0x00, 0xF0F0));
            LogMessage("JPEG final ACK sent");
        }

        public void FinishRawTransfer()
        {
            EnsureSynced();
            Send(CommandBuilder.Ack(CommandId.Data, 0x00, 0x0001));
            LogMessage("RAW final ACK sent");
        }

        public void Delay(int milliseconds)
        {
            if (milliseconds < 0)
                throw new ArgumentException("Delay must be >= 0 ms");
            Thread.Sleep(milliseconds);
        }

        // ── Camera Settings ──────────────────────────────────────────

        public void SetContrastBrightnessExposure(
            CameraLevel contrast = CameraLevel.Normal,
            CameraLevel brightness = CameraLevel.Normal,
            CameraLevel exposure = CameraLevel.Normal)
        {
            EnsureSynced();
            SendAndExpectAck(
                CommandBuilder.ContrastBrightnessExposure(contrast, brightness, exposure),
                CommandId.ContrastBrightnessExposure, "CBE");
        }

        public void SetLightFrequency(LightFrequency freq)
        {
            EnsureSynced();
            SendAndExpectAck(
                CommandBuilder.Light(freq),
                CommandId.Light, "LIGHT");
        }

        public void SetSleepTimeout(byte seconds)
        {
            EnsureSynced();
            SendAndExpectAck(
                CommandBuilder.Sleep(seconds),
                CommandId.Sleep, "SLEEP");
        }

        public void SetBaudRate(int baudRate)
        {
            EnsureSynced();

            var dividers = CommandBuilder.GetBaudDividers(baudRate);
            if (dividers == null)
                throw new ArgumentException($"Unsupported baud rate: {baudRate}");

            SendAndExpectAck(
                CommandBuilder.SetBaudRate(dividers.Value.Div1, dividers.Value.Div2),
                CommandId.SetBaudRate, "SET BAUD RATE");

            // Update local port to match
            Thread.Sleep(50);
            _port.BaudRate = baudRate;
            LogMessage($"Baud rate changed to {baudRate}");
        }

        public void SoftReset(ResetType type = ResetType.Full)
        {
            EnsureSynced();

            Send(CommandBuilder.Reset(type));

            if (type == ResetType.Full)
            {
                _synced = false;
                LogMessage("Full reset sent. Re-sync required.");
            }
            else
            {
                CameraResponse? resp = TryReadResponse(500);
                if (resp != null && resp.Value.IsAck)
                    LogMessage("State machine reset acknowledged.");
                else
                    LogMessage("State machine reset sent (no ACK received).");
            }
        }

        // ── Low-Level I/O ────────────────────────────────────────────

        /// <summary>
        /// Flush any stale bytes from the serial receive buffer.
        /// Called before each capture sequence to start clean.
        /// </summary>
        private void FlushSerialBuffer()
        {
            if (_port.IsOpen && _port.BytesToRead > 0)
            {
                int stale = _port.BytesToRead;
                _port.DiscardInBuffer();
                LogMessage($"  Flushed {stale} stale byte(s) from serial buffer");
            }
        }

        /// <summary>
        /// Reset the camera's internal state machine so it can accept
        /// a new INITIAL/SNAPSHOT/GET_PICTURE sequence.
        /// All reference implementations do this between captures:
        ///   - ScruffR: hardReset() + sync() before every capture
        ///   - nsstc-uae: only supports one capture per sync cycle
        /// We use the lighter-weight state-machine-only reset (0x01)
        /// to avoid a full re-sync.
        /// </summary>
        private void ResetStateMachine()
        {
            Send(CommandBuilder.Reset(ResetType.StateMachineOnly));
            CameraResponse? resp = TryReadResponse(1000);
            if (resp != null && resp.Value.IsAck)
                LogMessage("  State machine reset OK");
            else
                LogMessage("  State machine reset sent (no ACK — first capture)");
            Thread.Sleep(100);
        }

        private void Send(byte[] packet)
        {
            _port.Write(packet, 0, packet.Length);
        }

        private CameraResponse ReadResponse()
        {
            byte[] buf = ReadBytes(CameraResponse.PacketLength);
            return new CameraResponse(buf);
        }

        /// <summary>
        /// Read a response with a custom timeout (e.g. for slow operations like GET PICTURE after compression).
        /// </summary>
        private CameraResponse ReadResponseWithTimeout(int timeoutMs)
        {
            int oldTimeout = _port.ReadTimeout;
            _port.ReadTimeout = timeoutMs;
            try
            {
                return ReadResponse();
            }
            finally
            {
                _port.ReadTimeout = oldTimeout;
            }
        }

        private CameraResponse? TryReadResponse(int timeoutMs)
        {
            int oldTimeout = _port.ReadTimeout;
            _port.ReadTimeout = timeoutMs;
            try
            {
                byte[] buf = ReadBytes(CameraResponse.PacketLength);
                return new CameraResponse(buf);
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                _port.ReadTimeout = oldTimeout;
            }
        }

        private byte[] ReadBytes(int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _port.Read(buf, offset, count - offset);
                if (read <= 0)
                    throw new CameraException($"Serial read returned {read} bytes, expected more.");
                offset += read;
            }
            return buf;
        }

        /// <summary>
        /// Read a JPEG data package.
        /// Package layout: [ID_lo][ID_hi][Size_lo][Size_hi][...data...][Verify_lo][Verify_hi]
        /// </summary>
        private byte[] ReadPackage(int maxSize)
        {
            // Read first 4 bytes (ID + DataSize)
            byte[] header = ReadBytes(4);
            int dataSize = header[2] | (header[3] << 8);

            // Read data + 2-byte verify code
            int remaining = dataSize + 2;
            byte[] body = ReadBytes(remaining);

            // Combine into full package
            byte[] package = new byte[4 + remaining];
            Array.Copy(header, 0, package, 0, 4);
            Array.Copy(body, 0, package, 4, remaining);

            return package;
        }

        private void SendAndExpectAck(byte[] command, byte expectedCmdId, string description)
        {
            Send(command);
            CameraResponse resp = ReadResponse();

            if (resp.IsNak)
                throw new CameraException($"{description} failed", resp.NakErrorCode);

            if (!resp.IsAck || resp.AckedCommandId != expectedCmdId)
                throw new CameraException($"{description}: expected ACK(0x{expectedCmdId:X2}), got: {resp}");

            LogMessage($"  {description} acknowledged");
        }

        private void EnsureSynced()
        {
            if (!_synced)
                throw new CameraException("Camera is not synchronised. Call Connect() first.");
        }

        private void LogMessage(string message) => Log?.Invoke(message);

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
                _port.Dispose();
            }
        }
    }
}
