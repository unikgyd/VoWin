using VoSharp.Modem;
using VoSharp.Modem.At;

namespace VoSharp.Tests;

public sealed class QmiAtPreferenceTests
{
    [Theory]
    [InlineData("123456789012345", "123456789012345", true)]
    [InlineData("123456789012345", "123456789012346", false)]
    [InlineData("12345678901234", "12345678901234", false)]
    [InlineData("unknown", "unknown", false)]
    public void QmiAndAtMustIdentifyTheSamePhysicalModem(string qmi, string at, bool expected)
    {
        Assert.Equal(expected, ModemDriver.SameModemImei(qmi, at));
    }

    [Fact]
    public async Task ValidQmiValuesWinWithoutOpeningAtPort()
    {
        var qmi = new FakeQmiReader();
        await using var modem = new ModemDriver("COM999", qmi: qmi);

        Assert.False(await modem.GetSimInsertedAsync());
        Assert.Equal("123456789012345", await modem.GetImeiAsync());
        Assert.Equal("001010123456789", await modem.GetImsiAsync());
        Assert.Equal("8901234567890123456", await modem.GetIccidAsync());
        Assert.Equal(-85, (await modem.GetSignalAsync()).RssiDbm);
        Assert.Equal(NetworkRegStatus.Home, (await modem.GetRegistrationAsync()).Status);
        Assert.Null(modem.LastQmiFallbackReason);
        Assert.True(modem.HasQmiEndpoint);
        Assert.False(modem.IsOpen);
    }

    [Fact]
    public async Task CompleteQmiWdsImsContextIsProbedBeforeAt()
    {
        var qmi = new FakeQmiReader
        {
            SimInserted = true,
            ActiveImsPdn = new QmiImsPdn(7, "ims", System.Net.IPAddress.Loopback,
                [System.Net.IPAddress.Loopback])
        };
        await using var modem = new ModemDriver("COM999", qmi: qmi);
        var probe = await modem.ProbeHostImsPdnAsync();
        Assert.False(modem.IsOpen);
        Assert.Contains("QMI WDS", probe.Summary);
        Assert.Contains("QMI 优先", probe.ControlStackStatus);
        Assert.Equal("QMI WDS profile 7", Assert.Single(probe.Contexts).Label);
        Assert.False(probe.CanAttemptWindowsIms); // Loopback is not a cellular IMS interface.
    }

    [Fact]
    public async Task QmiOnlyNoSimProbeDoesNotRequireAnOpenAtPort()
    {
        var qmi = new FakeQmiReader();
        await using var modem = new ModemDriver("COM999", qmi: qmi);

        var probe = await modem.ProbeHostImsPdnAsync();

        Assert.Equal(HostImsReadiness.SimUnavailable, probe.Readiness);
        Assert.Equal("QMI UIM", probe.ProbeSource);
        Assert.False(probe.SimInserted);
        Assert.Null(probe.AtControlAvailable);
        Assert.Null(probe.UsbNetworkMode);
        Assert.Empty(probe.Contexts);
        Assert.False(probe.CanAttemptWindowsIms);
        Assert.False(modem.IsOpen);
        Assert.Equal(0, qmi.ActiveImsQueries);
    }

    [Fact]
    public async Task QmiOnlyNoSimProbeReportsConfiguredImsProfileWithoutActivatingPdn()
    {
        var qmi = new FakeQmiReader
        {
            ConfiguredImsProfiles = [new QmiImsProfile(7, "IPV4V6", "ims")]
        };
        await using var modem = new ModemDriver("COM999", qmi: qmi);

        var probe = await modem.ProbeHostImsPdnAsync();

        Assert.Equal(HostImsReadiness.SimUnavailable, probe.Readiness);
        Assert.Equal("QMI UIM + WDS profile", probe.ProbeSource);
        var context = Assert.Single(probe.Contexts);
        Assert.Equal("QMI WDS profile 7", context.Label);
        Assert.Equal("ims", context.Apn);
        Assert.Equal("IPV4V6", context.PdpType);
        Assert.False(context.IsActive);
        Assert.Equal(0, qmi.ActiveImsQueries);
        Assert.False(probe.CanAttemptWindowsIms);
    }

    [Fact]
    public async Task EmptyAndFailedQmiReadsFallBackToAt()
    {
        await using var modem = new ModemDriver("COM999", qmi: new FakeQmiReader());
        var order = new List<string>();
        var empty = await modem.PreferQmiAsync<string>(
            _ => { order.Add("qmi"); return Task.FromResult(string.Empty); },
            _ => { order.Add("at"); return Task.FromResult("at-result"); },
            value => value.Length > 0, CancellationToken.None);
        Assert.Equal("at-result", empty);
        Assert.Equal(["qmi", "at"], order);

        order.Clear();
        var failed = await modem.PreferQmiAsync<string>(
            _ => { order.Add("qmi"); throw new IOException("QMI endpoint unavailable"); },
            _ => { order.Add("at"); return Task.FromResult("at-result"); },
            value => value.Length > 0, CancellationToken.None);
        Assert.Equal("at-result", failed);
        Assert.Equal(["qmi", "at"], order);
        Assert.Contains("IOException", modem.LastQmiFallbackReason);
    }

    [Fact]
    public async Task CancellationDoesNotFallBackToAt()
    {
        await using var modem = new ModemDriver("COM999", qmi: new FakeQmiReader());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var atCalled = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => modem.PreferQmiAsync<string>(
            ct => Task.FromCanceled<string>(ct),
            _ => { atCalled = true; return Task.FromResult("at-result"); },
            value => value.Length > 0, cancellation.Token));
        Assert.False(atCalled);
    }

    private sealed class FakeQmiReader : IQmiModemReader, IQmiImsProfileReader
    {
        public int ActiveImsQueries { get; private set; }
        public IReadOnlyList<QmiImsProfile> ConfiguredImsProfiles { get; init; } = [];
        public bool SimInserted { get; init; }
        public QmiImsPdn? ActiveImsPdn { get; init; }
        public SignalQuality? Signal { get; init; } = new(14, -85, 4, "LTE");
        public NetworkRegistration? Registration { get; init; } =
            new(NetworkRegStatus.Home, "Test", "LTE", null);
        public Task<bool?> GetSimInsertedAsync(CancellationToken ct = default) => Task.FromResult<bool?>(SimInserted);
        public Task<string> GetImeiAsync(CancellationToken ct = default) => Task.FromResult("123456789012345");
        public Task<string> GetImsiAsync(CancellationToken ct = default) => Task.FromResult("001010123456789");
        public Task<string> GetIccidAsync(CancellationToken ct = default) => Task.FromResult("8901234567890123456");
        public Task<QmiImsPdn?> GetActiveImsPdnAsync(CancellationToken ct = default)
        {
            ActiveImsQueries++;
            return Task.FromResult(ActiveImsPdn);
        }
        public Task<IReadOnlyList<QmiImsProfile>> GetConfiguredImsProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult(ConfiguredImsProfiles);
        public Task<SignalQuality?> GetSignalAsync(CancellationToken ct = default) => Task.FromResult(Signal);
        public Task<NetworkRegistration?> GetRegistrationAsync(CancellationToken ct = default) => Task.FromResult(Registration);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
