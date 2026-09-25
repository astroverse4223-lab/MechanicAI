using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class WiringPage : Page, IViewModelPage
{
    public WiringPage()
    {
        ViewModel = App.GetService<WiringViewModel>();
        InitializeComponent();
    }

    public WiringViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnQuickQuestion(object sender, RoutedEventArgs e) =>
        ViewModel.AskQuickCommand.Execute((sender as FrameworkElement)?.DataContext as string);
}
