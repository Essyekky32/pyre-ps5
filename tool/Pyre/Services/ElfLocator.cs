using System.IO;

namespace Pyre.Services;

/// <summary>
/// One external payload the build expects to sit BESIDE Pyre.exe. Emitted by
/// <c>tool\build-embed.ps1</c> into the embedded manifest, so the tool knows the full
/// expected set (and can say plainly which are missing) without hard-coding any path.
/// </summary>
public sealed class SiteManifestPayload
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    /// <summary>A root-level optional-*.elf: shipped only with -IncludeOptionalPayloads.</summary>
    public bool Optional { get; set; }
}

/// <summary>What happened to one expected payload when it was looked for beside the exe.</summary>
public sealed class ElfFinding
{
    public string Path { get; init; } = string.Empty;

    public bool Optional { get; init; }

    /// <summary>The file beside the exe, or null when it was not found.</summary>
    public string? FullPath { get; init; }

    public bool Found => FullPath is not null;

    public long ExpectedSize { get; init; }

    public bool SizeMatches { get; init; }
}

/// <summary>The honest state of the external payloads for one launch.</summary>
public sealed class ElfReport
{
    public IReadOnlyList<string> Bases { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ElfFinding> Findings { get; init; } = Array.Empty<ElfFinding>();

    public int Expected => Findings.Count;

    public int FoundCount => Findings.Count(f => f.Found);

    public int MissingRequired => Findings.Count(f => !f.Found && !f.Optional);

    public int MissingOptional => Findings.Count(f => !f.Found && f.Optional);

    public bool AllRequiredFound => MissingRequired == 0;

    public IEnumerable<ElfFinding> MissingRequiredFinding =>
        Findings.Where(f => !f.Found && !f.Optional);

    public IEnumerable<ElfFinding> MissingOptionalFinding =>
        Findings.Where(f => !f.Found && f.Optional);

    public static ElfReport Empty(IReadOnlyList<string> bases) =>
        new() { Bases = bases, Findings = Array.Empty<ElfFinding>() };
}

/// <summary>
/// Resolves the ELF payloads the site asks for against the folder Pyre.exe itself lives in.
///
/// Wave 4 stopped packing the ELFs into the exe: they ship beside it. Resolution is derived
/// at runtime from the exe's own path (<see cref="Environment.ProcessPath"/>), never from a
/// hard-coded directory, so the whole thing stays portable - drop the exe anywhere and it
/// looks next to itself.
///
/// It is forgiving about layout on purpose (Cam drops files in without reading a spec). For
/// a request like <c>payloads/kstuff.elf</c> it tries, in order:
/// <list type="number">
///   <item>the exact relative path beside the exe - <c>&lt;base&gt;\payloads\kstuff.elf</c>;</item>
///   <item>a single tidy <c>&lt;base&gt;\elfs\</c> folder matched on the BASENAME -
///         <c>&lt;base&gt;\elfs\kstuff.elf</c>.</item>
/// </list>
/// Unity across every URL the site uses: a bare <c>elfs\</c> folder works for all of them.
///
/// Safety: a relative path that is rooted, contains a drive colon, or holds a <c>..</c>
/// segment is refused outright, so this can never be coaxed outside the base folders.
/// </summary>
public sealed class ElfLocator
{
    public const string ElfFolderName = "elfs";

    private readonly IReadOnlyList<string> _bases;

    private ElfLocator(IReadOnlyList<string> bases) => _bases = bases;

    public IReadOnlyList<string> Bases => _bases;

    /// <summary>
    /// Build a locator rooted on the exe's own folder (plus any extra override folder, for
    /// <c>--root</c> development). No absolute path is stored anywhere in this type.
    /// </summary>
    public static ElfLocator Create(string? overrideRoot)
    {
        return overrideRoot is null ? ForProcess() : ForProcess(overrideRoot);
    }

    public static ElfLocator ForProcess(params string?[] extraBases)
    {
        var bases = new List<string>();

        var processDirectory = ProcessDirectory();
        if (!string.IsNullOrEmpty(processDirectory))
        {
            bases.Add(processDirectory);
        }

        foreach (var extra in extraBases)
        {
            if (string.IsNullOrWhiteSpace(extra))
            {
                continue;
            }

            try
            {
                bases.Add(Path.GetFullPath(extra));
            }
            catch (Exception)
            {
                // an unusable extra folder simply is not searched
            }
        }

        return new ElfLocator(Normalize(bases));
    }

    /// <summary>
    /// The folder the running exe sits in. <see cref="Environment.ProcessPath"/> is the exe
    /// itself (single-file aware); <see cref="AppContext.BaseDirectory"/> is the fallback.
    /// </summary>
    public static string? ProcessDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(directory))
            {
                return directory;
            }
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        return baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Resolve a URL-relative path (either separator) to a real file beside the exe, or null.
    /// </summary>
    public string? Resolve(string relativePath)
    {
        if (!TryNormalizeRelative(relativePath, out var relative))
        {
            return null;
        }

        var basename = Path.GetFileName(relative);

        foreach (var root in _bases)
        {
            // (a) the exact relative path beside the exe
            var exact = Path.Combine(root, relative);
            if (File.Exists(exact))
            {
                return exact;
            }

            // (b) a single elfs\ folder matched on the basename
            if (basename.Length > 0)
            {
                var inElfs = Path.Combine(root, ElfFolderName, basename);
                if (File.Exists(inElfs))
                {
                    return inElfs;
                }
            }
        }

        return null;
    }

    /// <summary>Look for every expected payload and report plainly which are there and which are not.</summary>
    public ElfReport Inspect(IEnumerable<SiteManifestPayload> expected)
    {
        var findings = new List<ElfFinding>();

        foreach (var payload in expected)
        {
            var full = Resolve(payload.Path);

            long size = 0;
            var sizeMatches = false;
            if (full is not null)
            {
                try
                {
                    size = new FileInfo(full).Length;
                    sizeMatches = size == payload.Size;
                }
                catch (Exception)
                {
                    // size is informational; a failure to stat does not hide the file
                }
            }

            findings.Add(new ElfFinding
            {
                Path = payload.Path,
                Optional = payload.Optional,
                FullPath = full,
                ExpectedSize = payload.Size,
                SizeMatches = sizeMatches,
            });
        }

        return new ElfReport { Bases = _bases, Findings = findings };
    }

    /// <summary>Inspect the payload set the embedded manifest expects, for this launch.</summary>
    public static ElfReport InspectManifest(string? overrideRoot)
    {
        var locator = Create(overrideRoot);
        var manifest = EmbeddedSite.LoadManifest(out _);
        return manifest is null ? ElfReport.Empty(locator.Bases) : locator.Inspect(manifest.Payloads);
    }

    /// <summary>
    /// Turn a URL-relative path into an OS-relative path, or refuse it. Refused: empty, rooted,
    /// a drive colon, or any empty / "." / ".." segment - i.e. anything that could escape.
    /// </summary>
    internal static bool TryNormalizeRelative(string? relativePath, out string relative)
    {
        relative = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var slashed = relativePath.Replace('\\', '/').TrimStart('/');
        if (slashed.Length == 0 || slashed.Contains(':'))
        {
            return false;
        }

        var segments = slashed.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        relative = Path.Combine(segments);
        return true;
    }

    private static IReadOnlyList<string> Normalize(List<string> bases)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(bases.Count);

        foreach (var raw in bases)
        {
            var trimmed = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
