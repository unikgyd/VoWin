using System.Collections.Concurrent;

namespace VoWin.Services;

/// <summary>One-shot audio requests, owned by both call ID and physical slot.</summary>
internal sealed class AutoAnswerMessageRequests
{
    private readonly ConcurrentDictionary<string, string> _messages = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string callId, string? slotId) => $"{slotId ?? "fallback"}\0{callId}";
    public void Queue(string callId, string? slotId, string path) => _messages.TryAdd(Key(callId, slotId), path);
    public string? Take(string callId, string? slotId) => _messages.TryRemove(Key(callId, slotId), out var path) ? path : null;
    public void Remove(string callId, string? slotId) => _messages.TryRemove(Key(callId, slotId), out _);
    public void RemoveSlot(string slotId)
    {
        foreach (var key in _messages.Keys.Where(key => key.StartsWith(slotId + "\0", StringComparison.OrdinalIgnoreCase)))
            _messages.TryRemove(key, out _);
    }
    public void Clear() => _messages.Clear();
}
