using System.Reflection;

namespace MDCL.Core;

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
