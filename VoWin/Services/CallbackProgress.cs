namespace VoWin.Services;

/// <summary>
/// Forwards progress synchronously so diagnostics retain the exact order in
/// which the underlying operation reports its stages.
/// </summary>
internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public void Report(T value) => _callback(value);
}
