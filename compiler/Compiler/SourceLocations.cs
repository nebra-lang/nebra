using System.Text.RegularExpressions;

namespace Nebra.Compiler;

/// <summary>
/// Rewrites the positions in a runtime error or traceback from generated Lua to the Nebra source.
/// Lua shortens the name of a long chunk to its end, so a position is matched by the end of the
/// generated path. A position in anything else, or on a line without a source, is left as it is.
/// </summary>
public sealed partial class SourceLocations
{
    private readonly List<(string GeneratedPath, string Source, int[] Lines)> _files = [];

    public void Add(string generatedPath, string source, int[] lines)
    {
        _files.Add((Normalized(Path.GetFullPath(generatedPath)), Normalized(source), lines));
    }

    /// <summary>Returns <paramref name="text"/> with every position of a known generated file replaced.</summary>
    public string Translate(string text)
    {
        return Position().Replace(text, match => Translated(match) ?? match.Value);
    }

    private string? Translated(Match match)
    {
        var path = Normalized(match.Groups["path"].Value);
        if (path.StartsWith("...", StringComparison.Ordinal)) path = path[3..];

        var file = _files.FirstOrDefault(candidate => candidate.GeneratedPath.EndsWith(path, StringComparison.Ordinal));
        if (file.Lines == null || !int.TryParse(match.Groups["line"].Value, out var line)) return null;
        if (line < 1 || line > file.Lines.Length || file.Lines[line - 1] == 0) return null;

        return $"{file.Source}:{file.Lines[line - 1]}";
    }

    private static string Normalized(string path)
    {
        return path.Replace('\\', '/');
    }

    [GeneratedRegex("""(?<path>(?:[A-Za-z]:)?[^\s:"'()\[\]<>]+\.lua):(?<line>\d+)""")]
    private static partial Regex Position();
}
