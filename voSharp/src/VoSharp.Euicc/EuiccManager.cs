using VoSharp.Common.Events;
using VoSharp.Euicc.Download;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Sgp22;
using VoSharp.Euicc.Transport;

namespace VoSharp.Euicc;

/// <summary>
/// High-level manager for eUICC / eSIM operations.
/// Implements the GSMA SGP.22 ES10 (LPA-eUICC interface) profile management subset
/// (GetProfilesInfo, EnableProfile, DisableProfile, DeleteProfile, SetNickname, GetEuiccInfo).
/// Remote profile download is implemented in-process with .NET HTTPS and ES10 APDU relay.
/// </summary>
public class EuiccManager
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public IEuiccTransport Transport { get; }
    public AsyncEventBus? EventBus { get; }
    public string DefaultAid { get; set; } = Sgp22Client.IsdrAidStandard;

    public event EventHandler<EuiccProfilesChangedEventArgs>? ProfilesUpdated;
    public event EventHandler<EuiccOperationEventArgs>? OperationCompleted;

    public EuiccManager(IEuiccTransport transport, AsyncEventBus? eventBus = null)
    {
        Transport = transport;
        EventBus = eventBus;
    }

    public async Task<T> ExecuteSessionAsync<T>(Func<Sgp22Client, Task<T>> action, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int channel = await Transport.OpenLogicalChannelAsync(DefaultAid, ct).ConfigureAwait(false);
            try
            {
                var client = new Sgp22Client(Transport, channel);
                return await action(client).ConfigureAwait(false);
            }
            finally
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task ExecuteSessionAsync(Func<Sgp22Client, Task> action, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int channel = await Transport.OpenLogicalChannelAsync(DefaultAid, ct).ConfigureAwait(false);
            try
            {
                var client = new Sgp22Client(Transport, channel);
                await action(client).ConfigureAwait(false);
            }
            finally
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public Task<string> GetEIDAsync(CancellationToken ct = default)
    {
        return ExecuteSessionAsync(client => client.GetEIDAsync(ct), ct);
    }

    public async Task<List<Profile>> ListProfilesAsync(CancellationToken ct = default)
    {
        var profiles = await ExecuteSessionAsync(client => client.GetProfilesInfoAsync(ct), ct).ConfigureAwait(false);
        try { ProfilesUpdated?.Invoke(this, new EuiccProfilesChangedEventArgs(profiles)); } catch { }
        EventBus?.Publish("euicc.profiles.listed", "EuiccManager", profiles);
        return profiles;
    }

    public async Task<Profile?> GetActiveProfileAsync(CancellationToken ct = default)
    {
        var list = await ListProfilesAsync(ct).ConfigureAwait(false);
        return list.FirstOrDefault(p => p.State == ProfileState.Enabled);
    }

    public async Task SwitchProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionAsync(async client =>
            {
                // 1. Enable target profile
                await client.EnableProfileAsync(iccidOrAid, refresh, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Switch", targetIccidOrAid: iccidOrAid, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.switched", "EuiccManager", iccidOrAid);
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Switch", targetIccidOrAid: iccidOrAid, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public async Task DisableProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionAsync(client => client.DisableProfileAsync(iccidOrAid, refresh, ct), ct).ConfigureAwait(false);
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Disable", targetIccidOrAid: iccidOrAid, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.disabled", "EuiccManager", iccidOrAid);
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Disable", targetIccidOrAid: iccidOrAid, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public async Task DeleteProfileAsync(string iccidOrAid, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionAsync(client => client.DeleteProfileAsync(iccidOrAid, ct), ct).ConfigureAwait(false);
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Delete", targetIccidOrAid: iccidOrAid, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.deleted", "EuiccManager", iccidOrAid);
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Delete", targetIccidOrAid: iccidOrAid, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public async Task RenameProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionAsync(client => client.SetNicknameAsync(iccidOrAid, nickname, ct), ct).ConfigureAwait(false);
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Rename", targetIccidOrAid: iccidOrAid, nickname: nickname, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.renamed", "EuiccManager", new { Target = iccidOrAid, Nickname = nickname });
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Rename", targetIccidOrAid: iccidOrAid, nickname: nickname, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public Task<EuiccInfo> GetEuiccInfoAsync(CancellationToken ct = default)
    {
        return ExecuteSessionAsync(client => client.GetEuiccInfoAsync(ct), ct);
    }

    public async Task<EuiccDownloadResult> DownloadProfileAsync(
        string activationCode,
        string imei,
        string? confirmationCode = null,
        IProgress<EuiccDownloadProgress>? progress = null,
        CancellationToken ct = default,
        bool allowUntrustedTls = false,
        bool allowRetryAfterUncertain = false)
    {
        var parsedCode = EuiccActivationCode.Parse(activationCode);
        var normalizedImei = new string((imei ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!IsValidImei(normalizedImei))
            throw new ArgumentException("模组 IMEI 必须是校验位正确的 15 位号码，无法执行 eSIM 服务器认证。", nameof(imei));
        var normalizedConfirmationCode = confirmationCode?.Trim() ?? string.Empty;
        if (parsedCode.ConfirmationCodeRequired && string.IsNullOrEmpty(normalizedConfirmationCode))
            throw new ArgumentException("该激活码要求确认码，请填写运营商提供的确认码。", nameof(confirmationCode));
        if (normalizedConfirmationCode.Length > 64 || normalizedConfirmationCode.Any(char.IsControl))
            throw new ArgumentException("确认码长度或字符无效。", nameof(confirmationCode));

        var fingerprint = EuiccDownloadJournal.Fingerprint(parsedCode.CanonicalCode);
        var journalStarted = false;
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new EuiccDownloadProgress(5, "正在读取 eUICC 并执行写入前检查"));
            var before = await ListProfilesWithoutGateAsync(ct).ConfigureAwait(false);
            var beforeIccids = before.Select(p => p.ICCID).Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (EuiccDownloadJournal.Get(fingerprint) is { } previousAttempt)
            {
                var previouslyInstalled = before.FirstOrDefault(profile =>
                    (!string.IsNullOrWhiteSpace(previousAttempt.InstalledIccid) && profile.ICCID.Equals(previousAttempt.InstalledIccid, StringComparison.OrdinalIgnoreCase)) ||
                    !previousAttempt.ProfilesBefore.Contains(profile.ICCID, StringComparer.OrdinalIgnoreCase));
                if (previouslyInstalled is not null)
                {
                    return new EuiccDownloadResult(
                        previouslyInstalled.ICCID,
                        true,
                        "检测到该激活码对应的 Profile 已在卡内；已阻止重复下载。请在安装通知列表中确认运营商通知状态。");
                }
                if (allowRetryAfterUncertain)
                {
                    EuiccDownloadJournal.ResetForAuthorizedRetry(fingerprint);
                    progress?.Report(new EuiccDownloadProgress(7, "服务商已授权重试；已清除本地事务锁定，正在建立新下载事务"));
                }
                else
                {
                    throw new EuiccDownloadUncertainException(
                        "该激活码已有未完成或结果不确定的事务记录。为防止 SM-DP+ 已消耗二维码，已禁止再次下载；请先重新读取卡片和安装通知。",
                        cardCommitMayHaveCompleted: true);
                }
            }

            try
            {
                var info = await GetEuiccInfoWithoutGateAsync(ct).ConfigureAwait(false);
                if (info.FreeNvramBytes is > 0 and < 81_920)
                    throw new InvalidOperationException($"eUICC 剩余空间仅 {info.FreeNvramBytes} 字节，低于 80 KB 安全阈值；请先删除不用的 Profile。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ex.Message.Contains("80 KB", StringComparison.Ordinal))
            {
                // Some older eUICCs do not expose free-NVRAM in GetEuiccInfo1.
                // A successful profile list still proves that the ISD-R is usable.
            }

            EuiccDownloadJournal.Begin(fingerprint, beforeIccids);
            journalStarted = true;

            var downloader = new EuiccProfileDownloader(Transport, allowUntrustedTls);
            Exception? downloadError = null;
            string reportedIccid = string.Empty;
            EuiccWorkerDownloadResult? workerResult = null;
            var lastDownloadProgress = new EuiccDownloadProgress(10, "正在建立下载会话");
            var trackedProgress = new EuiccForwardingProgress(value =>
            {
                lastDownloadProgress = value;
                progress?.Report(value);
            });
            try
            {
                workerResult = await downloader.DownloadAsync(parsedCode, normalizedImei, normalizedConfirmationCode, trackedProgress, ct).ConfigureAwait(false);
                reportedIccid = workerResult.Iccid;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                downloadError = ex;
            }

            progress?.Report(downloadError is null
                ? new EuiccDownloadProgress(95, "正在重新读取 Profile 并核对写入结果")
                : new EuiccDownloadProgress(
                    lastDownloadProgress.Percent,
                    $"{lastDownloadProgress.Status}时失败；正在只读核验卡片，确认是否发生写入"));
            List<Profile>? after = null;
            Exception? verifyError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    after = await ListProfilesWithoutGateAsync(ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    verifyError = ex;
                }
            }

            var newProfiles = after?
                .Where(p => !string.IsNullOrWhiteSpace(p.ICCID) && !beforeIccids.Contains(p.ICCID))
                .ToList() ?? [];
            var installed = !string.IsNullOrWhiteSpace(reportedIccid)
                ? newProfiles.SingleOrDefault(p => p.ICCID.Equals(reportedIccid, StringComparison.OrdinalIgnoreCase))
                : newProfiles.Count == 1 ? newProfiles[0] : null;

            if (installed is null)
            {
                if (downloadError is EuiccProfileDownloaderException { RetrySafe: false } unsafeError)
                {
                    EuiccDownloadJournal.MarkUncertain(fingerprint);
                    var state = unsafeError.Ambiguous
                        ? "写卡数据传输已经开始，但未收到完整安装结果，当前无法确认卡内是否已经提交"
                        : "下载事务已经进入运营商处理阶段";
                    throw new EuiccDownloadUncertainException(
                        $"{state}；此激活码可能已经失效，禁止直接再次下载。请先重新读取 Profile 和安装通知。原始错误：{unsafeError.Message}",
                        unsafeError.Ambiguous,
                        unsafeError);
                }
                if (downloadError is not null)
                {
                    EuiccDownloadJournal.RemoveIfRetrySafe(fingerprint);
                    journalStarted = false;
                    throw downloadError;
                }
                if (verifyError is not null)
                {
                    EuiccDownloadJournal.MarkUncertain(fingerprint);
                    throw new EuiccDownloadUncertainException(
                        $"Profile 写入流程完成，但无法重新读取卡片确认结果：{verifyError.Message}。已禁止直接重试该激活码。",
                        cardCommitMayHaveCompleted: true,
                        verifyError);
                }
                EuiccDownloadJournal.MarkUncertain(fingerprint);
                throw new EuiccDownloadUncertainException(
                    "Profile 写入流程完成，但卡片列表中未出现可核验的新 Profile。激活码可能已经被消耗，已禁止直接重试；请先重新读取卡片和安装通知。",
                    cardCommitMayHaveCompleted: true);
            }


            EuiccDownloadJournal.Complete(fingerprint, installed.ICCID);
            journalStarted = false;

            try { ProfilesUpdated?.Invoke(this, new EuiccProfilesChangedEventArgs(after!)); } catch { }
            EventBus?.Publish("euicc.profile.downloaded", "EuiccManager", new { installed.ICCID, parsedCode.SmdpAddress });

            if (downloadError is not null || workerResult?.Recovered == true || !string.IsNullOrWhiteSpace(workerResult?.Warning))
            {
                var warning = workerResult?.Warning ?? $"Profile 已写入卡片，但运营商确认阶段返回警告：{downloadError?.Message}";
                return new EuiccDownloadResult(installed.ICCID, true, warning);
            }

            progress?.Report(new EuiccDownloadProgress(100, "Profile 已写入并通过卡片列表核验"));
            return new EuiccDownloadResult(installed.ICCID);
        }
        finally
        {
            if (journalStarted)
            {
                try { EuiccDownloadJournal.MarkUncertain(fingerprint); } catch { }
            }
            _sessionGate.Release();
        }
    }

    private async Task<List<Profile>> ListProfilesWithoutGateAsync(CancellationToken ct)
    {
        var channel = await Transport.OpenLogicalChannelAsync(DefaultAid, ct).ConfigureAwait(false);
        try
        {
            return await new Sgp22Client(Transport, channel).GetProfilesInfoAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
        }
    }

    private sealed class EuiccForwardingProgress(Action<EuiccDownloadProgress> callback) : IProgress<EuiccDownloadProgress>
    {
        public void Report(EuiccDownloadProgress value) => callback(value);
    }

    private async Task<EuiccInfo> GetEuiccInfoWithoutGateAsync(CancellationToken ct)
    {
        var channel = await Transport.OpenLogicalChannelAsync(DefaultAid, ct).ConfigureAwait(false);
        try
        {
            return await new Sgp22Client(Transport, channel).GetEuiccInfoAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
        }
    }

    private static bool IsValidImei(string imei)
    {
        if (imei.Length != 15 || imei.Any(c => c is < '0' or > '9')) return false;
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var digit = imei[i] - '0';
            if ((i & 1) == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        var checkDigit = (10 - sum % 10) % 10;
        return imei[14] - '0' == checkDigit;
    }
}
