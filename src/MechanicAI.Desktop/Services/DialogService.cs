using MechanicAI.Presentation.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Services;

/// <summary>ContentDialog-based dialogs. Only one ContentDialog may be open at a time, so calls are serialized.</summary>
public sealed class DialogService : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool destructive = false)
    {
        var dialog = Create(title);
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = confirmText;
        dialog.CloseButtonText = cancelText;
        dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = Create(title);
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    public async Task<string?> PromptAsync(string title, string label, string? initialText = null, string confirmText = "OK")
    {
        var input = new TextBox
        {
            Header = label,
            Text = initialText ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
            MaxHeight = 200,
        };
        var dialog = Create(title);
        dialog.Content = input;
        dialog.PrimaryButtonText = confirmText;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? input.Text : null;
    }

    private static ContentDialog Create(string title)
    {
        var root = App.MainWindow?.Content as FrameworkElement
                   ?? throw new InvalidOperationException("The main window is not ready.");
        return new ContentDialog
        {
            Title = title,
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"],
        };
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
