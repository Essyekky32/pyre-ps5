using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using Pyre.Cli;

namespace Pyre.Services;

public enum FirewallOutcome
{
    /// <summary>The rule is already there. We did nothing and asked for nothing.</summary>
    Exists,

    /// <summary>We created it.</summary>
    Added,

    /// <summary>The user said no to the UAC prompt. Hosting still works; we say so plainly.</summary>
    Declined,

    /// <summary>We tried and netsh said no, or elevation was not available at all.</summary>
    Failed,

    /// <summary>netsh itself could not be run.</summary>
    Unavailable,
}

public readonly record struct FirewallResult(FirewallOutcome Outcome, string Detail);

/// <summary>
/// The inbound rule that lets the PS5 reach this PC on port 8000.
///
/// Cam's rules, implemented literally:
/// <list type="bullet">
///   <item>Added once, never twice. Every attempt checks first; netsh would happily create a
///   duplicate rule with the same name, so "add" is always "check, then add if missing".</item>
///   <item>Pyre runs asInvoker (no always-elevate manifest). Elevation is requested only for
///   that single action, by relaunching this exe once with <c>--add-firewall-rule</c>.</item>
///   <item>Declining UAC degrades gracefully: hosting still runs, the GUI says the rule was
///   not added and why, and it is never asked again in that session.</item>
/// </list>
/// </summary>
internal static class FirewallManager
{
    public const string RuleName = "Pyre HTTP 8000";

    private static readonly string NetshPath =
        Path.Combine(Environment.SystemDirectory, "netsh.exe");

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Read-only check. No elevation, no prompt, no side effects.
    ///
    /// The output is matched on the rule NAME - never on netsh's own prose - because the
    /// "no rules match" sentence is localised and on a non-English Windows it is not English.
    /// The rule name we grep for is pure ASCII, so it survives any console code page.
    /// </summary>
    public static FirewallResult Check()
    {
        var (exitCode, stdout, stderr) = RunNetsh(
            $"advfirewall firewall show rule name=\"{RuleName}\"");

        if (exitCode == -1)
        {
            return new FirewallResult(FirewallOutcome.Unavailable, stderr);
        }

        var exists = stdout.Contains(RuleName, StringComparison.OrdinalIgnoreCase);

        return exists
            ? new FirewallResult(FirewallOutcome.Exists, $"inbound rule '{RuleName}' is present")
            : new FirewallResult(FirewallOutcome.Failed, $"no rule named '{RuleName}' yet");
    }

    public static bool RuleExists() => Check().Outcome == FirewallOutcome.Exists;

    /// <summary>Creates the rule. Needs Administrator. Callers must check first.</summary>
    public static FirewallResult Add()
    {
        if (!IsElevated)
        {
            return new FirewallResult(
                FirewallOutcome.Failed,
                "not running as Administrator, so netsh cannot add the rule");
        }

        var (exitCode, _, stderr) = RunNetsh(
            "advfirewall firewall add rule " +
            $"name=\"{RuleName}\" " +
            "dir=in action=allow protocol=TCP localport=8000 profile=private enable=yes");

        if (exitCode == 0)
        {
            return new FirewallResult(FirewallOutcome.Added, "inbound TCP 8000 allowed on private networks");
        }

        return new FirewallResult(
            FirewallOutcome.Failed,
            $"netsh exited {exitCode.ToString(CultureInfo.InvariantCulture)}: {Trim(stderr)}");
    }

    /// <summary>
    /// Check, and only add if it is missing. Safe to call repeatedly: the second call finds
    /// the rule and returns <see cref="FirewallOutcome.Exists"/> without touching the firewall.
    /// </summary>
    public static FirewallResult AddIfMissing()
    {
        var check = Check();
        if (check.Outcome is FirewallOutcome.Exists)
        {
            return check;
        }

        if (check.Outcome is FirewallOutcome.Unavailable)
        {
            return check;
        }

        return Add();
    }

    /// <summary>
    /// Every inbound rule the bootstrap needs. 8000 serves the site; 53 answers the console's
    /// DNS; 443 is the guide's TLS listener. All three are required for the "point your
    /// console's DNS at this PC" flow - with 53 but not 443 the console reaches the PC and
    /// then gets connection-refused, which looks like a broken guide rather than a missing rule.
    /// </summary>
    private static readonly (string Name, string Protocol, int Port)[] Rules =
    {
        (RuleName, "TCP", 8000),
        ("Pyre HTTP 80", "TCP", 80),
        ("Pyre DNS 53 UDP", "UDP", 53),
        ("Pyre DNS 53 TCP", "TCP", 53),
        ("Pyre HTTPS 443", "TCP", 443),
    };

    public static IReadOnlyList<(string Name, string Protocol, int Port)> RequiredRules => Rules;

