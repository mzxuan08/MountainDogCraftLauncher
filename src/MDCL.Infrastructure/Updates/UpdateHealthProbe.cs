using System.Diagnostics;
using System.Text.Json;

namespace MDCL.Infrastructure.Updates;

public static class UpdateHealthProbe
{
    public static async Task<bool> CheckAsync(string target, string expectedVersion, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".mdcl-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, "receipt.json");
        var nonce = Guid.NewGuid().ToString("N");
        var executable = target.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(target, "Contents/MacOS/MDCL.Desktop")
            : Path.Combine(target, OperatingSystem.IsWindows() ? "MDCL.Desktop.exe" : "MDCL.Desktop");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = target, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--health-check"); start.ArgumentList.Add(receipt);
        start.ArgumentList.Add("--health-nonce"); start.ArgumentList.Add(nonce);
        // Probe against isolated disposable data, never open/migrate the user's database.
        start.Environment["MDCL_DATA_ROOT"] = Path.Combine(directory, "data");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return false;
            var stdout = process.StandardOutput.ReadToEndAsync(lifetime.Token);
            var stderr = process.StandardError.ReadToEndAsync(lifetime.Token);
            try { await process.WaitForExitAsync(lifetime.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
                ct.ThrowIfCancellationRequested();
                return false;
            }
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0 || !File.Exists(receipt)) return false;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, ct));
            return json.RootElement.GetProperty("nonce").GetString() == nonce &&
                json.RootElement.GetProperty("version").GetString() == expectedVersion;
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception or KeyNotFoundException) { return false; }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
