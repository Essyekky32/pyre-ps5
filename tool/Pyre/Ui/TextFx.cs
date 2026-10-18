using System.Text;

namespace Pyre.Ui;

/// <summary>One numbered step in the how-to card.</summary>
public sealed record InstructionItem(string Number, string Text)
{
    /// <summary>
    /// Without this, screen readers (and UI Automation) announce the record's generated
    /// ToString() - "InstructionItem { Number = 01, Text = ... }" - instead of the step.
    /// </summary>
    public override string ToString() => Text;
}

internal static class TextFx
{
    /// <summary>
    /// The site's titles are letter-spaced (haze-menu.css: .haze-title{letter-spacing:.22em}).
    /// WPF's TextBlock has no letter-spacing, so the tracking is faked with thin spaces.
    /// Presentation only - nothing here changes a string in the language table.
    /// </summary>
    public static string Spaced(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length * 2);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\u2009');
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }
}
