using MechanicAI.Presentation.ViewModels;

namespace MechanicAI.Desktop.Views;

/// <summary>Implemented by every page so navigation can forward parameters to its view model.</summary>
public interface IViewModelPage
{
    ViewModelBase ViewModel { get; }
}
