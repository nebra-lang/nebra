using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Harness;

/// <summary>
/// Source text carrying named cursor markers written as <c>{|name|}</c>. The markers are
/// stripped from the text and their positions are recorded in LSP coordinates (zero-based line,
/// UTF-16 character offset), so a test can point at a location without counting columns.
/// </summary>
public sealed class MarkedSource
{
    private const string Open = "{|";
    private const string Close = "|}";

    public string Text { get; }
    public IReadOnlyDictionary<string, Position> Markers { get; }

    private MarkedSource(string text, IReadOnlyDictionary<string, Position> markers)
    {
        Text = text;
        Markers = markers;
    }

    public static MarkedSource Parse(string marked)
    {
        var text = new StringBuilder(marked.Length);
        var markers = new Dictionary<string, Position>(StringComparer.Ordinal);
        var line = 0;
        var character = 0;
        var index = 0;

        while (index < marked.Length)
        {
            if (string.CompareOrdinal(marked, index, Open, 0, Open.Length) == 0)
            {
                var end = marked.IndexOf(Close, index + Open.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new FormatException($"Unterminated marker at offset {index}.");
                }

                var name = marked.Substring(index + Open.Length, end - index - Open.Length);
                if (!markers.TryAdd(name, new Position(line, character)))
                {
                    throw new FormatException($"Duplicate marker '{name}'.");
                }

                index = end + Close.Length;
                continue;
            }

            var current = marked[index];
            text.Append(current);
            index++;

            if (current == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return new MarkedSource(text.ToString(), markers);
    }
}
