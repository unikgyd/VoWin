using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using VoSharp.Common.Aka;
using VoSharp.Common.Events;
using VoSharp.Common.Utils;
using VoSharp.Modem.At;

namespace VoSharp.Modem;

public class ModemDriver : IAsyncDisposable
{
    private readonly AtSession _session;
    private readonly IQmiModemReader? _qmi;
    private volatile bool _qmiDisabled;
    private readonly AsyncEventBus? _eventBus;
    private volatile bool _isRadioStateKnown;
    private volatile bool _isRadioDisabled;
    private volatile bool _radioStatusReportingEnabled;

    public event EventHandler<ModemConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<SignalChangedEventArgs>? SignalChanged;
    public event EventHandler<NetworkRegistrationChangedEventArgs>? RegistrationChanged;
    public event EventHandler<SimStateChangedEventArgs>? SimStateChanged;
    public event EventHandler<FlightModeChangedEventArgs>? FlightModeChanged;
    public event EventHandler<ModemUrcEventArgs>? UrcReceived;

    public ModemDriver(string portName, int baudRate = 115200, AsyncEventBus? eventBus = null,
        IQmiModemReader? qmi = null)
    {
        _session = new AtSession(portName, baudRate, eventBus);
        if (qmi is not null) _qmi = qmi;
        else
        {
            try { _qmi = QmiModemReader.FromEnvironment(portName); }
            catch (ArgumentException ex)
            {
                // A bad optional QMI endpoint must not prevent the AT fallback
                // channel from opening. Surface the configuration error safely.
                LastQmiFallbackReason = $"QMI endpoint configuration: {ex.Message}";
            }
        }
        _session.UrcPublicationFilter = ShouldPublishUrc;
        _eventBus = eventBus;
        _session.UrcReceived += (s, urc) =>
        {
            try { UrcReceived?.Invoke(this, new ModemUrcEventArgs(urc)); } catch { }
        };
    }

    public bool IsOpen => _session.IsOpen;
    public string PortName => _session.PortName;
    public AtSession Session => _session;
    public bool HasQmiEndpoint => _qmi is not null;
    public bool IsQmiActive => _qmi is not null && !_qmiDisabled;
    public string? LastQmiFallbackReason { get; private set; }
    public string? FirmwareRevision { get; private set; }

    /// <summary>Reads provisioned IMS identities from ADF.ISIM without changing card files.</summary>
    public Task<IsimIdentityProbeResult> ProbeIsimIdentityAsync(CancellationToken ct = default) =>
        new IsimIdentityReader(_session).ReadAsync(ct);

    /// <summary>
    /// Tries the QMI UIM logical-channel path before choosing an AT ISIM session.
    /// A failure here is safe to fall back because no IMS AKA vector was sent.
    /// </summary>
    public async Task<IsimIdentityProbeResult?> TryProbeQmiIsimIdentityAsync(CancellationToken ct = default)
    {
        if (_qmiDisabled || _qmi is not IQmiIsimReader isim) return null;
        try
        {
            var result = await isim.ReadIsimIdentityAsync(ct).ConfigureAwait(false);
            if (result.Readiness == IsimIdentityReadiness.Available)
            {
                LastQmiFallbackReason = null;
                return result;
            }
            LastQmiFallbackReason = $"QMI UIM ISIM: {result.Readiness}; checking AT.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastQmiFallbackReason = $"QMI UIM ISIM {ex.GetType().Name}; checking AT.";
        }
        return null;
    }

