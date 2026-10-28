using System.Runtime.InteropServices;
using System.Reflection;

namespace VoSharp.Telephony.Audio;

/// <summary>
/// Hardware/Native accelerated real-time AMR-NB (and AMR-WB) codec using OpenCORE-AMR.
/// Performs sub-millisecond per-frame encoding and decoding for bi-directional voice calls.
/// </summary>
public sealed class AmrNativeCodec : IDisposable
{
    private static bool _dllResolverRegistered;
    private static readonly object _resolverLock = new();
    private static string? _embeddedLibraryPath;

    static AmrNativeCodec()
    {
        EnsureDllResolver();
    }

    private static void EnsureDllResolver()
    {
        if (_dllResolverRegistered) return;
        lock (_resolverLock)
        {
            if (_dllResolverRegistered) return;

            NativeLibrary.SetDllImportResolver(typeof(AmrNativeCodec).Assembly, (libName, assembly, searchPath) =>
            {
                if (libName.Equals("opencore-amrwb", StringComparison.OrdinalIgnoreCase) ||
                    libName.Equals("opencore-amr", StringComparison.OrdinalIgnoreCase) ||
                    libName.Equals("opencore-amr.dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        // Linux distributions ship narrowband and wideband as
                        // separate SONAMEs. Never try/extract the Windows PE DLL.
                        foreach (var name in GetUnixLibraryNames(libName.Equals("opencore-amrwb", StringComparison.OrdinalIgnoreCase), OperatingSystem.IsMacOS()))
                            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var unixHandle)) return unixHandle;
                        return IntPtr.Zero;
                    }
                    string[] candidates =
                    [
                        ExtractEmbeddedLibrary(),
                        Path.Combine(AppContext.BaseDirectory, "opencore-amr.dll"),
                        Path.Combine(AppContext.BaseDirectory, "native", "opencore-amr.dll"),
                        Path.Combine(AppContext.BaseDirectory, "native", "amd64", "opencore-amr.dll"),
                        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "voSharp", "native", "amd64", "opencore-amr.dll"),
                        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "voSharp", "native", "amd64", "opencore-amr.dll")
                    ];

                    foreach (var path in candidates)
                    {
                        if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                        {
                            return handle;
                        }
                    }
                }
                return IntPtr.Zero;
            });

            _dllResolverRegistered = true;
        }
    }

    internal static string[] GetUnixLibraryNames(bool wideband, bool macOS = false) => macOS
        ? [wideband ? "libopencore-amrwb.0.dylib" : "libopencore-amrnb.0.dylib", wideband ? "libopencore-amrwb.dylib" : "libopencore-amrnb.dylib"]
        : [wideband ? "libopencore-amrwb.so.0" : "libopencore-amrnb.so.0", wideband ? "libopencore-amrwb.so" : "libopencore-amrnb.so"];

    // A single-file publish cannot load this codec directly from the bundle. Keep
    // it as an assembly resource and materialize it only for the Windows loader.
    private static string ExtractEmbeddedLibrary()
    {
        if (!string.IsNullOrWhiteSpace(_embeddedLibraryPath)) return _embeddedLibraryPath;

        var assembly = typeof(AmrNativeCodec).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Native.opencore-amr.dll", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return string.Empty;

        var version = assembly.GetName().Version?.ToString() ?? "current";
        var directory = Path.Combine(Path.GetTempPath(), "VoSharp", "native", version);
        var destination = Path.Combine(directory, "opencore-amr.dll");
        Directory.CreateDirectory(directory);
        if (!File.Exists(destination))
        {
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Embedded OpenCORE-AMR resource is unavailable.");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(output);
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { File.Delete(temporary); }
            }
            finally
            {
                if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
            }
        }
        return _embeddedLibraryPath = destination;
    }

    // P/Invoke definitions for opencore-amr.dll
    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Decoder_Interface_init();

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Decoder_Interface_exit(IntPtr state);

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Decoder_Interface_Decode(IntPtr state, byte[] inFrame, short[] outPcm, int bfi);

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Encoder_Interface_init(int dtx);

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Encoder_Interface_exit(IntPtr state);

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Encoder_Interface_Encode(IntPtr state, int mode, short[] inPcm, byte[] outFrame, int forceSpeech);

    [DllImport("opencore-amrwb", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr D_IF_init();

    [DllImport("opencore-amrwb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void D_IF_exit(IntPtr state);

    [DllImport("opencore-amrwb", CallingConvention = CallingConvention.Cdecl)]
    private static extern void D_IF_decode(IntPtr state, byte[] inFrame, short[] outPcm, int bfi);

    private IntPtr _nbDecState = IntPtr.Zero;
    private IntPtr _nbEncState = IntPtr.Zero;
    private IntPtr _wbDecState = IntPtr.Zero;

    private readonly object _decLock = new();
    private readonly object _encLock = new();
    private readonly object _wbDecLock = new();
    private bool _isDisposed;

    public bool IsAvailable { get; private set; }
    public bool IsWidebandAvailable => _wbDecState != IntPtr.Zero && !_isDisposed;

    public AmrNativeCodec(bool initEncoder = true)
    {
        try
        {
            _nbDecState = Decoder_Interface_init();
            if (initEncoder)
            {
                _nbEncState = Encoder_Interface_init(0);
            }
            IsAvailable = _nbDecState != IntPtr.Zero;
        }
        catch
        {
            IsAvailable = false;
            Console.Error.WriteLine("[AmrNativeCodec] AMR-NB native codec unavailable.");
        }
        // Optional wideband support must not disable a working NB decoder/encoder.
        try { _wbDecState = D_IF_init(); }
        catch { Console.Error.WriteLine("[AmrNativeCodec] AMR-WB native codec unavailable; narrowband availability is unchanged."); }
    }

    /// <summary>
    /// Decodes a single standard AMR-NB frame (TOC byte + speech bytes) into 160 PCM samples @ 8000Hz (20ms).
    /// </summary>
    public short[] DecodeAmrNbFrame(byte[] amrFrame, bool bfi = false)
    {
        if (!IsAvailable || _nbDecState == IntPtr.Zero || !AmrNbDecoder.IsStorageFrameValid(amrFrame))
            return new short[160];

        var pcm = new short[160];
        lock (_decLock)
        {
            if (_isDisposed || _nbDecState == IntPtr.Zero) return pcm;
            try
            {
                Decoder_Interface_Decode(_nbDecState, amrFrame.ToArray(), pcm, bfi || (amrFrame[0] & 4) == 0 ? 1 : 0);
            }
            catch
            {
                // Return silence on decode failure
            }
        }
        return pcm;
    }

    /// <summary>
    /// Decodes a single standard AMR-WB frame into 160 PCM samples @ 8000Hz (downsampled from 320 @ 16000Hz).
    /// </summary>
    public short[] DecodeAmrWbFrame(byte[] amrWbFrame, bool bfi = false)
    {
        if (!IsWidebandAvailable || !AmrNbDecoder.IsStorageFrameValid(amrWbFrame, wideband: true))
            return new short[160];

        var pcm16k = new short[320];
        lock (_wbDecLock)
        {
            if (_isDisposed || _wbDecState == IntPtr.Zero) return new short[160];
            try
            {
                D_IF_decode(_wbDecState, amrWbFrame.ToArray(), pcm16k, bfi || (amrWbFrame[0] & 4) == 0 ? 1 : 0);
            }
            catch
            {
                return new short[160];
            }
        }

        // Downsample 16kHz (320 samples) to 8kHz (160 samples)
        var pcm8k = new short[160];
        for (int i = 0; i < 160; i++)
        {
            int s1 = pcm16k[i * 2];
            int s2 = pcm16k[i * 2 + 1];
            pcm8k[i] = (short)((s1 + s2) / 2);
        }
        return pcm8k;
    }

    /// <summary>
    /// Enocdes 160 linear PCM 16-bit samples @ 8000Hz (20ms) into a standard AMR-NB frame.
    /// Mode 7 = 12.2 kbps (standard high-definition VoLTE/VoWiFi), Mode 0 = 4.75 kbps.
    /// </summary>
    public byte[] EncodeAmrNbFrame(short[] pcmSamples, int mode = 7)
    {
        if (!IsAvailable || _nbEncState == IntPtr.Zero || pcmSamples == null || pcmSamples.Length < 160 || mode is < 0 or > 7)
            return Array.Empty<byte>();

        byte[] outBuffer = new byte[64];
        int encodedBytes = 0;
        lock (_encLock)
        {
            if (_isDisposed || _nbEncState == IntPtr.Zero) return Array.Empty<byte>();
            try
            {
                // OpenCORE preprocesses its input in place despite the C
                // wrapper's const signature. Never mutate caller-owned audio.
                encodedBytes = Encoder_Interface_Encode(_nbEncState, mode, pcmSamples[..160], outBuffer, 0);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        if (encodedBytes <= 0)
            return Array.Empty<byte>();

        var result = new byte[encodedBytes];
        Buffer.BlockCopy(outBuffer, 0, result, 0, encodedBytes);
        return result;
    }

    /// <summary>
    /// Converts a raw AMR-NB frame (TOC byte + speech bytes) into an RFC 4867 RTP packet payload.
    /// In bandwidth-efficient mode, compacts the header and speech bits; in octet-aligned mode, prefixes CMR.
    /// </summary>
    public static byte[] AmrFrameToRtpPayload(byte[] amrFrame, bool octetAligned = false)
    {
        if (!AmrNbDecoder.IsStorageFrameValid(amrFrame))
            return Array.Empty<byte>();

        if (octetAligned)
        {
            // Octet-aligned: CMR (0xF0 = no request) + TOC + speech bytes
            var payload = new byte[1 + amrFrame.Length];
            payload[0] = 0xF0; // CMR: 1111 (no mode request), reserved: 0000
            Buffer.BlockCopy(amrFrame, 0, payload, 1, amrFrame.Length);
            payload[1] &= 0x7c;
            ReadOnlySpan<int> bits = [95, 103, 118, 134, 148, 159, 204, 244, 39, -1, -1, -1, -1, -1, -1, 0];
            var alignedSpeechBits = bits[amrFrame[0] >> 3 & 15];
            if (alignedSpeechBits % 8 != 0) payload[^1] &= (byte)(0xff << (8 - alignedSpeechBits % 8));
            return payload;
        }

        // Bandwidth-efficient mode (RFC 4867 §4.3.2):
        // CMR: 4 bits (1111 = 0xF)
        // F: 1 bit (0 = last frame)
        // FT: 4 bits
        // Q: 1 bit
        // Speech bits: packed tightly
        byte toc = amrFrame[0];
        byte ft = (byte)((toc >> 3) & 0x0F);
        byte q = (byte)((toc >> 2) & 0x01);

        // Storage frames pad each speech frame to an octet. BE RTP must not
        // copy that padding: RFC 4867 Table 1 and section 4.3.4 allow only
        // 0..7 padding bits at the end of the complete payload.
        ReadOnlySpan<int> frameBits = [95, 103, 118, 134, 148, 159, 204, 244, 39];
        if (ft is >= 9 and <= 14) return Array.Empty<byte>();
        int speechBits = ft == 15 ? 0 : frameBits[ft];
        if (amrFrame.Length != 1 + (speechBits + 7) / 8) return Array.Empty<byte>();
        int totalPayloadBytes = (10 + speechBits + 7) / 8;
        var outRtp = new byte[totalPayloadBytes];

        // Byte 0: CMR[3..0] (1111) | F (0) | FT[3..1]
        outRtp[0] = (byte)(0xF0 | ((ft >> 1) & 0x07));
        outRtp[1] = (byte)(((ft & 0x01) << 7) | ((q & 0x01) << 6));
        for (int bit = 0; bit < speechBits; bit++)
        {
            var value = (amrFrame[1 + bit / 8] >> (7 - bit % 8)) & 1;
            var target = 10 + bit;
            outRtp[target / 8] |= (byte)(value << (7 - target % 8));
        }

        return outRtp;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_decLock)
        {
            if (_nbDecState != IntPtr.Zero)
            {
                try { Decoder_Interface_exit(_nbDecState); } catch { }
                _nbDecState = IntPtr.Zero;
            }
        }

        lock (_encLock)
        {
            if (_nbEncState != IntPtr.Zero)
            {
                try { Encoder_Interface_exit(_nbEncState); } catch { }
                _nbEncState = IntPtr.Zero;
            }
        }

        lock (_wbDecLock)
        {
            if (_wbDecState != IntPtr.Zero)
            {
                try { D_IF_exit(_wbDecState); } catch { }
                _wbDecState = IntPtr.Zero;
            }
        }
    }
}
