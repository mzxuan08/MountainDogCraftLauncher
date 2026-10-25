using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Core.Errors;

namespace MDCL.Infrastructure.Authentication;

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Only HTTP(S) authorization URLs can be opened.", nameof(uri));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class LoopbackAuthorizationCodeProvider : IAuthorizationCodeProvider
{
    private readonly MicrosoftAuthProtocol _protocol;
    private readonly IBrowserLauncher _browser;
    private readonly TimeSpan _timeout;

    public LoopbackAuthorizationCodeProvider(MicrosoftAuthProtocol protocol, IBrowserLauncher browser, TimeSpan? timeout = null)
    {
        _protocol = protocol;
        _browser = browser;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<string> AcquireCodeAsync(MicrosoftAuthOptions options, string state, string challenge, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var listener = new TcpListener(IPAddress.Loopback, options.RedirectPort);
        try
        {
            listener.Start();
            await _browser.OpenAsync(_protocol.BuildAuthorizeUri(state, challenge), ct);
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var request = await ReadHeadersAsync(stream, timeout.Token);
            var firstLine = request.Split("\r\n", 2, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            if (parts.Length < 2 || !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var callback))
                throw new MdclException(MdclError.Of("AUTH_CALLBACK_INVALID", "Authentication", "登录回调格式无效。", false));
            var query = ParseQuery(callback.Query);
            if (query.TryGetValue("error", out var error))
            {
                var code = error == "access_denied" ? "AUTH_CANCELED" : "AUTH_OAUTH_FAILED";
                await WriteResponseAsync(stream, "登录未完成，可以关闭此页面并重试。", timeout.Token);
                throw new MdclException(MdclError.Of(code, "Authentication", "浏览器授权未完成：" + error, false));
            }
            if (!query.TryGetValue("state", out var returnedState) || !FixedEquals(returnedState, state))
                throw new MdclException(MdclError.Of("AUTH_STATE_MISMATCH", "Authentication", "登录 state 校验失败。", false));
            if (!query.TryGetValue("code", out var codeValue) || string.IsNullOrWhiteSpace(codeValue))
                throw new MdclException(MdclError.Of("AUTH_CODE_MISSING", "Authentication", "登录回调没有授权码。", false));
            await WriteResponseAsync(stream, "MDCL 登录回调已完成，可以关闭此页面。", timeout.Token);
            return codeValue;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MdclException(MdclError.Of("AUTH_CALLBACK_TIMEOUT", "Authentication", "等待浏览器登录回调超时。", true));
        }
        finally { listener.Stop(); }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder();
        while (builder.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            builder.Append((char)buffer[0]);
            if (builder.Length >= 4 && builder[^4] == '\r' && builder[^3] == '\n' && builder[^2] == '\r' && builder[^1] == '\n') break;
        }
        return builder.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            result[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task WriteResponseAsync(NetworkStream stream, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>MDCL</title><p>" + WebUtility.HtmlEncode(message) + "</p>");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }
}
