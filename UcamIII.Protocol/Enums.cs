namespace UcamIII.Protocol
{
    /// <summary>
    /// uCAM-III command IDs. Each command is a 6-byte packet starting with 0xAA followed by the command ID.
    /// </summary>
    public static class CommandId
    {
        public const byte Prefix = 0xAA;

        public const byte Initial = 0x01;
        public const byte GetPicture = 0x04;
        public const byte Snapshot = 0x05;
        public const byte SetPackageSize = 0x06;
        public const byte SetBaudRate = 0x07;
        public const byte Reset = 0x08;
        public const byte Data = 0x0A;
        public const byte Sync = 0x0D;
        public const byte Ack = 0x0E;
        public const byte Nak = 0x0F;
        public const byte Light = 0x13;
        public const byte ContrastBrightnessExposure = 0x14;
        public const byte Sleep = 0x15;
    }

    public enum ImageFormat : byte
    {
        GrayScale8Bit = 0x03,
        ColorCrYCbY = 0x08,
        ColorRgb565 = 0x06,
        Jpeg = 0x07,
    }

    public enum RawResolution : byte
    {
        Res80x60 = 0x01,
        Res160x120 = 0x03,
        Res128x128 = 0x09,
        Res128x96 = 0x0B,
    }

    /// <summary>
    /// Valid JPEG resolutions per datasheet. Only 3 sizes supported for JPEG.
    /// (128x96 and 128x128 are RAW-only resolutions.)
    /// </summary>
    public enum JpegResolution : byte
    {
        Res160x128 = 0x03,
        Res320x240 = 0x05,
        Res640x480 = 0x07,
    }

    public enum PictureType : byte
    {
        Snapshot = 0x01,
        Raw = 0x02,
        Jpeg = 0x05,
    }

    public enum SnapshotType : byte
    {
        Compressed = 0x00,
        Uncompressed = 0x01,
    }

    public enum ResetType : byte
    {
        Full = 0x00,
        StateMachineOnly = 0x01,
    }

    public enum LightFrequency : byte
    {
        Hz50 = 0x00,
        Hz60 = 0x01,
    }

    public enum CameraLevel : byte
    {
        Min = 0x00,
        Low = 0x01,
        Normal = 0x02,
        High = 0x03,
        Max = 0x04,
    }

    public enum DataType : byte
    {
        Snapshot = 0x01,
        Raw = 0x02,
        Jpeg = 0x05,
    }

    public enum NakError : byte
    {
        PictureTypeError = 0x01,
        PictureUpScale = 0x02,
        PictureScaleError = 0x03,
        UnexpectedReply = 0x04,
        SendPictureTimeout = 0x05,
        UnexpectedCommand = 0x06,
        SramJpegTypeError = 0x07,
        SramJpegSizeError = 0x08,
        PictureFormatError = 0x09,
        PictureSizeError = 0x0A,
        ParameterError = 0x0B,
        SendRegisterTimeout = 0x0C,
        CommandIdError = 0x0D,
        PictureNotReady = 0x0F,
        TransferPackageNumberError = 0x10,
        SetTransferPackageSizeWrong = 0x11,
        CommandHeaderError = 0xF0,
        CommandLengthError = 0xF1,
        SendPictureError = 0xF5,
        SendCommandError = 0xFF,
    }
}
