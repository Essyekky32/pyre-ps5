using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pyre.Services;

public sealed class FirewallSettings
{
    /// <summary>
    /// True once the outcome is known (rule present, added, declined or failed).
    /// While true Pyre does not re-check and does not re-ask - that is the whole point
    /// of persisting it.
    /// </summary>
    public bool Checked { get; set; }

    public string RuleName { get; set; } = FirewallManager.RuleName;

    /// <summary>unknown | exists | added | declined | failed | unavailable</summary>
    public string State { get; set; } = "unknown";

    public string Detail { get; set; } = string.Empty;

    public string LastAttemptUtc { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public int Port { get; set; } = 8000;

    public string Language { get; set; } = "en";

    /// <summary>Request lines in the log view. Off by default; capped when on.</summary>
    public bool LogRequests { get; set; }

    public string RuleName { get; set; } = FirewallManager.RuleName;

    public FirewallSettings Firewall { get; set; } = new();
}

/// <summary>
/// %LOCALAPPDATA%\Pyre\settings.json. Corrupt or missing simply means defaults - a broken
/// settings file must never stop the tool from hosting.
/// </summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.Firewall ??= new FirewallSettings();
                    if (loaded.Port is <= 0 or > 65535)
                    {
                        loaded.Port = 8000;
                    }

                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // defaults are fine
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception)
        {
            // a settings write failing is not worth interrupting hosting for
        }
    }
}
