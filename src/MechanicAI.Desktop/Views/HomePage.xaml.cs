using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MechanicAI.Desktop.Views;

public sealed partial class HomePage : Page, IViewModelPage
{
    public HomePage()
    {
        ViewModel = App.GetService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnSessionClick(object sender, ItemClickEventArgs e) => ViewModel.OpenSessionCommand.Execute(e.ClickedItem as SessionItem);

    private void OnVehicleClick(object sender, ItemClickEventArgs e) => ViewModel.OpenVehicleCommand.Execute(e.ClickedItem as VehicleItem);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ViewModel.GoToCommand.Execute(PageKey.Settings);

    private void OnQuickTextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.StartDiagnosisCommand.CanExecute(null)) return;
        e.Handled = true;
        ViewModel.StartDiagnosisCommand.Execute(null);
    }
}
