namespace qmiSharp.Core;

public enum QmiResultCode : ushort
{
    Success = 0x0000,
    Failure = 0x0001
}

public enum QmiErrorCode : ushort
{
    None = 0x0000,
    MalformedMsg = 0x0001,
    NoMemory = 0x0002,
    Internal = 0x0003,
    InvalidArg = 0x0004,
    DeviceNotReady = 0x0005,
    NetworkNotReady = 0x0006,
    NoThresholds = 0x0008,
    CallFailed = 0x000E,
    OutOfCall = 0x000F,
    InvalidProfile = 0x0019,
    NoEffect = 0x001A,
    ClientIdsExhausted = 0x001F,
    InvalidRegisterAction = 0x0020,
    InvalidId = 0x0029,
    CardCallControlRefFail = 0x0030,
    OpDeviceUnsupported = 0x0034,
    InvalidQmiCmd = 0x0047,
    PolicyMismatch = 0x004A,
    ExtendedInternal = 0x0051,
    IncompatibleState = 0x005A,
    NotSupported = 0x005E
}

public class QmiException : Exception
{
    public QmiServiceType Service { get; }
    public ushort MessageId { get; }
    public QmiResultCode Result { get; }
    public QmiErrorCode ErrorCode { get; }

    public QmiException(QmiServiceType service, ushort messageId, QmiResultCode result, QmiErrorCode errorCode)
        : base($"QMI error: Service={service} (0x{(ushort)service:X2}), MsgId=0x{messageId:X4}, Result={result}, Error={errorCode} (0x{(ushort)errorCode:X4})")
    {
        Service = service;
        MessageId = messageId;
        Result = result;
        ErrorCode = errorCode;
    }

    public QmiException(string message) : base(message)
    {
    }

    public QmiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
