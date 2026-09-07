using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace VoSharp.Common.Events;

/// <summary>
/// Event payload object with Topic, Source, and Timestamp.
/// </summary>
public record TelephonyEvent(
    string Topic,
    string Source,
    object? Payload,
    DateTime Timestamp
)
{
    public TelephonyEvent(string topic, string source, object? payload)
        : this(topic, source, payload, DateTime.UtcNow) { }
}

public static class EventTopics
{
    public const string All = "*";
    
    public const string ModemPrefix = "modem.";
    public const string ModemState = "modem.state";
    public const string ModemSim = "modem.sim";
    public const string ModemSignal = "modem.signal";
    public const string ModemUrc = "modem.urc";
    
    public const string NetworkPrefix = "network.";
    public const string NetworkRegistration = "network.registration";
    public const string NetworkBearer = "network.bearer";
    
    public const string ImsPrefix = "ims.";
    public const string ImsState = "ims.state";
    public const string ImsRegister = "ims.register";
    
    public const string CallPrefix = "call.";
    public const string CallState = "call.state";
    public const string CallIncoming = "call.incoming";
    public const string CallDialing = "call.dialing";
    public const string CallEnded = "call.ended";
    public const string CallDtmf = "call.dtmf";
    
    public const string SmsPrefix = "sms.";
    public const string SmsIncoming = "sms.incoming";
    public const string SmsReceived = "sms.received";
    public const string SmsSent = "sms.sent";
    public const string SmsStatusReport = "sms.status_report";
    
    public const string SystemPrefix = "system.";
    public const string SystemLog = "system.log";
    public const string SystemError = "system.error";
}

/// <summary>
/// High-throughput asynchronous event bus supporting wildcard topic matching.
/// Built on System.Threading.Channels to prevent blocking publishers.
/// </summary>
public sealed class AsyncEventBus : IAsyncDisposable
{
    private readonly Channel<TelephonyEvent> _channel;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processTask;

    public AsyncEventBus(int capacity = 1000)
    {
        _channel = Channel.CreateBounded<TelephonyEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _processTask = Task.Run(ProcessEventsAsync);
    }

    public void Publish(string topic, string source, object? payload)
    {
        var ev = new TelephonyEvent(topic, source, payload);
        _channel.Writer.TryWrite(ev);
    }

    public async ValueTask PublishAsync(string topic, string source, object? payload, CancellationToken ct = default)
    {
        var ev = new TelephonyEvent(topic, source, payload);
        await _channel.Writer.WriteAsync(ev, ct);
    }

    public IDisposable Subscribe(string topicPattern, Func<TelephonyEvent, Task> handler, SynchronizationContext? syncContext = null)
    {
        var id = Guid.NewGuid();
        var regex = TopicPatternToRegex(topicPattern);
        var sub = new Subscription(id, topicPattern, regex, handler, syncContext);
        _subscriptions.TryAdd(id, sub);

        return new Unsubscriber(() =>
        {
            if (_subscriptions.TryRemove(id, out var s))
            {
                s.Dispose();
            }
        });
    }

    public IDisposable Subscribe(string topicPattern, Action<TelephonyEvent> handler, SynchronizationContext? syncContext = null)
    {
        return Subscribe(topicPattern, ev =>
        {
            handler(ev);
            return Task.CompletedTask;
        }, syncContext);
    }

    public IDisposable Subscribe<T>(string topicPattern, Action<string, string, T> handler, SynchronizationContext? syncContext = null)
    {
        return Subscribe(topicPattern, ev =>
        {
            if (ev.Payload is T typed)
            {
                handler(ev.Topic, ev.Source, typed);
            }
            else if (ev.Payload is null && default(T) is null)
            {
                handler(ev.Topic, ev.Source, default!);
            }
            return Task.CompletedTask;
        }, syncContext);
    }

    public IDisposable Subscribe<T>(string topicPattern, Func<string, string, T, Task> handler, SynchronizationContext? syncContext = null)
    {
        return Subscribe(topicPattern, async ev =>
        {
            if (ev.Payload is T typed)
            {
                await handler(ev.Topic, ev.Source, typed).ConfigureAwait(false);
            }
            else if (ev.Payload is null && default(T) is null)
            {
                await handler(ev.Topic, ev.Source, default!).ConfigureAwait(false);
            }
        }, syncContext);
    }

    private async Task ProcessEventsAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var ev))
                {
                    foreach (var sub in _subscriptions.Values)
                    {
                        if (sub.Regex.IsMatch(ev.Topic))
                        {
                            sub.Enqueue(ev);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static Regex TopicPatternToRegex(string pattern)
    {
        if (pattern == "*") return new Regex("^.*$", RegexOptions.Compiled);
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        foreach (var sub in _subscriptions.Values)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
        try
        {
            await _processTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        public Guid Id { get; }
        public string Pattern { get; }
        public Regex Regex { get; }
        public Func<TelephonyEvent, Task> Handler { get; }
        public SynchronizationContext? SyncContext { get; }
        private readonly Channel<TelephonyEvent> _subChannel;
        private readonly CancellationTokenSource _subCts = new();
        private readonly Task _subTask;

        public Subscription(Guid id, string pattern, Regex regex, Func<TelephonyEvent, Task> handler, SynchronizationContext? syncContext = null, int capacity = 200)
        {
            Id = id;
            Pattern = pattern;
            Regex = regex;
            Handler = handler;
            SyncContext = syncContext;
            _subChannel = Channel.CreateBounded<TelephonyEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _subTask = Task.Run(ProcessSubEventsAsync);
        }

        public void Enqueue(TelephonyEvent ev)
        {
            _subChannel.Writer.TryWrite(ev);
        }

        private async Task ProcessSubEventsAsync()
        {
            var reader = _subChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_subCts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var ev))
                    {
                        try
                        {
                            if (SyncContext != null)
                            {
                                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                SyncContext.Post(async _ =>
                                {
                                    try { await Handler(ev).ConfigureAwait(false); tcs.TrySetResult(); }
                                    catch (Exception ex) { tcs.TrySetException(ex); }
                                }, null);
                                await tcs.Task.ConfigureAwait(false);
                            }
                            else
                            {
                                await Handler(ev).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // Handler exception swallowed
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _subCts.Cancel();
            _subChannel.Writer.TryComplete();
            _subCts.Dispose();
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
