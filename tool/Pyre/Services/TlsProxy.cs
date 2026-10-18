using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pyre.Services;

/// <summary>
/// The HTTPS half of the bootstrap, and the piece that makes "type this PC's address into
/// your console's DNS" actually work.
///
/// The console's User's Guide is an <b>HTTPS</b> request to <c>manuals.playstation.net</c>.
/// DnsServer answers that name with this PC, so the console then opens a TLS connection
/// here. Without a listener on 443 the console gets a connection refused and the guide
/// never appears - which is exactly why DNS alone is not enough.
///
/// This presents a self-signed certificate for that name (the console shows a certificate
/// warning, the user accepts it once) and relays the plaintext request to the site Pyre is
/// already serving on 8000, so there is only ever one copy of the site logic.
/// </summary>
public sealed class TlsProxy : IDisposable
{
    private const string GuideHost = "manuals.playstation.net";

    /// <summary>The community DNS that already works on Cam's console presents a certificate
    /// covering BOTH of these names, so this matches it. Without the .com the console would be
    /// offered a certificate that does not cover a name it might have asked for.</summary>
    private const string GuideHostAlt = "manuals.playstation.com";

    private readonly IPAddress _self;
    private readonly int _sitePort;
    private readonly Action<string>? _log;
    private TcpListener? _listener;
    private TcpListener? _plain;
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public TlsProxy(IPAddress self, int sitePort, Action<string>? log = null)
    {
        _self = self;
        _sitePort = sitePort;
        _log = log;
    }

    public bool Running => _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        // Generated once per run and held in memory only: a self-signed certificate for a
        // console bootstrap is not worth installing into the machine's certificate store.
        _certificate = CreateSelfSigned(GuideHost);

        _listener = new TcpListener(_self, 443);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _running = true;

        _log?.Invoke($"https: {GuideHost} on {_self}:443 -> site on 127.0.0.1:{_sitePort}");
        _ = Task.Run(() => LoopAsync(_listener, useTls: true, _cts.Token));

        // The community DNS that already works on this console serves the guide on BOTH 80 and
        // 443. Serving only 443 was a real difference: if the console asks plain HTTP first,
        // nothing answered at all. Covering both costs one extra listener.
        try
        {
            _plain = new TcpListener(_self, 80);
            _plain.Start();
            _log?.Invoke($"http:  {_self}:80 -> site on 127.0.0.1:{_sitePort}");
            var plain = _plain;
            _ = Task.Run(() => LoopAsync(plain, useTls: false, _cts.Token));
        }
        catch (SocketException ex)
        {
            _plain = null;
            _log?.Invoke($"http: could not bind port 80 ({ex.Message}) - HTTPS on 443 still serves the guide");
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _plain?.Stop();
        _plain = null;
        _certificate?.Dispose();
        _certificate = null;
        _log?.Invoke("https: stopped");
    }

