using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class LiveDataPage : Page, IViewModelPage
{
    public LiveDataPage()
    {
        ViewModel = App.GetService<LiveDataViewModel>();
        InitializeComponent();
    }

    public LiveDataViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;
}
