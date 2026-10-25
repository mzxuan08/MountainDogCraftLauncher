using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
var injected = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS");
Console.WriteLine("ENV_JAVA_TOOL_OPTIONS=" + (injected ?? "<null>"));
for (var index = 0; index < args.Length; index++)
    Console.WriteLine($"ARG[{index}]={Convert.ToBase64String(Encoding.UTF8.GetBytes(args[index]))}");

if (args.Contains("--utf8", StringComparer.Ordinal))
{
    Console.Write("中文🙂");
    Console.Out.Flush();
    await Task.Delay(20);
    Console.Write("结束");
}

if (args.Contains("--flood", StringComparer.Ordinal))
{
    await Task.WhenAll(
        Task.Run(async () => { for (var i = 0; i < 2000; i++) await Console.Out.WriteLineAsync("OUT-" + i); }),
        Task.Run(async () => { for (var i = 0; i < 2000; i++) await Console.Error.WriteLineAsync("ERR-" + i); }));
}

if (args.Contains("--sleep", StringComparer.Ordinal)) await Task.Delay(TimeSpan.FromSeconds(30));
if (args.Contains("--log-secrets", StringComparer.Ordinal))
{
    Console.WriteLine("Authorization: Bearer short-bearer");
    Console.Error.WriteLine("refresh_token=short-refresh");
}
var exitIndex = Array.IndexOf(args, "--exit");
return exitIndex >= 0 && exitIndex + 1 < args.Length && int.TryParse(args[exitIndex + 1], out var exitCode) ? exitCode : 0;
