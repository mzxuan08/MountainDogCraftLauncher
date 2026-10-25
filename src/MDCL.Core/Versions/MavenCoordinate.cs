namespace MDCL.Core.Versions;

public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public string Identity => $"{Group}:{Artifact}:{Version}:{Classifier ?? ""}@{Extension}";
    public string GaKey => $"{Group}:{Artifact}:{Classifier ?? ""}";

    public static MavenCoordinate Parse(string value)
    {
        var at = value.LastIndexOf('@');
        var extension = at >= 0 ? value[(at + 1)..] : "jar";
        var coordinate = at >= 0 ? value[..at] : value;
        var parts = coordinate.Split(':');
        if (parts.Length is < 3 or > 4) throw new FormatException($"Invalid Maven coordinate: {value}");
        return new MavenCoordinate(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null, extension);
    }

    public string DefaultPath => $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{Artifact}-{Version}{(Classifier is null ? "" : "-" + Classifier)}.{Extension}";
}
