using System;

namespace UcamIII.Protocol
{
    /// <summary>
    /// Thrown when the camera returns a NAK or an unexpected response.
    /// </summary>
    public class CameraException : Exception
    {
        public NakError? ErrorCode { get; }

        public CameraException(string message) : base(message) { }

        public CameraException(string message, NakError errorCode)
            : base($"{message} (NAK error: {errorCode})")
        {
            ErrorCode = errorCode;
        }

        public CameraException(string message, Exception inner) : base(message, inner) { }
    }
}
