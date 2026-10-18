using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pyre.Services;

/// <summary>
/// A small DNS responder for the PS5 bootstrap.
///
/// The console's User's Guide is a request to <c>manuals.playstation.net</c>. Point the
/// console's DNS at this PC and this answers:
///
///   <list type="bullet">
///   <item>any name containing "manual" -> THIS PC, so the guide lands on the site Pyre
///   is already hosting on 8000;</item>
///   <item>Sony / PSN / Scea / psndl names -> 0.0.0.0, so the console can never reach an
///   update server or send telemetry (the same job nanoDNS does on the console);</item>
///   <item>everything else -> forwarded to filtering upstreams, so the console keeps
///   working internet AND gets ad/tracker blocking for free.</item>
///   </list>
///
/// The last point is the trick: it is the same upstream retarget used on nanoDNS, so ad
/// blocking arrives without a blocklist being configured anywhere.
///
/// BINDING: this binds the SPECIFIC local address, never 0.0.0.0. Windows usually has
/// "Internet Connection Sharing (ICS)" holding 0.0.0.0:53; a specific-address bind is
/// permitted alongside it and wins the demux for that address. Binding 0.0.0.0 fails.
/// </summary>
public sealed class DnsServer : IDisposable
{
    /// <summary>Filtering resolvers, tried in order. Every one filters: a plain resolver
    /// anywhere in this list would silently serve ads again whenever the filtered ones
    /// were slow, and the failure would be invisible.</summary>
    private static readonly IPAddress[] Upstreams =
    {
        IPAddress.Parse("94.140.14.14"),   // AdGuard DNS
        IPAddress.Parse("94.140.15.15"),   // AdGuard DNS (second)
        IPAddress.Parse("76.76.2.2"),      // ControlD "Ads & Tracking"
    };

    private const int QueryTimeoutMs = 1500;
    private const uint AnswerTtlSeconds = 300;

    /// <summary>Names that must never resolve. Wildcards use the same shell-style
    /// semantics nanoDNS and the exploit hosts use.</summary>
    private static readonly string[] BlockMasks =
    {
        "*.playstation.com",
        "*.playstation.com.*",
        "playstation.com",
        "*.playstation.net",
        "*.playstation.net.*",
        "playstation.net",
        "*.psndl.net",
        "psndl.net",
        "*.sonyentertainmentnetwork.com",
        "sonyentertainmentnetwork.com",
        "*.scea.com",
        "scea.com",
        "*.sony.com",
        "sony.com",
    };

    private readonly IPAddress _self;
    private readonly Action<string>? _log;
    private readonly HashSet<string> _loggedBlocked = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public DnsServer(IPAddress self, Action<string>? log = null)
    {
        _self = self;
        _log = log;
    }

    public bool Running => _running;

    public string Address => _self.ToString();

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _listener = new UdpClient(new IPEndPoint(_self, 53));
        _cts = new CancellationTokenSource();
        _running = true;

