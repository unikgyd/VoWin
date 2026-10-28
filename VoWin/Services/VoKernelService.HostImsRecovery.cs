using VoSharp.Kernel;
using VoSharp.Kernel.Pool;
using VoSharp.Sip;
using VoWin.Models;

namespace VoWin.Services;

public partial class VoKernelService
{
    private sealed record HostImsRecoveryIdentity(ModemSlot Slot, string Iccid, string Imsi, string Imei);

    private async Task StopHostImsRecoveryAsync(ModuleManagementInstance instance)
    {
        var controller = Interlocked.Exchange(ref instance.HostRecovery, null);
        if (controller is null) return;
        await controller.DisposeAsync().ConfigureAwait(false);
        instance.Refresh();
    }

    private void ArmHostImsRecovery(ModuleManagementInstance instance)
    {
        var slot = instance.Slot;
        if (slot.Sim is not { } sim || string.IsNullOrWhiteSpace(slot.Imei) ||
            !slot.GetHostImsRegistrationStatus().IsRegistered || instance.HostRecovery is not null) return;
        var identity = new HostImsRecoveryIdentity(slot, sim.Iccid, sim.Imsi, slot.Imei);
        var controller = new HostImsRecoveryController(
            () => ObserveHostImsRecovery(identity),
            token => slot.WaitForHostImsRegistrarRetryAsync(identity.Iccid, token),
            token => RebuildHostImsAsync(identity, token));
        instance.HostRecovery = controller;
        controller.Changed += () =>
        {
            if (!ReferenceEquals(instance.HostRecovery, controller)) return;
            instance.Refresh();
            AddLog(controller.Status.IsPaused ? "WARN" : "INFO", "HostImsRecovery",
                $"Slot={slot.Id}; state={controller.Status.State}; attempts={controller.Status.Attempts}; {controller.Status.Message}");
        };
        instance.Refresh();
    }

    private HostImsRecoveryObservation ObserveHostImsRecovery(HostImsRecoveryIdentity identity)
    {
        var slot = identity.Slot;
        var host = slot.GetHostImsRegistrationStatus();
        var sameSlot = Volatile.Read(ref _serviceDisposed) == 0 &&
            Kernel.Pool.Slots.TryGetValue(slot.Id, out var current) && ReferenceEquals(current, slot) &&
            slot.CellularImsMode == TelephonyMode.HostIms;
        var available = slot.Sim is not null && !string.IsNullOrWhiteSpace(slot.Imei);
        var sameIdentity = available && slot.Sim!.Iccid == identity.Iccid &&
            slot.Sim.Imsi == identity.Imsi && slot.Imei == identity.Imei;
        return new(host.IsRegistered && host.ExpiresAtUtc > DateTime.UtcNow, sameIdentity, available,
            slot.Modem is { IsOpen: true } && !slot.IsFlightMode,
            slot.HostImsRecoveryCleanupPending, slot.HasCellularCall || slot.Calls.ActiveCall is not null,
            sameSlot, host.LossKind, host.AutomaticRecoveryPaused, host.RetryNotBeforeUtc, host.LastError);
    }

    private static void VerifyRecoveryIdentity(ModemSlot slot, HostImsRecoveryIdentity identity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(slot, identity.Slot) || slot.CellularImsMode != TelephonyMode.HostIms ||
            slot.Sim?.Iccid != identity.Iccid || slot.Sim.Imsi != identity.Imsi || slot.Imei != identity.Imei)
            throw new InvalidOperationException("设备、SIM 或工作方式已经改变，未继续自动恢复 Host IMS。");
    }

    private static async Task VerifyRecoveryHardwareAsync(ModemSlot slot, HostImsRecoveryIdentity identity, CancellationToken ct)
    {
        VerifyRecoveryIdentity(slot, identity, ct);
        var modem = slot.Modem ?? throw new InvalidOperationException("原模组尚未恢复连接。");
        var imei = await modem.GetImeiAsync(ct).ConfigureAwait(false);
        var iccid = await modem.GetIccidAsync(ct).ConfigureAwait(false);
        var permanent = await modem.GetPermanentImsiAsync(ct).ConfigureAwait(false);
        var imsi = string.IsNullOrWhiteSpace(permanent) ? await modem.GetImsiAsync(ct).ConfigureAwait(false) : permanent;
        VerifyRecoveryIdentity(slot, identity, ct);
        if (!ReferenceEquals(slot.Modem, modem) || imei != identity.Imei || iccid != identity.Iccid || imsi != identity.Imsi)
            throw new InvalidOperationException("自动恢复前的实际模组 / SIM 身份校验失败，未改变 IMS 模式。");
    }

    private async Task<HostImsRecoveryAttempt> RebuildHostImsAsync(HostImsRecoveryIdentity identity, CancellationToken ct)
    {
        var before = identity.Slot.GetHostImsRegistrationStatus().Failure;
        try
        {
            await EnableTelephonyModeCoreAsync(identity.Slot.Id, TelephonyMode.HostIms, identity, ct).ConfigureAwait(false);
            return new(identity.Slot.GetHostImsRegistrationStatus().IsRegistered, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var after = identity.Slot.GetHostImsRegistrationStatus();
            var retryable = after.LossKind != HostImsLossKind.RestorationFailed && !after.AutomaticRecoveryPaused &&
                (ex is TimeoutException or SipRegistrationRetryBlockedException { AutomaticRecoveryPaused: false } ||
                 ex is System.IO.IOException and not System.IO.FileNotFoundException and not System.IO.DirectoryNotFoundException ||
                 !ReferenceEquals(before, after.Failure) && after.Failure?.StatusCode is 408 or 500 or 503 or 504);
            return new(false, retryable, ex.Message);
        }
    }
}
