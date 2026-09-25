using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class DiagnosticsPage : Page, IViewModelPage
{
    public DiagnosticsPage()
    {
        ViewModel = App.GetService<DiagnosticsViewModel>();
        InitializeComponent();
    }

    public DiagnosticsViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    // Item-level actions: the item is the DataContext of the clicked element's template.

    private void OnTestClick(object sender, ItemClickEventArgs e) => ViewModel.SelectTestCommand.Execute(e.ClickedItem as TestItem);

    private void OnRevertTest(object sender, RoutedEventArgs e) => ViewModel.RevertTestCommand.Execute(Item<TestItem>(sender));

    private void OnConfirmCause(object sender, RoutedEventArgs e) => ViewModel.ConfirmDiagnosisCommand.Execute(Item<CauseItem>(sender));

    private void OnRuleOutCause(object sender, RoutedEventArgs e) => ViewModel.RuleOutCauseCommand.Execute(Item<CauseItem>(sender));

    private void OnReopenCause(object sender, RoutedEventArgs e) => ViewModel.ReopenCauseCommand.Execute(Item<CauseItem>(sender));

    private void OnAnswerQuestion(object sender, RoutedEventArgs e) => ViewModel.AnswerQuestionCommand.Execute(Item<QuestionItem>(sender));

    private void OnRemoveDtc(object sender, RoutedEventArgs e) => ViewModel.RemoveDtcCommand.Execute(Item<string>(sender));

    private static T? Item<T>(object sender)
        where T : class => (sender as FrameworkElement)?.DataContext as T;
}
