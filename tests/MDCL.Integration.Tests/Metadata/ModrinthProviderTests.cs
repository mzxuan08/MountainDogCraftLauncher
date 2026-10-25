using System.Net;
using System.Text;
using MDCL.Core.Errors;
using MDCL.Infrastructure.Metadata;
using Xunit;

namespace MDCL.Integration.Tests.Metadata;

public sealed class ModrinthProviderTests
{
    [Fact]
    public async Task Search_ParsesProjects()
    {
        var handler = new Handler("""{"hits":[{"project_id":"p1","slug":"demo","title":"Demo","description":"d","project_type":"mod","downloads":12,"loaders":["fabric"],"versions":["1.20.1"]}]}""");
        var provider = new ModrinthProvider(new HttpClient(handler));
        var projects = await provider.SearchAsync("demo", 5, CancellationToken.None);
        var project = Assert.Single(projects);
        Assert.Equal("p1", project.Id);
        Assert.Equal("fabric", project.Loaders[0]);
        Assert.Contains("/v2/search", handler.Paths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Versions_ApplyGameAndLoaderFilterAndParsePrimaryFile()
    {
        var handler = new Handler("""[{"id":"v1","project_id":"p1","version_number":"1.0","version_type":"release","game_versions":["1.20.1"],"loaders":["fabric"],"files":[{"filename":"demo.jar","url":"https://cdn.example/demo.jar","size":10,"hashes":{"sha512":"abc","sha1":"def"},"primary":true}]}]""");
        var provider = new ModrinthProvider(new HttpClient(handler));
        var versions = await provider.GetVersionsAsync("p1", "1.20.1", "fabric", CancellationToken.None);
        var version = Assert.Single(versions);
        Assert.True(version.Files[0].Primary);
        Assert.Equal("https://cdn.example/demo.jar", version.Files[0].Url.AbsoluteUri);
        Assert.Contains("game_versions=", handler.Paths[0], StringComparison.Ordinal);
        Assert.Contains("loaders=", handler.Paths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailure_IsStructured()
    {
        var handler = new Handler("{}", HttpStatusCode.ServiceUnavailable);
        var provider = new ModrinthProvider(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<MdclException>(() => provider.SearchAsync("demo", 5, CancellationToken.None));
        Assert.Equal("MODRINTH_HTTP", error.Error.ErrorCode);
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
