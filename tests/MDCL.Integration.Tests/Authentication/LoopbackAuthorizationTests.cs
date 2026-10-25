using System.Net;
using System.Net.Sockets;
using MDCL.Application.Ports;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Authentication;
using Xunit;

namespace MDCL.Integration.Tests.Authentication;

public sealed class LoopbackAuthorizationTests
{
    [Fact]
    public async Task UnrelatedRequest_DoesNotConsumeLogin_AndCallbackResponseDoesNotEchoCode()
    {
        var options = Options();
        var browser = new BrowserSpy();
        var provider = Provider(options, browser);
        var login = provider.AcquireCodeAsync(options, "expected-state", "challenge", CancellationToken.None);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var unrelated = await http.GetAsync($"http://127.0.0.1:{options.RedirectPort}/favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        Assert.False(login.IsCompleted);
        using var response = await http.GetAsync(options.RedirectUri + "?state=expected-state&code=private-fixture-code");
        Assert.Equal("private-fixture-code", await login.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("private-fixture-code", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", browser.AuthorizeUri!.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("state=wrong&error=access_denied", "AUTH_STATE_MISMATCH")]
    [InlineData("state=expected-state&error=access_denied", "AUTH_CANCELED")]
    [InlineData("state=expected-state&state=expected-state&code=fixture", "AUTH_CALLBACK_INVALID")]
    [InlineData("state=expected-state", "AUTH_CODE_MISSING")]
    public async Task InvalidCallbacks_AreDistinctAndCloseTheListener(string query, string code)
    {
        var options = Options();
        var browser = new BrowserSpy();
        var login = Provider(options, browser).AcquireCodeAsync(options, "expected-state", "challenge", CancellationToken.None);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.GetAsync(options.RedirectUri + "?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await Assert.ThrowsAsync<MdclException>(() => login);
        Assert.Equal(code, error.Error.ErrorCode);
        using var again = new TcpListener(IPAddress.Loopback, options.RedirectPort);
        again.Start();
    }

    [Fact]
    public async Task OccupiedPort_IsActionable_AndDoesNotOpenBrowser()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        var options = new MicrosoftAuthOptions("fixture-client", ((IPEndPoint)occupied.LocalEndpoint).Port);
        var browser = new BrowserSpy();
        var error = await Assert.ThrowsAsync<MdclException>(() => Provider(options, browser).AcquireCodeAsync(options, "state", "challenge", CancellationToken.None));
        Assert.Equal("AUTH_CALLBACK_PORT_IN_USE", error.Error.ErrorCode);
        Assert.False(browser.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task CallerCancellation_ReleasesListenerWithoutWaitingForBrowser()
    {
        var options = Options();
        var browser = new BrowserSpy();
        using var cancellation = new CancellationTokenSource();
        var login = Provider(options, browser).AcquireCodeAsync(options, "state", "challenge", cancellation.Token);
        await browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        using var again = new TcpListener(IPAddress.Loopback, options.RedirectPort);
        again.Start();
    }

    private static MicrosoftAuthOptions Options()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new MicrosoftAuthOptions("fixture-client", ((IPEndPoint)listener.LocalEndpoint).Port);
    }
    private static LoopbackAuthorizationCodeProvider Provider(MicrosoftAuthOptions options, BrowserSpy browser) =>
        new(new MicrosoftAuthProtocol(new HttpClient(), options), browser, TimeSpan.FromSeconds(10));
    private sealed class BrowserSpy : IBrowserLauncher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri? AuthorizeUri { get; private set; }
        public Task OpenAsync(Uri uri, CancellationToken ct)
        {
            AuthorizeUri = uri;
            Started.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
