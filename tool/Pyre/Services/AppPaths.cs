using System.IO;

namespace Pyre.Services;

/// <summary>
/// The only places Pyre touches the disk: its own settings folder, and the folder it
/// unpacks the embedded copy of the site into. It no longer reads a site folder beside
/// the exe - the site travels inside the exe.
/// </summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pyre");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Parent of every unpacked site copy: %LOCALAPPDATA%\Pyre\site.</summary>
    public static string SiteRootDirectory => Path.Combine(DataDirectory, "site");

    /// <summary>
    /// Where the embedded site for one build is unpacked:
    /// %LOCALAPPDATA%\Pyre\site\&lt;buildId&gt;\. The build id changes whenever the packed
    /// site changes, so an old copy is simply never looked at again.
    /// </summary>
    public static string EmbeddedSiteDirectory(string buildId) =>
        Path.Combine(SiteRootDirectory, buildId);
}
