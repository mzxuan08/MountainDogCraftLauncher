using System.Text.Json;
using MDCL.Core.Instances;
using MDCL.Desktop.Composition;
using MDCL.Infrastructure.Diagnostics;
using MDCL.Infrastructure.Launcher;
using Microsoft.Extensions.DependencyInjection;

// Opt-in real-network/manual acceptance runner. Never called by dotnet test.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected --name value pairs.");
    options.Add(args[i][2..], args[i + 1]);
}
string Required(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException("Missing --" + key);
var dataRoot = Path.GetFullPath(Required("root"));
var version = Required("version");
var name = options.GetValueOrDefault("name", "Acceptance " + version);
var evidence = Path.GetFullPath(Required("evidence"));
Directory.CreateDirectory(evidence);
Environment.SetEnvironmentVariable("MDCL_DATA_ROOT", dataRoot);
using var services = CompositionRoot.CreateServiceProvider();
var workflow = services.GetRequiredService<LauncherWorkflow>();
var redactor = new SensitiveDataRedactor();
void Log(string line) => Console.WriteLine(redactor.RedactPath(redactor.Redact(line), dataRoot));
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    await workflow.InitializeAsync(cts.Token);
    var instance = (await workflow.ListInstancesAsync(cts.Token)).FirstOrDefault(x => x.DisplayName == name)
        ?? await workflow.CreateInstanceAsync(name, version, cts.Token);
    Log($"instance={instance.InstanceId} version={version}");
    var progress = new Progress<MDCL.Core.Installation.OperationEvent>(x =>
    {
        if (x.MessageCode is "step-started" or "step-completed" or "operation-completed") Log($"{x.StepId}: {x.MessageCode}");
    });
    await workflow.InstallAsync(instance.InstanceId, progress, cts.Token);
    var loader = options.GetValueOrDefault("loader", "vanilla");
    if (loader == "fabric") await workflow.InstallFabricAsync(instance.InstanceId, Required("loader-version"), progress, cts.Token);
    else if (loader is "forge" or "neoforge") await workflow.InstallForgeAsync(instance.InstanceId,
        loader == "forge" ? LoaderKind.Forge : LoaderKind.NeoForge, Required("loader-version"), progress, cts.Token);
    var accounts = await workflow.Accounts.ListAsync(cts.Token);
    var account = accounts.FirstOrDefault(x => x.Kind == MDCL.Core.Accounts.AccountKind.Local)
        ?? await workflow.Accounts.AddLocalAsync("MDCLTest", cts.Token);
    for (var launch = 1; launch <= int.Parse(options.GetValueOrDefault("launch-count", "1")); launch++)
    {
        var outcome = await workflow.LaunchAsync(instance.InstanceId, account.Id, progress, cts.Token);
        await using var session = outcome.Session;
        Log($"process-started pid={session.ProcessId} java={outcome.Java.MajorVersion}/{outcome.Java.Architecture} launch={launch}");
        var record = new { instanceId = instance.InstanceId.ToString(), version, loader, launch,
            pid = session.ProcessId, javaMajor = outcome.Java.MajorVersion, javaVendor = outcome.Java.Vendor,
            architecture = outcome.Java.Architecture.ToString(), menuReached = "manual evidence required" };
        await File.WriteAllTextAsync(Path.Combine(evidence, $"launch-{launch}.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), cts.Token);
        var logTask = Task.Run(async () =>
        {
            await using var log = new StreamWriter(Path.Combine(evidence, $"launch-{launch}.log"));
            await foreach (var line in session.ReadLogsAsync(cts.Token))
            {
                var safe = redactor.RedactPath(redactor.Redact(line.Text), dataRoot);
                await log.WriteLineAsync($"[{line.Stream}] {safe}");
                await log.FlushAsync(cts.Token);
            }
        }, cts.Token);
        if (await Task.WhenAny(session.ExitTask, Task.Delay(TimeSpan.FromSeconds(int.Parse(options.GetValueOrDefault("hold", "300"))), cts.Token)) != session.ExitTask)
            await session.StopAsync(CancellationToken.None);
        var exit = await session.ExitTask;
        await logTask;
        await File.WriteAllTextAsync(Path.Combine(evidence, $"exit-{launch}.json"), JsonSerializer.Serialize(exit), cts.Token);
        Log($"process-exit code={exit.ExitCode} state={exit.State} userStop={exit.StoppedByUser}");
        if (exit.ExitCode != 0 || exit.StoppedByUser) return 1;
        await Task.Delay(200, cts.Token); // allow lease finalization before the next launch
    }
    return 0;
}
catch (Exception ex)
{
    Log(ex.ToString());
    return 1;
}
