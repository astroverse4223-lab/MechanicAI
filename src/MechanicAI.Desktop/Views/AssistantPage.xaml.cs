using System.Collections.Specialized;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace MechanicAI.Desktop.Views;

public sealed partial class AssistantPage : Page, IViewModelPage
{
    public AssistantPage()
    {
        ViewModel = App.GetService<AssistantViewModel>();
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
    }

    public AssistantViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Messages.Count > 0) MessageList.ScrollIntoView(ViewModel.Messages[^1]);
    }

    /// <summary>Enter sends; Shift+Enter inserts a new line.</summary>
    private void OnDraftPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (shift) return;
        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null)) ViewModel.SendCommand.Execute(null);
    }

    private void OnCopyMessage(object sender, RoutedEventArgs e) =>
        ViewModel.CopyMessageCommand.Execute((sender as FrameworkElement)?.DataContext as ChatMessageItem);
}
