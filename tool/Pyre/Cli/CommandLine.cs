namespace Pyre.Cli;

internal static class CommandLineSwitches
{
    public const string Serve = "--serve";
    public const string Headless = "--headless";
    public const string SelfTest = "--selftest";
    public const string DnsSelfTest = "--dns-selftest";
    public const string AddFirewallRule = "--add-firewall-rule";
    public const string CheckFirewall = "--check-firewall";
    public const string Help = "--help";
    public const string NoFirewall = "--no-firewall";
    public const string Root = "--root";
    public const string Port = "--port";
    public const string Log = "--log";
    public const string Duration = "--duration";
}

internal enum CliMode
{
    Gui,
    Serve,
    SelfTest,
    DnsSelfTest,
    AddFirewallRule,
    CheckFirewall,
    Help,
}

/// <summary>
/// Argument parsing for the whole exe. Anything unrecognised leaves the tool in GUI mode -
/// a bad argument must never stop Cam from opening the window.
/// </summary>
internal sealed class ParsedArgs
{
    public CliMode Mode { get; private set; } = CliMode.Gui;

    public string? Root { get; private set; }

    public int Port { get; private set; } = Server.PyreWebServer.DefaultPort;

    public bool PortSpecified { get; private set; }

    public string? LogFile { get; private set; }

    public int DurationSeconds { get; private set; }

    public bool NoFirewall { get; private set; }

    public static ParsedArgs Parse(string[] args)
    {
        var parsed = new ParsedArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var (name, inlineValue) = SplitInline(arg);

            switch (name.ToLowerInvariant())
            {
                case CommandLineSwitches.Serve:
                case CommandLineSwitches.Headless:
                    parsed.Mode = CliMode.Serve;
                    break;

                case CommandLineSwitches.SelfTest:
                    parsed.Mode = CliMode.SelfTest;
                    break;

                case CommandLineSwitches.DnsSelfTest:
                    parsed.Mode = CliMode.DnsSelfTest;
                    break;

                case CommandLineSwitches.AddFirewallRule:
                    parsed.Mode = CliMode.AddFirewallRule;
                    break;

                case CommandLineSwitches.CheckFirewall:
                    parsed.Mode = CliMode.CheckFirewall;
                    break;

                case CommandLineSwitches.Help:
                case "-h":
                case "/?":
                    parsed.Mode = CliMode.Help;
                    break;

                case CommandLineSwitches.NoFirewall:
                    parsed.NoFirewall = true;
                    break;

                case CommandLineSwitches.Root:
                    parsed.Root = TakeValue(args, ref i, inlineValue);
                    break;

                case CommandLineSwitches.Port:
                    if (int.TryParse(TakeValue(args, ref i, inlineValue), out var port) && port is > 0 and <= 65535)
                    {
                        parsed.Port = port;
                        parsed.PortSpecified = true;
                    }

                    break;

                case CommandLineSwitches.Log:
                    parsed.LogFile = TakeValue(args, ref i, inlineValue);
                    break;

                case CommandLineSwitches.Duration:
                    if (int.TryParse(TakeValue(args, ref i, inlineValue), out var seconds) && seconds >= 0)
                    {
                        parsed.DurationSeconds = seconds;
                    }

                    break;
            }
        }

        return parsed;
    }

    private static (string Name, string? InlineValue) SplitInline(string arg)
    {
        var equals = arg.IndexOf('=');
        return equals > 0
            ? (arg[..equals], arg[(equals + 1)..])
            : (arg, null);
    }

    private static string? TakeValue(string[] args, ref int index, string? inlineValue)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }

        if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
        {
            index++;
            return args[index];
        }

        return null;
    }
}
