using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class VehiclesPage : Page, IViewModelPage
{
    public VehiclesPage()
    {
        ViewModel = App.GetService<VehiclesViewModel>();
        InitializeComponent();
    }

    public VehiclesViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchText = args.QueryText;
        ViewModel.SearchCommand.Execute(null);
    }
}
