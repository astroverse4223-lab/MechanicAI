using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class HistoryPage : Page, IViewModelPage
{
    public HistoryPage()
    {
        ViewModel = App.GetService<HistoryViewModel>();
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnEntryClick(object sender, ItemClickEventArgs e) => ViewModel.OpenEntryCommand.Execute(e.ClickedItem as HistoryEntryItem);

    private void OnDiagnosisClick(object sender, ItemClickEventArgs e) => ViewModel.OpenSessionCommand.Execute(e.ClickedItem as ConfirmedDiagnosisItem);
}
