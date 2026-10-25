using System.Diagnostics;

namespace MDCL.Infrastructure.Updates;

public static class UpdateProcessGuard
{
    public static async Task WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"进程 {processId} 在 {timeout.TotalSeconds:0} 秒内未退出。");
            }
        }
    }
}