        _log?.Invoke($"dns: answering on {_self}:53  -  guide -> this PC, Sony blocked, everything else -> AdGuard/ControlD");
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        _log?.Invoke("dns: stopped");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await listener.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"dns: receive failed: {ex.Message}");
                continue;
            }

            _ = HandleAsync(listener, packet.Buffer, packet.RemoteEndPoint, token);
        }
    }

    private async Task HandleAsync(UdpClient listener, byte[] query, IPEndPoint from, CancellationToken token)
    {
        try
        {
            if (!TryReadQuestion(query, out var name, out var qtype, out var questionEnd))
            {
                return;
            }
            // Reply FIRST, log after. The log is a convenience; the reply is the job. If a log
            // call ever throws again, the answer the console is waiting for is already on the
            // wire instead of being lost behind an exception.
            if (name.Contains("manual", StringComparison.OrdinalIgnoreCase))
            {
                await SendAnswerAsync(listener, query, questionEnd, qtype, _self, from);
                _log?.Invoke($"dns: {name} -> this PC ({_self})");
                return;
            }

            if (MatchesAnyMask(name, BlockMasks))
            {
                var firstTime = _loggedBlocked.Add(name);
                await SendAnswerAsync(listener, query, questionEnd, qtype, IPAddress.Any, from);
                if (firstTime)
                {
                    _log?.Invoke($"dns: {name} -> blocked (Sony)");
                }

                return;
            }

            await ForwardAsync(listener, query, from, token);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"dns: handling failed for {from}: {ex.Message}");
        }
    }

    /// <summary>
    /// Answer from this machine.
    ///
    /// A (1) and ANY (255) carry the IPv4 address. AAAA (28) carries the unspecified address
    /// <c>::</c> - matching the community DNS that already works on this console, which answers
    /// <c>::</c> for the guide host rather than an empty record. A client given :: fails the
    /// IPv6 attempt instantly and falls back to IPv4; an empty record leaves the resolver to
    /// decide, so matching the reference removes a difference while debugging.
    ///
    /// Every other qtype gets NOERROR with no answers, so a blocked name cannot leak over a
    /// record type we did not think of.
    /// </summary>
    private static async Task SendAnswerAsync(UdpClient listener, byte[] query, int questionEnd, int qtype, IPAddress address, IPEndPoint to)
    {
        var isAaaa = qtype == 28;
        var withAddress = qtype is 1 or 255;
        var rdataLength = withAddress ? 4 : isAaaa ? 16 : 0;
        var answered = withAddress || isAaaa;

        var length = questionEnd + (answered ? 12 + rdataLength : 0);
        var response = new byte[length];

        Array.Copy(query, response, questionEnd);

        response[2] = (byte)(0x84 | (query[2] & 0x01));   // QR=1, AA=1, RD copied
        response[3] = 0x80;                                // RA=1, RCODE=0

        var ancount = answered ? 1 : 0;
        response[6] = (byte)(ancount >> 8);
        response[7] = (byte)(ancount & 0xFF);
        response[8] = 0;
        response[9] = 0;
        response[10] = 0;
        response[11] = 0;

        if (answered)
        {
            var o = questionEnd;
            response[o++] = 0xC0;
            response[o++] = 0x0C;                                   // name pointer to the question
            response[o++] = 0x00;
            response[o++] = isAaaa ? (byte)28 : (byte)1;            // TYPE A or AAAA
            response[o++] = 0x00; response[o++] = 0x01;             // CLASS IN
            response[o++] = (byte)(AnswerTtlSeconds >> 24);
            response[o++] = (byte)((AnswerTtlSeconds >> 16) & 0xFF);
            response[o++] = (byte)((AnswerTtlSeconds >> 8) & 0xFF);
            response[o++] = (byte)(AnswerTtlSeconds & 0xFF);
            response[o++] = (byte)(rdataLength >> 8);
            response[o++] = (byte)(rdataLength & 0xFF);

            if (withAddress)
            {
                var bytes = address.GetAddressBytes();
                response[o++] = bytes[0];
                response[o++] = bytes[1];
                response[o++] = bytes[2];
                response[o] = bytes[3];
            }
            else
            {
                // :: - sixteen zero bytes, already zero from the array initialiser
                o += 16;
            }
        }

        await listener.SendAsync(response, response.Length, to);
    }

    /// <summary>
    /// Relay to the upstreams in order. When none answers, the client gets SERVFAIL with
    /// no answers - deliberately not an address, because answering 0.0.0.0 here would
    /// black-hole a working name and look like the blocker misfiring.
    /// </summary>
    private async Task ForwardAsync(UdpClient listener, byte[] query, IPEndPoint from, CancellationToken token)
    {
        foreach (var upstream in Upstreams)
        {
            try
            {
                using var client = new UdpClient();
                client.Connect(new IPEndPoint(upstream, 53));
                await client.SendAsync(query, query.Length);

                var receive = client.ReceiveAsync();
                var finished = await Task.WhenAny(receive, Task.Delay(QueryTimeoutMs, token));
                if (finished != receive)
                {
                    continue;
                }

                var reply = await receive;
                await listener.SendAsync(reply.Buffer, reply.Buffer.Length, from);
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"dns: upstream {upstream} failed: {ex.Message}");
            }
        }

        _log?.Invoke("dns: no upstream answered - sending SERVFAIL");
        if (TryReadQuestion(query, out _, out _, out var questionEnd))
        {
            var servfail = new byte[questionEnd];
            Array.Copy(query, servfail, questionEnd);
            servfail[2] = (byte)(0x80 | (query[2] & 0x01));
            servfail[3] = (byte)(0x80 | 0x02);   // RA=1, RCODE=2 (server failure)
            for (var i = 6; i < 12; i++)
            {
                servfail[i] = 0;
            }

            await listener.SendAsync(servfail, servfail.Length, from);
        }
    }

    /// <summary>Read QNAME and QTYPE out of a query; return where the question section ends.</summary>
    private static bool TryReadQuestion(byte[] query, out string name, out int qtype, out int end)
    {
        name = string.Empty;
        qtype = 0;
        end = 0;

        if (query.Length < 12)
        {
            return false;
        }

        var i = 12;
        var labels = new List<string>();

        while (i < query.Length)
        {
            var len = query[i];
            if (len == 0)
            {
                i++;
                break;
            }

            if ((len & 0xC0) != 0 || i + 1 + len > query.Length)
            {
                return false;   // compression is not expected inside a question
            }

            labels.Add(Encoding.ASCII.GetString(query, i + 1, len));
            i += 1 + len;
        }

        if (i + 4 > query.Length)
        {
            return false;
        }

        name = string.Join('.', labels);
        qtype = (query[i] << 8) | query[i + 1];
        end = i + 4;
        return name.Length > 0;
    }

    private static bool MatchesAnyMask(string host, string[] masks)
    {
        foreach (var mask in masks)
        {
            if (MaskMatch(host, 0, mask, 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shell-style match: '*' spans one or more characters, '?' exactly one.</summary>
    private static bool MaskMatch(string s, int si, string m, int mi)
    {
        while (mi < m.Length)
        {
            if (m[mi] == '*')
            {
                var rest = mi + 1;
                if (rest == m.Length)
                {
                    return si < s.Length;   // a trailing '*' must still consume something
                }

                for (var skip = si; skip < s.Length; skip++)
                {
                    if (MaskMatch(s, skip, m, rest))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (si >= s.Length)
            {
                return false;
            }

            if (m[mi] == '?')
            {
                si++;
                mi++;
                continue;
            }

            if (char.ToLowerInvariant(s[si]) != char.ToLowerInvariant(m[mi]))
            {
                return false;
            }

            si++;
            mi++;
        }

        return si == s.Length;
    }

    public void Dispose() => Stop();
}
