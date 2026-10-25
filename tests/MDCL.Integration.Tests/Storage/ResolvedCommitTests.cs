using MDCL.Core;
using MDCL.Core.Errors;
using MDCL.Core.Java;
using MDCL.Core.Versions;
using MDCL.Infrastructure.Storage;
using Xunit;

namespace MDCL.Integration.Tests.Storage;

public sealed class ResolvedCommitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestCommitFailure_BlocksReopenedSnapshotUntilRepair(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MDCL-commit-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ResolvedVersionStore();
            var old = new ResolvedVersion("1.20.1", "OldMain", [], [], [], [], null, null, null,
                new JavaRequirement(17, 17, null, CpuArchitecture.X64, [], []), [], "old");
            await store.SaveAsync(directory, old, CancellationToken.None);
            var next = old with { MainClass = "NewLoaderMain", LoaderVersion = "0.16.14", ContentHash = "new" };
            await Assert.ThrowsAnyAsync<Exception>(() => store.CommitAsync(directory, next,
                () => cancel ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new IOException("injected manifest save failure")), CancellationToken.None));
            var reopened = new ResolvedVersionStore();
            var error = await Assert.ThrowsAsync<MdclException>(() => reopened.LoadAsync(directory, CancellationToken.None));
            Assert.Equal("INSTANCE_INSTALL_INCOMPLETE", error.Error.ErrorCode);
            Assert.True(File.Exists(Path.Combine(directory, ResolvedVersionStore.PendingFileName)));
            await reopened.CommitAsync(directory, old, () => Task.CompletedTask, CancellationToken.None);
            Assert.Equal("old", (await reopened.LoadAsync(directory, CancellationToken.None)).ContentHash);
            Assert.False(File.Exists(Path.Combine(directory, ResolvedVersionStore.PendingFileName)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
