using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.ViewModels.Pages;

public partial class SystemStatusViewModel
{
    [ObservableProperty] private bool _detailedDiagnosticsEnabled;
    [ObservableProperty] private bool _isDiagnosticSettingBusy;
    [ObservableProperty] private bool _isDiagnosticExportBusy;
    [ObservableProperty] private string _diagnosticExportStatus = string.Empty;
    private bool _syncingDiagnostics;

    public bool CanChangeDiagnosticSetting => !IsDiagnosticSettingBusy;
    public string DiagnosticRecordingStatus => _kernelService.DiagnosticRecordingStatus;
    public string DiagnosticExportButtonText => IsDiagnosticExportBusy ? "正在导出…" : "导出诊断包";

    partial void OnIsDiagnosticSettingBusyChanged(bool value) => OnPropertyChanged(nameof(CanChangeDiagnosticSetting));
    partial void OnIsDiagnosticExportBusyChanged(bool value) => OnPropertyChanged(nameof(DiagnosticExportButtonText));
    partial void OnDetailedDiagnosticsEnabledChanged(bool value)
    {
        if (!_syncingDiagnostics) _ = SaveDiagnosticSettingAsync(value);
    }

    private void InitializeDiagnosticBindings()
    {
        SyncDiagnosticSetting();
        if (_kernelService is INotifyPropertyChanged changes)
            changes.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(_kernelService.GetModuleInstance) or nameof(_kernelService.GetTelephonyModeStatus))
                    NotifyAll();
                if (e.PropertyName is nameof(_kernelService.DetailedDiagnosticsEnabled) or nameof(_kernelService.DiagnosticRecordingStatus))
                    App.Current?.Dispatcher.BeginInvoke(new Action(SyncDiagnosticSetting));
            };
    }

    private void SyncDiagnosticSetting()
    {
        _syncingDiagnostics = true;
        try { DetailedDiagnosticsEnabled = _kernelService.DetailedDiagnosticsEnabled; }
        finally { _syncingDiagnostics = false; }
        OnPropertyChanged(nameof(DiagnosticRecordingStatus));
    }

    private async Task SaveDiagnosticSettingAsync(bool enabled)
    {
        IsDiagnosticSettingBusy = true;
        try
        {
            await _kernelService.SetDetailedDiagnosticsAsync(enabled);
            DiagnosticExportStatus = enabled ? "记录已开启" : "记录已停止";
        }
        catch (Exception ex)
        {
            DiagnosticExportStatus = $"保存日志设置失败：{Services.DiagnosticLogRedactor.Redact(ex.Message)}";
        }
        finally
        {
            SyncDiagnosticSetting();
            IsDiagnosticSettingBusy = false;
        }
    }
}
