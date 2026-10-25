namespace MDCL.Core.Errors;

public sealed record MdclError(
    string ErrorCode,
    string Stage,
    string Summary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SuggestedActions,
    bool Retryable,
    string? OperationId = null)
{
    public static MdclError Of(string code, string stage, string summary, bool retryable = false, params string[] evidence) =>
        new(code, stage, summary, evidence, [], retryable);
}

public sealed class MdclException : Exception
{
    public MdclException(MdclError error, Exception? innerException = null)
        : base($"{error.ErrorCode}: {error.Summary}", innerException) => Error = error;

    public MdclError Error { get; }
}
