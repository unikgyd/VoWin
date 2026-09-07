using System.Net;
using System.Net.Sockets;

namespace VoSharp.Telephony.VoWifi;

/// <summary>
/// Real UDP / NAT-T datagram transport for 3GPP VoWiFi IKEv2 / IPsec tunneling (TS 24.302 / RFC 7296).
/// </summary>
public class Ikev2Transport : IDisposable
{
    private UdpClient? _udpClient;
    private IPEndPoint? _remoteEp;
    private readonly int _timeoutMs;

    public Ikev2Transport(int timeoutMs = 4000)
    {
        _timeoutMs = timeoutMs;
    }

    public void Connect(IPAddress remoteIp, int port = Ikev2Protocol.DefaultIkev2Port)
    {
        _remoteEp = new IPEndPoint(remoteIp, port);
        _udpClient = new UdpClient();
        _udpClient.Client.ReceiveTimeout = _timeoutMs;
        _udpClient.Client.SendTimeout = _timeoutMs;
        _udpClient.Connect(_remoteEp);
    }

    public async Task<byte[]> SendAndReceiveAsync(byte[] requestData, bool isNatT = false, CancellationToken ct = default)
    {
        if (_udpClient == null || _remoteEp == null)
            throw new InvalidOperationException("IKEv2 transport is not connected.");

        byte[] payloadToSend = requestData;
        if (isNatT)
        {
            // RFC 3948: Non-ESP Marker (4 zero bytes) prepended to IKE packets on UDP 4500
            payloadToSend = new byte[4 + requestData.Length];
            Array.Copy(requestData, 0, payloadToSend, 4, requestData.Length);
        }

        await _udpClient.SendAsync(payloadToSend, payloadToSend.Length).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);

        try
        {
            var result = await _udpClient.ReceiveAsync(cts.Token).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (isNatT && buffer.Length >= 4 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0 && buffer[3] == 0)
            {
                // Strip 4-byte Non-ESP marker
                return buffer[4..];
            }

            return buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"ePDG at {_remoteEp} timed out after {_timeoutMs}ms without response.");
        }
    }

    public void Dispose()
    {
        try { _udpClient?.Dispose(); } catch { }
        _udpClient = null;
    }
}
