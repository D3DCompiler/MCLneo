using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace MCLneo.Core.Minecraft;

public sealed record LaunchFeatures(
    bool IsDemoUser = false,
    bool HasCustomResolution = false,
    bool HasQuickPlaysSupport = false,
    bool IsQuickPlaySingleplayer = false,
    bool IsQuickPlayMultiplayer = false,
    bool IsQuickPlayRealms = false);

public static class VersionRules
{
    public static string OsName
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "osx";
            return "linux";
        }
    }

    public static string OsArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "x86_64"
    };

    public static bool IsLibraryAllowed(JsonObject library, LaunchFeatures? features = null)
    {
        var rules = library["rules"] as JsonArray;
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var node in rules.OfType<JsonObject>())
        {
            if (!RuleMatches(node, features ?? new LaunchFeatures()))
            {
                continue;
            }

            allowed = string.Equals(node["action"]?.GetValue<string>(), "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    public static bool RuleMatches(JsonObject rule, LaunchFeatures features)
    {
        if (rule["os"] is JsonObject os)
        {
            var name = os["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name) && !string.Equals(name, OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var arch = os["arch"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(arch) && !string.Equals(arch, OsArch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var version = os["version"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(version))
            {
                try
                {
                    if (!Regex.IsMatch(Environment.OSVersion.VersionString, version))
                    {
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    // Invalid regex from metadata: do not fail the launch over it.
                }
            }
        }

        if (rule["features"] is JsonObject featuresNode)
        {
            foreach (var (key, valueNode) in featuresNode)
            {
                var expected = valueNode?.GetValue<bool>() ?? false;
                var actual = key switch
                {
                    "is_demo_user" => features.IsDemoUser,
                    "has_custom_resolution" => features.HasCustomResolution,
                    "has_quick_plays_support" => features.HasQuickPlaysSupport,
                    "is_quick_play_singleplayer" => features.IsQuickPlaySingleplayer,
                    "is_quick_play_multiplayer" => features.IsQuickPlayMultiplayer,
                    "is_quick_play_realms" => features.IsQuickPlayRealms,
                    _ => false
                };
                if (actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Reads an argument array (strings and rule-guarded values) into plain strings.</summary>
    public static List<string> ReadArguments(JsonArray? arguments, LaunchFeatures features)
    {
        var result = new List<string>();
        if (arguments is null)
        {
            return result;
        }

        foreach (var node in arguments)
        {
            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    result.Add(text);
                    break;
                case JsonObject obj:
                {
                    var rules = obj["rules"] as JsonArray;
                    if (rules is not null && rules.Count > 0)
                    {
                        var allowed = false;
                        foreach (var rule in rules.OfType<JsonObject>())
                        {
                            if (RuleMatches(rule, features))
                            {
                                allowed = string.Equals(rule["action"]?.GetValue<string>(), "allow",
                                    StringComparison.OrdinalIgnoreCase);
                            }
                        }

                        if (!allowed)
                        {
                            continue;
                        }
                    }

                    var value = obj["value"];
                    if (value is JsonArray array)
                    {
                        result.AddRange(array.Select(v => v?.GetValue<string>()).Where(v => v is not null)!);
                    }
                    else if (value is JsonValue single && single.TryGetValue<string>(out var textValue))
                    {
                        result.Add(textValue);
                    }

                    break;
                }
            }
        }

        return result;
    }

    public static string WindowsNativeClassifier => OsArch switch
    {
        "arm64" => "natives-windows-arm64",
        "x86" => "natives-windows-x86",
        _ => "natives-windows"
    };

    /// <summary>True when a library is a native payload for another OS/architecture.</summary>
    public static bool IsForeignNativeLibrary(string name)
    {
        if (!name.Contains(":natives-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var classifier = name.Split(':').Last();
        if (!classifier.StartsWith("natives-" + OsName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return OsName == "windows" &&
               !string.Equals(classifier, WindowsNativeClassifier, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetNativeClassifier(JsonObject library, out string classifier)
    {
        classifier = "";
        var natives = library["natives"] as JsonObject;
        if (natives is not null)
        {
            var key = OsName;
            var value = natives[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            classifier = value
                .Replace("${arch}", OsArch == "x86" ? "32" : "64", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        var name = library["name"]?.GetValue<string>() ?? "";
        if (IsForeignNativeLibrary(name) || !name.Contains(":natives-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        classifier = name.Split(':').Last();
        return string.Equals(classifier, WindowsNativeClassifier, StringComparison.OrdinalIgnoreCase);
    }
}
