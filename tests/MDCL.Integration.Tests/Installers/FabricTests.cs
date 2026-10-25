using System.Net;
using System.Text;
using MDCL.Application.Ports;
using MDCL.Application.Versions;
using MDCL.Core;
using MDCL.Core.Instances;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Installers;
using Xunit;

namespace MDCL.Integration.Tests.Installers;

public sealed class FabricTests
{
    [Fact]
    public async Task FabricProvider_SelectsStableLoaderAndAddsMavenHashes()
    {
        var handler = new Handler();
        var provider = new FabricComponentVersionProvider(new HttpClient(handler));
        var selection = new VersionSelection("1.20.1", LoaderKind.Fabric);
        var target = new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
        var result = await provider.GetVersionAsync(selection,
            new VersionManifestEntry("1.20.1", "https://piston-meta.mojang.com/1.20.1.json", null, "release"), target, CancellationToken.None);
        Assert.Equal("0.16.10", result.LoaderVersion);
        Assert.Equal(2, result.Document.Libraries.Count);
        Assert.All(result.Document.Libraries, library =>
        {
            Assert.Equal("fabric-maven-sha1", library.HashSource);
            Assert.Equal(40, library.Downloads?.Artifact?.Sha1?.Length);
        });
        Assert.Equal(2, handler.SidecarRequests);
    }

    [Fact]
    public async Task VersionResolver_AppliesFabricProfileAndKeepsLoaderVersion()
    {
        var baseDoc = VersionDocumentParser.Parse("""
            { "id": "1.20.1", "mainClass": "net.minecraft.client.main.Main", "javaVersion": { "majorVersion": 17 },
              "libraries": [] }
            """, "fixture://base");
        var fabricDoc = VersionDocumentParser.Parse("""
            { "id": "fabric-loader-0.16.10-1.20.1", "inheritsFrom": "1.20.1",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.10", "url": "https://maven.fabricmc.net/" } ] }
            """, "fixture://fabric");
        fabricDoc = fabricDoc with
        {
            Libraries = [fabricDoc.Libraries[0] with
            {
                Downloads = new LibraryDownloads(new LibraryArtifact("net/fabricmc/fabric-loader/0.16.10/fabric-loader-0.16.10.jar",
                    new Uri("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.10/fabric-loader-0.16.10.jar"),
                    new string('a', 40), 10), new Dictionary<string, LibraryArtifact>()),
                HashSource = "fabric-maven-sha1"
            }]
        };
        var resolver = new VersionResolver(new FakeSource(baseDoc), [new FakeComponent(fabricDoc)]);
        var target = new TargetPlatform(LauncherOperatingSystem.Windows, CpuArchitecture.X64, CpuArchitecture.X64);
        var resolved = await resolver.ResolveAsync(new VersionSelection("1.20.1", LoaderKind.Fabric), target, CancellationToken.None);
        Assert.Equal("0.16.10", resolved.LoaderVersion);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", resolved.MainClass);
        Assert.Contains(resolved.Libraries, x => x.Coordinate.StartsWith("net.fabricmc:fabric-loader", StringComparison.Ordinal));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int SidecarRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/versions/loader/1.20.1")
                return Json("""[{"loader":{"version":"0.16.10","stable":true}}]""");
            if (path == "/v2/versions/loader/1.20.1/0.16.10/profile/json")
                return Json("""
                    { "id": "fabric-loader-0.16.10-1.20.1", "inheritsFrom": "1.20.1",
                      "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
                      "libraries": [
                        {"name":"net.fabricmc:fabric-loader:0.16.10","url":"https://maven.fabricmc.net/"},
                        {"name":"org.ow2.asm:asm:9.7","url":"https://maven.fabricmc.net/"}
                      ] }
                    """);
            if (path.EndsWith(".sha1", StringComparison.Ordinal))
            {
                SidecarRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('b', 40)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        });
    }

    private sealed class FakeSource(VersionDocument document) : IVersionMetadataSource
    {
        public Task<VersionManifest> GetManifestAsync(CancellationToken ct) => Task.FromResult(new VersionManifest("1.20.1", "1.20.1",
            [new VersionManifestEntry("1.20.1", "fixture://1.20.1", null, "release")]));
        public Task<VersionMetadataRecord> GetVersionAsync(VersionManifestEntry entry, CancellationToken ct) =>
            Task.FromResult(new VersionMetadataRecord(document, "fixture://1.20.1", null));
    }

    private sealed class FakeComponent(VersionDocument document) : IComponentVersionProvider
    {
        public LoaderKind Kind => LoaderKind.Fabric;
        public Task<ComponentVersion> GetVersionAsync(VersionSelection selection, VersionManifestEntry baseEntry, TargetPlatform target, CancellationToken ct) =>
            Task.FromResult(new ComponentVersion(document, "fixture://fabric", null, "0.16.10"));
    }
}
