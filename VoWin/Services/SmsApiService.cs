using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using VoWin.Models;

namespace VoWin.Services;

public sealed class SmsApiService : ISmsApiService, IHostedService, IAsyncDisposable
{
    private readonly IPreferenceDatabaseService _database;
    private readonly SmsApiSettingsStore _store;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private ListenerRun? _run;
    public SmsApiSettings Settings { get; private set; } = new();
    public GatewayStatusItem Indicator { get; private set; } = new("短信 API", "未启用", "只读接口，默认关闭。");
    public event Action? StatusChanged;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public SmsApiService(IPreferenceDatabaseService database) : this(database, new()) { }
    internal SmsApiService(IPreferenceDatabaseService database, SmsApiSettingsStore store) { _database = database; _store = store; }

    private sealed class ListenerRun(HttpListener listener)
    {
        public readonly HttpListener Listener = listener;
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Clients = new(8, 8);
        public readonly ConcurrentDictionary<long, Task> Requests = new();
        public long NextId;
        public Task Accept = Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); StartCore(); }
        catch (Exception ex) { SetStatus(StatusLight.Error, SafeFailure(ex)); }
        finally { _lifecycle.Release(); }
    }
    public async Task SaveAndApplyAsync(SmsApiSettings settings, CancellationToken ct = default)
    {
        if (settings.Enabled) settings.Validate();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposeTask != null, this);
            SetStatus(StatusLight.Busy, "正在应用短信 API 配置…");
            await StopCoreAsync().ConfigureAwait(false);
            await _store.SaveAsync(settings, ct).ConfigureAwait(false);
            Settings = settings;
            StartCore();
        }
        catch (Exception ex) { SetStatus(StatusLight.Error, SafeFailure(ex)); throw new InvalidOperationException(SafeFailure(ex)); }
        finally { _lifecycle.Release(); }
    }
    private void StartCore()
    {
        if (!Settings.Enabled) { SetStatus(StatusLight.Inactive, "未启用；不监听端口。"); return; }
        var bind = Settings.Validate();
        if (!IPAddress.IsLoopback(bind) && !NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.Equals(bind))) throw new ArgumentException("监听 IP 不属于本机当前网络接口，请检查局域网／VPN 地址。");
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(Settings.BaseUrl);
            listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
            listener.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(5);
            listener.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(10);
            listener.TimeoutManager.DrainEntityBody = TimeSpan.FromSeconds(2);
            listener.Start();
        }
        catch { listener.Close(); throw; }
        var run = new ListenerRun(listener); _run = run;
        var processor = new SmsApiRequestProcessor(Settings, _database);
        SetStatus(StatusLight.Ready, $"监听 {Settings.BaseUrl} · 只读 · 需要 Bearer Token；关闭 VoWin 后不可用。");
        run.Accept = Task.Run(() => AcceptAsync(run, processor));
    }

    private async Task AcceptAsync(ListenerRun run, SmsApiRequestProcessor processor)
    {
        try
        {
            while (!run.Stop.IsCancellationRequested)
            {
                var context = await run.Listener.GetContextAsync().ConfigureAwait(false);
                if (!run.Clients.Wait(0))
                {
                    context.Response.StatusCode = 503; context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.Headers["Retry-After"] = "1"; context.Response.ContentLength64 = 0; context.Response.Close(); continue;
                }
                var id = Interlocked.Increment(ref run.NextId);
                var request = Task.Run(async () =>
                {
                    try { await RespondAsync(context, processor, run.Stop.Token).ConfigureAwait(false); }
                    catch { try { context.Response.Abort(); } catch { } }
                    finally { run.Clients.Release(); }
                });
                run.Requests[id] = request;
                _ = request.ContinueWith(done => { _ = done.Exception; run.Requests.TryRemove(id, out _); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception) when (run.Stop.IsCancellationRequested) { }
        catch { SetStatus(StatusLight.Error, "短信 API 监听中断，请检查端口后重新保存配置。"); }
    }

    private static async Task RespondAsync(HttpListenerContext context, SmsApiRequestProcessor processor, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var request = context.Request;
        var result = await processor.ProcessAsync(request.HttpMethod, request.RawUrl ?? "", request.Headers["Authorization"],
            request.Headers["Origin"], request.LocalEndPoint.Address, request.RemoteEndPoint.Address, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        var response = context.Response;
        response.StatusCode = result.Status;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store, private";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        if (result.Status == 401) response.Headers["WWW-Authenticate"] = "Bearer realm=\"VoWin SMS API\"";
        if (result.Status == 405) response.Headers["Allow"] = "GET";
        if (result.RetryAfter > 0) response.Headers["Retry-After"] = result.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload = JsonSerializer.SerializeToUtf8Bytes(result.Body, JsonOptions);
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
        response.Close();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposeTask) is { } disposal) { await disposal.ConfigureAwait(false); return; }
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); SetStatus(StatusLight.Inactive, "接口已停止；不再接受查询。"); }
        finally { _lifecycle.Release(); }
    }
    private async Task StopCoreAsync()
    {
        var run = Interlocked.Exchange(ref _run, null);
        if (run == null) return;
        run.Stop.Cancel(); run.Listener.Close();
        try { await run.Accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(run.Requests.Values).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        // Pending cancelled I/O may still unwind. Do not dispose its token/semaphore underneath it.
    }
    private void SetStatus(StatusLight light, string detail)
    {
        Indicator = new("短信 API", detail, detail) { Light = light };
        try { StatusChanged?.Invoke(); } catch { }
    }
    private static string SafeFailure(Exception ex) => ex switch
    {
        HttpListenerException { ErrorCode: 5 } => "监听权限不足，请以管理员身份运行 VoWin；未自动修改 URL ACL 或防火墙。",
        HttpListenerException => "无法监听，请检查 IP 是否属于本机、端口是否被占用及 HTTP.sys 权限。",
        ArgumentException => ex.Message,
        InvalidDataException => ex.Message,
        _ => "短信 API 配置未应用，请检查配置文件权限或重新保存；接口保持关闭。"
    };
    public ValueTask DisposeAsync() { lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
}
