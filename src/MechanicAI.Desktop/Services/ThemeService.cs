using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Settings;
using MechanicAI.Presentation.Abstractions;
using Microsoft.UI.Xaml;

namespace MechanicAI.Desktop.Services;

/// <summary>Applies the Appearance theme setting to the main window and follows changes.</summary>
public sealed class ThemeService(ISettingsStore settings, IDispatcher dispatcher)
{
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        Apply(settings.Current.Appearance.Theme);
        settings.Changed += (_, s) => dispatcher.Run(() => Apply(s.Appearance.Theme));
    }

    private static void Apply(AppTheme theme)
    {
        if (App.MainWindow?.Content is not FrameworkElement root) return;
        root.RequestedTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        App.MainWindow.UpdateTitleBarTheme();
    }
}
