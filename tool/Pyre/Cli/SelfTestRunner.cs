using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pyre.Server;
using Pyre.Services;

namespace Pyre.Cli;

/// <summary>
/// Verifies the shipped server against the files on disk - the "unit test the server class"
/// half of proving this works, with real sockets and real HTTP, in-process.
///
/// Every content check is a SHA256 comparison between what came back over HTTP and what is
/// sitting in the folder, which is the actual promise: the bytes are served untouched.
/// </summary>
internal static class SelfTestRunner
{
    private const int DefaultTestPort = 18080;

    /// <summary>
    /// The PYRE autoloader installer, pushed at runtime by install-autoloader.html. Wave 4
    /// ships it beside the exe rather than inside it, so this names it in the EXTERNAL payload
    /// list (the manifest assertion below checks that, not the embedded file list).
    /// </summary>
    private const string AutoloaderRelativePath = "our-payloads/pyre-autoloader-installer_v0.5.2-pyre.elf";

    private static int _passed;
    private static int _failed;

    public static int Run(ParsedArgs args)
    {
        _passed = 0;
        _failed = 0;

        var port = args.PortSpecified ? args.Port : DefaultTestPort;
        var site = EmbeddedSite.Resolve(args.Root);

        CliLog.Line($"selftest: site {CliRunner.DescribeSource(site)}");
        CliLog.Line($"selftest: port {port}");

        if (!site.Ok)
        {
            CliLog.Line($"  [FAIL] {site.Error}");
            CliLog.Line("SELFTEST: 0 passed, 1 failed");
            return 1;
        }

        var root = site.Root;
        CliLog.Line($"selftest: root {root}");

        // The wave-2 check: the unpacked copy must match the manifest embedded in the exe,
        // byte for byte, before any HTTP check means anything.
        CheckEmbeddedManifest(site);

        // The payloads live beside the exe now. Look for the whole expected set, honestly.
        var elfs = ElfLocator.Create(args.Root);
        var elfReport = ElfLocator.InspectManifest(args.Root);
        CliLog.Line($"selftest: payload bases: {string.Join("; ", elfReport.Bases)}");

        var server = new PyreWebServer(root, port, elfs);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            CliLog.Line($"  [FAIL] could not start the server on {port}: {ex.Message}");
            CliLog.Line("SELFTEST: 0 passed, 1 failed");
            return 1;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var baseUrl = $"http://127.0.0.1:{port}";

            CheckSiteFiles(client, baseUrl, root);
            CheckElfPayloads(client, baseUrl, elfReport);
            CheckMissingElf(client, baseUrl, elfReport);
            CheckHead(client, baseUrl, root);
            CheckMissingFile(client, baseUrl);
            CheckRootServesIndex(client, baseUrl, root);
            CheckRange(client, baseUrl, root);
            CheckPathTraversalIsRefused(client, baseUrl);
            CheckConcurrency(client, baseUrl, root);
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
        }

