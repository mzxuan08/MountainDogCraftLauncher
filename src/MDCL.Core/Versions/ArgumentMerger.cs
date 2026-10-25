using System.Text;

namespace MDCL.Core.Versions;

public static class ArgumentMerger
{
    public static IReadOnlyList<string> Resolve(IReadOnlyList<ArgumentEntry> entries, RuleContext context)
    {
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Rules.Count > 0 && !RuleEvaluator.IsAllowed(entry.Rules, context, false)) continue;
            result.AddRange(entry.Values);
        }
        return result;
    }

    public static IReadOnlyList<string> ParseLegacy(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            if (character == '\\' && index + 1 < commandLine.Length && (commandLine[index + 1] == '"' || commandLine[index + 1] == '\\'))
            {
                current.Append(commandLine[++index]);
                continue;
            }
            current.Append(character);
        }
        if (quoted) throw new FormatException("Legacy argument string has an unterminated quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