    /// <summary>Does not retry a transmitted AKA vector through AT on QMI failure.</summary>
    public Task<AkaResult> AuthenticateQmiIsimAkaAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        if (_qmiDisabled || _qmi is not IQmiIsimReader isim)
            throw new InvalidOperationException("QMI ISIM is not available for this modem slot.");
        return isim.AuthenticateIsimAsync(challenge, ct);
    }

    /// <summary>Checks QMI USIM capability before a network AKA challenge is consumed.</summary>
    public async Task<bool> TryCanAuthenticateQmiUsimAsync(CancellationToken ct = default)
    {
        if (_qmiDisabled || _qmi is not IQmiUsimReader usim) return false;
        try
        {
            var available = await usim.CanAuthenticateUsimAsync(ct).ConfigureAwait(false);
            if (available) LastQmiFallbackReason = null;
            else LastQmiFallbackReason = "QMI UIM has no ready selected USIM channel; using AT.";
            return available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastQmiFallbackReason = $"QMI UIM USIM {ex.GetType().Name}; using AT.";
            return false;
        }
    }

    /// <summary>Never replays a sent QMI AKA challenge through AT on failure.</summary>
    public Task<AkaResult> AuthenticateQmiUsimAkaAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        if (_qmiDisabled || _qmi is not IQmiUsimReader usim)
            throw new InvalidOperationException("QMI USIM is not available for this modem slot.");
        return usim.AuthenticateUsimAsync(challenge, ct);
    }

    /// <summary>Runs IMS AKA against ADF.ISIM when the card provisions an ISIM application.</summary>
    public Task<AkaResult> AuthenticateIsimAkaAsync(AkaChallenge challenge, CancellationToken ct = default) =>
        new IsimIdentityReader(_session).AuthenticateAsync(challenge, ct);

    /// <summary>Sets the physical DTR signal while retaining the AT reader.</summary>
    public void SetDataTerminalReady(bool asserted) => _session.SetDataTerminalReady(asserted);

    public void Open()
    {
        _session.Open();
        try { ConnectionChanged?.Invoke(this, new ModemConnectionChangedEventArgs(PortName, true)); } catch { }
    }

    /// <summary>Temporarily releases the AT COM handle without disposing the driver.</summary>
    public bool Close() => _session.Close();

    /// <summary>Closes the AT channel and waits for its reader to exit.</summary>
    public Task<bool> CloseAsync(CancellationToken ct = default) => _session.CloseAsync(ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT", 1000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            // Disable AT echo
            await _session.ExecuteCommandAsync("ATE0", 1000, ct).ConfigureAwait(false);
            if (_qmi is not null && !_qmiDisabled)
                await VerifyQmiMatchesAtModemAsync(ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    internal async Task<T> PreferQmiAsync<T>(
        Func<CancellationToken, Task<T>>? qmiRead,
        Func<CancellationToken, Task<T>> atRead,
        Func<T, bool> isUsable,
        CancellationToken ct)
    {
        if (!_qmiDisabled && qmiRead is not null)
        {
            try
            {
                var value = await qmiRead(ct).ConfigureAwait(false);
                if (isUsable(value))
                {
                    LastQmiFallbackReason = null;
                    return value;
                }
                LastQmiFallbackReason = "QMI returned no usable value; using AT.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LastQmiFallbackReason = $"QMI {ex.GetType().Name}; using AT.";
            }
        }
        return await atRead(ct).ConfigureAwait(false);
    }

    public Task<string> GetImeiAsync(CancellationToken ct = default) =>
        PreferQmiAsync(_qmi is null ? null : _qmi.GetImeiAsync, GetImeiFromAtAsync,
            value => IsDecimalIdentifier(value, 14, 18), ct);

    private static bool IsDecimalIdentifier(string? value, int minLength, int maxLength) =>
        value is not null && value.Length >= minLength && value.Length <= maxLength &&
        value.All(char.IsAsciiDigit);

    internal static bool SameModemImei(string? qmiImei, string? atImei) =>
        IsDecimalIdentifier(qmiImei, 15, 15) &&
        IsDecimalIdentifier(atImei, 15, 15) &&
        string.Equals(qmiImei, atImei, StringComparison.Ordinal);

    private async Task VerifyQmiMatchesAtModemAsync(CancellationToken ct)
    {
        try
        {
            var qmiImei = await _qmi!.GetImeiAsync(ct).ConfigureAwait(false);
            var atImei = await GetImeiFromAtAsync(ct).ConfigureAwait(false);
            if (SameModemImei(qmiImei, atImei)) return;
            _qmiDisabled = true;
            LastQmiFallbackReason = "QMI and AT modem identities do not match; using AT for this slot.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _qmiDisabled = true;
            LastQmiFallbackReason = $"QMI identity check failed ({ex.GetType().Name}); using AT for this slot.";
        }
    }

    private async Task<string> GetImeiFromAtAsync(CancellationToken ct)
    {
        var resp = await _session.ExecuteCommandAsync("AT+GSN", 2000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CGSN", 2000, ct).ConfigureAwait(false);

        if (resp.Success)
        {
            foreach (var line in resp.Lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(trimmed, @"\d{14,18}");
                if (match.Success) return match.Value;
            }
        }
        return string.Empty;
    }

    public async Task<string> GetFirmwareRevisionAsync(CancellationToken ct = default)
    {
        var response = await _session.ExecuteCommandAsync("AT+QGMR", 2000, ct).ConfigureAwait(false);
        if (!response.Success)
            response = await _session.ExecuteCommandAsync("ATI", 2000, ct).ConfigureAwait(false);

        FirmwareRevision = response.Lines
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("AT", StringComparison.OrdinalIgnoreCase) &&
                !line.Equals("OK", StringComparison.OrdinalIgnoreCase));
        return FirmwareRevision ?? string.Empty;
    }

    /// <summary>
    /// Probes whether the modem exposes an active IMS PDN all the way to a
    /// Windows network interface. All commands are read-only; this method never
    /// creates, activates, edits or deletes a PDP context.
    /// </summary>
    public async Task<HostImsProbeResult> ProbeHostImsPdnAsync(CancellationToken ct = default)
    {
        var simInserted = await GetSimInsertedAsync(ct).ConfigureAwait(false);
        HostImsProbeResult? qmiProbe = null;
        var qmiReader = _qmi;
        IReadOnlyList<QmiImsProfile> qmiProfiles = [];
        var qmiProfilesChecked = false;

        if (simInserted == false && qmiReader is IQmiImsProfileReader profileReader && !_qmiDisabled)
        {
            try
            {
                qmiProfiles = await profileReader.GetConfiguredImsProfilesAsync(ct).ConfigureAwait(false);
                qmiProfilesChecked = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LastQmiFallbackReason = $"QMI WDS profile preflight {ex.GetType().Name}; using AT when available.";
            }
        }

        if (simInserted != false && qmiReader is not null && !_qmiDisabled)
        {
            try
            {
                var qmiIms = await qmiReader.GetActiveImsPdnAsync(ct).ConfigureAwait(false);
                if (qmiIms is not null)
                {
                    qmiProbe = BuildQmiHostImsProbe(simInserted, qmiIms, DescribeControlStack());
                    if (qmiProbe.CanAttemptWindowsIms) return qmiProbe;
                    LastQmiFallbackReason = "QMI WDS IMS context has no verified Windows route; checking AT contexts.";
                }
                else LastQmiFallbackReason = "QMI WDS did not expose a complete active IMS context; using AT.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                LastQmiFallbackReason = $"QMI WDS {ex.GetType().Name}; using AT.";
            }
        }

        if (!_session.IsOpen && qmiProbe is not null) return qmiProbe;

        if (simInserted == false && !_session.IsOpen && qmiReader is not null && !_qmiDisabled)
        {
            var profiles = qmiProfiles.Select(profile => new HostImsPdnContext(
                -1000 - profile.ProfileIndex, profile.PdpType, profile.Apn, false,
                [], [], [], [], [])).ToArray();
            return new HostImsProbeResult(
                HostImsReadiness.SimUnavailable,
                null,
                null,
                profiles,
                $"QMI confirmed that no selected SIM is present; WDS {(qmiProfilesChecked ? $"found {profiles.Length} configured IMS profiles" : "profile query was unavailable")}. AT/USB mode was not checked because the AT port is closed.")
            {
                SimInserted = false,
                ProbeSource = profiles.Length > 0 ? "QMI UIM + WDS profile" : "QMI UIM",
                ControlStackStatus = DescribeControlStack(),
                WindowsCellularAdapters = HostImsPdnParser.GetWindowsCellularAdapters()
            };
        }

        var configuredResponse = await _session.ExecuteCommandAsync("AT+CGDCONT?", 4000, ct).ConfigureAwait(false);
        var imsResponse = await _session.ExecuteCommandAsync("AT+QCFG=\"ims\"", 3000, ct).ConfigureAwait(false);
        var usbResponse = await _session.ExecuteCommandAsync("AT+QCFG=\"usbnet\"", 3000, ct).ConfigureAwait(false);
        var configured = HostImsPdnParser.ParseConfigured(configuredResponse.Lines)
            .Where(context => HostImsPdnParser.IsImsApn(context.Apn))
            .ToArray();
        var imsSetting = HostImsPdnParser.ParseQuectelImsEnabled(imsResponse.Lines);
        var usbMode = HostImsPdnParser.ParseQuectelUsbNetworkMode(usbResponse.Lines);
        if (simInserted == false)
        {
            // Dynamic PDP queries cannot establish IMS readiness without a SIM and
            // may wait for their full AT timeout. Keep the useful configuration and
            // USB-mode preflight, but do not infer an active data context.
            var preflightContexts = qmiProfiles.Count > 0
                ? qmiProfiles.Select(profile => new HostImsPdnContext(
                    -1000 - profile.ProfileIndex, profile.PdpType, profile.Apn, false,
                    [], [], [], [], [])).ToArray()
                : configured.Select(profile => new HostImsPdnContext(
                    profile.ContextId, profile.PdpType, profile.Apn, false,
                    [], [], [], [], [])).ToArray();
            var atAvailable = configuredResponse.Success || imsResponse.Success || usbResponse.Success;
            return new HostImsProbeResult(
                HostImsReadiness.SimUnavailable,
                imsSetting,
                usbMode,
                preflightContexts,
                $"No SIM is inserted or selected. AT preflight {(atAvailable ? "responded" : "did not respond successfully")}; USB network mode is {(usbMode == null ? "unreported" : "reported")}. IMS PDN/P-CSCF cannot be evaluated yet.")
            {
                SimInserted = false,
                AtControlAvailable = atAvailable,
                ProbeSource = qmiProfiles.Count > 0 ? "QMI WDS profile + AT" : "AT",
                ControlStackStatus = DescribeControlStack(),
                WindowsCellularAdapters = HostImsPdnParser.GetWindowsCellularAdapters()
            };
        }

        var activeResponse = await _session.ExecuteCommandAsync("AT+CGACT?", 4000, ct).ConfigureAwait(false);
        var runtimeResponse = await _session.ExecuteCommandAsync("AT+CGCONTRDP", 5000, ct).ConfigureAwait(false);
        var runtime = HostImsPdnParser.ParseRuntime(runtimeResponse.Lines)
            .Where(context => HostImsPdnParser.IsImsApn(context.Apn) || configured.Any(item => item.ContextId == context.ContextId))
            .ToArray();
        var activation = HostImsPdnParser.ParseActivation(activeResponse.Lines);
        var failedQueries = new List<string>();
        if (!configuredResponse.Success) failedQueries.Add("AT+CGDCONT?");
        if (!activeResponse.Success && runtime.Length == 0) failedQueries.Add("AT+CGACT?");
        if (!runtimeResponse.Success && HostImsPdnParser.IsRuntimeQueryFailureCritical(configured, activation))
            failedQueries.Add("AT+CGCONTRDP");
        var hostAddresses = HostImsPdnParser.GetHostAddresses();
        var ids = configured.Select(item => item.ContextId).Concat(runtime.Select(item => item.ContextId)).Distinct().Order().ToArray();
        var contexts = ids.Select(cid =>
        {
            var profile = configured.FirstOrDefault(item => item.ContextId == cid);
            var live = runtime.FirstOrDefault(item => item.ContextId == cid);
            var localAddresses = live?.LocalAddresses ?? [];
            var interfaces = localAddresses
                .Where(hostAddresses.ContainsKey)
                .SelectMany(address => hostAddresses[address])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new HostImsPdnContext(
                cid,
                profile?.PdpType ?? "unknown",
                live?.Apn ?? profile?.Apn ?? "ims",
                HostImsPdnParser.IsContextActive(cid, activation, live != null),
                localAddresses,
                live?.Gateways ?? [],
                live?.DnsServers ?? [],
                live?.PcscfServers ?? [],
                interfaces)
            {
                HostOwnedAddresses = localAddresses.Where(hostAddresses.ContainsKey).ToArray()
            };
        }).ToArray();

        var readiness = HostImsPdnParser.Classify(simInserted, failedQueries.Count > 0, contexts);
        var routeChecks = HostImsRouteVerifier.Check(HostImsPdnParser.GetEndpointCandidates(contexts));
        var summary = readiness switch
        {
            HostImsReadiness.ProbeFailed => $"IMS context queries failed ({string.Join(", ", failedQueries)}); configuration cannot be determined.",
            HostImsReadiness.HostRoutable when routeChecks.Any(check => check.IsVerified) =>
                "IMS address and P-CSCF are visible on Windows; at least one candidate has a matching Windows source and interface route.",
            HostImsReadiness.HostRoutable =>
                "IMS address and P-CSCF are visible on Windows, but no candidate has a verified P-CSCF route; registration is disabled.",
            HostImsReadiness.ModemInternalOnly => "The modem has an active IMS PDN, but Windows does not own its address/P-CSCF route. QMI/MBIM multi-PDN or PPP exposure is required.",
            HostImsReadiness.ConfiguredButInactive => "An IMS APN profile exists, but no active IMS PDN runtime address was reported.",
            _ => "No IMS PDP context was reported by the modem."
        };
        var atProbe = new HostImsProbeResult(
            readiness,
            imsSetting,
            usbMode,
            contexts,
            summary)
        {
            SimInserted = simInserted,
            ControlStackStatus = DescribeControlStack(),
            WindowsCellularAdapters = HostImsPdnParser.GetWindowsCellularAdapters(),
            RouteChecks = routeChecks
        };
        if (qmiProbe is null || atProbe.CanAttemptWindowsIms ||
            atProbe.Readiness == HostImsReadiness.HostRoutable)
            return atProbe;
        // WDS proved an active IMS context even when AT cannot see that
        // context. Do not erase this evidence with an empty AT CID list.
        return qmiProbe with
        {
            Summary = $"{qmiProbe.Summary} AT fallback: {atProbe.Summary}",
            ControlStackStatus = DescribeControlStack()
        };
    }

    private string DescribeControlStack() => _qmi is null
        ? "AT（未配置 QMI 端点）"
        : _qmiDisabled
            ? $"AT（QMI 已停用：{LastQmiFallbackReason ?? "身份核验失败"}）"
            : LastQmiFallbackReason is null
                ? "QMI 优先，AT 回退"
                : $"QMI/AT 回退：{LastQmiFallbackReason}";

    internal static HostImsProbeResult BuildQmiHostImsProbe(bool? simInserted,
        QmiImsPdn qmiIms, string controlStackStatus)
    {
        if (!HostImsPdnParser.IsImsApn(qmiIms.Apn) ||
            qmiIms.LocalAddress.Equals(System.Net.IPAddress.Any) ||
            qmiIms.LocalAddress.Equals(System.Net.IPAddress.IPv6Any) ||
            qmiIms.PcscfServers.Count == 0 ||
            qmiIms.PcscfServers.Any(address =>
                address.AddressFamily != qmiIms.LocalAddress.AddressFamily ||
                address.Equals(System.Net.IPAddress.Any) ||
                address.Equals(System.Net.IPAddress.IPv6Any)))
            throw new FormatException("QMI WDS did not return a complete IMS APN/address/P-CSCF context.");
        var hostAddresses = HostImsPdnParser.GetHostAddresses();
        var interfaces = hostAddresses.TryGetValue(qmiIms.LocalAddress, out var matches)
            ? matches : [];
        var context = new HostImsPdnContext(
            qmiIms.ProfileIndex is { } index ? -1000 - index : -1,
            "IP", qmiIms.Apn, true,
            [qmiIms.LocalAddress], [], [], qmiIms.PcscfServers, interfaces)
        {
            HostOwnedAddresses = interfaces.Length > 0 ? [qmiIms.LocalAddress] : []
        };
        var contexts = new[] { context };
        var readiness = HostImsPdnParser.Classify(simInserted, false, contexts);
        var routeChecks = HostImsRouteVerifier.Check(HostImsPdnParser.GetEndpointCandidates(contexts));
        var summary = readiness == HostImsReadiness.ModemInternalOnly
            ? "QMI WDS reports an active IMS APN and P-CSCF, but Windows does not own its address."
            : routeChecks.Any(check => check.IsVerified)
                ? "QMI WDS reports an active IMS APN; its address and P-CSCF route are owned by a Windows cellular interface."
                : "QMI WDS reports an active IMS APN, but the Windows P-CSCF route is not verified.";
        return new HostImsProbeResult(readiness, null, null, contexts, summary)
        {
            SimInserted = simInserted,
            ProbeSource = "QMI WDS",
            ControlStackStatus = controlStackStatus,
            WindowsCellularAdapters = HostImsPdnParser.GetWindowsCellularAdapters(),
            RouteChecks = routeChecks
        };
    }

    /// <summary>Reads physical/selected SIM presence without changing the baseband.</summary>
    public Task<bool?> GetSimInsertedAsync(CancellationToken ct = default) =>
        PreferQmiAsync(_qmi is null ? null : _qmi.GetSimInsertedAsync, GetSimInsertedFromAtAsync,
            value => value.HasValue, ct);

    private async Task<bool?> GetSimInsertedFromAtAsync(CancellationToken ct)
    {
        var simResponse = await _session.ExecuteCommandAsync("AT+QSIMSTAT?", 3000, ct).ConfigureAwait(false);
        var simInserted = HostImsPdnParser.ParseQuectelSimInserted(simResponse.Lines);
        if (simInserted == null)
        {
            var pinResponse = await _session.ExecuteCommandAsync("AT+CPIN?", 3000, ct).ConfigureAwait(false);
            simInserted = pinResponse.Success && pinResponse.Lines.Any(line => line.Contains("+CPIN:", StringComparison.OrdinalIgnoreCase))
                ? true
                : pinResponse.ErrorCode?.Trim().EndsWith("CME ERROR: 10", StringComparison.OrdinalIgnoreCase) == true
                    ? false
                    : null;
        }
        return simInserted;
    }

    public Task<string> GetImsiAsync(CancellationToken ct = default) =>
        PreferQmiAsync(_qmi is null ? null : _qmi.GetImsiAsync, GetImsiFromAtAsync,
            value => IsDecimalIdentifier(value, 14, 16), ct);

    private async Task<string> GetImsiFromAtAsync(CancellationToken ct)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CIMI", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            foreach (var line in resp.Lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(trimmed, @"\d{14,16}");
                if (match.Success) return match.Value;
            }
        }
        return string.Empty;
    }

    public Task<string> GetIccidAsync(CancellationToken ct = default) =>
        PreferQmiAsync(_qmi is null ? null : _qmi.GetIccidAsync, GetIccidFromAtAsync,
            value => IsDecimalIdentifier(value, 18, 22), ct);

    private async Task<string> GetIccidFromAtAsync(CancellationToken ct)
    {
        var resp = await _session.ExecuteCommandAsync("AT+QCCID", 2000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CCID", 2000, ct).ConfigureAwait(false);

        if (resp.Success)
        {
            foreach (var line in resp.Lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(trimmed, @"\d{18,22}");
                if (match.Success) return match.Value;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Reads the subscriber's own number from EF-MSISDN through the standard
    /// 3GPP AT+CNUM command. Many data-only and prepaid profiles intentionally
    /// leave this file empty; an empty result therefore means "not supplied by
    /// the card", not a modem or parsing failure.
    /// </summary>
    public async Task<string> GetPhoneNumberAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CNUM", 3000, ct).ConfigureAwait(false);
        return resp.Success ? ParseOwnPhoneNumber(resp.Lines) : string.Empty;
    }

    /// <summary>
    /// Reads the SMS service-centre address provisioned on the active SIM.  IMS
    /// SMS uses it both as the RP destination and, absent a SIM-provided PSI, as
    /// the SIP MESSAGE target.  It must therefore come from the SIM/modem rather
    /// than a carrier-specific fallback compiled into the client.
    /// </summary>
    public async Task<string> GetSmsCenterAsync(CancellationToken ct = default)
    {
        var response = await _session.ExecuteCommandAsync("AT+CSCA?", 3000, ct).ConfigureAwait(false);
        return response.Success ? ParseSmsCenter(response.Lines) : string.Empty;
    }

    internal static string ParseSmsCenter(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line,
                @"^\s*\+CSCA:\s*""(?<number>\+?[0-9]{1,20})""\s*,\s*\d+\s*$",
                RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["number"].Value;
        }
        return string.Empty;
    }

    /// <summary>
    /// Reads the SIM service-provider name reported by Quectel firmware instead
    /// of substituting the currently serving network name.
    /// </summary>
    public async Task<string> GetServiceProviderNameAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+QSPN", 2500, ct).ConfigureAwait(false);
        return resp.Success ? ParseServiceProviderName(resp.Lines) : string.Empty;
    }

    internal static string ParseServiceProviderName(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            // Quectel: +QSPN: <FNN>,<SNN>,<SPN>,<alphabet>,<RPLMN>
            var match = Regex.Match(
                line,
                "^\\s*\\+QSPN:\\s*\"(?<fnn>[^\"]*)\"\\s*,\\s*\"(?<snn>[^\"]*)\"\\s*,\\s*\"(?<spn>[^\"]*)\"\\s*,\\s*(?<alphabet>[01])",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var spn = match.Groups["spn"].Value.Trim();
            if (spn.Length == 0)
                return string.Empty;

            if (match.Groups["alphabet"].Value == "1" &&
                spn.Length % 4 == 0 && spn.All(Uri.IsHexDigit))
            {
                try
                {
                    return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(spn)).TrimEnd('\0').Trim();
                }
                catch (FormatException)
                {
                }
            }

            return spn;
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads the permanent subscriber identity from USIM EF_IMSI. Some multi-IMSI
    /// profiles expose a temporary roaming identity through AT+CIMI while keeping
    /// the home identity in this standard file.
    /// </summary>
    public async Task<string> GetPermanentImsiAsync(CancellationToken ct = default)
    {
        foreach (var command in new[]
                 {
                     "AT+CRSM=176,28423,0,0,9",
                     "AT+CRSM=176,28423,0,0,9,\"\",\"3F007FFF\""
                 })
        {
            var response = await _session.ExecuteCommandAsync(command, 2500, ct).ConfigureAwait(false);
            var imsi = ParsePermanentImsi(response.Lines);
            if (imsi.Length is >= 14 and <= 16)
                return imsi;
        }
        return string.Empty;
    }

    public static string ParsePermanentImsi(IEnumerable<string> lines)
    {
        foreach (var hex in ExtractSuccessfulCrsmData(lines))
        {
            byte[] bytes;
            try { bytes = Convert.FromHexString(hex); }
            catch (FormatException) { continue; }
            if (bytes.Length < 2)
                continue;

            var identityOctets = Math.Min(bytes[0], bytes.Length - 1);
            if (identityOctets < 7)
                continue;

            var digits = new StringBuilder(identityOctets * 2 - 1);
            var firstDigit = bytes[1] >> 4;
            if (firstDigit > 9)
                continue;
            digits.Append((char)('0' + firstDigit));

            for (var index = 2; index <= identityOctets; index++)
            {
                var low = bytes[index] & 0x0F;
                var high = bytes[index] >> 4;
                if (low <= 9) digits.Append((char)('0' + low));
                else if (low != 0x0F) { digits.Clear(); break; }
                if (high <= 9) digits.Append((char)('0' + high));
                else if (high != 0x0F) { digits.Clear(); break; }
            }

            var imsi = digits.ToString();
            if (imsi.Length is >= 14 and <= 16)
                return imsi;
        }
        return string.Empty;
    }

    /// <summary>
    /// Reads the MNC length from USIM EF_AD (3GPP TS 31.102). This is the only
    /// subscriber-owned source that removes the ambiguity between two- and
    /// three-digit MNCs; guessing from an IMSI or carrier-name table is not
    /// globally correct.
    /// </summary>
    public async Task<int?> GetHomeMncLengthAsync(CancellationToken ct = default)
    {
        foreach (var command in new[]
                 {
                     "AT+CRSM=176,28589,0,0,4",
                     "AT+CRSM=176,28589,0,0,4,\"\",\"3F007FFF\""
                 })
        {
            var response = await _session.ExecuteCommandAsync(command, 2500, ct).ConfigureAwait(false);
            var length = ParseHomeMncLength(response.Lines);
            if (length is 2 or 3)
                return length;
        }

        return null;
    }

    public static int? ParseHomeMncLength(IEnumerable<string> lines)
    {
        foreach (var data in ExtractSuccessfulCrsmData(lines))
        {
            if (data.Length < 2)
                continue;
            var finalOctet = Convert.ToByte(data[^2..], 16);
            var mncLength = finalOctet & 0x0F;
            if (mncLength is 2 or 3)
                return mncLength;
        }

        return null;
    }

    /// <summary>
    /// Reads equivalent/home PLMN candidates from the USIM. Entries come from
    /// EF_EHPLMN and EF_HPLMNwAcT and therefore follow the active profile rather
    /// than a bundled carrier database.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetHomePlmnsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        foreach (var item in new[]
                 {
                     (Command: "AT+CRSM=176,28633,0,0,0", RecordLength: 3), // EF_EHPLMN
                     (Command: "AT+CRSM=176,28514,0,0,0", RecordLength: 5)  // EF_HPLMNwAcT
                 })
        {
            var response = await _session.ExecuteCommandAsync(item.Command, 2500, ct).ConfigureAwait(false);
            foreach (var plmn in ParsePlmnList(response.Lines, item.RecordLength))
            {
                if (!result.Contains(plmn, StringComparer.Ordinal))
                    result.Add(plmn);
            }
        }
        return result;
    }

    public static IReadOnlyList<string> ParsePlmnList(IEnumerable<string> lines, int recordLength)
    {
        if (recordLength < 3)
            throw new ArgumentOutOfRangeException(nameof(recordLength));

        var result = new List<string>();
        foreach (var hex in ExtractSuccessfulCrsmData(lines))
        {
            byte[] bytes;
            try { bytes = Convert.FromHexString(hex); }
            catch (FormatException) { continue; }

            for (var offset = 0; offset + recordLength <= bytes.Length; offset += recordLength)
            {
                var plmn = DecodePlmn(bytes.AsSpan(offset, 3));
                if (plmn != null && !result.Contains(plmn, StringComparer.Ordinal))
                    result.Add(plmn);
            }
        }
        return result;
    }

    private static string? DecodePlmn(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 3 || encoded[0] == 0xFF && encoded[1] == 0xFF && encoded[2] == 0xFF)
            return null;

        var mcc1 = encoded[0] & 0x0F;
        var mcc2 = encoded[0] >> 4;
        var mcc3 = encoded[1] & 0x0F;
        var mnc1 = encoded[2] & 0x0F;
        var mnc2 = encoded[2] >> 4;
        var mnc3 = encoded[1] >> 4;
        if (mcc1 > 9 || mcc2 > 9 || mcc3 > 9 || mnc1 > 9 || mnc2 > 9 || mnc3 is > 9 and not 0x0F)
            return null;

        return $"{mcc1}{mcc2}{mcc3}{mnc1}{mnc2}" + (mnc3 == 0x0F ? string.Empty : mnc3);
    }

    private static IEnumerable<string> ExtractSuccessfulCrsmData(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(
                line,
                @"\+CRSM:\s*(?<sw1>\d+)\s*,\s*(?<sw2>\d+)\s*,\s*""(?<data>[0-9A-Fa-f]+)""",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;
            var sw1 = int.Parse(match.Groups["sw1"].Value);
            var sw2 = int.Parse(match.Groups["sw2"].Value);
            if (sw1 is 144 or 145 or 159 && sw2 == 0)
                yield return match.Groups["data"].Value;
        }
    }

    internal static string ParseOwnPhoneNumber(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            // 3GPP TS 27.007: +CNUM: [<alpha>],<number>,<type>[,...]
            // Quectel firmware may omit <alpha>, but keeps the comma before the
            // quoted number. Accept both forms without treating the alpha label
            // itself as a phone number.
            var match = Regex.Match(
                line,
                @"^\s*\+CNUM:\s*(?:""[^""]*""\s*)?,?\s*""?(?<number>\+?[0-9][0-9 ()-]*)""?\s*,\s*(?<type>\d+)",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var number = Regex.Replace(match.Groups["number"].Value, @"[\s()-]", "");
            if (number.Length == 0) continue;

            // TON 145 denotes an international number. Some cards omit the
            // literal '+' even though the type says it is international.
            if (match.Groups["type"].Value == "145" && number[0] != '+')
                number = "+" + number;

            return number;
        }

        return string.Empty;
    }

    public async Task<bool> RefreshSimAsync(CancellationToken ct = default, bool preserveFlightMode = false)
    {
        // Cycle the baseband so CIMI/QCCID/CNUM cannot return the previous
        // profile from a modem cache. Restore CFUN=4 when the caller was in
        // flight mode instead of accidentally enabling cellular RF.
        await _session.ExecuteCommandAsync("AT+CFUN=0", 3000, ct).ConfigureAwait(false);
        try { await Task.Delay(800, ct).ConfigureAwait(false); } catch { }
        var targetCfun = preserveFlightMode ? 4 : 1;
        await _session.ExecuteCommandAsync($"AT+CFUN={targetCfun}", 3000, ct).ConfigureAwait(false);
        _isRadioStateKnown = true;
        _isRadioDisabled = preserveFlightMode;
        _radioStatusReportingEnabled = !preserveFlightMode;

        // A single CPIN: READY can be the tail end of the previous eSIM
        // profile's REFRESH. Require two consecutive observations before
        // callers are allowed to inspect card files or start AKA.
        var consecutiveReady = 0;
        for (int i = 0; i < 16; i++)
        {
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
            var cpin = await _session.ExecuteCommandAsync("AT+CPIN?", 1000, ct).ConfigureAwait(false);
            if (cpin.Success && cpin.FirstDataLine.Contains("READY"))
            {
                consecutiveReady++;
                if (consecutiveReady >= 2)
                {
                    _eventBus?.Publish(EventTopics.ModemSim, "Modem", "READY");
                    return true;
                }
            }
            else consecutiveReady = 0;
        }

        return false;
    }

    public async Task<bool> SetFlightModeAsync(bool enable, CancellationToken ct = default)
    {
        var previousKnown = _isRadioStateKnown;
        var previousDisabled = _isRadioDisabled;
        var previousReportingEnabled = _radioStatusReportingEnabled;

        // Some modems emit a transient registration status before CFUN returns OK.
        _isRadioStateKnown = true;
        _isRadioDisabled = enable;
        _radioStatusReportingEnabled = !enable;
        try
        {
            string cmd = enable ? "AT+CFUN=4" : "AT+CFUN=1";
            var resp = await _session.ExecuteCommandAsync(cmd, 3000, ct).ConfigureAwait(false);
            if (resp.Success)
            {
                // An OK only confirms that the command was accepted.  Confirm
                // the resulting functional level so callers never persist a
                // misleading flight-mode state when the modem declines or
                // defers the radio transition.
                var verifiedCfun = await GetFlightModeAsync(ct).ConfigureAwait(false);
                var verified = enable ? verifiedCfun is 0 or 4 : verifiedCfun == 1;
                if (!verified)
                {
                    _isRadioStateKnown = previousKnown;
                    _isRadioDisabled = previousDisabled;
                    _radioStatusReportingEnabled = previousReportingEnabled;
                    return false;
                }
                _eventBus?.Publish("modem.flightmode", "Modem", enable ? "ON" : "OFF");
                try { FlightModeChanged?.Invoke(this, new FlightModeChangedEventArgs(enable, enable ? 4 : 1)); } catch { }
                return true;
            }
        }
        catch
        {
            _isRadioStateKnown = previousKnown;
            _isRadioDisabled = previousDisabled;
            _radioStatusReportingEnabled = previousReportingEnabled;
            throw;
        }
        _isRadioStateKnown = previousKnown;
        _isRadioDisabled = previousDisabled;
        _radioStatusReportingEnabled = previousReportingEnabled;
        return false;
    }

    public async Task<int> GetFlightModeAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CFUN?", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+CFUN:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var cfun))
            {
                _isRadioDisabled = cfun is 0 or 4;
                _isRadioStateKnown = true;
                return cfun;
            }
        }
        return 1;
    }

    private bool ShouldPublishUrc(string line)
    {
        if (!IsRadioStatusUrc(line)) return true;
        return _isRadioStateKnown && !_isRadioDisabled && _radioStatusReportingEnabled;
    }

    internal static bool IsRadioStatusUrc(string line) =>
        line.StartsWith("+CREG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CEREG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CGREG:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("+CSQ:", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> RebootBasebandAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CFUN=1,1", 3000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<SignalQuality> GetSignalAsync(CancellationToken ct = default)
    {
        if (_isRadioStateKnown && !_isRadioDisabled)
            _radioStatusReportingEnabled = true;
        var signal = await PreferQmiAsync<SignalQuality?>(
            _qmi is null ? null : _qmi.GetSignalAsync,
            async token => await GetSignalFromAtAsync(token).ConfigureAwait(false),
            value => value is { RssiRaw: not 99 }, ct).ConfigureAwait(false)
            ?? new SignalQuality(99, 0, 0, "Unknown");
        try { SignalChanged?.Invoke(this, new SignalChangedEventArgs(signal)); } catch { }
        return signal;
    }

    private async Task<SignalQuality> GetSignalFromAtAsync(CancellationToken ct)
    {
        if (_isRadioStateKnown && !_isRadioDisabled)
        {
            _radioStatusReportingEnabled = true;
        }
        var resp = await _session.ExecuteCommandAsync("AT+CSQ", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+CSQ:\s*(\d+),(\d+)");
            if (match.Success)
            {
                var raw = int.Parse(match.Groups[1].Value);
                if (raw == 99 || raw < 0)
                {
                    var noSig = new SignalQuality(99, 0, 0, "LTE/NR");
                    return noSig;
                }

                var dbm = -113 + (raw * 2);
                var bars = raw switch
                {
                    >= 19 => 5, // >= -75 dBm (极佳)
                    >= 14 => 4, // >= -85 dBm (良好)
                    >= 9 => 3,  // >= -95 dBm (一般)
                    >= 4 => 2,  // >= -105 dBm (较弱)
                    >= 1 => 1,  // > -113 dBm (微弱)
                    _ => 0      // <= -113 dBm (无信号)
                };
                var sq = new SignalQuality(raw, dbm, bars, "LTE/NR");
                return sq;
            }
        }
        var defSq = new SignalQuality(99, 0, 0, "Unknown");
        return defSq;
    }

    public async Task<NetworkRegistration> GetRegistrationAsync(CancellationToken ct = default)
    {
        if (_isRadioStateKnown && !_isRadioDisabled)
            _radioStatusReportingEnabled = true;
        var registration = await PreferQmiAsync<NetworkRegistration?>(
            _qmi is null ? null : _qmi.GetRegistrationAsync,
            async token => await GetRegistrationFromAtAsync(token).ConfigureAwait(false),
            value => value is not null && value.Status != NetworkRegStatus.Unknown,
            ct).ConfigureAwait(false)
            ?? new NetworkRegistration(NetworkRegStatus.Unknown, null, null, null);
        try
        {
            RegistrationChanged?.Invoke(this,
                new NetworkRegistrationChangedEventArgs(registration,
                    NetworkRegStatus.Unknown, registration.Status));
        }
        catch { }
        return registration;
    }

    private async Task<NetworkRegistration> GetRegistrationFromAtAsync(CancellationToken ct)
    {
        if (_isRadioStateKnown && !_isRadioDisabled)
        {
            _radioStatusReportingEnabled = true;
        }
        var resp = await _session.ExecuteCommandAsync("AT+CEREG?", 2000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CREG?", 2000, ct).ConfigureAwait(false);

        var regStatus = NetworkRegStatus.Unknown;
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+(?:CEREG|CREG):\s*(?:\d+,)?(\d+)");
            if (match.Success)
            {
                regStatus = (NetworkRegStatus)int.Parse(match.Groups[1].Value);
            }
        }

        var copsResp = await _session.ExecuteCommandAsync("AT+COPS?", 2000, ct).ConfigureAwait(false);
        string? opName = null;
        if (copsResp.Success)
        {
            var match = Regex.Match(copsResp.FirstDataLine, @"\+COPS:\s*\d+,\d+,""([^""]+)""");
            if (match.Success) opName = match.Groups[1].Value;
        }

        var reg = new NetworkRegistration(regStatus, opName, "LTE", null);
        return reg;
    }

    public async Task<bool> DialVoiceAsync(string phoneNumber, CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallDialing, "Modem", phoneNumber);
        _eventBus?.Publish(EventTopics.CallState, "Modem", "RINGING");
        var resp = await _session.ExecuteCommandAsync($"ATD{phoneNumber};", 5000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<bool> HangupVoiceAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("ATH", 3000, ct).ConfigureAwait(false);
        if (!resp.Success)
            resp = await _session.ExecuteCommandAsync("AT+CHUP", 3000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            _eventBus?.Publish(EventTopics.CallEnded, "Modem", "HANGUP");
            _eventBus?.Publish(EventTopics.CallState, "Modem", "ENDED");
        }
        return resp.Success;
    }

    public async Task<bool> AnswerVoiceAsync(CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallState, "Modem", "ACTIVE");
        var resp = await _session.ExecuteCommandAsync("ATA", 3000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    /// <summary>
    /// Reads the baseband call table. Only entries with mode=0 are voice calls;
    /// Quectel firmware may also expose always-on IMS/data bearers in +CLCC.
    /// </summary>
    public async Task<IReadOnlyList<ModemVoiceCall>> GetVoiceCallsAsync(CancellationToken ct = default)
    {
        var resp = await _session.ExecuteCommandAsync("AT+CLCC", 2000, ct).ConfigureAwait(false);
        if (!resp.Success) return Array.Empty<ModemVoiceCall>();

        return ParseVoiceCalls(resp.Lines);
    }

    public static IReadOnlyList<ModemVoiceCall> ParseVoiceCalls(IEnumerable<string> lines)
    {
        var result = new List<ModemVoiceCall>();
        foreach (var line in lines)
        {
            var match = Regex.Match(line,
                "\\+CLCC:\\s*(\\d+),(\\d+),(\\d+),(\\d+),(\\d+)(?:,\"([^\"]*)\",(\\d+))?");
            if (!match.Success) continue;

            result.Add(new ModemVoiceCall(
                Index: int.Parse(match.Groups[1].Value),
                IsOutgoing: match.Groups[2].Value == "0",
                Status: int.Parse(match.Groups[3].Value),
                Mode: int.Parse(match.Groups[4].Value),
                IsMultiparty: match.Groups[5].Value == "1",
                Number: match.Groups[6].Success ? match.Groups[6].Value : null,
                NumberType: match.Groups[7].Success ? int.Parse(match.Groups[7].Value) : null));
        }

        return result.Where(call => call.Mode == 0).ToArray();
    }

    /// <summary>
    /// Enables the Quectel USB Audio Class PCM route for a cellular voice call.
    /// A false result means the installed modem firmware does not expose voice PCM.
    /// </summary>
    public async Task<bool> EnableUsbVoiceAudioAsync(CancellationToken ct = default)
    {
        var set = await _session.ExecuteCommandAsync("AT+QPCMV=1,2", 2000, ct).ConfigureAwait(false);
        if (!set.Success) return false;

        var query = await _session.ExecuteCommandAsync("AT+QPCMV?", 2000, ct).ConfigureAwait(false);
        return query.Success && query.Lines.Any(line =>
            Regex.IsMatch(line, @"\+QPCMV:\s*1\s*,\s*2", RegexOptions.IgnoreCase));
    }

    public async Task DisableUsbVoiceAudioAsync(CancellationToken ct = default)
    {
        try { await _session.ExecuteCommandAsync("AT+QPCMV=0", 2000, ct).ConfigureAwait(false); }
        catch { }
    }

    public async Task<bool> SendDtmfAsync(char digit, CancellationToken ct = default)
    {
        _eventBus?.Publish(EventTopics.CallDtmf, "Modem", digit.ToString());
        var resp = await _session.ExecuteCommandAsync($"AT+VTS={digit}", 2000, ct).ConfigureAwait(false);
        return resp.Success;
    }

    public async Task<int> GetSimSlotAsync(CancellationToken ct = default)
    {
        // Try Quectel AT+QUIMSLOT?
        var resp = await _session.ExecuteCommandAsync("AT+QUIMSLOT?", 2000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            var match = Regex.Match(resp.FirstDataLine, @"\+QUIMSLOT:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var slot))
                return slot;
        }

        // Try standard AT+QDSIM?
        var resp2 = await _session.ExecuteCommandAsync("AT+QDSIM?", 2000, ct).ConfigureAwait(false);
        if (resp2.Success)
        {
            var match = Regex.Match(resp2.FirstDataLine, @"\+QDSIM:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var slot))
                return slot;
        }

        return 1;
    }

    public async Task<(byte[] Res, byte[] Ck, byte[] Ik)?> AuthenticateUsimAkaAsync(byte[] rand, byte[] autn, CancellationToken ct = default)
    {
        if (rand.Length != 16 || autn.Length != 16)
            return null;

        // 3GPP TS 31.102 USIM Authenticate APDU:
        // CLA=00, INS=88, P1=00, P2=81 (3G context / UMTS AKA), Lc=22 (34 bytes), Data: 10 <RAND 16B> 10 <AUTN 16B>
        var payload = new byte[34];
        payload[0] = 0x10;
        Array.Copy(rand, 0, payload, 1, 16);
        payload[17] = 0x10;
        Array.Copy(autn, 0, payload, 18, 16);

        var apduHex = "0088008122" + Convert.ToHexString(payload);

        // 1. Try CCHO / CGLA with USIM ADF
        int channelId = 0;
        foreach (var aid in new[] { "A0000000871002", "A0000000871002FF86FF0389FFFFFFFF" })
        {
            var openResp = await _session.ExecuteCommandAsync($"AT+CCHO=\"{aid}\"", 2000, ct).ConfigureAwait(false);
            if (openResp.Success)
            {
                var m = Regex.Match(openResp.RawOutput ?? "", @"\+CCHO:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var cid) && cid > 0)
                {
                    channelId = cid;
                    break;
                }
            }
        }

        string? rawApduHexResp = null;
        if (channelId > 0)
        {
            try
            {
                var claByte = (byte)channelId;
                var modApduHex = claByte.ToString("X2") + apduHex[2..];
                var cglaCmd = $"AT+CGLA={channelId},{modApduHex.Length},\"{modApduHex}\"";
                var cglaResp = await _session.ExecuteCommandAsync(cglaCmd, 4000, ct).ConfigureAwait(false);
                if (cglaResp.Success)
                {
                    var match = Regex.Match(cglaResp.RawOutput ?? "", @"\+CGLA:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
                    if (match.Success)
                        rawApduHexResp = match.Groups[1].Value;
                }
            }
            finally
            {
                await _session.ExecuteCommandAsync($"AT+CCHC={channelId}", 2000, ct).ConfigureAwait(false);
            }
        }

        // 2. Fallback to AT+CSIM
        if (string.IsNullOrEmpty(rawApduHexResp))
        {
            var csimCmd = $"AT+CSIM={apduHex.Length},\"{apduHex}\"";
            var csimResp = await _session.ExecuteCommandAsync(csimCmd, 4000, ct).ConfigureAwait(false);
            if (csimResp.Success)
            {
                var match = Regex.Match(csimResp.RawOutput ?? "", @"\+CSIM:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
                if (match.Success)
                    rawApduHexResp = match.Groups[1].Value;
            }
        }

        if (string.IsNullOrEmpty(rawApduHexResp))
            return null;

        var respBytes = Convert.FromHexString(rawApduHexResp);
        // 3GPP TS 31.102 Success response format:
        // Tag 0xDB (GSM/UMTS Success), Len, Tag 0x... RES, Tag 0x... CK, Tag 0x... IK
        if (respBytes.Length >= 4 && respBytes[0] == 0xDB)
        {
            int offset = 1;
            int totalLen = respBytes[offset++];
            if (offset < respBytes.Length)
            {
                int resLen = respBytes[offset++];
                if (offset + resLen <= respBytes.Length)
                {
                    var res = respBytes[offset..(offset + resLen)];
                    offset += resLen;

                    if (offset < respBytes.Length)
                    {
                        int ckLen = respBytes[offset++];
                        if (offset + ckLen <= respBytes.Length)
                        {
                            var ck = respBytes[offset..(offset + ckLen)];
                            offset += ckLen;

                            if (offset < respBytes.Length)
                            {
                                int ikLen = respBytes[offset++];
                                if (offset + ikLen <= respBytes.Length)
                                {
                                    var ik = respBytes[offset..(offset + ikLen)];
                                    return (res, ck, ik);
                                }
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    public async Task<bool> SetSimSlotAsync(int slot, CancellationToken ct = default)
    {
        // Quectel / DJI Cellular Dongle hardware SIM slot switch (1=Physical SIM / 2=Built-in eSIM)
        var resp = await _session.ExecuteCommandAsync($"AT+QUIMSLOT={slot}", 5000, ct).ConfigureAwait(false);
        if (resp.Success)
        {
            try { SimStateChanged?.Invoke(this, new SimStateChangedEventArgs(null, slot, "SWITCHED")); } catch { }
            return true;
        }

        var resp2 = await _session.ExecuteCommandAsync($"AT+QDSIM={slot}", 5000, ct).ConfigureAwait(false);
        if (resp2.Success)
        {
            try { SimStateChanged?.Invoke(this, new SimStateChangedEventArgs(null, slot, "SWITCHED")); } catch { }
            return true;
        }
        return false;
    }

    public async Task<byte[]> SendCsimApduAsync(byte[] apdu, CancellationToken ct = default, int timeoutMs = 10000)
    {
        var apduHex = HexUtils.ToHexString(apdu);
        AtResponse? resp = null;

        // Retry up to 3 times for transient +CME ERROR: 0 (SIM busy / channel race per VoCat)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            resp = await _session.ExecuteCommandAsync($"AT+CSIM={apduHex.Length},\"{apduHex}\"", timeoutMs, ct).ConfigureAwait(false);
            if (resp.Success) break;

            bool isTransient = resp.Lines.Any(l => l.Contains("+CME ERROR: 0", StringComparison.OrdinalIgnoreCase));
            if (isTransient && attempt < 2)
            {
                await Task.Delay(60, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        if (resp == null || !resp.Success)
            throw new InvalidOperationException($"AT+CSIM failed: {string.Join(" ", resp?.Lines ?? Array.Empty<string>())}");

        foreach (var line in resp.Lines)
        {
            var match = Regex.Match(line, @"\+CSIM:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
            if (match.Success)
            {
                return HexUtils.FromHexString(match.Groups[1].Value);
            }
        }

        throw new InvalidOperationException($"Could not extract CSIM response from: {string.Join(" ", resp.Lines)}");
    }

    public async Task<AtResponse> SendRawAtCommandAsync(string command, int timeoutMs = 3000, CancellationToken ct = default)
    {
        return await _session.ExecuteCommandAsync(command, timeoutMs, ct).ConfigureAwait(false);
    }

    public async Task<AtResponse> SendPduWithPromptAsync(int tpduLength, string pduHex, CancellationToken ct = default)
    {
        return await _session.ExecutePromptCommandAsync($"AT+CMGS={tpduLength}", pduHex, promptTimeoutMs: 3000, completionTimeoutMs: 15000, ct: ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Configures modem for 3GPP PDU SMS mode, preferred storage, and unsolicited event notifications (AT+CNMI).
    /// </summary>
    public async Task<bool> InitializeSmsModeAsync(CancellationToken ct = default)
    {
        if (!IsOpen) return false;

        try
        {
            // Enable caller ID and extended call result codes so incoming
            // cellular calls are delivered as RING/+CRING and +CLIP URCs.
            await _session.ExecuteCommandAsync("AT+CLIP=1", 1000, ct).ConfigureAwait(false);
            await _session.ExecuteCommandAsync("AT+CRC=1", 1000, ct).ConfigureAwait(false);

            // 1. Select Phase 2+ SMS service
            await _session.ExecuteCommandAsync("AT+CSMS=1", 1000, ct).ConfigureAwait(false);

            // 2. Set SMS format to PDU mode (0)
            await _session.ExecuteCommandAsync("AT+CMGF=0", 1000, ct).ConfigureAwait(false);

            // 3. Set Preferred Storage to SIM (SM) or device memory (ME)
            var cpmsResp = await _session.ExecuteCommandAsync("AT+CPMS=\"SM\",\"SM\",\"SM\"", 2000, ct).ConfigureAwait(false);
            if (!cpmsResp.Success)
            {
                await _session.ExecuteCommandAsync("AT+CPMS=\"ME\",\"ME\",\"ME\"", 2000, ct).ConfigureAwait(false);
            }

            // 4. Configure New Message Indications to TE (+CMTI for incoming SMS, +CDSI for status reports)
            // mode=2 (buffer when busy), mt=1 (+CMTI with memory & index), bm=0, ds=1 (+CDSI for delivery reports), bfr=0
            var cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,1,0,1,0", 2000, ct).ConfigureAwait(false);
            if (!cnmiResp.Success)
            {
                cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,1,0,0,0", 2000, ct).ConfigureAwait(false);
                if (!cnmiResp.Success)
                {
                    cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=1,1,0,0,0", 2000, ct).ConfigureAwait(false);
                    if (!cnmiResp.Success)
                    {
                        // Direct-to-TE delivery fallback (+CMT/+CDS).
                        cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,2,0,1,0", 2000, ct).ConfigureAwait(false);
                        if (!cnmiResp.Success)
                            cnmiResp = await _session.ExecuteCommandAsync("AT+CNMI=2,2,0,0,0", 2000, ct).ConfigureAwait(false);
                    }
                }
            }

            return cnmiResp.Success;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { ConnectionChanged?.Invoke(this, new ModemConnectionChangedEventArgs(PortName, false)); } catch { }
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (_qmi is not null) await _qmi.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public record SerialPortDetail(
    string PortName,
    string DeviceKey,
    bool IsQuectel,
    bool IsAtPort,
    bool IsNmeaOrDm,
    bool IsBluetooth
);

public record ModemVoiceCall(
    int Index,
    bool IsOutgoing,
    int Status,
    int Mode,
    bool IsMultiparty,
    string? Number,
    int? NumberType
);

public static class WindowsModemDetector
{
    public static List<SerialPortDetail> GetDetailedPorts()
    {
        var list = new List<SerialPortDetail>();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
                if (key != null)
                {
                    foreach (var valName in key.GetValueNames())
                    {
                        var port = key.GetValue(valName)?.ToString();
                        if (string.IsNullOrEmpty(port)) continue;

                        bool isBth = valName.Contains("Bth", StringComparison.OrdinalIgnoreCase);
                        bool isQuectel = valName.Contains("QCUSB", StringComparison.OrdinalIgnoreCase) ||
                                         valName.Contains("Quectel", StringComparison.OrdinalIgnoreCase);

                        // In Qualcomm / Quectel USB architecture:
                        // _3 / _4 is the AT Command interface (COM6)
                        // _1 is Diagnostic Monitor (DM / COM8)
                        // _2 is NMEA GPS stream (COM7)
                        bool isAt = isQuectel && (valName.EndsWith("_3") || valName.EndsWith("_4") || port.Equals("COM6", StringComparison.OrdinalIgnoreCase));
                        bool isNmeaDm = isQuectel && (valName.EndsWith("_1") || valName.EndsWith("_2"));

                        list.Add(new SerialPortDetail(port, valName, isQuectel, isAt, isNmeaDm, isBth));
                    }
                }
            }
            catch { }
        }

        if (list.Count == 0)
        {
            foreach (var p in SerialPort.GetPortNames())
            {
                list.Add(new SerialPortDetail(p, p, false, false, false, false));
            }
        }

        return list;
    }


    public static string[] GetAvailableComPorts()
    {
        var details = GetDetailedPorts();
        return details.Select(d => d.PortName).Distinct().ToArray();
    }

    public static async Task<string?> ProbeModemPortAsync(int baudRate = 115200, CancellationToken ct = default)
    {
        var details = GetDetailedPorts();

        // Sort candidates:
        // Priority 1: Verified AT ports (e.g. COM6 / QCUSB_COM6_3)
        // Priority 2: Standard non-cellular serial ports
        // Exclude: Bluetooth virtual ports & Quectel NMEA/DM ports (which do not accept AT commands)
        var candidates = details
            .Where(d => !d.IsBluetooth && !d.IsNmeaOrDm)
            .OrderByDescending(d => d.IsAtPort)
            .ThenByDescending(d => d.IsQuectel)
            .Select(d => d.PortName)
            .Distinct()
            .ToList();

        foreach (var port in candidates)
        {
            try
            {
                using var probeCts = new CancellationTokenSource(800);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);

                await using var driver = new ModemDriver(port, baudRate);
                driver.Open();

                if (await driver.PingAsync(linked.Token).ConfigureAwait(false))
                {
                    return port;
                }
            }
            catch
            {
                // Ignore port open or permission errors
            }
        }

        return null;
    }
}
