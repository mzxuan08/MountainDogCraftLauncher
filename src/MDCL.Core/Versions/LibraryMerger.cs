using MDCL.Core.Errors;

namespace MDCL.Core.Versions;

public interface ILibraryMerger
{
    IReadOnlyList<LibraryDocument> Merge(
        IReadOnlyList<LibraryDocument> parent,
        IReadOnlyList<LibraryDocument> child,
        bool preferChildVersion);
}

public sealed class LibraryMerger : ILibraryMerger
{
    public IReadOnlyList<LibraryDocument> Merge(IReadOnlyList<LibraryDocument> parent, IReadOnlyList<LibraryDocument> child, bool preferChildVersion)
    {
        var result = new List<LibraryDocument>(parent);
        foreach (var library in child)
        {
            var coordinate = MavenCoordinate.Parse(library.Name);
            var matches = result.Select((candidate, index) => (candidate, index))
                .Where(x => MavenCoordinate.Parse(x.candidate.Name).GaKey == coordinate.GaKey)
                .ToList();
            if (matches.Count == 0)
            {
                result.Add(library);
                continue;
            }

            var (existing, index) = matches[^1];
            var existingCoordinate = MavenCoordinate.Parse(existing.Name);
            if (existingCoordinate.Version != coordinate.Version && !preferChildVersion)
            {
                throw new MdclException(MdclError.Of("VERSION_LIBRARY_CONFLICT", "VersionResolution",
                    $"同一库坐标出现不同版本：{existing.Name} 与 {library.Name}。", false, coordinate.GaKey));
            }

            var replacement = library;
            var existingArtifact = existing.Downloads?.Artifact;
            if (replacement.Downloads?.Artifact is null && existingArtifact is not null)
                replacement = replacement with { Downloads = (replacement.Downloads ?? new LibraryDownloads(null, new Dictionary<string, LibraryArtifact>())) with { Artifact = existingArtifact } };
            result[index] = replacement;
        }
        return result;
    }
}
