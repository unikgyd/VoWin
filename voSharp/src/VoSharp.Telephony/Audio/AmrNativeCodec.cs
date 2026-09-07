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
                if (libName.Equals("opencore-amr", StringComparison.OrdinalIgnoreCase) ||
                    libName.Equals("opencore-amr.dll", StringComparison.OrdinalIgnoreCase))
                {
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

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr D_IF_init();

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern void D_IF_exit(IntPtr state);

    [DllImport("opencore-amr", CallingConvention = CallingConvention.Cdecl)]
    private static extern void D_IF_decode(IntPtr state, byte[] inFrame, short[] outPcm, int bfi);

    private IntPtr _nbDecState = IntPtr.Zero;
    private IntPtr _nbEncState = IntPtr.Zero;
    private IntPtr _wbDecState = IntPtr.Zero;

    private readonly object _decLock = new();
    private readonly object _encLock = new();
    private readonly object _wbDecLock = new();
    private bool _isDisposed;

    public bool IsAvailable { get; private set; }

    public AmrNativeCodec(bool initEncoder = true)
    {
        try
        {
            _nbDecState = Decoder_Interface_init();
            if (initEncoder)
            {
                _nbEncState = Encoder_Interface_init(0);
            }
            _wbDecState = D_IF_init();
            IsAvailable = _nbDecState != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Console.WriteLine($"[AmrNativeCodec] Native opencore-amr initialization warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes a single standard AMR-NB frame (TOC byte + speech bytes) into 160 PCM samples @ 8000Hz (20ms).
    /// </summary>
    public short[] DecodeAmrNbFrame(byte[] amrFrame, bool bfi = false)
    {
        if (!IsAvailable || _nbDecState == IntPtr.Zero || amrFrame == null || amrFrame.Length == 0)
            return new short[160];

        var pcm = new short[160];
        lock (_decLock)
        {
            if (_isDisposed || _nbDecState == IntPtr.Zero) return pcm;
            try
            {
                Decoder_Interface_Decode(_nbDecState, amrFrame, pcm, bfi ? 1 : 0);
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
        if (!IsAvailable || _wbDecState == IntPtr.Zero || amrWbFrame == null || amrWbFrame.Length == 0)
            return new short[160];

        var pcm16k = new short[320];
        lock (_wbDecLock)
        {
            if (_isDisposed || _wbDecState == IntPtr.Zero) return new short[160];
            try
            {
                D_IF_decode(_wbDecState, amrWbFrame, pcm16k, bfi ? 1 : 0);
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
        if (!IsAvailable || _nbEncState == IntPtr.Zero || pcmSamples == null || pcmSamples.Length < 160)
            return Array.Empty<byte>();

        byte[] outBuffer = new byte[64];
        int encodedBytes = 0;
        lock (_encLock)
        {
            if (_isDisposed || _nbEncState == IntPtr.Zero) return Array.Empty<byte>();
            try
            {
                encodedBytes = Encoder_Interface_Encode(_nbEncState, mode, pcmSamples, outBuffer, 0);
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
        if (amrFrame == null || amrFrame.Length == 0)
            return Array.Empty<byte>();

        if (octetAligned)
        {
            // Octet-aligned: CMR (0xF0 = no request) + TOC + speech bytes
            var payload = new byte[1 + amrFrame.Length];
            payload[0] = 0xF0; // CMR: 1111 (no mode request), reserved: 0000
            Buffer.BlockCopy(amrFrame, 0, payload, 1, amrFrame.Length);
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

        int speechBytes = amrFrame.Length - 1;
        // Total bits = 4 (CMR) + 1 (F) + 4 (FT) + 1 (Q) + speechBytes * 8 = 10 + speechBytes * 8
        int totalPayloadBytes = (10 + speechBytes * 8 + 7) / 8;
        var outRtp = new byte[totalPayloadBytes];

        // Byte 0: CMR[3..0] (1111) | F (0) | FT[3..1]
        outRtp[0] = (byte)(0xF0 | ((ft >> 1) & 0x07));
        // Byte 1: FT[0] | Q | speech bits 0..5
        byte firstSpeechByte = speechBytes > 0 ? amrFrame[1] : (byte)0;
        outRtp[1] = (byte)(((ft & 0x01) << 7) | ((q & 0x01) << 6) | ((firstSpeechByte >> 2) & 0x3F));

        // Remaining speech bits
        for (int i = 1; i < speechBytes; i++)
        {
            byte cur = amrFrame[i];
            byte next = (i + 1 < amrFrame.Length) ? amrFrame[i + 1] : (byte)0;
            outRtp[i + 1] = (byte)(((cur & 0x03) << 6) | ((next >> 2) & 0x3F));
        }

        if (speechBytes > 0 && 1 + speechBytes < totalPayloadBytes)
        {
            outRtp[1 + speechBytes] = (byte)((amrFrame[^1] & 0x03) << 6);
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
