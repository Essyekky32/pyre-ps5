using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pyre.Services;

namespace Pyre.Server;

/// <summary>
/// A minimal, self-contained static file server: the replacement for the frozen
/// <c>serve.py</c> (ThreadingHTTPServer on 0.0.0.0:8000, Cache-Control: no-store).
///
/// Ground rules it keeps, because the site next to it is FROZEN:
/// <list type="bullet">
///   <item>READ ONLY. It never writes, renames, moves or transforms anything under the root.</item>
///   <item>Bytes go out exactly as they sit on disk - no minify, no rewrite, no compression.</item>
///   <item>Every response carries <c>Cache-Control: no-store</c>, exactly like serve.py.</item>
///   <item>The page root is the unpacked embedded copy (or an explicit <c>--root</c> folder).
///         The <c>.elf</c> payloads are resolved beside the exe instead - see <see cref="ElfLocator"/>.</item>
/// </list>
///
/// Written on raw sockets on purpose: HttpListener needs a netsh URL ACL for a non-admin
/// process, and this app is deliberately unelevated. One task per connection, so the
/// console can pull many assets at once without stalling.
/// </summary>
public sealed class PyreWebServer
{
    public const int DefaultPort = 8000;

    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxConcurrentConnections = 256;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _slots = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly ConcurrentDictionary<long, TcpClient> _clients = new();

    /// <summary>
    /// Resolves the <c>.elf</c> payloads beside the exe. The embedded site carries no ELF at
    /// all now, so a payload request is answered from here instead of the served root. Null
    /// means payloads are served like any other file (the frozen pre-wave-4 behaviour).
    /// </summary>
    private readonly ElfLocator? _elfs;

    private long _clientSeq;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public PyreWebServer(string root, int port = DefaultPort, ElfLocator? elfs = null)
    {
        Root = Path.GetFullPath(root);
        Port = port;
        _elfs = elfs;
    }

    public string Root { get; }

    public int Port { get; }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>
    /// Off by default. The console pulls dozens of assets per page and CAM DOES NOT WANT
    /// that flooding the GUI - so when this is false the per-request event is never even
    /// raised, not merely hidden.
    /// </summary>
    public bool LogRequests { get; set; }

    /// <summary>Lifecycle noise: started, stopped.</summary>
    public event Action<string>? Notice;

    /// <summary>Something went wrong. These are shown prominently.</summary>
    public event Action<string>? Error;

    /// <summary>One line per served request, but only while <see cref="LogRequests"/> is true.</summary>
    public event Action<string>? RequestServed;

    /// <summary>Is the folder next to the exe actually the PYRE site?</summary>
    public static bool LooksLikeSite(string root) =>
        File.Exists(Path.Combine(root, "index.html"));

    public static string Url(string ip, int port) => $"http://{ip}:{port}/";

