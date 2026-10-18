using System.Windows;

namespace Pyre;

/// <summary>
/// The application object exists only to hold the palette/theme in App.xaml.
/// Startup lives in <see cref="Program"/> so that the headless modes never build a window.
/// </summary>
public partial class App : Application
{
}
