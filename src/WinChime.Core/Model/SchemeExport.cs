using System.Text.Json.Serialization;

namespace WinChime.Core.Model;

/// <summary>
/// Portable, human-readable scheme file (.winchime.json). Deliberately stores the
/// unexpanded registry value so a scheme authored on one machine still resolves
/// %SystemRoot% correctly on another.
/// </summary>
public sealed class SchemeExport
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled scheme";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Key is "AppKey\EventKey"; value is the unexpanded wav path ("" = silent).</summary>
    [JsonPropertyName("assignments")]
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional: wav files bundled next to the json, relative to it.</summary>
    [JsonPropertyName("bundledMediaFolder")]
    public string? BundledMediaFolder { get; set; }
}
