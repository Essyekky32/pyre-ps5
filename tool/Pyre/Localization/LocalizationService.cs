using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Pyre.Localization;

public sealed record LanguageOption(string Code, string DisplayName)
{
    /// <summary>
    /// Keeps UI Automation (and screen readers) announcing "English" rather than the
    /// record's generated ToString().
    /// </summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// The string table behind the language dropdown.
///
/// This is real plumbing, not a dead control: every user-visible string in the window comes
/// through <see cref="T(string)"/>, the dropdown is populated from the embedded tables that
/// are actually present in the build, and switching language re-applies the whole window
/// live and persists the choice.
///
/// To add a language later: drop `Localization\Strings.&lt;code&gt;.json` into the project
/// (copy of Strings.en.json, translated) and rebuild. It appears in the dropdown by itself.
/// No English string is invented for any other language here - English is the only table
/// that ships in wave 1.
/// </summary>
internal sealed class LocalizationService
{
    public const string DefaultLanguage = "en";

    private const string ResourcePrefix = "Pyre.Localization.Strings.";
    private const string ResourceSuffix = ".json";

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current;
    private Dictionary<string, string> _fallback;

    private LocalizationService()
    {
        var assembly = typeof(LocalizationService).Assembly;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var code = name[ResourcePrefix.Length..^ResourceSuffix.Length];

            // Tolerate a descriptive file name (Strings.en.json) as well as en.json: the
            // language code is what matters, the prefix is not.
            if (code.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase))
            {
                code = code["Strings.".Length..];
            }
            var table = ReadTable(assembly, name);
            if (table is not null)
            {
                _tables[code] = table;
            }
        }

        if (_tables.Count == 0)
        {
            _tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        _fallback = _tables.TryGetValue(DefaultLanguage, out var english)
            ? english
            : _tables.Values.First();

        _current = _fallback;
        Language = _tables.ContainsKey(DefaultLanguage) ? DefaultLanguage : _tables.Keys.First();

        Available = _tables
            .Select(kv => new LanguageOption(kv.Key, DisplayNameFor(kv.Key, kv.Value)))
            .OrderBy(o => o.Code.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => o.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static LocalizationService Instance { get; } = new();

    public event Action? LanguageChanged;

    public string Language { get; private set; }

    public IReadOnlyList<LanguageOption> Available { get; }

    public bool SetLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !_tables.TryGetValue(code, out var table) ||
            table is null)
        {
            return false;
        }

        var changed = !string.Equals(Language, code, StringComparison.OrdinalIgnoreCase);
        Language = code;
        _current = table;

        if (changed)
        {
            LanguageChanged?.Invoke();
        }

        return true;
    }

    public string T(string key)
    {
        if (_current.TryGetValue(key, out var value) && value.Length > 0)
        {
            return value;
        }

        // Nothing translated yet for this key: show the English rather than an empty label.
        if (_fallback.TryGetValue(key, out var fallback) && fallback.Length > 0)
        {
            return fallback;
        }

        return key;
    }

    public string T(string key, params object?[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static string DisplayNameFor(string code, Dictionary<string, string> table)
    {
        if (table.TryGetValue("_meta.displayName", out var displayName) && displayName.Length > 0)
        {
            return displayName;
        }

        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static Dictionary<string, string>? ReadTable(Assembly assembly, string resourceName)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (parsed is null)
            {
                return null;
            }

            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, element) in parsed)
            {
                if (key.StartsWith('_'))
                {
                    // "_meta": flattened to "_meta.code" style keys for the dropdown label.
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in element.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                table[$"{key}.{property.Name}"] = property.Value.GetString() ?? string.Empty;
                            }
                        }
                    }

                    continue;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    table[key] = element.GetString() ?? string.Empty;
                }
            }

            return table;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
