using System.Text;
using System.Text.RegularExpressions;

namespace MCLneo.Core.Servers;

/// <summary>
/// Reads the integrated server log of a game instance to recover the current
/// player list. This is a fallback for LAN worlds whose status response does
/// not include a player sample (or which do not answer Server List Ping).
/// It is only meaningful on the machine that hosts the world.
/// </summary>
public static partial class LocalPlayerLogService
{
    private const int MaxTailBytes = 1024 * 1024;

    public static IReadOnlyList<string> ReadOnlinePlayers(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "logs", "latest.log");
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaxTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, leaveOpen: false);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }

        var players = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var clean = StripFormatting(line).TrimEnd('\r');
            var match = PlayerEventRegex().Match(clean);
            if (!match.Success) continue;

            var name = match.Groups["name"].Value;
            var action = match.Groups["action"].Value;
            if (action.Equals("joined", StringComparison.OrdinalIgnoreCase))
            {
                if (!players.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    players.Add(name);
                }
            }
            else
            {
                players.RemoveAll(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        return players;
    }

    private static string StripFormatting(string value)
    {
        // Remove ANSI color sequences as well as the § formatting codes that
        // occasionally appear in logs from modded servers.
        var ansi = AnsiRegex().Replace(value, "");
        return FormattingRegex().Replace(ansi, "");
    }

    [GeneratedRegex(@"(?:^|\]:\s*)(?<name>[A-Za-z0-9_]{1,16})\s+(?<action>joined|left)\s+the\s+game",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerEventRegex();

    [GeneratedRegex(@"\x1B\[[0-9;]*m", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"§[0-9A-FK-ORa-fk-or]", RegexOptions.Compiled)]
    private static partial Regex FormattingRegex();
}
