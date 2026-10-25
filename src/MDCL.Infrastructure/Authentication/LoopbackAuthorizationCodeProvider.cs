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
            var expected = new Uri(options.RedirectUri);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, timeout.Token);
                var lines = request.Split("\r\n", StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                var host = lines.FirstOrDefault(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
                if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith('/') || parts[1].StartsWith("//", StringComparison.Ordinal) ||
                    !string.Equals(host, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(expected.GetLeftPart(UriPartial.Authority) + parts[1], UriKind.Absolute, out var callback) ||
                    callback.AbsolutePath != expected.AbsolutePath || !string.IsNullOrEmpty(callback.Fragment))
                {
                    await WriteResponseAsync(stream, "此地址不接收登录回调。请返回 Microsoft 登录页面。", timeout.Token, 404);
                    continue;
                }
                try
                {
                    var query = ParseQuery(callback.Query);
                    // Validate state even for denial/error responses: a foreign
                    // request cannot cancel or impersonate this authorization.
                    if (!query.TryGetValue("state", out var returnedState) || !FixedEquals(returnedState, state))
                        throw new MdclException(MdclError.Of("AUTH_STATE_MISMATCH", "Authentication", "登录 state 校验失败。", false));
                    if (query.TryGetValue("error", out var error))
                        throw new MdclException(MdclError.Of(error == "access_denied" ? "AUTH_CANCELED" : "AUTH_OAUTH_FAILED",
                            "Authentication", "浏览器授权未完成，可以返回启动器重试。", false));
                    if (!query.TryGetValue("code", out var codeValue) || string.IsNullOrWhiteSpace(codeValue))
                        throw new MdclException(MdclError.Of("AUTH_CODE_MISSING", "Authentication", "登录回调没有授权码。", false));
                    await WriteResponseAsync(stream, "浏览器授权已返回 MDCL，正在验证账号。请查看启动器中的最终结果。", timeout.Token);
                    return codeValue;
                }
                catch (MdclException)
                {
                    await WriteResponseAsync(stream, "登录回调验证失败或授权已取消，请查看 MDCL 并重新登录。", timeout.Token, 400);
                    throw;
                }
            }
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new MdclException(MdclError.Of("AUTH_CALLBACK_PORT_IN_USE", "Authentication",
                "登录回调端口已被占用，请关闭其他 MDCL 登录窗口后重试。", true), error);
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
            if (!result.TryAdd(Uri.UnescapeDataString(pair[..equals]), Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '))))
                throw new MdclException(MdclError.Of("AUTH_CALLBACK_INVALID", "Authentication", "登录回调包含重复参数。", false));
        }
        return result;
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task WriteResponseAsync(NetworkStream stream, string message, CancellationToken ct, int status = 200)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>MDCL</title><p>" + WebUtility.HtmlEncode(message) + "</p>");
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nX-Content-Type-Options: nosniff\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }
}
