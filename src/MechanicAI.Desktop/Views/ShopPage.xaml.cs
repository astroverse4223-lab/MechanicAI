using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class ShopPage : Page, IViewModelPage
{
    public ShopPage()
    {
        ViewModel = App.GetService<ShopViewModel>();
        InitializeComponent();
    }

    public ShopViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnEstimateClick(object sender, ItemClickEventArgs e) => ViewModel.EditEstimateCommand.Execute(e.ClickedItem as EstimateItem);

    private void OnDeleteEstimate(object sender, RoutedEventArgs e) =>
        ViewModel.DeleteEstimateCommand.Execute((sender as FrameworkElement)?.DataContext as EstimateItem);

    private void OnRemoveLine(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveLineCommand.Execute((sender as FrameworkElement)?.DataContext as EstimateLineItem);

    private void OnCustomerClick(object sender, ItemClickEventArgs e) => ViewModel.EditCustomerCommand.Execute(e.ClickedItem as CustomerItem);

    private void OnDeleteCustomer(object sender, RoutedEventArgs e) => ViewModel.DeleteCustomerCommand.Execute(ViewModel.SelectedCustomer);

    private void OnCustomerSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.CustomerSearch = args.QueryText;
        ViewModel.SearchCustomersCommand.Execute(null);
    }
}
