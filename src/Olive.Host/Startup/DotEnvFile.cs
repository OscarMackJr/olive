using System.Text.RegularExpressions;

namespace Olive.Host.Startup;

// Minimal .env parser (D-19): KEY=VALUE lines, # comments, one pair of surrounding quotes stripped.
public static partial class DotEnvFile
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex KeyPattern();

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || !KeyPattern().IsMatch(line[..eq].Trim()))
            {
                // Never echo the line: it may hold a secret.
                throw new FormatException($".env line {lineNumber} is invalid.");
            }

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            result[line[..eq].Trim()] = value;
        }

        return result;
    }
}
