using qmiSharp.Transport;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class UsbAudioModemBindingTests
{
    [Fact]
    public void IdenticallyNamedEndpointsAreSelectedByContainerNotOrder()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        WindowsWaveEndpoint[] endpoints = [
            new(0, true, "capture-b", b, "AC Interface"), new(1, false, "playback-a", a, "AC Interface"),
            new(1, true, "capture-a", a, "AC Interface"), new(0, false, "playback-b", b, "AC Interface")];
        var first = WindowsUsbAudioEndpoints.Select(endpoints, a);
        var second = WindowsUsbAudioEndpoints.Select(endpoints.Reverse(), b);
        Assert.Equal("capture-a", first.Capture.EndpointId); Assert.Equal(1, first.Capture.DeviceNumber);
        Assert.Equal("playback-b", second.Playback.EndpointId); Assert.Equal(0, second.Playback.DeviceNumber);
        Assert.Throws<InvalidOperationException>(() => WindowsUsbAudioEndpoints.Select(endpoints.Append(endpoints[2]), a));
        Assert.Throws<InvalidOperationException>(() => WindowsUsbAudioEndpoints.Select(endpoints.Where(e => e.Capture), a));
        Assert.Throws<ArgumentException>(() => WindowsUsbAudioEndpoints.Select(endpoints, Guid.Empty));
        WindowsWaveEndpoint[] withHost = [..endpoints.Select(e => e with { Name = "Microphone" }),
            new(2, true, "host-capture", Guid.NewGuid(), "Microphone"), new(3, true, "unknown", Guid.Empty, "Microphone")];
        Assert.Equal(2, WindowsUsbAudioEndpoints.SelectHost(withHost, new HashSet<Guid> { a, b }, true));
        Assert.Equal(-1, WindowsUsbAudioEndpoints.SelectHost(endpoints, new HashSet<Guid> { a, b }, true));
    }

    [ReadOnlyUsbAudioFact]
    [Trait("Lab", "ReadOnlyUsbAudio")]
    public void LiveUsbAudioEndpointsPairToSelectedAtPortWithoutOpeningStreams()
    {
        var port = Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_PORT")!;
        var physical = WindowsUsbTopology.SelectPort(WindowsUsbTopology.EnumeratePresent(), port);
        var selected = WindowsUsbAudioEndpoints.Select(WindowsUsbAudioEndpoints.Enumerate(), physical.ContainerId);
        Assert.NotEqual(Guid.Empty, selected.Capture.ContainerId);
        Assert.Equal(selected.Capture.ContainerId, selected.Playback.ContainerId);
        Assert.NotEmpty(selected.Capture.EndpointId); Assert.NotEmpty(selected.Playback.EndpointId);
    }

    [UsbAudioOpenFact]
    [Trait("Lab", "UsbAudioOpen")]
    public async Task LiveSelectedUsbEndpointsOpenWithoutHostMicrophoneOrCarrierCall()
    {
        var port = Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_PORT")!;
        var container = UsbAudioModemBinding.RequireSelected(WindowsUsbTopology.EnumeratePresent(), port, true);
        await Task.Run(async () =>
        {
            using var worker = new CellularAudioWorker();
            using var uplink = new System.IO.MemoryStream(new byte[320]); // one silent 20 ms frame, no host recording
            worker.StartExternal(container, uplink, System.IO.Stream.Null);
            Assert.True(worker.IsRunning);
            await Task.Delay(1000);
            Assert.True(worker.IsRunning);
            worker.Stop();
            Assert.False(worker.IsRunning);
        });
    }

    [UsbAudioHelperFact]
    [Trait("Lab", "UsbAudioHelper")]
    public async Task PublishedAudioHelperUsesSelectedContainerAndStopsCleanly()
    {
        Assert.True(VoKernelService.HasAdministratorToken());
        var executable = Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_HELPER")!;
        Assert.True(System.IO.Path.IsPathFullyQualified(executable) && System.IO.File.Exists(executable));
        var port = Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_PORT")!;
        var container = UsbAudioModemBinding.RequireSelected(WindowsUsbTopology.EnumeratePresent(), port, true);
        await using var bridge = new CellularAudioBridge(executable);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await bridge.StartAsync(port, container, cancellationToken: deadline.Token, externalPcm: true);
        Assert.True(bridge.IsRunning);
        bridge.SendExternalPcm(new short[160]);
        await Task.Delay(1000, deadline.Token);
        await bridge.StopAsync();
        Assert.False(bridge.IsRunning);
    }

    [Fact]
    public void AudioMustBelongToSelectedPhysicalModemAndAmbiguityIsRejected()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        WindowsUsbFunction port = new(@"USB\VID_2C7C&PID_0125", a, Guid.Empty, "AT", "COM6", null, null);
        WindowsUsbFunction modem = new(@"USB\VID_2C7C&PID_0125", a, WindowsUsbTopology.ModemClass, "Quectel modem", null, null, null);
        WindowsUsbFunction audio = new(@"USB\VID_2C7C&PID_0125", a, new("4d36e96c-e325-11ce-bfc1-08002be10318"), "AC Interface", null, null, null);
        UsbAudioModemBinding.RequireSelected([port, modem], "COM6", false);
        UsbAudioModemBinding.RequireSelected([port, modem, audio], "COM6", true);
        Assert.Throws<InvalidOperationException>(() => UsbAudioModemBinding.RequireSelected([port, modem], "COM6", true));
        Assert.Throws<InvalidOperationException>(() => UsbAudioModemBinding.RequireSelected([port, modem, audio with { ContainerId = b }], "COM6", true));
        Assert.Equal(a, UsbAudioModemBinding.RequireSelected([port, modem, audio, modem with { ContainerId = b }, audio with { ContainerId = b }], "COM6", true));
        Assert.Throws<InvalidOperationException>(() => UsbAudioModemBinding.RequireSelected([port, modem, audio, audio], "COM6", true));
        WindowsUsbFunction adb = new(@"USB\VID_2C7C&PID_0125&MI_05", a, Guid.Empty, "ADB Interface", null, null, null);
        UsbAudioModemBinding.RequireQdc507Runtime([port, modem, adb], "COM6", a);
        Assert.Throws<InvalidOperationException>(() => UsbAudioModemBinding.RequireQdc507Runtime([port, modem, adb, adb with { ContainerId = b }], "COM6", a));
        UsbAudioModemBinding.RequireQdc507Runtime([port, modem, adb, adb with { ContainerId = b }], "COM6", a, "111111111111111");
        Assert.Throws<InvalidOperationException>(() => UsbAudioModemBinding.RequireQdc507Runtime([port, modem, adb with { ContainerId = b }], "COM6", a, "111111111111111"));
        Assert.Throws<InvalidOperationException>(() => UsbAudioModemBinding.RequireQdc507Runtime([port, modem, adb], "COM6", b));
    }
}

internal sealed class ReadOnlyUsbAudioFactAttribute : FactAttribute
{
    public ReadOnlyUsbAudioFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_PORT")))
            Skip = "Set VOWIN_TEST_USB_AUDIO_PORT to explicitly query USB audio identities; no stream is opened.";
    }
}

internal sealed class UsbAudioOpenFactAttribute : FactAttribute
{
    public UsbAudioOpenFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_OPEN") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_PORT")))
            Skip = "Explicit USB audio lab only: opens selected modem streams, not host microphone; no carrier call or route changes.";
    }
}

internal sealed class UsbAudioHelperFactAttribute : FactAttribute
{
    public UsbAudioHelperFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_HELPER")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOWIN_TEST_USB_AUDIO_PORT")))
            Skip = "Explicit administrator lab only: published helper, selected USB streams, no host microphone or carrier call.";
    }
}
