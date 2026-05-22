using System.Text.RegularExpressions;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P1.5: Parses xray -version output and checks compatibility with the adaptive scheduler.
/// The adaptive scheduler depends on xray selector dedup behavior, verified against xray v26.3.27.
/// </summary>
public static partial class XrayVersionChecker
{
    /// <summary>
    /// Minimum xray version verified to work with the adaptive scheduler.
    /// The adaptive scheduler depends on xray selector prefix-match + dedup behavior
    /// (confirmed in v26.3.27). Older versions are untested and may silently misbehave.
    /// </summary>
    public static readonly Version MinVersion = new(26, 3, 27);

    // Matches "Xray 1.8.1", "Xray v26.3.27", "Xray 26.3.27" etc.
    [GeneratedRegex(@"Xray\s+v?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    /// <summary>
    /// Tries to parse a version string from xray's -version output.
    /// </summary>
    public static Version? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = VersionPattern().Match(output);
        if (!match.Success)
            return null;

        return Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>
    /// Checks whether the given version is compatible with the adaptive scheduler.
    /// Returns null if version couldn't be determined.
    /// </summary>
    public static bool? IsCompatible(string output)
    {
        var version = ParseVersion(output);
        if (version == null)
            return null;

        return version >= MinVersion;
    }

    /// <summary>
    /// Generates the human-readable compatibility message.
    /// </summary>
    public static string GetCompatibilityMessage(string output)
    {
        var version = ParseVersion(output);
        if (version == null)
            return $"Could not parse xray version. Minimum required: {MinVersion}";

        return version >= MinVersion
            ? $"xray {version} is compatible with adaptive scheduler (min: {MinVersion})"
            : $"xray {version} is below minimum ({MinVersion}). Adaptive scheduling may not work correctly. Please upgrade xray-core.";
    }
}
