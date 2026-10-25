using System.Diagnostics;
using MDCL.Infrastructure.Installers;
using MDCL.Infrastructure.Java;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

[Collection("ProcessEnvironment")]
public sealed class OwnedProcessCancellationTests
{
    [Fact]
    public async Task CancelInstaller_TerminatesOwnedChildBeforeReturning()
    {
        using var temp = new TestDirectory();
        var pidFile = Path.Combine(temp.Path, "pid");
        using var ct = new CancellationTokenSource();
        var running = new ProcessInstallerRunner().RunAsync(HelperPath(), ["--pid-file", pidFile, "--sleep"],
            Path.GetDirectoryName(HelperPath())!, TimeSpan.FromSeconds(20), ct.Token);
        var pid = await ReadPidAsync(pidFile);
        try
        {
            ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.False(IsRunning(pid));
        }
        finally { KillFixture(pid); }
    }

    [Fact]
    public async Task CancelJavaProbe_TerminatesOwnedChildBeforeReturning()
    {
        using var temp = new TestDirectory();
        var pidFile = Path.Combine(temp.Path, "pid");
        var previous = Environment.GetEnvironmentVariable("MDCL_TEST_PROBE_PID_FILE");
        Environment.SetEnvironmentVariable("MDCL_TEST_PROBE_PID_FILE", pidFile);
        int pid = 0;
        try
        {
            using var ct = new CancellationTokenSource();
            var running = new ProcessJavaProbe(TimeSpan.FromSeconds(20)).ProbeAsync(HelperPath(), ct.Token);
            pid = await ReadPidAsync(pidFile);
            ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.False(IsRunning(pid));
        }
        finally { Environment.SetEnvironmentVariable("MDCL_TEST_PROBE_PID_FILE", previous); KillFixture(pid); }
    }

    private static string HelperPath()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var configuration = new DirectoryInfo(baseDir).Parent!.Name;
        return Path.GetFullPath(Path.Combine(baseDir, "../../../../Fixtures/ArgumentEcho/bin", configuration,
            "net10.0", OperatingSystem.IsWindows() ? "ArgumentEcho.exe" : "ArgumentEcho"));
    }
    private static async Task<int> ReadPidAsync(string file)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            try { if (File.Exists(file) && int.TryParse(await File.ReadAllTextAsync(file), out var pid)) return pid; }
            catch (IOException) { } // the fixture may still be atomically closing its first write
            await Task.Delay(20);
        }
        throw new TimeoutException("Fixture did not become ready.");
    }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void KillFixture(int pid)
    {
        if (pid <= 0) return;
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(true); process.WaitForExit(5000); }
        catch (ArgumentException) { }
    }
}
