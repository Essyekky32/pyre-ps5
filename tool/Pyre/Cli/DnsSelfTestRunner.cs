using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pyre.Services;

namespace Pyre.Cli;

/// <summary>
/// Proves the DNS responder with real queries over the wire.
///
/// It deliberately queries the LAN address, not 127.0.0.1. That is the path the console
/// takes, and it is the only way to tell whether this PC's own listener wins the UDP demux
/// against "Internet Connection Sharing", which holds 0.0.0.0:53 on most Windows machines.
///
/// Four questions, one for each rule:
///   manuals.playstation.net  -> this PC        (the User's Guide redirect)
///   update.playstation.net   -> 0.0.0.0        (Sony is refused)
///   archive.org              -> a real address (internet still works)
///   doubleclick.net          -> 0.0.0.0        (the upstream is filtering ads)
/// </summary>
internal static class DnsSelfTestRunner
{
    private static int _passed;
    private static int _failed;

    /// <summary>The address under test. Every probe asks THIS address, so the test measures
    /// the same path the console uses rather than a loopback shortcut.</summary>
    private static IPAddress _self = IPAddress.Loopback;

    public static int Run(ParsedArgs args)
    {
        _passed = 0;
        _failed = 0;

        var lan = LanAddress.Detect();
        if (!lan.Found)
        {
            CliLog.Line("dns selftest: no LAN address detected, nothing to bind");
            return 1;
        }

        var self = IPAddress.Parse(lan.Ip);
        _self = self;
        CliLog.Line($"dns selftest: binding {self}:53 and asking it real questions");
        CliLog.Line($"dns selftest: (lan source: {lan.Source})");

        var server = new DnsServer(self, line => CliLog.Line("  " + line));
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            Fail($"could not bind {self}:53 - {ex.Message}");
            CliLog.Line("dns selftest: FAILED to bind. Another listener owns this address.");
            return 1;
        }

        // The site has to be up for the TLS relay to have something to hand off to.
        var site = EmbeddedSite.Resolve(args.Root);
        if (!site.Ok)
        {
            CliLog.Line($"dns selftest: site unavailable - {site.Error}");
            return 2;
        }

        var web = new Server.PyreWebServer(site.Root, args.Port) { LogRequests = false };
        TlsProxy? tls = null;

        try
        {
            web.Start();
            CliLog.Line($"dns selftest: site up on port {args.Port}");

            tls = new TlsProxy(self, args.Port, line => CliLog.Line("  " + line));
            try
            {
                tls.Start();
            }
            catch (SocketException ex)
            {
                tls = null;
                Fail($"could not bind {self}:443 - {ex.Message}");
            }

            // give both receive loops a moment to be listening
            System.Threading.Thread.Sleep(300);

            Expect("manuals.playstation.net", self, "the console's User's Guide is redirected to this PC");
            Expect("update.playstation.net", IPAddress.Any, "Sony update hosts are refused");
            ExpectNot("archive.org", IPAddress.Any, "normal names still resolve, so the console keeps internet");
            Expect("doubleclick.net", IPAddress.Any, "the upstream is filtering ads and trackers");

            if (tls is not null)
            {
                ExpectHttp(self, "the guide also answers on plain HTTP 80, as the working reference does");
                ExpectHttps(self, "/", "the guide loads over HTTPS with the self-signed certificate");
                ExpectHttps(self, "/document/en/ps5/index.html", "the REAL guide path - the console asks for this, not /");
            }
        }
        finally
        {
            tls?.Stop();
            tls?.Dispose();
            web.StopAsync().GetAwaiter().GetResult();
            server.Stop();
        }

