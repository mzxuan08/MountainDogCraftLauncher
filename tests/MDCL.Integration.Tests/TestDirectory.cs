namespace MDCL.Integration.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MDCL-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // Windows can retain a final file handle briefly after process exit.
        // Process tests assert ownership/termination before fixture cleanup.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(Path, true); break; }
            catch (DirectoryNotFoundException) { break; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }
}
