using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using VoSharp.Kernel.Pool;
using VoSharp.Modem;
using VoSharp.Sip;
using VoSharp.Telephony;
using VoSharp.Telephony.Audio;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Tests;

[Collection("Windows modem lab")]
public sealed class WindowsHostImsWinDivertRealPppTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi, SetLastError = true)]
    private delegate nint OpenNative([MarshalAs(UnmanagedType.LPStr)] string filter,
        int layer, short priority, ulong flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SendNative(nint handle, byte[] packet, uint packetLength,
        out uint sentLength, byte[] address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ReceiveNative(nint handle, [Out] byte[] packet,
        uint packetLength, out uint receivedLength, [Out] byte[] address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ShutdownNative(nint handle, int how);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CloseNative(nint handle);

    [Fact]
    [Trait("Lab", "ExplicitRealImsPpp")]
    public void ExplicitRealPppSendsOneIdentityFreeSyntheticEspPacket()
    {
        var dll = Environment.GetEnvironmentVariable("VOWIN_REAL_PPP_ESP_PROBE_DLL");
        if (string.IsNullOrEmpty(dll)) return; // Never run on the default test path.
        var local = IPAddress.Parse(Environment.GetEnvironmentVariable("VOWIN_REAL_PPP_ESP_LOCAL")
            ?? throw new InvalidOperationException("Missing live IMS source."));
        var pcscf = IPAddress.Parse(Environment.GetEnvironmentVariable("VOWIN_REAL_PPP_ESP_PCSCF")
            ?? throw new InvalidOperationException("Missing live P-CSCF."));
        var index = int.Parse(Environment.GetEnvironmentVariable("VOWIN_REAL_PPP_ESP_INDEX")
            ?? throw new InvalidOperationException("Missing live PPP index."));
        using var io = new WindowsHostImsWinDivertIo(dll, local, pcscf, index);
        var packet = new byte[56];
        packet[0] = 0x60;
        packet[5] = 16;
        packet[6] = 50;
        packet[7] = 64;
        local.GetAddressBytes().CopyTo(packet, 8);
        pcscf.GetAddressBytes().CopyTo(packet, 24);
        new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0, 0, 0, 1,
            (byte)'V', (byte)'o', (byte)'W', (byte)'i', (byte)'n',
            (byte)'P', (byte)'P', (byte)'P' }.CopyTo(packet, 40);
        io.Send(packet);
    }

    [Fact]
    [Trait("Lab", "ExplicitRealImsPpp")]
    public async Task ExplicitRealPppCapturesAndDecryptsSyntheticInboundEspWithoutCarrierEgress()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_INBOUND_SYNTHETIC") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var dll = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_WINDIVERT_DLL");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));
        Assert.True(!string.IsNullOrWhiteSpace(dll) && Path.IsPathFullyQualified(dll) &&
            File.Exists(dll) && File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "WinDivert64.sys")));

        await using var slot = new ModemSlot("real-host-ims-inbound-esp", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        try
        {
            var status = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            var bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(status.Bearer);
            var local = bearer.LocalAddress;
            var pcscf = bearer.PcscfAddresses[0];
            using var receiver = new WindowsHostImsWinDivertIo(dll!, local, pcscf,
                bearer.InterfaceIndex);
            var library = NativeLibrary.Load(dll!);
            nint injector = new(-1);
            CloseNative? close = null;
            try
            {
                var open = Marshal.GetDelegateForFunctionPointer<OpenNative>(
                    NativeLibrary.GetExport(library, "WinDivertOpen"));
                var send = Marshal.GetDelegateForFunctionPointer<SendNative>(
                    NativeLibrary.GetExport(library, "WinDivertSend"));
                close = Marshal.GetDelegateForFunctionPointer<CloseNative>(
                    NativeLibrary.GetExport(library, "WinDivertClose"));
                // Priority 1 injects into the inbound path before the product
                // receiver at priority 0. That receiver consumes the exact
                // ESP packet, so the Windows IP stack cannot emit a response.
                injector = open("false", 0, 1, 0x0008);
                Assert.NotEqual(new nint(-1), injector);
                var packet = new byte[56];
                packet[0] = 0x60;
                packet[5] = 16;
                packet[6] = 50;
                packet[7] = 64;
                pcscf.GetAddressBytes().CopyTo(packet, 8);
                local.GetAddressBytes().CopyTo(packet, 24);
                new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0, 0, 0, 1,
                    (byte)'V', (byte)'o', (byte)'W', (byte)'i', (byte)'n',
                    (byte)'I', (byte)'n', (byte)'b' }.CopyTo(packet, 40);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var receiveTask = Task.Run(() => receiver.Receive(timeout.Token));
                var address = new byte[80];
                BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(8), 1u << 20);
                BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(16),
                    checked((uint)bearer.InterfaceIndex));
                Assert.True(send(injector, packet, (uint)packet.Length, out var sent, address),
                    $"WinDivert inbound injection failed: {Marshal.GetLastPInvokeError()}");
                Assert.Equal((uint)packet.Length, sent);
                Assert.Equal(packet, await receiveTask.WaitAsync(TimeSpan.FromSeconds(6)));

                var ueProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
                    0x1001, 0x1002, 40000, 40001);
                var peerProposal = new SecurityProposal("hmac-sha-1-96", "aes-cbc",
                    0x2001, 0x2002, 41000, 41001);
                var key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
                using var host = new HostImsUserspaceEspSession(local, pcscf,
                    receiver, ueProposal);
                host.Activate(new SecurityAgreement(ueProposal, "lab", peerProposal.SpiClient,
                    peerProposal.SpiServer, peerProposal.PortClient, peerProposal.PortServer),
                    key, key);
                using var peer = new ImsIpsecTransport(pcscf, local, peerProposal);
                peer.Activate(new SecurityAgreement(peerProposal, "lab", ueProposal.SpiClient,
                    ueProposal.SpiServer, ueProposal.PortClient, ueProposal.PortServer),
                    key, key);
                var response = new SipMessage { IsRequest = false, StatusCode = 200, ReasonPhrase = "OK" };
                response.SetHeader("Via", "SIP/2.0/UDP [2001:db8::1]:40000;branch=z9hG4bK-ppp-lab");
                response.SetHeader("From", "<sip:lab@invalid.test>;tag=peer");
                response.SetHeader("To", "<sip:lab@invalid.test>;tag=local");
                response.SetHeader("Call-ID", "ppp-esp-lab@invalid.test");
                response.SetHeader("CSeq", "1 REGISTER");
                var protectedPacket = peer.Protect(new SipDatagram(response.ToBytes(),
                    new(pcscf, peerProposal.PortClient), new(local, ueProposal.PortServer)));
                Assert.Equal(50, protectedPacket[6]);
                using var protectedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var decryptedTask = Task.Run(() => host.Receive(protectedTimeout.Token));
                Assert.True(send(injector, protectedPacket, (uint)protectedPacket.Length,
                    out sent, address),
                    $"WinDivert protected inbound injection failed: {Marshal.GetLastPInvokeError()}");
                Assert.Equal((uint)protectedPacket.Length, sent);
                var decrypted = await decryptedTask.WaitAsync(TimeSpan.FromSeconds(6));
                Assert.Equal(ueProposal.PortServer, decrypted.LocalEndPoint.Port);
                Assert.Equal(peerProposal.PortClient, decrypted.RemoteEndPoint.Port);
                Assert.Equal(200, SipMessage.Parse(decrypted.Payload).StatusCode);
                // Both packets were injected with Outbound=false into Windows
                // only; the first contains no SIP and the second a fake identity.
            }
            finally
            {
                if (injector != new nint(-1)) close?.Invoke(injector);
                NativeLibrary.Free(library);
            }
        }
        finally { await slot.StopHostImsPppBearerAsync(); }
    }

    [Fact]
    [Trait("Lab", "ExplicitRealImsPpp")]
    public async Task ExplicitRealPppDeliversSyntheticInboundPcmaAndAmrToWindowsDecoderWithoutCarrierEgress()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_INBOUND_RTP_SYNTHETIC") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var dll = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_WINDIVERT_DLL");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));
        Assert.True(!string.IsNullOrWhiteSpace(dll) && Path.IsPathFullyQualified(dll) &&
            File.Exists(dll) && File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "WinDivert64.sys")));

        await using var slot = new ModemSlot("real-host-ims-inbound-rtp", port);
        Assert.True(await slot.AttachAsync(initializeSms: false));
        try
        {
            var status = await slot.StartHostImsPppBearerAsync(script!, ras!, maxLeaseSeconds: 180);
            var bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(status.Bearer);
            var local = bearer.LocalAddress;
            var peer = bearer.PcscfAddresses[0];
            using var media = new RtpSession(null, localAddress: local,
                outgoingInterfaceIndex: bearer.InterfaceIndex);
            media.CustomSender = _ => Task.CompletedTask;
            media.CustomRtcpSender = _ => Task.CompletedTask;
            var decoded = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            media.OnAudioDecoded = samples => decoded.TrySetResult(samples);
            media.SetRemoteEndpoint(peer, 40000, payloadType: 8, codec: "PCMA");

            var library = NativeLibrary.Load(dll!);
            nint injector = new(-1);
            CloseNative? close = null;
            try
            {
                var open = Marshal.GetDelegateForFunctionPointer<OpenNative>(
                    NativeLibrary.GetExport(library, "WinDivertOpen"));
                var send = Marshal.GetDelegateForFunctionPointer<SendNative>(
                    NativeLibrary.GetExport(library, "WinDivertSend"));
                close = Marshal.GetDelegateForFunctionPointer<CloseNative>(
                    NativeLibrary.GetExport(library, "WinDivertClose"));
                injector = open("false", 0, 1, 0x0008);
                Assert.NotEqual(new nint(-1), injector);

                var rtp = new byte[172];
                rtp[0] = 0x80;
                rtp[1] = 8;
                BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2, 2), 1);
                BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8, 4), 0x12345678);
                Array.Fill(rtp, (byte)0xD5, 12, 160); // PCMA silence, not subscriber audio.
                var packet = IpPacketUtils.BuildUdpPacket(peer, local, 40000,
                    checked((ushort)media.LocalPort), rtp);
                var address = new byte[80];
                BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(8), 1u << 20);
                BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(16),
                    checked((uint)bearer.InterfaceIndex));
                Assert.True(send(injector, packet, (uint)packet.Length, out var sent, address),
                    $"WinDivert inbound RTP injection failed: {Marshal.GetLastPInvokeError()}");
                Assert.Equal((uint)packet.Length, sent);
                var samples = await decoded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(160, samples.Length);
                Assert.Equal(1UL, media.TotalPacketsReceived);
                Assert.All(samples, sample => Assert.Equal((short)8, sample));

                using var amr = new RtpSession(null, localAddress: local,
                    outgoingInterfaceIndex: bearer.InterfaceIndex);
                amr.CustomSender = _ => Task.CompletedTask;
                amr.CustomRtcpSender = _ => Task.CompletedTask;
                var amrDecoded = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                amr.OnAudioDecoded = pcm => amrDecoded.TrySetResult(pcm);
                amr.SetRemoteEndpoint(peer, 40002, payloadType: 102, codec: "AMR",
                    amrOctetAligned: true, amrModeSet: "2");
                using var encoder = new AmrNativeCodec(initEncoder: true);
                Assert.True(encoder.IsAvailable);
                var tone = new short[160];
                for (var i = 0; i < tone.Length; i++)
                    tone[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 8000) * 12000);
                var frame = encoder.EncodeAmrNbFrame(tone, mode: 2);
                Assert.NotEmpty(frame);
                Assert.Equal(2, frame[0] >> 3 & 0x0f);
                var amrPayload = AmrNativeCodec.AmrFrameToRtpPayload(frame, octetAligned: true);
                var amrRtp = new byte[12 + amrPayload.Length];
                amrRtp[0] = 0x80;
                amrRtp[1] = 102;
                BinaryPrimitives.WriteUInt16BigEndian(amrRtp.AsSpan(2, 2), 2);
                BinaryPrimitives.WriteUInt32BigEndian(amrRtp.AsSpan(8, 4), 0x87654321);
                amrPayload.CopyTo(amrRtp.AsSpan(12));
                var amrPacket = IpPacketUtils.BuildUdpPacket(peer, local, 40002,
                    checked((ushort)amr.LocalPort), amrRtp);
                Assert.True(send(injector, amrPacket, (uint)amrPacket.Length,
                    out sent, address),
                    $"WinDivert inbound AMR injection failed: {Marshal.GetLastPInvokeError()}");
                Assert.Equal((uint)amrPacket.Length, sent);
                var amrSamples = await amrDecoded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(160, amrSamples.Length);
                Assert.Equal(1UL, amr.TotalPacketsReceived);
                Assert.True(amrSamples.Sum(sample => Math.Abs((int)sample)) > 1000);
                // Inbound-only injection; both RTP and RTCP uplink senders are no-ops.
            }
            finally
            {
                if (injector != new nint(-1)) close?.Invoke(injector);
                NativeLibrary.Free(library);
            }
        }
        finally { await slot.StopHostImsPppBearerAsync(); }
    }

    [Fact]
    [Trait("Lab", "ExplicitRealImsPpp")]
    public async Task ExplicitRealPppRoutesWindowsRtpToImsInterfaceButConsumesItBeforeCarrierEgress()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OUTBOUND_RTP_INTERCEPT") != "1") return;
        var script = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_PPP_OWNER");
        var ras = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_RAS_EXE");
        var dll = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_WINDIVERT_DLL");
        var port = Environment.GetEnvironmentVariable("VOWIN_HOST_IMS_AT_PORT") ?? "COM6";
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script));
        Assert.True(!string.IsNullOrWhiteSpace(ras) && File.Exists(ras));
        Assert.True(!string.IsNullOrWhiteSpace(dll) && Path.IsPathFullyQualified(dll) &&
            File.Exists(dll) && File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "WinDivert64.sys")));

        var destination = IPAddress.Parse("2001:db8::1974"); // Documentation-only media target.
        await using var lease = await WindowsHostImsPppBearerLease.StartAsync(
            script!, ras!, port, maxLeaseSeconds: 180);
        var bearer = Assert.IsType<WindowsHostImsPppBearerInfo>(lease.Bearer);
        await lease.EnsureMediaRouteAsync(destination);
        using var media = new RtpSession(null, localAddress: bearer.LocalAddress,
            outgoingInterfaceIndex: bearer.InterfaceIndex);
        media.CustomRtcpSender = _ => Task.CompletedTask;

        var library = NativeLibrary.Load(dll!);
        nint consumer = new(-1);
        CloseNative? close = null;
        ShutdownNative? shutdown = null;
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenNative>(
                NativeLibrary.GetExport(library, "WinDivertOpen"));
            var receive = Marshal.GetDelegateForFunctionPointer<ReceiveNative>(
                NativeLibrary.GetExport(library, "WinDivertRecv"));
            shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownNative>(
                NativeLibrary.GetExport(library, "WinDivertShutdown"));
            close = Marshal.GetDelegateForFunctionPointer<CloseNative>(
                NativeLibrary.GetExport(library, "WinDivertClose"));
            var filter = $"outbound and ipv6 and udp and remoteAddr == {destination} " +
                         "and udp.DstPort == 40000";
            // Receive-only without SNIFF consumes the matching test RTP packet.
            // The filter intentionally does not assume the source/interface;
            // those are assertions on the intercepted Windows route choice.
            consumer = open(filter, 0, 0, 0x0004);
            Assert.NotEqual(new nint(-1), consumer);
            var handle = consumer;
            var captureTask = Task.Run(() =>
            {
                var buffer = new byte[2048];
                var address = new byte[80];
                if (!receive(handle, buffer, (uint)buffer.Length, out var length, address))
                    throw new InvalidOperationException(
                        $"WinDivert outbound RTP receive failed: {Marshal.GetLastPInvokeError()}");
                return (Packet: buffer.AsSpan(0, checked((int)length)).ToArray(), Address: address);
            });
            media.SetRemoteEndpoint(destination, 40000, payloadType: 8, codec: "PCMA");
            media.SendAudioPcm(new short[160]);
            var capture = await captureTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(6, capture.Packet[0] >> 4);
            Assert.Equal(17, capture.Packet[6]);
            Assert.Equal(bearer.LocalAddress.GetAddressBytes(), capture.Packet.AsSpan(8, 16).ToArray());
            Assert.Equal(destination.GetAddressBytes(), capture.Packet.AsSpan(24, 16).ToArray());
            Assert.Equal((ushort)media.LocalPort,
                BinaryPrimitives.ReadUInt16BigEndian(capture.Packet.AsSpan(40, 2)));
            Assert.Equal((ushort)40000,
                BinaryPrimitives.ReadUInt16BigEndian(capture.Packet.AsSpan(42, 2)));
            Assert.Equal((byte)0x80, capture.Packet[48]);
            Assert.Equal((byte)8, capture.Packet[49] & 0x7f);
            Assert.Equal(40 + 8 + 12 + 160, capture.Packet.Length);
            Assert.Equal((uint)bearer.InterfaceIndex,
                BinaryPrimitives.ReadUInt32LittleEndian(capture.Address.AsSpan(16, 4)));
            Assert.NotEqual(0u,
                BinaryPrimitives.ReadUInt32LittleEndian(capture.Address.AsSpan(8, 4)) & (1u << 17));
            // Deliberately do not re-inject: no media reaches the carrier.
        }
        finally
        {
            // Keep the outbound consumer installed until the sender is stopped.
            media.Dispose();
            if (consumer != new nint(-1))
            {
                shutdown?.Invoke(consumer, 1);
                close?.Invoke(consumer);
            }
            NativeLibrary.Free(library);
        }
    }
}