        CliLog.Line(string.Empty);
        CliLog.Line($"DNS SELFTEST: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Plain HTTP on 80. The community DNS that works on this console serves the
    /// guide on 80 as well as 443, so if the console asks HTTP first, this is the answer.</summary>
    private static void ExpectHttp(IPAddress self, string why)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var response = client.GetAsync($"http://{self}/").GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode && body.Length > 0)
            {
                Pass($"http://{self}/ -> {(int)response.StatusCode}, {body.Length} bytes  ({why})");
            }
            else
            {
                Fail($"http://{self}/ -> {(int)response.StatusCode}, {body.Length} bytes  ({why})");
            }
        }
        catch (Exception ex)
        {
            Fail($"http://{self}/ -> {ex.Message}  ({why})");
        }
    }

    /// <summary>Connect over HTTPS the way the console would. Certificate validation is
    /// skipped deliberately: the certificate is self-signed, the console shows a warning and
    /// the user accepts it, so that is the expected path rather than a fault.</summary>
    private static void ExpectHttps(IPAddress self, string path, string why)
    {
        try
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };

            using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var response = client.GetAsync($"https://{self}{path}").GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode && body.Length > 0)
            {
                Pass($"https://{self}{path} -> {(int)response.StatusCode}, {body.Length} bytes  ({why})");
            }
            else
            {
                Fail($"https://{self}{path} -> {(int)response.StatusCode}, {body.Length} bytes  ({why})");
            }
        }
        catch (Exception ex)
        {
            Fail($"https://{self}{path} -> {ex.Message}  ({why})");
        }
    }

    private static void Expect(string name, IPAddress expected, string why)
    {
        var got = Ask(name);
        if (got is null)
        {
            Fail($"{name}: no answer at all  ({why})");
            return;
        }

        if (got.Equals(expected))
        {
            Pass($"{name} -> {got}  ({why})");
        }
        else
        {
            Fail($"{name} -> {got}, expected {expected}  ({why})");
        }
    }

    private static void ExpectNot(string name, IPAddress unwanted, string why)
    {
        var got = Ask(name);
        if (got is null)
        {
            Fail($"{name}: no answer at all  ({why})");
            return;
        }

        if (got.Equals(unwanted))
        {
            Fail($"{name} -> {got} which is the unwanted address  ({why})");
        }
        else
        {
            Pass($"{name} -> {got}  ({why})");
        }
    }

    /// <summary>Send one A query and return the first address, or null if nothing came back.</summary>
    private static IPAddress? Ask(string host)
    {
        try
        {
            var query = BuildQuery(host, out var id);
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 4000;
            client.Connect(new IPEndPoint(_self, 53));
            client.Send(query, query.Length);

            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var reply = client.Receive(ref endpoint);
            return FirstAnswer(reply, id);
        }
        catch (SocketException ex)
        {
            CliLog.Line($"  ({host}: {ex.SocketErrorCode})");
            return null;
        }
        catch (Exception ex)
        {
            CliLog.Line($"  ({host}: {ex.Message})");
            return null;
        }
    }

    private static byte[] BuildQuery(string host, out ushort id)
    {
        id = (ushort)Random.Shared.Next(1, ushort.MaxValue);

        var buffer = new List<byte>
        {
            (byte)(id >> 8),
            (byte)(id & 0xFF),
            0x01, 0x00,   // standard query, recursion desired
            0x00, 0x01,   // qdcount 1
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00,
        };

        foreach (var label in host.Split('.'))
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }

        buffer.Add(0x00);         // end of name
        buffer.Add(0x00);         // qtype A
        buffer.Add(0x01);
        buffer.Add(0x00);         // qclass IN
        buffer.Add(0x01);
        return buffer.ToArray();
    }

    /// <summary>Walk the answer section and return the first A record.</summary>
    private static IPAddress? FirstAnswer(byte[] reply, ushort expectedId)
    {
        if (reply.Length < 12)
        {
            return null;
        }

        var id = (ushort)((reply[0] << 8) | reply[1]);
        if (id != expectedId)
        {
            return null;
        }

        var ancount = (reply[6] << 8) | reply[7];
        if (ancount == 0)
        {
            return null;
        }

        // skip past the question
        var i = 12;
        while (i < reply.Length && reply[i] != 0)
        {
            i += 1 + reply[i];
        }

        i += 5;   // root label + qtype(2) + qclass(2)

        for (var record = 0; record < ancount && i + 12 <= reply.Length; record++)
        {
            // name (pointer or labels)
            if ((reply[i] & 0xC0) == 0xC0)
            {
                i += 2;
            }
            else
            {
                while (i < reply.Length && reply[i] != 0)
                {
                    i += 1 + reply[i];
                }

                i++;
            }

            if (i + 10 > reply.Length)
            {
                return null;
            }

            var type = (reply[i] << 8) | reply[i + 1];
            var rdlength = (reply[i + 8] << 8) | reply[i + 9];
            i += 10;

            if (i + rdlength > reply.Length)
            {
                return null;
            }

            if (type == 1 && rdlength == 4)
            {
                return new IPAddress(new[] { reply[i], reply[i + 1], reply[i + 2], reply[i + 3] });
            }

            i += rdlength;
        }

        return null;
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
}
