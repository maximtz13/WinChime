using System.Text.Json.Serialization;

namespace WinChime.Core.Model;

/// <summary>
/// Written alongside every backup set. Registry-only: this app never modifies a file on
/// disk, so a snapshot of the relevant registry values is a complete record of what it
/// changed and is everything the revert path needs.
/// </summary>
public sealed class BackupManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("windowsBuild")]
    public string WindowsBuild { get; set; } = "";

    /// <summary>Full snapshot of HKCU\AppEvents assignments ("App\Event" -> raw value).</summary>
    [JsonPropertyName("soundAssignments")]
    public Dictionary<string, string> SoundAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Arbitrary registry values captured as "hive\path!name" -> string form.</summary>
    [JsonPropertyName("registryValues")]
    public Dictionary<string, string?> RegistryValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pre-formatted for the backups list. Done here rather than with a XAML StringFormat
    /// because date format strings inside a markup extension need awkward colon escaping.
    /// </summary>
    [JsonIgnore]
    public string CreatedLocalText => CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
