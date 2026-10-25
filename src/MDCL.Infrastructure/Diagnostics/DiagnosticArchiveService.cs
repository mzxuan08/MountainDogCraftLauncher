using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using MDCL.Application.Ports;
using MDCL.Core;

namespace MDCL.Infrastructure.Diagnostics;

public sealed class DiagnosticArchiveService(IAppPaths paths)
{
    private static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web){WriteIndented=true};
    private readonly SensitiveDataRedactor _redactor=new();
    private sealed record Report(string ProductVersion,string Rid,string OperatingSystem,string Framework,IReadOnlyList<string> Logs);

    public string CreatePreview(IEnumerable<string> logs)
    {
        var safe=logs.TakeLast(200).Select(line=>
        {
            var value=_redactor.RedactPath(_redactor.Redact(line),paths.ConfigRoot);
            value=_redactor.RedactPath(value,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return value.Length>2048?value[..2048]+"…":value;
        }).ToArray();
        return JsonSerializer.Serialize(new Report(BuildInfo.Version,RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription,RuntimeInformation.FrameworkDescription,safe),Options);
    }

    public async Task<string> ExportAsync(string reviewedPreview,CancellationToken ct)
    {
        if(reviewedPreview.Length>2*1024*1024) throw new ArgumentException("Diagnostic preview is too large.");
        var report=JsonSerializer.Deserialize<Report>(reviewedPreview,Options)??throw new ArgumentException("Invalid diagnostic preview.");
        // Serialize only the allowlisted schema; no database, account or user files are read.
        var content=_redactor.Redact(JsonSerializer.Serialize(report,Options));
        var directory=Path.Combine(paths.LogRoot,"diagnostics"); Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"MDCL-diagnostics-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            using var zip=ZipFile.Open(file,ZipArchiveMode.Create);
            await using var writer=new StreamWriter(zip.CreateEntry("report.json").Open());
            await writer.WriteAsync(content.AsMemory(),ct);
            return file;
        }
        catch { if(File.Exists(file)) File.Delete(file); throw; }
    }
}
