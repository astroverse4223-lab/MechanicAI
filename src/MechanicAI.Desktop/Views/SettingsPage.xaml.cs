using MechanicAI.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MechanicAI.Desktop.Views;

public sealed partial class SettingsPage : Page, IViewModelPage
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    ViewModelBase IViewModelPage.ViewModel => ViewModel;

    private void OnClearAnthropicKey(object sender, RoutedEventArgs e) => ViewModel.ClearKeyCommand.Execute("anthropic");

    private void OnClearOpenAiKey(object sender, RoutedEventArgs e) => ViewModel.ClearKeyCommand.Execute("openai");

    private void OnClearSearchKey(object sender, RoutedEventArgs e) => ViewModel.ClearKeyCommand.Execute("search");
}
