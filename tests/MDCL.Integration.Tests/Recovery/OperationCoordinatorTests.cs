using MDCL.Application.Ports;
using MDCL.Core.Installation;
using MDCL.Core.Instances;
using MDCL.Infrastructure.Operations;
using MDCL.Infrastructure.Storage;
using MDCL.Infrastructure.Downloads;
using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace MDCL.Integration.Tests.Recovery;

public sealed class OperationCoordinatorTests
{
    [Fact]
    public async Task CompletedJournal_RepairsCorruptArtifactAndInstallsIntoAnotherTarget()
    {
        using var temp = new TestDirectory();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var handler = new BytesHandler(bytes);
        using var http = new HttpClient(handler);
        var coordinator = new OperationCoordinator(new ArtifactDownloader(http), Path.Combine(temp.Path, "staging"));
        var artifact = new ArtifactSpec("repair", [new Uri("https://example.invalid/a")], bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), "SHA256", "fixture", "a.bin", ArtifactTrustLevel.TrustedMetadata);
        var plan = Plan("repair-plan", new InstallStep("files", [], [artifact]));
        var target = Path.Combine(temp.Path, "first");
        Assert.Equal(OperationState.Completed, (await coordinator.RunAsync(plan, target, null, CancellationToken.None)).State);
        await File.WriteAllTextAsync(Path.Combine(target, "a.bin"), "broken");
        Assert.Equal(OperationState.Completed, (await coordinator.RunAsync(plan, target, null, CancellationToken.None)).State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(target, "a.bin")));
        var other = Path.Combine(temp.Path, "second");
        Assert.Equal(OperationState.Completed, (await coordinator.RunAsync(plan, other, null, CancellationToken.None)).State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(other, "a.bin")));
        await coordinator.RunAsync(plan, target, null, CancellationToken.None);
        Assert.Equal(3, handler.Requests); // the fourth pass must use the verified cache
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    [Fact]
    public async Task SuccessfulPlan_IsIdempotentAndJournaled()
    {
        using var temp = new TestDirectory();
        var downloader = new StubDownloader();
        var coordinator = new OperationCoordinator(downloader, Path.Combine(temp.Path, "staging"));
        var plan = Plan("plan-idempotent",
            new InstallStep("s1", [], [Artifact("one")]),
            new InstallStep("s2", ["s1"], [Artifact("two")]));
        var events = new List<OperationEvent>();
        var progress = new InlineProgress<OperationEvent>(events.Add);
        var first = await coordinator.RunAsync(plan, Path.Combine(temp.Path, "target"), progress, CancellationToken.None);
        var second = await coordinator.RunAsync(plan, Path.Combine(temp.Path, "target"), progress, CancellationToken.None);
        Assert.Equal(OperationState.Completed, first.State);
        Assert.Equal(OperationState.Completed, second.State);
        Assert.Equal(2, downloader.CallsFor("one"));
        Assert.Equal(2, downloader.CallsFor("two"));
        Assert.Equal(2, events.Count(x => x.MessageCode == "operation-completed" && x.Phase == OperationState.Completed));
        var journals = Directory.GetFiles(Path.Combine(temp.Path, "staging"), "journal.json", SearchOption.AllDirectories);
        var journal = await new InstallJournalStore(Assert.Single(journals)).LoadAsync(CancellationToken.None);
        Assert.NotNull(journal);
        Assert.Equal("Completed", journal.State);
    }

    [Fact]
    public async Task MissingDependency_FailsWithoutRunningStep()
    {
        using var temp = new TestDirectory();
        var downloader = new StubDownloader();
        var coordinator = new OperationCoordinator(downloader, Path.Combine(temp.Path, "staging"));
        var plan = Plan("plan-missing-dep", new InstallStep("s2", ["s1"], [Artifact("two")]));
        var result = await coordinator.RunAsync(plan, Path.Combine(temp.Path, "target"), null, CancellationToken.None);
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Equal("INSTALL_DEPENDENCY_MISSING", result.Error?.ErrorCode);
        Assert.Equal(0, downloader.CallsFor("two"));
    }

    [Fact]
    public async Task Cancel_LeavesRecoverableJournalAndCanResume()
    {
        using var temp = new TestDirectory();
        var downloader = new StubDownloader { BlockFirst = true };
        var coordinator = new OperationCoordinator(downloader, Path.Combine(temp.Path, "staging"));
        var plan = Plan("plan-cancel", new InstallStep("s1", [], [Artifact("one")]));
        using var cts = new CancellationTokenSource();
        var running = coordinator.RunAsync(plan, Path.Combine(temp.Path, "target"), null, cts.Token);
        await downloader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var canceled = await running;
        Assert.Equal(OperationState.Canceled, canceled.State);
        downloader.BlockFirst = false;
        var resumed = await coordinator.RunAsync(plan, Path.Combine(temp.Path, "target"), null, CancellationToken.None);
        Assert.Equal(OperationState.Completed, resumed.State);
        Assert.True(File.Exists(Path.Combine(temp.Path, "target", "one.bin")));
    }

    private static InstallPlan Plan(string id, params InstallStep[] steps) => new(
        id, InstanceId.New(), "hash-" + id, 1, steps, "snapshot");

    private static ArtifactSpec Artifact(string key) => new(
        key, [new Uri("https://example.invalid/" + key)], 1, new string('a', 64), "SHA256", "fixture", key + ".bin", ArtifactTrustLevel.TrustedMetadata);

    private sealed class StubDownloader : IArtifactDownloader
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFirst { get; set; }

        public int CallsFor(string key) => _calls.GetValueOrDefault(key);

        public async Task<ArtifactReceipt> EnsureAsync(ArtifactSpec artifact, string targetRoot, IProgress<TransferEvent>? progress, CancellationToken ct)
        {
            _calls.TryGetValue(artifact.ArtifactKey, out var calls);
            _calls[artifact.ArtifactKey] = calls + 1;
            if (BlockFirst && calls == 0)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetRoot);
            var path = Path.Combine(targetRoot, artifact.RelativeTargetPath);
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, artifact.ArtifactKey, ct);
            return new ArtifactReceipt(artifact.ArtifactKey, path, artifact.ExpectedHash!, artifact.CandidateUris[0], false);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
