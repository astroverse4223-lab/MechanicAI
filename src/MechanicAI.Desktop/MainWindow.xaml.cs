using MechanicAI.Desktop.Services;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace MechanicAI.Desktop;

/// <summary>Shell window: Mica backdrop, custom title bar, navigation, global notices, startup and lock overlays.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        ViewModel = App.GetService<ShellViewModel>();
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1440, 920));
        UpdateTitleBarTheme();

        var navigation = App.GetService<NavigationService>();
        navigation.Attach(ContentFrame);
        navigation.Navigated += OnNavigated;
        RootGrid.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>Shows a fatal startup message over the whole window.</summary>
    public void ShowStartupFailure(string message) => ViewModel.ReportStartupFailure(message);

    /// <summary>Keeps the system caption buttons readable in light and dark themes.</summary>
    public void UpdateTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00);
    }

    private void OnNavigated(object? sender, PageKey page)
    {
        if (page == PageKey.Settings)
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        var tag = page.ToString();
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            ViewModel.NavigateCommand.Execute(PageKey.Settings);
            return;
        }

        if (args.InvokedItemContainer?.Tag is string tag && Enum.TryParse<PageKey>(tag, out var page))
        {
            ViewModel.NavigateCommand.Execute(page);
        }
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => ViewModel.GoBackCommand.Execute(null);

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) await ViewModel.UpdateSuggestionsAsync(sender.Text);
    }

    private async void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion as SearchSuggestion;
        var text = args.QueryText;
        sender.Text = string.Empty;
        await ViewModel.SubmitSearchAsync(chosen, text);
    }

    private void OnUnlockKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.UnlockCommand.CanExecute(null)) ViewModel.UnlockCommand.Execute(null);
    }
}
