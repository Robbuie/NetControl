using Microsoft.Win32;

namespace NetControl.App.Appearance;

/// <summary>
/// Windows' own light or dark app mode - File Manager's <c>themeswitch.windows_light</c> - and a
/// way to hear when it changes.
///
/// <para>The read is one value in the current user's hive: local, instant, nothing on the network.
/// Anything it cannot read comes back null, and null means "keep the theme the picker says", so a
/// locked-down profile that hides the key gets the chosen theme rather than an error.</para>
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True for light, false for dark, null when Windows will not say.</summary>
    public static bool? AppsUseLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Calls <paramref name="changed"/> when Windows' personalisation settings change - which is
    /// when the light/dark switch fires. Raised on a system thread; the caller marshals. Dispose
    /// the result to stop: SystemEvents is a static event, and a handler left on it is a leak that
    /// outlives every window.
    /// </summary>
    public static IDisposable Watch(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        void OnChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
            {
                changed();
            }
        }

        SystemEvents.UserPreferenceChanged += OnChanged;
        return new Subscription(() => SystemEvents.UserPreferenceChanged -= OnChanged);
    }

    private sealed class Subscription(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
