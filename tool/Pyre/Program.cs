using System.Windows;

namespace Pyre;

/// <summary>
/// Entry point. GUI by default; every other mode is a real, documented argument so the tool
/// can be verified and automated without anyone clicking anything.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = Cli.ParsedArgs.Parse(args);

        // Headless modes never build a WPF Application - no window, no dispatcher, real exit code.
        if (parsed.Mode != Cli.CliMode.Gui)
        {
            return Cli.CliRunner.Run(parsed);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run(new MainWindow(parsed));
    }
}
