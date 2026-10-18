using System.Collections.Frozen;
using System.IO;

namespace Pyre.Server;

/// <summary>
/// Content types for the site we serve. The list is deliberately explicit rather than
/// relying on the OS registry, which varies per machine.
///
/// Note on .elf: there is no registered media type for ELF executables. Python's
/// mimetypes (what the frozen serve.py used) also returns application/octet-stream for
/// it, so we match that: application/octet-stream, consistently, every time.
/// </summary>
internal static class MimeTypes
{
    public const string Default = "application/octet-stream";

    private static readonly FrozenDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Requested by the brief
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
            [".elf"] = "application/octet-stream",
            [".ttf"] = "font/ttf",
            [".wasm"] = "application/wasm",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".svg"] = "image/svg+xml",
            [".ico"] = "image/x-icon",
            [".bin"] = "application/octet-stream",

            // Everything else the existing site actually ships
            [".htm"] = "text/html; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".map"] = "application/json; charset=utf-8",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".otf"] = "font/otf",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".xml"] = "application/xml; charset=utf-8",
            [".zip"] = "application/zip",
            [".pdf"] = "application/pdf",
            [".md"] = "text/markdown; charset=utf-8",
            [".dat"] = "application/octet-stream",
            [".pkg"] = "application/octet-stream",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string ForPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return Default;
        }

        return Map.TryGetValue(ext, out var type) ? type : Default;
    }
}
