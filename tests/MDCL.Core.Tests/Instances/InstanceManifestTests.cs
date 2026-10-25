using MDCL.Core.Errors;
using MDCL.Core.Instances;
using Xunit;

namespace MDCL.Core.Tests.Instances;

public sealed class InstanceManifestTests
{
    [Fact]
    public void Rename_DoesNotChangeStableInstanceId()
    {
        var id = InstanceId.New();
        var original = Manifest(id, "原版");
        var renamed = original with { DisplayName = "改名后" };
        Assert.Equal(id, renamed.InstanceId);
        Assert.NotEqual(original.DisplayName, renamed.DisplayName);
    }

    [Fact]
    public void SchemaRoundTrip_PreservesArgumentArrayAndOpaqueVersion()
    {
        var manifest = Manifest(InstanceId.New(), "中文 & 空格") with
        {
            BaseVersion = "26.3",
            ExtraJvmArgs = ["-Dname=含 空格 & 字符", "-Xmx4G"]
        };
        var json = InstanceManifestSerializer.Serialize(manifest);
        var loaded = InstanceManifestSerializer.Deserialize(json);
        Assert.Equal("26.3", loaded.BaseVersion);
        Assert.Equal(manifest.ExtraJvmArgs, loaded.ExtraJvmArgs);
        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("minecraftArguments", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchema_IsRejectedInsteadOfDowngraded()
    {
        var json = InstanceManifestSerializer.Serialize(Manifest(InstanceId.New(), "future"));
        json = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        var error = Assert.Throws<MdclException>(() => InstanceManifestSerializer.Deserialize(json));
        Assert.Equal("INSTANCE_SCHEMA_UNSUPPORTED", error.Error.ErrorCode);
    }

    [Theory]
    [InlineData(LauncherOperatingSystem.Windows, CpuArchitecture.X64, "win-x64", "windows")]
    [InlineData(LauncherOperatingSystem.MacOS, CpuArchitecture.X64, "osx-x64", "osx")]
    [InlineData(LauncherOperatingSystem.MacOS, CpuArchitecture.Arm64, "osx-arm64", "osx")]
    public void TargetPlatform_MapsRidAndMojangNamesSeparately(LauncherOperatingSystem os, CpuArchitecture arch, string rid, string mojang)
    {
        var target = new TargetPlatform(os, arch, arch);
        Assert.Equal(rid, target.RuntimeIdentifier);
        Assert.Equal(mojang, target.MojangOsName);
    }

    internal static InstanceManifest Manifest(InstanceId id, string name) => new()
    {
        InstanceId = id,
        DisplayName = name,
        BaseVersion = "1.20.1",
        GameDirectory = ".minecraft",
        CreatedAt = DateTimeOffset.UnixEpoch
    };
}
