using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class TrainingPage : Page, IViewModelPage
{
    public TrainingPage()
    {
        ViewModel = App.GetService<TrainingViewModel>();
        InitializeComponent();
    }

    public TrainingViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnQuizClick(object sender, ItemClickEventArgs e) => ViewModel.StartQuizCommand.Execute(e.ClickedItem as QuizItem);
}
