using System.Collections.Concurrent;
using VoSharp.Common.Events;

namespace VoSharp.StateMachine;

public delegate void StateHook(TelephonyState from, TelephonyState to, object? payload);

public class TelephonyStateMachine
{
    private readonly Lock _lock = new();
    private TelephonyState _currentState = TelephonyState.Init;
    private readonly Dictionary<TelephonyState, List<StateHook>> _enterHooks = new();
    private readonly Dictionary<TelephonyState, List<StateHook>> _exitHooks = new();
    private readonly List<TransitionRecord> _history = new();
    private readonly int _historyLimit;
    private AsyncEventBus? _eventBus;
    private readonly List<IDisposable> _eventSubscriptions = new();

    public event EventHandler<TelephonyStateChangedEventArgs>? StateChanged;

    public static readonly IReadOnlyDictionary<TelephonyState, IReadOnlyDictionary<StateTrigger, TelephonyState>> AllowedTransitions =
        new Dictionary<TelephonyState, IReadOnlyDictionary<StateTrigger, TelephonyState>>
        {
            [TelephonyState.Init] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerSimReady] = TelephonyState.SimReady,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.SimReady] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerNetSearch] = TelephonyState.NetworkSearching,
                [StateTrigger.TriggerNetAttach] = TelephonyState.NetworkRegistered,
                [StateTrigger.TriggerDataConnect] = TelephonyState.DataConnecting,
                [StateTrigger.TriggerSimRemoved] = TelephonyState.Init,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.NetworkSearching] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerNetAttach] = TelephonyState.NetworkRegistered,
                [StateTrigger.TriggerNetLost] = TelephonyState.NetworkSearching,
                [StateTrigger.TriggerDataConnect] = TelephonyState.DataConnecting,
                [StateTrigger.TriggerSimRemoved] = TelephonyState.Init,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.NetworkRegistered] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerDataConnect] = TelephonyState.DataConnecting,
                [StateTrigger.TriggerDataConnected] = TelephonyState.DataConnected,
                [StateTrigger.TriggerNetLost] = TelephonyState.NetworkSearching,
                [StateTrigger.TriggerSimRemoved] = TelephonyState.Init,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.DataConnecting] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerDataConnected] = TelephonyState.DataConnected,
                [StateTrigger.TriggerDataDisconnect] = TelephonyState.NetworkRegistered,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.DataConnected] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerImsRegister] = TelephonyState.ImsRegistering,
                [StateTrigger.TriggerImsRegistered] = TelephonyState.ImsRegistered,
                [StateTrigger.TriggerDataDisconnect] = TelephonyState.NetworkRegistered,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.ImsRegistering] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerImsRegistered] = TelephonyState.ImsRegistered,
                [StateTrigger.TriggerImsDeregister] = TelephonyState.DataConnected,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.ImsRegistered] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerCallDial] = TelephonyState.CallRinging,
                [StateTrigger.TriggerCallRing] = TelephonyState.CallRinging,
                [StateTrigger.TriggerImsDeregister] = TelephonyState.DataConnected,
                [StateTrigger.TriggerNetLost] = TelephonyState.NetworkSearching,
                [StateTrigger.TriggerError] = TelephonyState.Error,
            },
            [TelephonyState.CallRinging] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerCallAnswer] = TelephonyState.CallActive,
                [StateTrigger.TriggerCallHangup] = TelephonyState.CallEnded,
                [StateTrigger.TriggerError] = TelephonyState.CallEnded,
            },
            [TelephonyState.CallActive] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerCallHangup] = TelephonyState.CallEnded,
                [StateTrigger.TriggerError] = TelephonyState.CallEnded,
            },
            [TelephonyState.CallEnded] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerReset] = TelephonyState.ImsRegistered,
                [StateTrigger.TriggerCallDial] = TelephonyState.CallRinging,
                [StateTrigger.TriggerImsDeregister] = TelephonyState.DataConnected,
            },
            [TelephonyState.Error] = new Dictionary<StateTrigger, TelephonyState>
            {
                [StateTrigger.TriggerReset] = TelephonyState.Init,
                [StateTrigger.TriggerSimReady] = TelephonyState.SimReady,
            }
        };

    public TelephonyStateMachine(int historyLimit = 50)
    {
        _historyLimit = historyLimit;
    }

    public TelephonyState CurrentState
    {
        get
        {
            lock (_lock)
            {
                return _currentState;
            }
        }
    }

    public void OnEnter(TelephonyState state, StateHook hook)
    {
        lock (_lock)
        {
            if (!_enterHooks.TryGetValue(state, out var list))
            {
                list = new List<StateHook>();
                _enterHooks[state] = list;
            }
            list.Add(hook);
        }
    }

    public void OnExit(TelephonyState state, StateHook hook)
    {
        lock (_lock)
        {
            if (!_exitHooks.TryGetValue(state, out var list))
            {
                list = new List<StateHook>();
                _exitHooks[state] = list;
            }
            list.Add(hook);
        }
    }

    public bool Fire(StateTrigger trigger, object? payload = null)
    {
        List<StateHook>? exitCallbacks = null;
        List<StateHook>? enterCallbacks = null;
        TelephonyState from;
        TelephonyState to;

        lock (_lock)
        {
            from = _currentState;
            if (!AllowedTransitions.TryGetValue(from, out var targets) ||
                !targets.TryGetValue(trigger, out to))
            {
                return false;
            }

            if (_exitHooks.TryGetValue(from, out var exits))
            {
                exitCallbacks = new List<StateHook>(exits);
            }

            _currentState = to;

            var record = new TransitionRecord(from, to, trigger, DateTime.UtcNow, payload?.ToString());
            _history.Add(record);
            if (_history.Count > _historyLimit)
            {
                _history.RemoveAt(0);
            }

            if (_enterHooks.TryGetValue(to, out var enters))
            {
                enterCallbacks = new List<StateHook>(enters);
            }
        }

        if (exitCallbacks != null)
        {
            foreach (var hook in exitCallbacks)
            {
                hook(from, to, payload);
            }
        }

        if (enterCallbacks != null)
        {
            foreach (var hook in enterCallbacks)
            {
                hook(from, to, payload);
            }
        }

        try
        {
            StateChanged?.Invoke(this, new TelephonyStateChangedEventArgs(from, to, trigger, payload?.ToString()));
        }
        catch { }

        _eventBus?.Publish(EventTopics.ModemState, "StateMachine", new
        {
            From = from.ToString(),
            To = to.ToString(),
            Trigger = trigger.ToString(),
            Payload = payload
        });

        return true;
    }

    public IReadOnlyList<TransitionRecord> GetHistory()
    {
        lock (_lock)
        {
            return _history.ToArray();
        }
    }

    public void ConnectEventBus(AsyncEventBus bus)
    {
        lock (_lock)
        {
            foreach (var sub in _eventSubscriptions)
            {
                sub.Dispose();
            }
            _eventSubscriptions.Clear();

            _eventBus = bus;

            _eventSubscriptions.Add(bus.Subscribe(EventTopics.ModemSim, ev =>
            {
                var p = ev.Payload?.ToString()?.ToUpperInvariant();
                if (p is "READY" or "SIM_INSERTED")
                    Fire(StateTrigger.TriggerSimReady, ev.Payload);
                else if (p is "ABSENT" or "SIM_REMOVED")
                    Fire(StateTrigger.TriggerSimRemoved, ev.Payload);
            }));

            _eventSubscriptions.Add(bus.Subscribe(EventTopics.NetworkRegistration, ev =>
            {
                var p = ev.Payload?.ToString()?.ToUpperInvariant();
                if (p is "HOME" or "ROAMING" or "REGISTERED")
                    Fire(StateTrigger.TriggerNetAttach, ev.Payload);
                else if (p is "SEARCHING")
                    Fire(StateTrigger.TriggerNetSearch, ev.Payload);
                else if (p is "UNREGISTERED" or "LOST")
                    Fire(StateTrigger.TriggerNetLost, ev.Payload);
            }));

            _eventSubscriptions.Add(bus.Subscribe(EventTopics.ImsState, ev =>
            {
                var p = ev.Payload?.ToString()?.ToUpperInvariant();
                if (p is "REGISTERED")
                    Fire(StateTrigger.TriggerImsRegistered, ev.Payload);
                else if (p is "REGISTERING")
                    Fire(StateTrigger.TriggerImsRegister, ev.Payload);
                else if (p is "DEREGISTERED")
                    Fire(StateTrigger.TriggerImsDeregister, ev.Payload);
            }));

            _eventSubscriptions.Add(bus.Subscribe(EventTopics.CallState, ev =>
            {
                var p = ev.Payload?.ToString()?.ToUpperInvariant();
                if (p is "RINGING")
                    Fire(StateTrigger.TriggerCallRing, ev.Payload);
                else if (p is "ACTIVE")
                    Fire(StateTrigger.TriggerCallAnswer, ev.Payload);
                else if (p is "ENDED")
                    Fire(StateTrigger.TriggerCallHangup, ev.Payload);
            }));
        }
    }
}