    /// <summary>Name-only check for one rule. Matched on the name, never on netsh's prose,
    /// because that sentence is localised and this name is pure ASCII.</summary>
    private static bool RuleExistsByName(string name)
    {
        var (exitCode, stdout, _) = RunNetsh($"advfirewall firewall show rule name=\"{name}\"");
        return exitCode != -1 && stdout.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private static FirewallResult AddOne(string name, string protocol, int port)
    {
        if (!IsElevated)
        {
            return new FirewallResult(FirewallOutcome.Failed, "not running as Administrator");
        }

        var (exitCode, _, stderr) = RunNetsh(
            "advfirewall firewall add rule " +
            $"name=\"{name}\" " +
            $"dir=in action=allow protocol={protocol} localport={port} profile=private enable=yes");

        return exitCode == 0
            ? new FirewallResult(FirewallOutcome.Added, $"inbound {protocol} {port} allowed")
            : new FirewallResult(
                FirewallOutcome.Failed,
                $"netsh exited {exitCode.ToString(CultureInfo.InvariantCulture)}: {Trim(stderr)}");
    }

    /// <summary>
    /// Check-and-add EVERY rule, each checked by name first, so this is safe to call on every
    /// start and can never create a duplicate.
    /// </summary>
    public static FirewallResult AddAllIfMissing()
    {
        var added = 0;
        var present = 0;
        var problems = new List<string>();

        foreach (var rule in Rules)
        {
            if (RuleExistsByName(rule.Name))
            {
                present++;
                continue;
            }

            var result = AddOne(rule.Name, rule.Protocol, rule.Port);
            if (result.Outcome == FirewallOutcome.Added)
            {
                added++;
            }
            else
            {
                problems.Add($"{rule.Name}: {result.Detail}");
            }
        }

        if (problems.Count > 0)
        {
            return new FirewallResult(FirewallOutcome.Failed, string.Join("; ", problems));
        }

        return added > 0
            ? new FirewallResult(FirewallOutcome.Added, $"added {added}, {present} already present")
            : new FirewallResult(FirewallOutcome.Exists, $"all {present} already present");
    }

    /// <summary>Report each rule's presence without changing anything.</summary>
    public static IReadOnlyList<string> MissingRules()
    {
        var missing = new List<string>();
        foreach (var rule in Rules)
        {
            if (!RuleExistsByName(rule.Name))
            {
                missing.Add($"{rule.Name} ({rule.Protocol} {rule.Port})");
            }
        }

        return missing;
    }

    /// <summary>
    /// The one-action elevation. If we are already elevated we just add it; otherwise this
    /// relaunches Pyre.exe with <c>--add-firewall-rule</c> and the "runas" verb, waits for it,
    /// and reports what happened. Declining the prompt is a normal outcome, not an error.
    /// </summary>
    public static FirewallResult EnsureRule()
    {
        // Check EVERY rule, not just the site's. Checking only 8000 was a real bug: once that
        // rule existed from an earlier run this returned "exists" immediately, so the DNS and
        // HTTPS rules were never added. The console could then reach the site but could not
        // resolve anything - which presents as "no internet and no guide" with nothing on
        // screen to say a firewall rule was the reason.
        var missing = MissingRules();
        if (missing.Count == 0)
        {
            return new FirewallResult(FirewallOutcome.Exists, $"all {Rules.Length} inbound rules are present");
        }

        var check = Check();
        if (check.Outcome == FirewallOutcome.Unavailable)
        {
            return check;
        }

        if (IsElevated)
        {
            return AddAllIfMissing();
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return new FirewallResult(FirewallOutcome.Failed, "could not locate Pyre.exe to elevate");
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = CommandLineSwitches.AddFirewallRule,
                UseShellExecute = true,
                Verb = "runas",
            };

            using var elevated = Process.Start(psi);
            if (elevated is null)
            {
                return new FirewallResult(FirewallOutcome.Failed, "the elevated helper did not start");
            }

            if (!elevated.WaitForExit(120_000))
            {
                try
                {
                    elevated.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // ignore
                }

                return new FirewallResult(FirewallOutcome.Failed, "the elevated helper timed out");
            }

            // Believe the firewall, not the exit code: ask again which rules are still missing.
            var stillMissing = MissingRules();
            if (stillMissing.Count == 0)
            {
                return new FirewallResult(
                    FirewallOutcome.Added,
                    $"all {Rules.Length} inbound rules allowed on private networks");
            }

            return new FirewallResult(
                FirewallOutcome.Failed,
                $"still missing after elevation: {string.Join(", ", stillMissing)} (helper exit code {elevated.ExitCode.ToString(CultureInfo.InvariantCulture)})");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - the user pressed No on the UAC dialog.
            return new FirewallResult(FirewallOutcome.Declined, "the Windows elevation prompt was declined");
        }
        catch (Win32Exception ex)
        {
            return new FirewallResult(FirewallOutcome.Failed, $"could not elevate: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new FirewallResult(FirewallOutcome.Failed, $"could not elevate: {ex.Message}");
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(NetshPath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Byte-preserving: we only need to grep ASCII out of it.
                StandardOutputEncoding = Encoding.Latin1,
                StandardErrorEncoding = Encoding.Latin1,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "netsh did not start");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // ignore
                }

                return (-1, string.Empty, "netsh did not answer in time");
            }

            return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Win32Exception ex)
        {
            return (-1, string.Empty, $"netsh could not be run: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return (-1, string.Empty, $"netsh could not be run: {ex.Message}");
        }
    }

    private static string Trim(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > 200 ? single[..200] : single;
    }
}