    /// <summary>
    /// Binds and starts accepting. Throws SocketException when the port is taken - the
    /// caller turns that into a plain-language message (Cam's own serve.py holds 8000 when
    /// it is already running).
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"The folder to serve does not exist: {Root}");
        }

        var listener = new TcpListener(IPAddress.Any, Port);
        // No SO_REUSEADDR on purpose: on Windows that lets two processes bind the same
        // port and the console would land on whichever socket the stack felt like.
        listener.Start(512);

        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        Notice?.Invoke($"listening on 0.0.0.0:{Port}");
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // already down
        }

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // a dead socket is not an error
            }
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // accept loop is allowed to die when the listener closes
            }
        }

        // Give in-flight handlers a moment to unwind, then let go.
        await Task.Delay(60).ConfigureAwait(false);

        _acceptTask = null;
        _listener = null;
        _cts = null;
        _clients.Clear();
        cts.Dispose();
        Notice?.Invoke("stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                Error?.Invoke($"accept failed: {ex.Message}");
                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // Over the cap: drop it rather than queue for ever.
            if (!await _slots.WaitAsync(0, ct).ConfigureAwait(false))
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // ignore
                }

                continue;
            }

            var id = Interlocked.Increment(ref _clientSeq);
            _clients[id] = client;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await HandleClientAsync(client, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke($"connection error: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        _clients.TryRemove(id, out _);
                        try
                        {
                            client.Dispose();
                        }
                        catch (Exception)
                        {
                            // ignore
                        }

                        _slots.Release();
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        client.NoDelay = true;
        await using var stream = client.GetStream();

        while (!serverToken.IsCancellationRequested)
        {
            var request = await ReadRequestAsync(stream, serverToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            // We only ever need GET and HEAD, and there is no request body handling, so a
            // request that carries a body gets a 405 and the connection closes (that keeps
            // the stream in sync - we never have to drain a body we do not understand).
            var unsupported = !request.IsGet && !request.IsHead;
            var keepAlive = request.KeepAlive && !unsupported && request.ContentLength == 0;

            try
            {
                if (unsupported)
                {
                    await WriteErrorAsync(
                        stream,
                        405,
                        "Method Not Allowed",
                        "Pyre serves GET and HEAD only.\n",
                        keepAlive: false,
                        extraHeaders: "Allow: GET, HEAD",
                        serverToken).ConfigureAwait(false);
                }
                else if (request.ContentLength > 0)
                {
                    await WriteErrorAsync(
                        stream,
                        405,
                        "Method Not Allowed",
                        "This server does not accept request bodies.\n",
                        keepAlive: false,
                        extraHeaders: "Allow: GET, HEAD",
                        serverToken).ConfigureAwait(false);
                }
                else
                {
                    await ServeAsync(stream, request, keepAlive, serverToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            if (!keepAlive)
            {
                return;
            }
        }
    }

    // ---------------------------------------------------------------- request parsing

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(RequestTimeout);
        var ct = timeout.Token;

        using var buffer = new MemoryStream(MaxHeaderBytes);
        var chunk = new byte[4096];

        while (true)
        {
            var terminator = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            if (terminator >= 0)
            {
                return ParseRequest(buffer.GetBuffer(), terminator);
            }

            if (buffer.Length > MaxHeaderBytes)
            {
                return null;
            }

            int read;
            try
            {
                read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (read <= 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>Index just past CRLFCRLF (or LFLF), or -1.</summary>
    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
            {
                return i + 4;
            }
        }

        for (var i = 0; i + 1 < length; i++)
        {
            if (data[i] == 10 && data[i + 1] == 10)
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static HttpRequest? ParseRequest(byte[] data, int headerLength)
    {
        // Headers are ISO-8859-1 by definition; Latin1 also never mangles bytes.
        var text = Encoding.Latin1.GetString(data, 0, headerLength);
        var lines = text.Split('\n');
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].TrimEnd('\r');
        var parts = requestLine.Split(' ');
        if (parts.Length < 3)
        {
            return null;
        }

        var request = new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Target = parts[1],
            Version = parts[^1].Trim(),
        };

        request.IsGet = request.Method == "GET";
        request.IsHead = request.Method == "HEAD";

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            request.Headers[name] = value;
        }

        if (request.Headers.TryGetValue("Content-Length", out var contentLength) &&
            long.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out var len) &&
            len > 0)
        {
            request.ContentLength = len;
        }

        request.Headers.TryGetValue("Range", out var range);
        request.Range = range;

        // HTTP/1.1 keeps the connection unless told otherwise; HTTP/1.0 is the reverse.
        var http11 = request.Version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);
        var connection = request.Headers.TryGetValue("Connection", out var c) ? c : string.Empty;
        request.KeepAlive = connection.Contains("close", StringComparison.OrdinalIgnoreCase)
            ? false
            : http11 || connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);

        return request;
    }

    // ---------------------------------------------------------------- resolving + serving

    private async Task ServeAsync(NetworkStream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        var resolved = ResolveTarget(request.Target);

        switch (resolved.Kind)
        {
            case ResolveKind.BadRequest:
                Log(request, 400, 0);
                await WriteErrorAsync(stream, 400, "Bad Request", "Bad request target.\n", keepAlive, null, ct)
                    .ConfigureAwait(false);
                return;

            case ResolveKind.Forbidden:
                Log(request, 403, 0);
                await WriteErrorAsync(
                        stream,
                        403,
                        "Forbidden",
                        "That path is outside the folder Pyre serves.\n",
                        keepAlive,
                        null,
                        ct)
                    .ConfigureAwait(false);
                return;

            case ResolveKind.NotFound:
                Log(request, 404, 0);
                await WriteErrorAsync(
                        stream,
                        404,
                        "Not Found",
                        "Not found: " + request.Target + "\n",
                        keepAlive,
                        null,
                        ct)
                    .ConfigureAwait(false);
                return;
        }

        await SendFileAsync(stream, request, resolved.Path, keepAlive, ct).ConfigureAwait(false);
    }

    private async Task SendFileAsync(NetworkStream stream, HttpRequest request, string path, bool keepAlive, CancellationToken ct)
    {
        FileStream file;
        try
        {
            // ReadWrite sharing so a file being written by something else is still readable,
            // same spirit as serve.py's plain open().
            file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            await NotFoundAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
            return;
        }
        catch (DirectoryNotFoundException)
        {
            await NotFoundAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
            return;
        }
        catch (IOException)
        {
            // Includes "file vanished between resolve and open" - not a 500.
            await NotFoundAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Log(request, 403, 0);
            await WriteErrorAsync(stream, 403, "Forbidden", "Access denied.\n", keepAlive, null, ct)
                .ConfigureAwait(false);
            return;
        }

        await using (file.ConfigureAwait(false))
        {
            try
            {
                var length = file.Length;

                var status = 200;
                var reason = "OK";
                long start = 0;
                long count = length;
                string? contentRange = null;

                if (request.Range is { Length: > 0 } rangeHeader &&
                    TryParseRange(rangeHeader, length, out var rangeStart, out var rangeCount, out var unsatisfiable))
                {
                    if (unsatisfiable)
                    {
                        Log(request, 416, 0);
                        await WriteErrorAsync(
                                stream,
                                416,
                                "Range Not Satisfiable",
                                "Requested range not satisfiable.\n",
                                keepAlive,
                                $"Content-Range: bytes */{length.ToString(CultureInfo.InvariantCulture)}",
                                ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    status = 206;
                    reason = "Partial Content";
                    start = rangeStart;
                    count = rangeCount;
                    contentRange =
                        $"bytes {start.ToString(CultureInfo.InvariantCulture)}-" +
                        $"{(start + count - 1).ToString(CultureInfo.InvariantCulture)}/" +
                        length.ToString(CultureInfo.InvariantCulture);
                }

                var headers = new List<(string Name, string Value)>(10)
                {
                    ("Content-Type", MimeTypes.ForPath(path)),
                    ("Content-Length", count.ToString(CultureInfo.InvariantCulture)),
                    ("Cache-Control", "no-store"),
                    ("Accept-Ranges", "bytes"),
                };

                if (contentRange is not null)
                {
                    headers.Add(("Content-Range", contentRange));
                }

                try
                {
                    headers.Add(("Last-Modified", FormatHttpDate(File.GetLastWriteTimeUtc(path))));
                }
                catch (Exception)
                {
                    // file went away; the Last-Modified header is optional
                }

                await WriteHeadAsync(stream, status, reason, headers, keepAlive ? "keep-alive" : "close", ct)
                    .ConfigureAwait(false);

                if (request.IsHead || count == 0)
                {
                    Log(request, status, count);
                    return;
                }

                file.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[CopyBufferSize];
                var remaining = count;
                while (remaining > 0)
                {
                    var want = (int)Math.Min(buffer.Length, remaining);
                    var read = await file.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }

                Log(request, status, count - remaining);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // client hung up mid-stream: routine
            }
            catch (Exception ex)
            {
                Error?.Invoke($"serving {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task NotFoundAsync(NetworkStream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        Log(request, 404, 0);
        await WriteErrorAsync(
                stream,
                404,
                "Not Found",
                "Not found: " + request.Target + "\n",
                keepAlive,
                null,
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(
        NetworkStream stream,
        int status,
        string reason,
        string body,
        bool keepAlive,
        string? extraHeaders,
        CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = new List<(string Name, string Value)>(5)
        {
            ("Content-Type", "text/plain; charset=utf-8"),
            ("Content-Length", payload.Length.ToString(CultureInfo.InvariantCulture)),
            ("Cache-Control", "no-store"),
        };

        if (extraHeaders is not null)
        {
            foreach (var extra in extraHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = extra.IndexOf(':');
                if (colon > 0)
                {
                    headers.Add((extra[..colon].Trim(), extra[(colon + 1)..].Trim()));
                }
            }
        }

        await WriteHeadAsync(stream, status, reason, headers, keepAlive ? "keep-alive" : "close", ct)
            .ConfigureAwait(false);

        // A HEAD response never carries a body, even for an error.
        await stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
    }

    private static async Task WriteHeadAsync(
        NetworkStream stream,
        int status,
        string reason,
        List<(string Name, string Value)> headers,
        string connection,
        CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Server: Pyre/1.0\r\n");
        sb.Append("Date: ").Append(FormatHttpDate(DateTime.UtcNow)).Append("\r\n");
        foreach (var (name, value) in headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        sb.Append("Connection: ").Append(connection).Append("\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
    }

    private static string FormatHttpDate(DateTime utc) =>
        utc.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    private void Log(HttpRequest request, int status, long bytes)
    {
        if (!LogRequests)
        {
            return;
        }

        var handler = RequestServed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler($"{request.Method} {request.Target} -> {status} ({bytes.ToString(CultureInfo.InvariantCulture)} B)");
        }
        catch (Exception)
        {
            // a logging subscriber must never take the server down
        }
    }

    // ---------------------------------------------------------------- paths

    private enum ResolveKind
    {
        File,
        NotFound,
        Forbidden,
        BadRequest,
    }

    private readonly record struct ResolveResult(ResolveKind Kind, string Path);

    private ResolveResult ResolveTarget(string target)
    {
        if (string.IsNullOrEmpty(target) || target[0] != '/')
        {
            // Tolerate the absolute-form a proxy-style client might send.
            if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            {
                target = absolute.AbsolutePath;
            }
            else
            {
                return new ResolveResult(ResolveKind.BadRequest, string.Empty);
            }
        }

        var query = target.AsSpan().IndexOfAny('?', '#');
        var pathOnly = query >= 0 ? target[..query] : target;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(pathOnly);
        }
        catch (UriFormatException)
        {
            return new ResolveResult(ResolveKind.BadRequest, string.Empty);
        }

        if (decoded.Contains('\0'))
        {
            return new ResolveResult(ResolveKind.BadRequest, string.Empty);
        }

        var relative = decoded.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

        // Payload binaries live BESIDE the exe, not in the served (embedded) root. Any *.elf
        // request is resolved against the exe's own folder first - the exact relative path,
        // then a single elfs\ folder matched on the basename. A missing payload falls through
        // to the normal path handling below, which yields a clean 404 (never a 500). The
        // locator refuses anything that could escape, so traversal stays refused.
        if (_elfs is not null && relative.EndsWith(".elf", StringComparison.OrdinalIgnoreCase))
        {
            var payload = _elfs.Resolve(relative);
            if (payload is not null)
            {
                return new ResolveResult(ResolveKind.File, payload);
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(Root, relative));
        }
        catch (Exception)
        {
            return new ResolveResult(ResolveKind.BadRequest, string.Empty);
        }

        // Never serve outside the root the exe sits in.
        var rootPrefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolveResult(ResolveKind.Forbidden, full);
        }

        if (File.Exists(full))
        {
            return new ResolveResult(ResolveKind.File, full);
        }

        if (Directory.Exists(full))
        {
            // "/" and any directory map to its index.html. No directory listing: the console
            // has no use for one and it is one less thing exposed on the LAN.
            var index = Path.Combine(full, "index.html");
            if (File.Exists(index))
            {
                return new ResolveResult(ResolveKind.File, index);
            }
        }

        return new ResolveResult(ResolveKind.NotFound, full);
    }

    /// <summary>
    /// Single byte-range only, which is all a range request needs to be handled "gracefully".
    /// Multi-range returns false, meaning the caller serves the whole file (200) instead.
    /// </summary>
    private static bool TryParseRange(string header, long length, out long start, out long count, out bool unsatisfiable)
    {
        start = 0;
        count = length;
        unsatisfiable = false;

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[6..].Trim();
        if (spec.Length == 0 || spec.Contains(',') || length <= 0)
        {
            return false;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var firstPart = spec[..dash].Trim();
        var secondPart = spec[(dash + 1)..].Trim();

        long rangeStart;
        long rangeEnd;

        if (firstPart.Length == 0)
        {
            // Suffix form: "bytes=-500" = the last 500 bytes.
            if (!long.TryParse(secondPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                unsatisfiable = true;
                return true;
            }

            var wanted = Math.Min(suffix, length);
            rangeStart = length - wanted;
            rangeEnd = length - 1;
        }
        else
        {
            if (!long.TryParse(firstPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out rangeStart) || rangeStart < 0)
            {
                return false;
            }

            if (secondPart.Length == 0)
            {
                rangeEnd = length - 1;
            }
            else if (!long.TryParse(secondPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out rangeEnd) || rangeEnd < rangeStart)
            {
                return false;
            }

            if (rangeEnd > length - 1)
            {
                rangeEnd = length - 1;
            }

            if (rangeStart > rangeEnd)
            {
                unsatisfiable = true;
                return true;
            }
        }

        start = rangeStart;
        count = rangeEnd - rangeStart + 1;
        return true;
    }

    private sealed class HttpRequest
    {
        public string Method { get; init; } = string.Empty;

        public string Target { get; init; } = string.Empty;

        public string Version { get; init; } = "HTTP/1.1";

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long ContentLength { get; set; }

        public bool KeepAlive { get; set; } = true;

        public bool IsGet { get; set; }

        public bool IsHead { get; set; }

        public string? Range { get; set; }
    }
}
