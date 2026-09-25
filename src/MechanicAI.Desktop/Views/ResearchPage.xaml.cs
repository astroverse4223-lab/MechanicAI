using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MechanicAI.Desktop.Views;

public sealed partial class ResearchPage : Page, IViewModelPage
{
    public ResearchPage()
    {
        ViewModel = App.GetService<ResearchViewModel>();
        InitializeComponent();
    }

    public ResearchViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.SearchCommand.CanExecute(null)) return;
        e.Handled = true;
        ViewModel.SearchCommand.Execute(null);
    }

    private void OnOpenSource(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SourceItem source) ViewModel.OpenSourceCommand.Execute(source.Url);
    }

    private void OnOpenCitation(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CitationItem citation) ViewModel.OpenSourceCommand.Execute(citation.Url);
    }

    private void OnBookmarkSource(object sender, RoutedEventArgs e) =>
        ViewModel.BookmarkCommand.Execute((sender as FrameworkElement)?.DataContext as SourceItem);
}
