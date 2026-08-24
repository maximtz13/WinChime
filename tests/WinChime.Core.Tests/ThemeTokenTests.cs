using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinChime.Core.Tests;

/// <summary>
/// Checks the theme dictionaries as XML rather than by loading them, because the failures this
/// guards against are silent at both compile time and run time.
///
/// A key defined in one theme file and missing from another does not throw. WPF resolves the
/// DynamicResource to nothing and paints transparent, so the bug surfaces as an invisible
/// control in one theme only, usually in whichever theme the developer was not using. A
/// misspelled key reference behaves the same way.
///
/// These tests read the source tree directly. The alternative would be referencing the WPF
/// application from a test project, which would drag a UI framework into a suite that CI runs
/// headless.
/// </summary>
public sealed class ThemeTokenTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] TokenFiles =
    [
        "Tokens.Light.xaml",
        "Tokens.Dark.xaml",
        "Tokens.HighContrast.xaml",
    ];

    // ------------------------------------------------------------------ parity --

    /// <summary>
    /// The load-bearing test. ThemeManager swaps one of these dictionaries for another at
    /// runtime, so any key that is not in all three is a control that disappears when the user
    /// changes theme.
    /// </summary>
    [Fact]
    public void EveryThemeFileDefinesTheSameKeys()
    {
        var sets = TokenFiles.ToDictionary(file => file, file => KeysIn(ThemePath(file)));

        var reference = sets[TokenFiles[0]];

        foreach (var (file, keys) in sets.Skip(1))
        {
            var missing = reference.Except(keys).Order().ToList();
            var extra = keys.Except(reference).Order().ToList();

            Assert.True(missing.Count == 0, $"{file} is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{file} defines keys no other theme has: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void TheTokenSetIsNotAccidentallyEmpty()
    {
        // Guards the test itself: a parsing mistake that found no keys would make the parity
        // test above pass vacuously.
        Assert.True(KeysIn(ThemePath(TokenFiles[0])).Count > 30);
    }

    // -------------------------------------------------------------- references --

    /// <summary>
    /// Every resource key used anywhere in the WPF layer has to be defined somewhere. This is
    /// what catches a typo, which otherwise renders as a transparent control rather than an
    /// error.
    /// </summary>
    [Fact]
    public void EveryResourceReferenceResolvesToADefinedKey()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in TokenFiles) defined.UnionWith(KeysIn(ThemePath(file)));

        defined.UnionWith(KeysIn(ThemePath("Geometry.xaml")));
        defined.UnionWith(KeysIn(ThemePath("Controls.xaml")));

        var unresolved = new List<string>();

        foreach (var file in AppXamlFiles())
        {
            foreach (var key in ReferencedKeys(File.ReadAllText(file)))
            {
                if (!defined.Contains(key)) unresolved.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.True(unresolved.Count == 0,
            "Resource keys referenced but never defined:\n  " + string.Join("\n  ", unresolved.Order()));
    }

    /// <summary>
    /// The accent brushes are overwritten at runtime by ThemeManager, which looks them up by
    /// these exact strings. A rename in the XAML would leave the override writing to keys
    /// nothing reads, and the app would silently stop following the Windows accent.
    /// </summary>
    [Theory]
    [InlineData("Accent.Fill")]
    [InlineData("Accent.Hover")]
    [InlineData("Accent.Pressed")]
    [InlineData("Accent.Text")]
    [InlineData("Accent.Subtle")]
    [InlineData("Row.Selected")]
    [InlineData("Surface.Card")]
    public void KeysThatThemeManagerWritesByNameExist(string key)
    {
        Assert.Contains(key, KeysIn(ThemePath("Tokens.Light.xaml")));
    }

    // ------------------------------------------------------------------ helpers --

    private static HashSet<string> KeysIn(string path)
    {
        var document = XDocument.Load(path);

        return new HashSet<string>(
            document.Descendants()
                .Select(element => element.Attribute(Xaml + "Key")?.Value)
                .Where(key => key is not null)
                .Select(key => key!),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Pulls the key out of {DynamicResource Foo} and {StaticResource Foo}. Deliberately skips
    /// a nested markup extension such as {StaticResource {x:Type Button}}, which names a type
    /// rather than a key of ours.
    /// </summary>
    private static IEnumerable<string> ReferencedKeys(string xaml)
    {
        var matches = Regex.Matches(xaml, @"\{(?:Dynamic|Static)Resource\s+([^}{]+)\}");

        foreach (Match match in matches)
        {
            var key = match.Groups[1].Value.Trim();

            if (key.Length > 0) yield return key;
        }
    }

    private static string ThemePath(string file) => Path.Combine(AppDirectory(), "Theme", file);

    private static IEnumerable<string> AppXamlFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string AppDirectory() => Path.Combine(RepositoryRoot(), "src", "WinChime.App");

    /// <summary>
    /// Walks up from the test binary to the solution file. Tests run from bin, which is several
    /// levels below the sources they are reading here.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinChime.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate WinChime.sln above the test binary.");
    }
}
