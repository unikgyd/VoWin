using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoWin.Models;
using VoWin.Services;
using VoWin.ViewModels.Pages;

namespace VoWin.Tests;

public sealed class SmsApiTests
{
    private const string Token = "synthetic_sms_api_token_ABCDEFGHIJKLMNOPQRSTUV";
    private static SmsApiSettings Enabled => new() { Enabled = true, Token = Token };
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static JsonDocument Body(SmsApiResponse result) => JsonDocument.Parse(JsonSerializer.Serialize(result.Body, WebJson));
    private static Task<SmsApiResponse> Request(SmsApiRequestProcessor processor, string path = "/api/v1/sms",
        string? auth = "Bearer " + Token, string method = "GET", string? origin = null, IPAddress? local = null, IPAddress? remote = null)
        => processor.ProcessAsync(method, path, auth, origin, local ?? IPAddress.Loopback, remote ?? IPAddress.Loopback);

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "vowin-sms-api-" + Guid.NewGuid().ToString("N"));
        public readonly PreferenceDatabaseService Database;
        public string DbPath => Path.Combine(DirectoryPath, "prefs.db");
        public Fixture() => Database = new(DbPath);
        public Task Save(string id, string text, DateTime time, string? slot = "line-a", string sender = "TEST", bool outgoing = false)
            => Database.SaveSmsMessageAsync(new() { Id = id, Text = text, Timestamp = time, SlotId = slot,
                SenderOrRecipient = sender, IsOutgoing = outgoing, RawPdu = "must-never-be-exported" });
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); return ValueTask.CompletedTask; }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static IPreferenceDatabaseService NeverRead()
    {
        var database = DispatchProxy.Create<IPreferenceDatabaseService, PhoneCallServiceProxy>();
        ((PhoneCallServiceProxy)database).Handler = (m, _) => throw new InvalidOperationException("Unauthorized DB access: " + m.Name);
        return database;
    }

    [Fact]
    public async Task MissingWrongOrQueryStringTokensNeverAccessTheDatabase()
    {
        var processor = new SmsApiRequestProcessor(Enabled, NeverRead());
        Assert.Equal(401, (await Request(processor, auth: null)).Status);
        Assert.Equal(401, (await Request(processor, auth: "Bearer wrong")).Status);
        Assert.Equal(401, (await Request(processor, "/api/v1/sms?token=" + Token, auth: null)).Status);
        Assert.Equal(400, (await Request(processor, "/api/v1/sms?token=" + Token)).Status);
        Assert.Equal(503, (await Request(new(new(), NeverRead()))).Status);
    }

    [Theory]
    [InlineData("POST", "/api/v1/sms", 405)] [InlineData("DELETE", "/api/v1/sms", 405)]
    [InlineData("OPTIONS", "/api/v1/sms", 405)] [InlineData("GET", "/api/v1/send", 404)]
    [InlineData("GET", "/api/v1/sms?limit=101", 400)] [InlineData("GET", "/api/v1/sms?limit=0", 400)]
    [InlineData("GET", "/api/v1/sms?keyword=one&keyword=two", 400)] [InlineData("GET", "/api/v1/sms?maxAgeSeconds=604801", 400)]
    [InlineData("GET", "/api/v1/otp?otpOnly=false", 400)] [InlineData("GET", "/api/v1/otp?limit=2", 400)]
    [InlineData("GET", "/api/v1/sms?since=2026-10-19T12:00:00", 400)] [InlineData("GET", "/api/v1/sms?keyword=%ZZ", 400)]
    public async Task UnsupportedMethodsAndInvalidQueriesFailBeforeDatabaseAccess(string method, string path, int status)
        => Assert.Equal(status, (await Request(new(Enabled, NeverRead()), path, method: method)).Status);

    [Fact]
    public async Task OriginSpoofedLocalBindingAndPublicClientsAreRejected()
    {
        var processor = new SmsApiRequestProcessor(Enabled, NeverRead());
        Assert.Equal(403, (await Request(processor, origin: "https://evil.test")).Status);
        Assert.Equal(403, (await Request(processor, local: IPAddress.Parse("10.0.0.2"))).Status);
        Assert.Equal(403, (await Request(processor, remote: IPAddress.Parse("8.8.8.8"))).Status);
    }

    [Fact]
    public async Task LiteralUnicodeKeywordSenderSlotTimeAndOrderFiltersOnlyReturnIncomingMessages()
    {
        await using var fixture = new Fixture(); var now = DateTime.UtcNow;
        await fixture.Save("old", "ÜBER 登录 code 111111", now.AddDays(-2));
        await fixture.Save("outgoing", "ÜBER 登录 code 222222", now.AddSeconds(-1), outgoing: true);
        await fixture.Save("future", "ÜBER 登录 code 333333", now.AddHours(1));
        await fixture.Save("other-line", "ÜBER 登录 code 444444", now.AddSeconds(-2), "line-b");
        await fixture.Save("older", "ÜBER 登录 code 555555", now.AddSeconds(-20), sender: "+12345");
        await fixture.Save("newer", "ÜBER 登录 code 666666", now.AddSeconds(-10), sender: "+12345");
        var processor = new SmsApiRequestProcessor(Enabled, fixture.Database);
        var response = await Request(processor, "/api/v1/sms?keyword=%C3%BCber%20%E7%99%BB%E5%BD%95&slotId=line-a&sender=%2B12345&limit=2");
        Assert.Equal(200, response.Status); using var json = Body(response);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "newer", "older" }, items.Select(i => i.GetProperty("id").GetString()));
        Assert.All(items, item => { Assert.False(item.TryGetProperty("rawPdu", out _)); Assert.EndsWith("Z", item.GetProperty("timestampUtc").GetString()); });
        Assert.DoesNotContain("must-never-be-exported", json.RootElement.GetRawText());
        Assert.Equal(6, (await fixture.Database.GetAllSmsMessagesAsync()).Count); // No consumption or mutation.
    }

    [Fact]
    public async Task SqlAndWildcardCharactersAreLiteralAndOtpReturnsLatestMatchWithoutConsumingIt()
    {
        await using var fixture = new Fixture(); var now = DateTime.UtcNow;
        await fixture.Save("plain", "nothing here", now.AddSeconds(-1));
        await fixture.Save("code", "Service 100% code 654321 ' OR 1=1--", now.AddSeconds(-2));
        var processor = new SmsApiRequestProcessor(Enabled, fixture.Database);
        foreach (var keyword in new[] { "%", "' OR 1=1--" })
        {
            using var json = Body(await Request(processor, "/api/v1/sms?keyword=" + Uri.EscapeDataString(keyword)));
            Assert.Equal("code", Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        }
        for (var n = 0; n < 2; n++)
        {
            using var json = Body(await Request(processor, "/api/v1/otp?keyword=Service"));
            Assert.True(json.RootElement.GetProperty("found").GetBoolean()); Assert.Equal("654321", json.RootElement.GetProperty("code").GetString());
        }
        using var missing = Body(await Request(processor, "/api/v1/otp?keyword=missing"));
        Assert.False(missing.RootElement.GetProperty("found").GetBoolean()); Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("code").ValueKind);
    }

    [Fact]
    public void DefaultAgesExplicitSinceAndMaximumAgeRemainBounded()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddSeconds(-300).UtcDateTime, SmsApiRequestProcessor.ParseQuery("", true, now).SinceUtc);
        Assert.Equal(now.AddDays(-1).UtcDateTime, SmsApiRequestProcessor.ParseQuery("", false, now).SinceUtc);
        var since = now.AddMinutes(-1).ToString("o");
        Assert.Equal(now.AddMinutes(-1).UtcDateTime, SmsApiRequestProcessor.ParseQuery("since=" + Uri.EscapeDataString(since), false, now).SinceUtc);
        Assert.Equal(now.AddDays(-7).UtcDateTime, SmsApiRequestProcessor.ParseQuery("maxAgeSeconds=604800&since=2000-01-01T00:00:00Z", false, now).SinceUtc);
    }

    [Fact]
    public async Task RequestLimitIsGlobalBoundedAndResetsWithoutUnboundedClientMaps()
    {
        var clock = new Clock(DateTimeOffset.UtcNow); var processor = new SmsApiRequestProcessor(Enabled, NeverRead(), clock);
        for (var n = 0; n < 120; n++) Assert.Equal(401, (await Request(processor, auth: null)).Status);
        var limited = await Request(processor); Assert.Equal(429, limited.Status); Assert.InRange(limited.RetryAfter, 1, 60);
        clock.Now += TimeSpan.FromMinutes(1); Assert.Equal(401, (await Request(processor, auth: null)).Status);
    }

    [Fact]
    public async Task ChangingTokenImmediatelyRejectsTheOldTokenAndErrorsNeverEchoProviderDetails()
    {
        var processor = new SmsApiRequestProcessor(Enabled with { Token = new string('Z', 40) }, NeverRead());
        Assert.Equal(401, (await Request(processor)).Status);
        var response = await Request(processor, auth: "Bearer " + new string('Z', 40));
        Assert.Equal(503, response.Status);
        using var json = Body(response); Assert.DoesNotContain("Unauthorized DB", json.RootElement.GetRawText()); Assert.DoesNotContain(Token, json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("0.0.0.0", true)] [InlineData("::", true)] [InlineData("8.8.8.8", true)]
    [InlineData("10.0.0.2", false)] [InlineData("https://example.test", true)] [InlineData("*", true)]
    public void UnsafeBindingsAreNotAccepted(string address, bool lan)
        => Assert.Throws<ArgumentException>(() => (Enabled with { BindAddress = address, AllowLanAccess = lan }).Validate());

    [Fact]
    public async Task TokenIsProtectedAtRestDisabledSettingsRestoreAndDisposalIsIdempotent()
    {
        await using var fixture = new Fixture(); var path = Path.Combine(fixture.DirectoryPath, "sms-api.json");
        var store = new SmsApiSettingsStore(path); Assert.False((await store.LoadAsync()).Enabled);
        await store.SaveAsync(Enabled with { Enabled = false }); Assert.DoesNotContain(Token, await File.ReadAllTextAsync(path));
        Assert.DoesNotContain(Token, Enabled.ToString()); Assert.Equal(Token, (await store.LoadAsync()).Token);
        await using var service = new SmsApiService(fixture.Database, store); await service.StartAsync(default);
        Assert.Equal(StatusLight.Inactive, service.Indicator.Light);
        await service.SaveAndApplyAsync(Enabled with { Enabled = false, Token = new string('Z', 40) });
        Assert.Equal(new string('Z', 40), (await store.LoadAsync()).Token);
        await service.DisposeAsync(); await service.DisposeAsync(); await service.StopAsync(default);
    }

    [Fact]
    public async Task MalformedPersistedConfigurationFailsClosedWithoutAbortingApplicationStartup()
    {
        await using var fixture = new Fixture(); var path = Path.Combine(fixture.DirectoryPath, "bad.json");
        Directory.CreateDirectory(fixture.DirectoryPath); await File.WriteAllTextAsync(path, "{ broken JSON with a secret }");
        await using var service = new SmsApiService(fixture.Database, new(path)); await service.StartAsync(default);
        Assert.False(service.Settings.Enabled); Assert.Equal(StatusLight.Error, service.Indicator.Light);
        Assert.DoesNotContain("secret", service.Indicator.Detail);
    }

    [Fact]
    public async Task OtpScanIsBoundedAndLongBodiesHaveAnExplicitTruncationMarker()
    {
        await using var fixture = new Fixture(); await fixture.Database.InitializeAsync();
        using (var connection = new SqliteConnection("Data Source=" + fixture.DbPath))
        {
            await connection.OpenAsync(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO SmsMessages (Id,RemoteNumber,Text,Timestamp,IsOutgoing,DeliveryState) VALUES (@id,'TEST',@text,@time,0,0)";
            command.Parameters.Add("@id", SqliteType.Text); command.Parameters.Add("@text", SqliteType.Text); command.Parameters.Add("@time", SqliteType.Text);
            for (var n = 0; n < 1001; n++)
            {
                command.Parameters["@id"].Value = "synthetic-" + n;
                command.Parameters["@text"].Value = "no otp present";
                command.Parameters["@time"].Value = DateTime.UtcNow.AddSeconds(-1).ToString("o"); await command.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        var processor = new SmsApiRequestProcessor(Enabled, fixture.Database);
        using (var json = Body(await Request(processor, "/api/v1/otp")))
        { Assert.False(json.RootElement.GetProperty("found").GetBoolean()); Assert.True(json.RootElement.GetProperty("scanLimited").GetBoolean()); }
        await fixture.Save("long-body", "unique-long-body " + new string('x', 6000), DateTime.UtcNow.AddSeconds(-1));
        using var result = Body(await Request(processor, "/api/v1/sms?keyword=unique-long-body"));
        var item = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("textTruncated").GetBoolean()); Assert.Equal(4096, item.GetProperty("text").GetString()!.Length);
    }

    private static int LocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    [Fact]
    public async Task NativeLoopbackHttpReturnsPrivateJsonRotatesTokenRestoresAndStopsWithoutHardware()
    {
        await using var fixture = new Fixture(); await fixture.Save("wire", "Wire service code 246810", DateTime.UtcNow.AddSeconds(-1));
        var store = new SmsApiSettingsStore(Path.Combine(fixture.DirectoryPath, "api.json"));
        await using var service = new SmsApiService(fixture.Database, store);
        var settings = Enabled with { Port = LocalPort() };
        await service.SaveAndApplyAsync(settings); Assert.Equal(StatusLight.Ready, service.Indicator.Light);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(4) };
        using (var unauthenticated = await http.GetAsync("api/v1/sms"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            Assert.True(unauthenticated.Headers.CacheControl!.NoStore);
            Assert.Equal("Bearer", Assert.Single(unauthenticated.Headers.WwwAuthenticate).Scheme);
            Assert.False(unauthenticated.Headers.Contains("Access-Control-Allow-Origin"));
        }
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        using (var authenticated = await http.GetAsync("api/v1/otp?keyword=Wire"))
        {
            authenticated.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync());
            Assert.Equal("246810", json.RootElement.GetProperty("code").GetString());
            Assert.Equal("nosniff", Assert.Single(authenticated.Headers.GetValues("X-Content-Type-Options")));
        }
        using (var post = await http.PostAsync("api/v1/sms", new StringContent(""))) Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        var rotated = settings with { Token = new string('Z', 40) }; await service.SaveAndApplyAsync(rotated);
        using (var old = await http.GetAsync("api/v1/sms")) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", rotated.Token);
        using (var current = await http.GetAsync("api/v1/sms")) current.EnsureSuccessStatusCode();
        await service.StopAsync(default); await service.StartAsync(default); // Restore persisted switch and token.
        using (var restored = await http.GetAsync("api/v1/otp")) restored.EnsureSuccessStatusCode();
        await service.SaveAndApplyAsync(rotated with { Enabled = false });
        try { using var stopped = await http.GetAsync("api/v1/sms"); Assert.False(stopped.IsSuccessStatusCode); }
        catch (HttpRequestException) { } // Http.sys may reject or close the now-unregistered prefix.
        await service.DisposeAsync(); await service.DisposeAsync();
        Assert.False((await store.LoadAsync()).Enabled);
    }

    [Fact]
    public async Task NativePortConflictShowsErrorAndDisableAlwaysClosesEvenWithInvalidUnsavedFields()
    {
        await using var fixture = new Fixture(); var settings = Enabled with { Port = LocalPort() };
        using var reservation = new HttpListener(); reservation.Prefixes.Add(settings.BaseUrl); reservation.Start();
        await using var service = new SmsApiService(fixture.Database, new(Path.Combine(fixture.DirectoryPath, "api.json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAndApplyAsync(settings));
        Assert.Equal(StatusLight.Error, service.Indicator.Light);
        await service.SaveAndApplyAsync(new() { Enabled = false, BindAddress = "invalid unsaved address" });
        Assert.Equal(StatusLight.Inactive, service.Indicator.Light);
    }

    [Fact]
    public async Task SettingsUiGeneratesWithoutApplyingAndSavesExplicitly()
    {
        var api = DispatchProxy.Create<ISmsApiService, PhoneCallServiceProxy>(); SmsApiSettings? saved = null;
        ((PhoneCallServiceProxy)api).Handler = (method, args) => method.Name switch
        {
            "get_Settings" => new SmsApiSettings { Token = Token },
            "get_Indicator" => new GatewayStatusItem("API", "off", "off"),
            "SaveAndApplyAsync" => Save((SmsApiSettings)args![0]!),
            "add_StatusChanged" or "remove_StatusChanged" => null, _ => throw new NotSupportedException(method.Name)
        };
        Task Save(SmsApiSettings settings) { saved = settings; return Task.CompletedTask; }
        var kernel = DispatchProxy.Create<IVoKernelService, PhoneCallServiceProxy>();
        var model = new SettingsViewModel(kernel, smsApi: api); model.LoadSmsApiSettings();
        Assert.Equal(Token, model.SmsApiToken); model.GenerateSmsApiTokenCommand.Execute(null);
        Assert.Null(saved); Assert.Equal(64, model.SmsApiToken.Length);
        model.SmsApiEnabled = true; await model.SaveSmsApiCommand.ExecuteAsync(null);
        Assert.NotNull(saved); Assert.True(saved.Enabled); Assert.Equal(model.SmsApiToken, saved.Token);
    }
}
