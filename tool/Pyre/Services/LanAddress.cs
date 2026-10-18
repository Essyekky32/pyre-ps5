using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Pyre.Services;

public enum LanIpSource
{
    /// <summary>Read off the socket the OS would use for an outbound connection - the real default route.</summary>
    DefaultRoute,

    /// <summary>Interface that owns an IPv4 default gateway.</summary>
    GatewayInterface,

    /// <summary>The old serve.py behaviour: scrape ipconfig.</summary>
    IpConfigFallback,

    /// <summary>Nothing usable found.</summary>
    None,
}

public readonly record struct LanIpResult(string Ip, LanIpSource Source, string AdapterName)
{
    public bool Found => Source != LanIpSource.None && Ip.Length > 0;

    public static readonly LanIpResult None = new(string.Empty, LanIpSource.None, string.Empty);
}

/// <summary>
/// Works out "the PC's LAN IPv4 for the connection actually in use" - deliberately the one
/// address, no adapter picker and no VPN diagnostics UI.
///
/// Order: ask the routing table (best), then the adapter holding the default gateway,
/// then fall back to scraping ipconfig exactly like the frozen serve.py did - and the
/// caller SAYS SO in the GUI when it had to fall back.
/// </summary>
internal static class LanAddress
{
    public static LanIpResult Detect()
    {
        var fromRoute = FromDefaultRoute();
        if (fromRoute.Found)
        {
            return fromRoute;
        }

        var fromGateway = FromGatewayInterface();
        if (fromGateway.Found)
        {
            return fromGateway;
        }

        return FromIpConfig();
    }

    /// <summary>
    /// A UDP socket "connected" to a public address never sends a packet, it just makes the
    /// OS pick the local address it would route from. That is the default-route interface.
    /// </summary>
    private static LanIpResult FromDefaultRoute()
    {
        foreach (var probe in new[] { "1.1.1.1", "8.8.8.8" })
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(IPAddress.Parse(probe), 53));
                if (socket.LocalEndPoint is IPEndPoint local && Usable(local.Address))
                {
                    var adapter = AdapterFor(local.Address);
                    return new LanIpResult(local.Address.ToString(), LanIpSource.DefaultRoute, adapter);
                }
            }
            catch (SocketException)
            {
                // no route out this way - try the next probe, then fall through
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }
        }

        return LanIpResult.None;
    }

    private static LanIpResult FromGatewayInterface()
    {
        try
        {
            var candidates = new List<(LanIpResult Result, int Rank)>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                     !g.Address.Equals(IPAddress.Any)))
                {
                    continue;
                }

                var ip = props.UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(Usable);
                if (ip is null)
                {
                    continue;
                }

                var text = ip.ToString();
                var rank = text.StartsWith("192.168.", StringComparison.Ordinal) ? 0
                    : text.StartsWith("10.", StringComparison.Ordinal) ? 1
                    : IsPrivate172(text) ? 2
                    : 3;

                candidates.Add((new LanIpResult(text, LanIpSource.GatewayInterface, nic.Name), rank));
            }

            if (candidates.Count == 0)
            {
                return LanIpResult.None;
            }

            return candidates.OrderBy(c => c.Rank).First().Result;
        }
        catch (NetworkInformationException)
        {
            return LanIpResult.None;
        }
    }

    /// <summary>The frozen serve.py fallback: ipconfig, prefer 192.168.x.x, else "localhost".</summary>
    private static LanIpResult FromIpConfig()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", string.Empty)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Latin1,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return LanIpResult.None;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            var found = Regex.Matches(output, @"IPv4[^:]*:\s*([\d.]+)")
                .Select(m => m.Groups[1].Value)
                .Where(v => IPAddress.TryParse(v, out var parsed) && Usable(parsed))
                .ToList();

            var best = found.FirstOrDefault(v => v.StartsWith("192.168.", StringComparison.Ordinal))
                       ?? found.FirstOrDefault(v => v.StartsWith("10.", StringComparison.Ordinal))
                       ?? found.FirstOrDefault(IsPrivate172)
                       ?? found.FirstOrDefault();

            return best is null
                ? LanIpResult.None
                : new LanIpResult(best, LanIpSource.IpConfigFallback, string.Empty);
        }
        catch (Exception)
        {
            return LanIpResult.None;
        }
    }

    private static bool Usable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224)
        {
            return false;
        }

        // 169.254.x.x means "no DHCP" - never a useful console address.
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsPrivate172(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 &&
               parts[0] == "172" &&
               int.TryParse(parts[1], out var second) &&
               second >= 16 && second <= 31;
    }

    private static string AdapterFor(IPAddress address)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address)))
                {
                    return nic.Name;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // adapter name is only used for the detail line
        }

        return string.Empty;
    }
}
