namespace MDCL.Core.Versions;

public sealed record AssetObject(string Hash, long Size);
public sealed record AssetIndexDocument(string Id, IReadOnlyDictionary<string, AssetObject> Objects);
