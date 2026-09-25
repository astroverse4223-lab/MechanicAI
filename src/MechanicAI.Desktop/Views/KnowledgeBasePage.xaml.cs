using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class KnowledgeBasePage : Page, IViewModelPage
{
    public KnowledgeBasePage()
    {
        ViewModel = App.GetService<KnowledgeBaseViewModel>();
        InitializeComponent();
    }

    public KnowledgeBaseViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnFilterSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchText = args.QueryText;
        ViewModel.RefreshCommand.Execute(null);
    }
}
