using MechanicAI.Presentation.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Helpers;

/// <summary>
/// Static helpers for x:Bind function bindings (keeps XAML free of converter resources).
/// Usage: Visibility="{x:Bind h:Ui.VisibleIfText(ViewModel.Answer), Mode=OneWay}".
/// </summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility CollapsedIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static InfoBarSeverity Severity(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Success => InfoBarSeverity.Success,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        NoticeSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public static InfoBarSeverity WarningSeverity(bool critical) => critical ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    /// <summary>Chat bubbles: the user's messages on the right, replies on the left.</summary>
    public static HorizontalAlignment BubbleAlignment(bool isUser) => isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
