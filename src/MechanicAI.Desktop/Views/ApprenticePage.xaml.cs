using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class ApprenticePage : Page, IViewModelPage
{
    public ApprenticePage()
    {
        ViewModel = App.GetService<ApprenticeViewModel>();
        InitializeComponent();
    }

    public ApprenticeViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;
}
