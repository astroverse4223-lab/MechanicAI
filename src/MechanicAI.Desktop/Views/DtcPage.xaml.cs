using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MechanicAI.Desktop.Views;

public sealed partial class DtcPage : Page, IViewModelPage
{
    public DtcPage()
    {
        ViewModel = App.GetService<DtcLookupViewModel>();
        InitializeComponent();
    }

    public DtcLookupViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.SearchCommand.CanExecute(null)) return;
        e.Handled = true;
        ViewModel.SearchCommand.Execute(null);
    }

    private void OnRelatedClick(object sender, ItemClickEventArgs e) => ViewModel.OpenRelatedCommand.Execute(e.ClickedItem as string);
}