        CliLog.Line($"SELFTEST: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Verifies the site on disk against the manifest packed into this exe: every SHA256 in
    /// the manifest against the file that was unpacked from it. This is the check that makes
    /// "the exe carries the site" true rather than merely claimed.
    /// </summary>
    private static void CheckEmbeddedManifest(SiteResolution site)
    {
        if (site.Kind != SiteSourceKind.Embedded)
        {
            CliLog.Line("  [SKIP] --root override - the embedded manifest does not apply");
            return;
        }

        var manifest = EmbeddedSite.LoadManifest(out var error);
        if (manifest is null)
        {
            Fail($"embedded manifest: {error}");
            return;
        }

        CliLog.Line(
            $"selftest: embedded manifest build {manifest.BuildId}, {manifest.FileCount} files, " +
            $"{manifest.TotalBytes} bytes, zip sha256 {manifest.ZipSha256}");

        if (EmbeddedSite.VerifyExtracted(site.Root, manifest, out var mismatches, out var firstProblem))
        {
            Pass($"embedded site: all {manifest.Files.Count} files match the embedded manifest sha256 (build {manifest.BuildId})");
        }
        else
        {
            Fail($"embedded site: {mismatches} of {manifest.Files.Count} files do not match the embedded manifest - {firstProblem}");
        }

        if (EmbeddedSite.ManifestCarriesNoElfs(manifest, out var noElfDetail))
        {
            Pass($"embedded site: carries no payload binaries - {noElfDetail}");
        }
        else
        {
            Fail($"embedded site carries binaries that must ship beside the exe instead: {noElfDetail}");
        }

        // The PYRE autoloader installer is a runtime payload: the "install the PYRE
        // autoloader" page pushes it to the console's loader. Wave 4 ships it BESIDE the exe,
        // so the assertion is about the external payload list, not the embedded file list.
        if (EmbeddedSite.ManifestExpectsPayload(manifest, AutoloaderRelativePath, out var autoloader))
        {
            Pass($"payload list: expects the PYRE autoloader installer beside the exe ({AutoloaderRelativePath}, {autoloader!.Size} bytes)");
        }
        else
        {
            Fail($"payload list is missing the PYRE autoloader installer ({AutoloaderRelativePath})");
        }

        if (manifest.Payloads.Count == 0)
        {
            Fail("payload list is empty - the site asks for its ELFs beside the exe");
        }
        else
        {
            var required = manifest.Payloads.Count(p => !p.Optional);
            var optional = manifest.Payloads.Count(p => p.Optional);
            Pass($"payload list: {manifest.Payloads.Count} expected beside the exe ({required} required, {optional} optional)");
        }
    }

    private static void CheckSiteFiles(HttpClient client, string baseUrl, string root)
    {
        var files = new List<(string Relative, string ExpectType)>
        {
            ("index.html", "text/html"),
            ("run.html", "text/html"),
            ("haze-menu.css", "text/css"),
            ("haze-start.js", "text/javascript"),
            ("haze-site.js", "text/javascript"),
            ("payload-sources.json", "application/json"),
            ("serve.py", "application/octet-stream"),
            ("HazeUI-Regular.ttf", "font/ttf"),
            ("README-PYRE.md", "text/markdown"),
        };

        foreach (var (relative, expectType) in files)
        {
            var disk = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(disk))
            {
                Fail($"{relative}: not on disk, cannot compare");
                continue;
            }

            var url = baseUrl + "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

            try
            {
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                var statusOk = response.StatusCode == HttpStatusCode.OK;
                var typeOk = (response.Content.Headers.ContentType?.ToString() ?? string.Empty)
                    .StartsWith(expectType, StringComparison.OrdinalIgnoreCase);
                var cacheControl = string.Join(",", response.Headers.CacheControl?.ToString() ?? string.Empty);
                var noStore = cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase);
                var digestMatch = Sha256(body) == Sha256File(disk);
                var lengthMatch = body.Length == new FileInfo(disk).Length;

                if (statusOk && typeOk && noStore && digestMatch && lengthMatch)
                {
                    Pass($"GET /{relative} -> 200, {response.Content.Headers.ContentType}, no-store, {body.Length} B, sha256 matches disk");
                }
                else
                {
                    Fail(
                        $"GET /{relative}: status={response.StatusCode} type={response.Content.Headers.ContentType} " +
                        $"no-store={noStore} bytes={body.Length} lengthMatch={lengthMatch} sha256Match={digestMatch}");
                }
            }
            catch (Exception ex)
            {
                Fail($"GET /{relative}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void CheckHead(HttpClient client, string baseUrl, string root)
    {
        var disk = Path.Combine(root, "index.html");
        var expectedLength = new FileInfo(disk).Length;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, baseUrl + "/index.html");
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

            if (response.StatusCode == HttpStatusCode.OK &&
                response.Content.Headers.ContentLength == expectedLength &&
                body.Length == 0)
            {
                Pass($"HEAD /index.html -> 200, Content-Length {expectedLength}, no body");
            }
            else
            {
                Fail(
                    $"HEAD /index.html: status={response.StatusCode} " +
                    $"contentLength={response.Content.Headers.ContentLength} bodyBytes={body.Length}");
            }
        }
        catch (Exception ex)
        {
            Fail($"HEAD /index.html: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckMissingFile(HttpClient client, string baseUrl)
    {
        try
        {
            using var response = client.GetAsync(baseUrl + "/definitely-not-here-" + Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Pass("GET /missing -> 404 (not a 500)");
            }
            else
            {
                Fail($"GET /missing -> {response.StatusCode}, expected 404");
            }
        }
        catch (Exception ex)
        {
            Fail($"GET /missing: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckRootServesIndex(HttpClient client, string baseUrl, string root)
    {
        try
        {
            using var response = client.GetAsync(baseUrl + "/").GetAwaiter().GetResult();
            var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var same = Sha256(body) == Sha256File(Path.Combine(root, "index.html"));

            if (response.StatusCode == HttpStatusCode.OK && same)
            {
                Pass("GET / -> 200 and served index.html itself");
            }
            else
            {
                Fail($"GET / -> {response.StatusCode}, sha256 match={same}");
            }
        }
        catch (Exception ex)
        {
            Fail($"GET /: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckRange(HttpClient client, string baseUrl, string root)
    {
        var disk = Path.Combine(root, "index.html");
        var head = new byte[100];
        using (var fs = File.OpenRead(disk))
        {
            fs.ReadExactly(head, 0, head.Length);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/index.html");
            request.Headers.Range = new RangeHeaderValue(0, 99);
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

            var ok = response.StatusCode == HttpStatusCode.PartialContent &&
                     body.Length == 100 &&
                     head.AsSpan().SequenceEqual(body) &&
                     response.Content.Headers.ContentRange is not null;

            if (ok)
            {
                Pass($"GET /index.html with Range: bytes=0-99 -> 206, {response.Content.Headers.ContentRange}, bytes match disk");
            }
            else
            {
                Fail(
                    $"range request: status={response.StatusCode} bytes={body.Length} " +
                    $"contentRange={response.Content.Headers.ContentRange} bytesMatch={body.AsSpan().SequenceEqual(head.AsSpan())}");
            }
        }
        catch (Exception ex)
        {
            Fail($"range request: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CheckPathTraversalIsRefused(HttpClient client, string baseUrl)
    {
        // Percent-encoded dots, so the request reaches the server as "%2e%2e" and the SERVER
        // has to be the thing that refuses it (HttpClient would have normalised a literal
        // "../" away before it ever left this process).
        var probes = new[] { "/%2e%2e/app.manifest", "/..%2fapp.manifest", "/%2e%2e%5capp.manifest" };

        foreach (var probe in probes)
        {
            try
            {
                using var response = client.GetAsync(baseUrl + probe).GetAwaiter().GetResult();

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    Pass($"GET {probe} -> {(int)response.StatusCode} (cannot escape the served folder)");
                }
                else
                {
                    Fail($"GET {probe} -> {(int)response.StatusCode}, expected 4xx");
                }
            }
            catch (Exception ex)
            {
                Fail($"traversal check {probe}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void CheckConcurrency(HttpClient client, string baseUrl, string root)
    {
        var targets = new[] { "index.html", "haze-menu.css", "haze-start.js", "haze-site.js", "serve.py", "HazeUI-Regular.ttf" }
            .Where(f => File.Exists(Path.Combine(root, f)))
            .ToArray();

        if (targets.Length == 0)
        {
            Fail("concurrency: no files to fetch");
            return;
        }

        var expected = targets.ToDictionary(t => t, t => Sha256File(Path.Combine(root, t)), StringComparer.Ordinal);
        var requests = 24;

        var tasks = Enumerable.Range(0, requests).Select(i =>
        {
            var target = targets[i % targets.Length];
            return Task.Run(async () =>
            {
                using var response = await client.GetAsync($"{baseUrl}/{target}").ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return response.StatusCode == HttpStatusCode.OK && Sha256(body) == expected[target];
            });
        }).ToArray();

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            var failures = tasks.Count(t => t.IsFaulted || !t.Result);

            if (failures == 0)
            {
                Pass($"{requests} concurrent requests across {targets.Length} files: all 200, all sha256 matched");
            }
            else
            {
                Fail($"{failures} of {requests} concurrent requests failed or mismatched");
            }
        }
        catch (Exception ex)
        {
            Fail($"concurrency: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch every payload that is actually present beside the exe and prove the HTTP body is
    /// byte-identical to the file there. Payloads that are absent are REPORTED, never counted
    /// as a failure: the empty-folder case (exe on its own) is a valid, supported launch.
    /// </summary>
    private static void CheckElfPayloads(HttpClient client, string baseUrl, ElfReport report)
    {
        if (report.Expected == 0)
        {
            Note("the embedded manifest lists no external payloads to check");
            return;
        }

        var present = 0;

        foreach (var finding in report.Findings)
        {
            if (!finding.Found)
            {
                Note(finding.Optional
                    ? $"optional payload not shipped: {finding.Path}"
                    : $"REQUIRED payload missing beside the exe: {finding.Path}");
                continue;
            }

            present++;
            var disk = finding.FullPath!;
            var url = baseUrl + "/" + string.Join('/', finding.Path.Split('/').Select(Uri.EscapeDataString));

            try
            {
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                var statusOk = response.StatusCode == HttpStatusCode.OK;
                var typeOk = (response.Content.Headers.ContentType?.ToString() ?? string.Empty)
                    .StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase);
                var noStore = (response.Headers.CacheControl?.ToString() ?? string.Empty)
                    .Contains("no-store", StringComparison.OrdinalIgnoreCase);
                var digestMatch = Sha256(body) == Sha256File(disk);
                var lengthMatch = body.Length == new FileInfo(disk).Length;

                if (statusOk && typeOk && noStore && digestMatch && lengthMatch)
                {
                    Pass($"GET /{finding.Path} -> 200, application/octet-stream, no-store, {body.Length} B, sha256 matches the file beside the exe");
                }
                else
                {
                    Fail(
                        $"GET /{finding.Path}: status={response.StatusCode} type={response.Content.Headers.ContentType} " +
                        $"no-store={noStore} bytes={body.Length} lengthMatch={lengthMatch} sha256Match={digestMatch}");
                }
            }
            catch (Exception ex)
            {
                Fail($"GET /{finding.Path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Note(
            $"payload inventory: {present} of {report.Expected} present beside the exe " +
            $"({report.MissingRequired} required missing, {report.MissingOptional} optional not shipped)");
    }

    /// <summary>
    /// A missing payload must be a clean 404 - never a 500 and never an empty 200. Probes a
    /// name that cannot exist, plus any required payload that is genuinely absent this run.
    /// </summary>
    private static void CheckMissingElf(HttpClient client, string baseUrl, ElfReport report)
    {
        var probes = new List<string> { "/payloads/__pyre_missing_probe__.elf" };
        probes.AddRange(report.MissingRequiredFinding.Select(f => "/" + f.Path));

        foreach (var probe in probes.Distinct(StringComparer.Ordinal).Take(4))
        {
            try
            {
                using var response = client.GetAsync(baseUrl + probe).GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Pass($"GET {probe} -> 404 (a missing payload is a clean 404, not a 500)");
                }
                else
                {
                    Fail($"GET {probe} -> {(int)response.StatusCode}, expected 404");
                }
            }
            catch (Exception ex)
            {
                Fail($"GET {probe}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void Pass(string message)
    {
        _passed++;
        CliLog.Line("  [PASS] " + message);
    }

    private static void Fail(string message)
    {
        _failed++;
        CliLog.Line("  [FAIL] " + message);
    }

    /// <summary>Reported, not scored: the payload situation is information, not a pass condition.</summary>
    private static void Note(string message) => CliLog.Line("  [INFO] " + message);

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
