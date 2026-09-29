namespace qmiSharp.Client;

public sealed class QmiClientOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool SyncOnOpen { get; set; } = true;
}
