using System.Windows;
using System.Windows.Input;

namespace NetControl.App.Appearance;

/// <summary>
/// What the caption buttons in <c>ChromeWindow</c>'s title bar do. Registered once, on every
/// Window class, so the shared template can bind to the standard <see cref="SystemCommands"/> and
/// no window needs code-behind for its own minimise, maximise and close.
/// </summary>
public static class WindowChromeCommands
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        Bind(SystemCommands.MinimizeWindowCommand, SystemCommands.MinimizeWindow, w => w.ResizeMode != ResizeMode.NoResize);
        Bind(SystemCommands.MaximizeWindowCommand, SystemCommands.MaximizeWindow, w => w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
        Bind(SystemCommands.RestoreWindowCommand, SystemCommands.RestoreWindow, w => w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
        Bind(SystemCommands.CloseWindowCommand, SystemCommands.CloseWindow, _ => true);
    }

    private static void Bind(RoutedCommand command, Action<Window> act, Func<Window, bool> allowed) =>
        CommandManager.RegisterClassCommandBinding(
            typeof(Window),
            new CommandBinding(
                command,
                (sender, _) =>
                {
                    if (sender is Window window)
                    {
                        act(window);
                    }
                },
                (sender, e) => e.CanExecute = sender is Window window && allowed(window)));
}
