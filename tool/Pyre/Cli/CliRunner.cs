using System.IO;
using System.Net.Sockets;
using Pyre.Localization;
using Pyre.Server;
using Pyre.Services;

namespace Pyre.Cli;

internal static class CliRunner
{
    private static LocalizationService Loc => LocalizationService.Instance;

    public static int Run(ParsedArgs args)
    {
        CliLog.AttachToParentConsole();
        CliLog.OpenFile(args.LogFile);

        return args.Mode switch
        {
            CliMode.Help => Help(),
            CliMode.Serve => Serve(args),
            CliMode.SelfTest => SelfTestRunner.Run(args),
            CliMode.DnsSelfTest => DnsSelfTestRunner.Run(args),
            CliMode.AddFirewallRule => AddFirewallRule(),
            CliMode.CheckFirewall => CheckFirewall(),
            _ => 0,
        };
    }

    private static int Help()
    {
        CliLog.Line(Loc.T("cli.help"));
        CliLog.Line(string.Empty);
        CliLog.Line(Loc.T("cli.usage"));
        CliLog.Line(string.Empty);
        CliLog.Line(Loc.T("cli.options"));
        return 0;
    }

    /// <summary>
    /// No window at all: start the same server the GUI starts, print the address, sit there.
    /// Used to verify the shipped exe without anyone clicking anything.
    /// </summary>
    private static int Serve(ParsedArgs args)
    {
        var site = EmbeddedSite.Resolve(args.Root);

        if (!site.Ok)
        {
            if (site.ErrorKind == SiteErrorKind.OverrideMissing)
            {
                CliLog.Line($"no site found: {site.Error}");
                CliLog.Line("--root is a deliberate override for development; without it Pyre serves the copy embedded in the exe.");
            }
            else
            {
                CliLog.Line($"embedded site could not be verified: {site.Error}");
                CliLog.Line("refusing to serve: the unpacked bytes did not match the embedded manifest.");
            }

            return 2;
        }

        var root = site.Root;
        var elfs = ElfLocator.Create(args.Root);
        var server = new PyreWebServer(root, args.Port, elfs) { LogRequests = true };
        server.Error += message => CliLog.Line("error: " + message);
        server.Notice += message => CliLog.Line("notice: " + message);
        server.RequestServed += message => CliLog.Line(message);

        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            CliLog.Line($"could not bind port {args.Port}: {ex.Message}");
            return 3;
        }

        var lan = LanAddress.Detect();
        var ip = lan.Found ? lan.Ip : "127.0.0.1";

        CliLog.Line($"root: {root}");
        CliLog.Line($"site: {DescribeSource(site)}");
        CliLog.Line($"lan : {lan.Ip} ({lan.Source})");
        CliLog.Line($"url : {PyreWebServer.Url(ip, args.Port)}");
        LogPayloads(ElfLocator.InspectManifest(args.Root));

        var stop = new ManualResetEventSlim(false);
        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };
        }
        catch (Exception)
        {
            // no console to hook
        }

        if (args.DurationSeconds > 0)
        {
            stop.Wait(TimeSpan.FromSeconds(args.DurationSeconds));
        }
        else
        {
            stop.Wait();
        }

        server.StopAsync().GetAwaiter().GetResult();
        CliLog.Line("stopped");
        return 0;
    }

    /// <summary>
    /// The elevated one-off. Launched by Pyre itself with Verb = "runas", never by hand
    /// (although it is safe to run by hand: it checks before it adds).
    /// </summary>
    private static int AddFirewallRule()
    {
        // Adds every rule the bootstrap needs (8000 site, 53 DNS, 443 guide TLS), each
        // checked by name first so re-running can never duplicate one.
        var result = FirewallManager.AddAllIfMissing();

        var settings = SettingsStore.Load();
        settings.Firewall.Checked = true;
        settings.Firewall.RuleName = FirewallManager.RuleName;
        settings.Firewall.Detail = result.Detail;
        settings.Firewall.State = StateFor(result.Outcome);
        settings.Firewall.LastAttemptUtc = DateTime.UtcNow.ToString("o");
        SettingsStore.Save(settings);

        CliLog.Line($"firewall: {result.Outcome} - {result.Detail}");

        return result.Outcome is FirewallOutcome.Added or FirewallOutcome.Exists ? 0 : 1;
    }

    private static int CheckFirewall()
    {
        var result = FirewallManager.Check();
        CliLog.Line($"firewall: {result.Outcome} - {result.Detail}");
        var missing = FirewallManager.MissingRules();
        CliLog.Line(missing.Count == 0
            ? "firewall: every rule present"
            : "firewall: MISSING " + string.Join(", ", missing));
        CliLog.Line($"elevated: {FirewallManager.IsElevated}");
        return result.Outcome == FirewallOutcome.Exists ? 0 : 1;
    }

    internal static string DescribeSource(SiteResolution site) => site.Kind == SiteSourceKind.Embedded
        ? $"embedded copy (build {site.BuildId}, {site.FileCount} files verified)"
        : "override folder (--root)";

    /// <summary>
    /// Say plainly, in the headless log too, which payloads are beside the exe and which are
    /// not - a missing payload must never be silent.
    /// </summary>
    private static void LogPayloads(ElfReport report)
    {
        CliLog.Line($"payloads: {report.FoundCount}/{report.Expected} beside the exe ({report.MissingRequired} required missing, {report.MissingOptional} optional not shipped)");
        foreach (var basis in report.Bases)
        {
            CliLog.Line($"payload dir: {basis}");
        }

        foreach (var finding in report.MissingRequiredFinding)
        {
            CliLog.Line($"payload MISSING: {finding.Path}");
        }
    }

    private static string StateFor(FirewallOutcome outcome) => outcome switch
    {
        FirewallOutcome.Exists => "exists",
        FirewallOutcome.Added => "added",
        FirewallOutcome.Declined => "declined",
        FirewallOutcome.Unavailable => "unavailable",
        _ => "failed",
    };
}
