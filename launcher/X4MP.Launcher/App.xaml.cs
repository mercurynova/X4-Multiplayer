using System.Windows;

namespace X4MP.Launcher;

/// <summary>
/// The launcher's entry point. It only wires the window: everything that reads the game or the server lives in
/// X4MP.Launcher.Core, which the tests drive without a display.
/// </summary>
public partial class App : Application
{
}
