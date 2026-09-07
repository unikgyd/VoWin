using System.Runtime.InteropServices;
using System.Text;
using VoSharp.Common.Utils;

namespace VoSharp.Sim.Pcsc;

public static partial class WinSCard
{
    public const uint SCARD_SCOPE_USER = 0;
    public const uint SCARD_SHARE_SHARED = 2;
    public const uint SCARD_PROTOCOL_T0 = 1;
    public const uint SCARD_PROTOCOL_T1 = 2;
    public const uint SCARD_LEAVE_CARD = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SCARD_IO_REQUEST
    {
        public uint dwProtocol;
        public uint cbPciLength;
    }

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardReleaseContext(IntPtr hContext);

    [DllImport("winscard.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int SCardListReaders(IntPtr hContext, string? mszGroups, byte[]? mszReaders, ref uint pcchReaders);

    [DllImport("winscard.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int SCardConnect(IntPtr hContext, string szReader, uint dwShareMode, uint dwPreferredProtocols, out IntPtr phCard, out uint pdwActiveProtocol);

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardDisconnect(IntPtr hCard, uint dwDisposition);

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, uint cbSendLength, IntPtr pioRecvPci, byte[] pbRecvBuffer, ref uint pcbRecvLength);
}

public class PcscReader : IDisposable
{
    private IntPtr _context = IntPtr.Zero;
    private IntPtr _card = IntPtr.Zero;
    private uint _activeProtocol;

    public bool Initialize()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return false;

        int res = WinSCard.SCardEstablishContext(WinSCard.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out _context);
        return res == 0;
    }

    public string[] ListReaders()
    {
        if (_context == IntPtr.Zero && !Initialize())
            return Array.Empty<string>();

        uint len = 0;
        int res = WinSCard.SCardListReaders(_context, null, null, ref len);
        if (res != 0 || len == 0)
            return Array.Empty<string>();

        var buffer = new byte[len * 2];
        res = WinSCard.SCardListReaders(_context, null, buffer, ref len);
        if (res != 0)
            return Array.Empty<string>();

        var str = Encoding.Unicode.GetString(buffer, 0, (int)len * 2);
        return str.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool Connect(string readerName)
    {
        if (_context == IntPtr.Zero && !Initialize())
            return false;

        int res = WinSCard.SCardConnect(
            _context,
            readerName,
            WinSCard.SCARD_SHARE_SHARED,
            WinSCard.SCARD_PROTOCOL_T0 | WinSCard.SCARD_PROTOCOL_T1,
            out _card,
            out _activeProtocol
        );
        return res == 0;
    }

    public byte[]? TransmitApdu(byte[] apdu)
    {
        if (_card == IntPtr.Zero)
            return null;

        var ioRequest = new WinSCard.SCARD_IO_REQUEST
        {
            dwProtocol = _activeProtocol,
            cbPciLength = (uint)Marshal.SizeOf<WinSCard.SCARD_IO_REQUEST>()
        };

        var recvBuffer = new byte[512];
        uint recvLen = (uint)recvBuffer.Length;

        int res = WinSCard.SCardTransmit(
            _card,
            ref ioRequest,
            apdu,
            (uint)apdu.Length,
            IntPtr.Zero,
            recvBuffer,
            ref recvLen
        );

        if (res != 0) return null;

        var result = new byte[recvLen];
        Array.Copy(recvBuffer, result, recvLen);
        return result;
    }

    public void Disconnect()
    {
        if (_card != IntPtr.Zero)
        {
            WinSCard.SCardDisconnect(_card, WinSCard.SCARD_LEAVE_CARD);
            _card = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Disconnect();
        if (_context != IntPtr.Zero)
        {
            WinSCard.SCardReleaseContext(_context);
            _context = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