    private async Task LoopAsync(TcpListener listener, bool useTls, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client, useTls, token), token);
        }
    }

    private async Task ServeAsync(TcpClient client, bool useTls, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var network = client.GetStream();

                if (!useTls)
                {
                    await RelayOverAsync(network, "http", token);
                    return;
                }

                using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(
                    _certificate!,
                    clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                    checkCertificateRevocation: false);

                await RelayOverAsync(ssl, "https", token);

                // A TLS socket closed without close_notify is a protocol error to a strict
                // client: curl reports "server closed abruptly" and DISCARDS the response, and
                // the console's WebKit showed a white screen for the same reason. .NET's own
                // HttpClient is lenient, which is why the selftest passed while the console
                // failed. Shut the write side down properly so the response is actually kept.
                try
                {
                    await ssl.ShutdownAsync();
                }
                catch (Exception)
                {
                    // the peer may already be gone; nothing useful to do
                }
            }
            catch (AuthenticationException ex)
            {
                _log?.Invoke($"https: tls handshake failed: {ex.Message}");
            }
            catch (IOException)
            {
                // console closed early; normal
            }
            catch (Exception ex)
            {
                _log?.Invoke($"https: {ex.Message}");
            }
        }
    }

    private async Task RelayOverAsync(Stream clientStream, string scheme, CancellationToken token)
    {
        var requestHead = await ReadHeadAsync(clientStream, token);
        if (requestHead.Length == 0)
        {
            return;
        }

        _log?.Invoke($"{scheme}: {FirstLine(requestHead)}");

        var response = await RelayAsync(requestHead, token);
        if (response.Length > 0)
        {
            await clientStream.WriteAsync(response, token);
            await clientStream.FlushAsync(token);
        }
    }

    /// <summary>
    /// Forward the request to the site on 8000 and read the whole response back.
    ///
    /// The forwarded request is forced to <c>Connection: close</c> so the reply ends at EOF;
    /// otherwise a keep-alive server would leave this waiting on a socket it thinks is
    /// reusable. The response is read to EOF rather than parsed, because reading to EOF is
    /// what "close" guarantees and it avoids having to reimplement chunked decoding.
    /// </summary>
    private async Task<byte[]> RelayAsync(string requestHead, CancellationToken token)
    {
        using var site = new TcpClient();
        await site.ConnectAsync(IPAddress.Loopback, _sitePort, token);

        using var stream = site.GetStream();

        var head = RewriteRequest(requestHead);
        var bytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);

        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Rewrite the request before handing it to the site on 8000.
    ///
    /// Two things, both learned by asking the community DNS that already works on this console
    /// what the guide actually requests:
    ///
    ///   GET /document/en/ps5/index.html HTTP/1.1   <- the REAL path, not "/"
    ///
    /// Serving only "/" produced a white screen, because the guide asked for a path the site did
    /// not have and got a 404. The <c>/document/&lt;lang&gt;/&lt;platform&gt;/</c> prefix is
    /// stripped so the request lands on the site root; everything AFTER the prefix is kept, so
    /// relative assets the page then asks for still resolve.
    ///
    /// The Connection header is forced to close so the reply ends at EOF.
    /// </summary>
    private static string RewriteRequest(string head)
    {
        var lines = head.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (i == 0)
            {
                var parts = line.Split(' ');
                if (parts.Length >= 3)
                {
                    builder.Append(parts[0]).Append(' ')
                           .Append(RewriteGuidePath(parts[1])).Append(' ')
                           .Append(parts[2]).Append("\r\n");
                    continue;
                }

                builder.Append(line).Append("\r\n");
                continue;
            }

            // drop any existing Connection header; we set our own
            if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Length == 0)
            {
                builder.Append("Connection: close\r\n");
                builder.Append("\r\n");
                break;
            }

            builder.Append(line).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>/document/en/ps5/anything -> /anything ; /document/en/ps5 -> /</summary>
    private static string RewriteGuidePath(string path)
    {
        const string prefix = "/document/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var rest = path[prefix.Length..];

        // drop the language and the platform segment
        for (var segment = 0; segment < 2; segment++)
        {
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return "/";
            }

            rest = rest[(slash + 1)..];
        }

        return rest.Length == 0 ? "/" : "/" + rest;
    }

    /// <summary>Read up to and including the blank line that ends the request headers.</summary>
    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken token)
    {
        var builder = new StringBuilder();
        var one = new byte[1];
        var consecutiveNewlines = 0;

        for (var i = 0; i < 32768; i++)
        {
            var read = await stream.ReadAsync(one, token);
            if (read == 0)
            {
                break;
            }

            var c = (char)one[0];
            builder.Append(c);

            consecutiveNewlines = c switch
            {
                '\n' => consecutiveNewlines + 1,
                '\r' => consecutiveNewlines,
                _ => 0,
            };

            if (consecutiveNewlines >= 2)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string FirstLine(string head)
    {
        var end = head.IndexOf('\n');
        var line = end > 0 ? head[..end] : head;
        return line.Trim();
    }

    /// <summary>A self-signed certificate for the guide's name, valid from yesterday so a
    /// slightly-off console clock still accepts it.</summary>
    private static X509Certificate2 CreateSelfSigned(string host)
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={host}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(GuideHost);
        san.AddDnsName(GuideHostAlt);
        request.CertificateExtensions.Add(san.Build());

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // serverAuth

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        // CreateSelfSigned returns a certificate whose private key is tied to the RSA object
        // that is about to be disposed; re-import the PFX so the key survives it.
        var pfx = certificate.Export(X509ContentType.Pfx);
        certificate.Dispose();

        // net8 has no X509CertificateLoader (that arrived in .NET 9), so import via the
        // constructor. Exportable keeps the private key usable for AuthenticateAsServer.
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }

    public void Dispose() => Stop();
}
