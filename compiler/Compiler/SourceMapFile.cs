using System.Text.Json;

namespace Nebra.Compiler;

/// <summary>
/// Writes the map from the lines of a generated file to the lines of its source, a JSON object
/// with the generated <c>file</c>, the <c>source</c> relative to the map, and <c>lines</c>, whose
/// entry at index <c>n</c> is the source line of generated line <c>n + 1</c>, or 0 for none.
/// </summary>
public static class SourceMapFile
{
    private const int FormatVersion = 1;

    public static string Render(string generatedPath, string sourcePath, int[] lines)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(generatedPath))!;
        var map = new
        {
            version = FormatVersion,
            file = Path.GetFileName(generatedPath),
            source = Path.GetRelativePath(directory, Path.GetFullPath(sourcePath)).Replace('\\', '/'),
            lines
        };

        return JsonSerializer.Serialize(map);
    }
}
