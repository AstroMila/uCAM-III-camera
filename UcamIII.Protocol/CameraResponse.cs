using System;

namespace UcamIII.Protocol
{
    /// <summary>
    /// Parsed response from the uCAM-III. Every response is exactly 6 bytes.
    /// </summary>
    public readonly struct CameraResponse
    {
        public const int PacketLength = 6;

        /// <summary>Raw 6-byte packet.</summary>
        public byte[] Raw { get; }

        public byte Prefix => Raw[0];
        public byte CommandId => Raw[1];
        public byte Param1 => Raw[2];
        public byte Param2 => Raw[3];
        public byte Param3 => Raw[4];
        public byte Param4 => Raw[5];

        public CameraResponse(byte[] raw)
        {
            if (raw.Length != PacketLength)
                throw new ArgumentException($"Expected {PacketLength} bytes, got {raw.Length}");
            Raw = raw;
        }

        public bool IsValid => Prefix == Protocol.CommandId.Prefix;
        public bool IsAck => IsValid && CommandId == Protocol.CommandId.Ack;
        public bool IsNak => IsValid && CommandId == Protocol.CommandId.Nak;
        public bool IsSync => IsValid && CommandId == Protocol.CommandId.Sync;
        public bool IsData => IsValid && CommandId == Protocol.CommandId.Data;

        /// <summary>For ACK packets, the command ID being acknowledged.</summary>
        public byte AckedCommandId => Param1;

        /// <summary>For NAK packets, the error code.</summary>
        public NakError NakErrorCode => (NakError)Param3;

        /// <summary>For DATA packets, the data type.</summary>
        public DataType ResponseDataType => (DataType)Param1;

        /// <summary>For DATA packets, the 3-byte image data length.</summary>
        public int ImageDataLength => Param2 | (Param3 << 8) | (Param4 << 16);

        public override string ToString()
        {
            string hex = BitConverter.ToString(Raw).Replace("-", " ");
            if (IsAck) return $"ACK(cmd=0x{AckedCommandId:X2}) [{hex}]";
            if (IsNak) return $"NAK(err={NakErrorCode}) [{hex}]";
            if (IsSync) return $"SYNC [{hex}]";
            if (IsData) return $"DATA(type={ResponseDataType}, len={ImageDataLength}) [{hex}]";
            return $"RESPONSE [{hex}]";
        }
    }
}
