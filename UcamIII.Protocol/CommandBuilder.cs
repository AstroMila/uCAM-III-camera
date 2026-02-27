namespace UcamIII.Protocol
{
    /// <summary>
    /// Builds 6-byte command packets for the uCAM-III protocol.
    /// Every command is exactly 6 bytes: [0xAA] [CmdID] [P1] [P2] [P3] [P4]
    /// </summary>
    public static class CommandBuilder
    {
        public static byte[] Build(byte commandId, byte p1 = 0, byte p2 = 0, byte p3 = 0, byte p4 = 0)
        {
            return new byte[] { CommandId.Prefix, commandId, p1, p2, p3, p4 };
        }

        public static byte[] Sync() => Build(CommandId.Sync);

        public static byte[] Ack(byte commandId, byte counter = 0, ushort packageId = 0)
        {
            return Build(CommandId.Ack, commandId, counter,
                (byte)(packageId & 0xFF), (byte)((packageId >> 8) & 0xFF));
        }

        public static byte[] Initial(ImageFormat format, RawResolution rawRes = 0, JpegResolution jpegRes = 0)
        {
            // Per datasheet INITIAL command:
            //   P1 = 0x00
            //   P2 = Image Format (Color Type)
            //   P3 = Raw Resolution (only used when format is RAW)
            //   P4 = JPEG Resolution (only used when format is JPEG)
            return Build(CommandId.Initial, 0x00, (byte)format, (byte)rawRes, (byte)jpegRes);
        }

        public static byte[] SetPackageSize(ushort size = 512)
        {
            return Build(CommandId.SetPackageSize, 0x08,
                (byte)(size & 0xFF), (byte)((size >> 8) & 0xFF));
        }

        public static byte[] Snapshot(SnapshotType type = SnapshotType.Compressed, ushort skipFrames = 0)
        {
            return Build(CommandId.Snapshot, (byte)type,
                (byte)(skipFrames & 0xFF), (byte)((skipFrames >> 8) & 0xFF));
        }

        public static byte[] GetPicture(PictureType type)
        {
            return Build(CommandId.GetPicture, (byte)type);
        }

        public static byte[] SetBaudRate(byte divider1, byte divider2)
        {
            return Build(CommandId.SetBaudRate, divider1, divider2);
        }

        public static byte[] Reset(ResetType type = ResetType.Full)
        {
            return Build(CommandId.Reset, (byte)type);
        }

        public static byte[] ResetImmediate()
        {
            return Build(CommandId.Reset, 0x00, 0x00, 0x00, 0xFF);
        }

        public static byte[] Light(LightFrequency freq)
        {
            return Build(CommandId.Light, (byte)freq);
        }

        public static byte[] ContrastBrightnessExposure(
            CameraLevel contrast = CameraLevel.Normal,
            CameraLevel brightness = CameraLevel.Normal,
            CameraLevel exposure = CameraLevel.Normal)
        {
            return Build(CommandId.ContrastBrightnessExposure,
                (byte)contrast, (byte)brightness, (byte)exposure);
        }

        public static byte[] Sleep(byte timeoutSeconds = 0)
        {
            return Build(CommandId.Sleep, timeoutSeconds);
        }

        /// <summary>
        /// Known baud-rate divider pairs for SET BAUD RATE command.
        /// Returns null if the baud rate is not in the table.
        /// </summary>
        public static (byte Div1, byte Div2)? GetBaudDividers(int baudRate)
        {
            switch (baudRate)
            {
                case 2400: return (0x1F, 0x2F);
                case 4800: return (0x1F, 0x17);
                case 9600: return (0x1F, 0x0B);
                case 19200: return (0x1F, 0x05);
                case 38400: return (0x1F, 0x02);
                case 57600: return (0x1F, 0x01);
                case 115200: return (0x1F, 0x00);
                case 153600: return (0x07, 0x02);
                case 230400: return (0x07, 0x01);
                case 460800: return (0x07, 0x00);
                case 921600: return (0x01, 0x01);
                case 1228800: return (0x02, 0x00);
                case 1843200: return (0x01, 0x00);
                default: return null;
            }
        }
    }
}
