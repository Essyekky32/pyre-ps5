using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pyre.Cli;

/// <summary>
/// Output for the headless modes. Pyre.exe is a windowed exe, so it has no console of its
/// own: we attach to whatever console launched us (if any) and always mirror everything to
/// the --log file when one was given. That is what makes these modes verifiable.
/// </summary>
internal static class CliLog
{
    private static readonly object Gate = new();
    private static string? _file;
    private static bool _attached;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    public static void AttachToParentConsole()
    {
        if (_attached)
        {
            return;
        }

        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return;
            }

            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(stdout);

            var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
            Console.SetError(stderr);

            _attached = true;
        }
        catch (Exception)
        {
            // No console: the log file (if any) still gets everything.
        }
    }

    public static void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _file = full;
            File.AppendAllText(full, $"--- pyre {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}");
        }
        catch (Exception)
        {
            _file = null;
        }
    }

    public static void Line(string text)
    {
        lock (Gate)
        {
            try
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
            }
            catch (Exception)
            {
                // no console attached
            }

            if (_file is not null)
            {
                try
                {
                    File.AppendAllText(_file, text + Environment.NewLine);
                }
                catch (Exception)
                {
                    // a log write must never break the run
                }
            }
        }
    }
}
