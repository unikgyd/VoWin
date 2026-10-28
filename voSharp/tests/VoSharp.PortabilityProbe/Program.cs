using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using VoSharp.Tests;
using Xunit;

// Deliberately curated hardware-free protocol tests. No system D-Bus, AT/QMI,
// microphone, real SIM, persistent credentials, public SIP or Asterisk service.
Type[] suites = [typeof(SipGatewayTests), typeof(SipGatewayPeerTests), typeof(SipGatewayLineRoutingTests),
    typeof(SipServerFeatureTests), typeof(G711VectorTests), typeof(PortableMediaTests), typeof(GatewayHostTests)];
var protocolOutput = Console.Out;
Console.SetOut(Console.Error);
var reportPath = args is ["--report", var path] ? Path.GetFullPath(path) : null;
if (args.Length != 0 && reportPath is null) throw new ArgumentException("Use --report <new output file> or no arguments.");
var results = new List<object>(); var failed = 0; var passed = 0;
foreach (var suite in suites)
foreach (var method in suite.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    var fact = method.GetCustomAttribute<FactAttribute>();
    if (fact is null) continue;
    if (fact.Skip is not null) throw new InvalidOperationException("The portable probe cannot silently skip selected tests.");
    var rows = method.GetCustomAttributes<InlineDataAttribute>().Select(data => data.GetData(method).Single()).ToArray();
    if (rows.Length == 0) rows = [Array.Empty<object>()];
    for (var index = 0; index < rows.Length; index++)
    {
        var name = $"{suite.Name}.{method.Name}[{index}]";
        try
        {
            var instance = Activator.CreateInstance(suite);
            // xUnit converts attribute literals (Int32) to the declared byte/short
            // parameter types before invocation. Reflection alone does not. Keep
            // the exact same values and assertions as the ordinary test runner.
            var parameters = method.GetParameters();
            if (parameters.Length != rows[index].Length) throw new ArgumentException("Unexpected test-data arity.");
            var arguments = rows[index].Select((value, argumentIndex) =>
            {
                var target = Nullable.GetUnderlyingType(parameters[argumentIndex].ParameterType) ?? parameters[argumentIndex].ParameterType;
                return value is null || target.IsInstanceOfType(value) ? value : target.IsEnum
                    ? Enum.ToObject(target, value) : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
            }).ToArray();
            var pending = method.Invoke(instance, arguments);
            if (pending is Task task) await task.WaitAsync(TimeSpan.FromSeconds(60));
            if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (instance is IDisposable disposable) disposable.Dispose();
            passed++; results.Add(new { name, passed = true });
        }
        catch (Exception error)
        {
            var cause = error.InnerException ?? error;
            // This curated suite uses synthetic peers only. Keep assertion
            // diagnostics so a failed Linux run can be investigated, not hidden
            // by a later green run or mistaken for a platform limitation.
            Console.Error.WriteLine($"{name}: {cause}");
            failed++; results.Add(new { name, passed = false, error = cause.GetType().Name, detail = cause.Message });
        }
    }
}
var report = new {
    success = failed == 0, framework = RuntimeInformation.FrameworkDescription,
    platform = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    passed, failed, skipped = 0, hardwareAccessed = false, publicNetworkUsed = false, results,
};
if (reportPath is not null)
{
    await using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
    await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(new { success = failed == 0, passed, failed, skipped = 0 }));
}
else await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
