using System.Text.Json.Serialization;

namespace WinChime.Core.Model;

/// <summary>
/// The manifest inside a cursor pack. The cursor counterpart to <see cref="SchemeExport"/>.
///
/// Kept as its own type rather than reusing SchemeExport, because the key space is different
/// and silently mixing them would be hard to notice: a sound assignment is keyed
/// "AppKey\EventKey", a cursor assignment by a single role name such as Arrow or Wait. A pack
/// opened as the wrong type would deserialize cleanly and then apply nothing.
/// </summary>
public sealed class CursorSchemeExport
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled cursor scheme";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Key is a cursor role name from <c>CursorRoles.All</c>; value is the unexpanded path.
    /// An empty value means Windows draws that cursor itself, which is a normal state rather
    /// than a missing entry.
    /// </summary>
    [JsonPropertyName("assignments")]
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional: cursor files bundled inside the pack, relative to the manifest.</summary>
    [JsonPropertyName("bundledCursorFolder")]
    public string? BundledCursorFolder { get; set; }
}
