using MechanicAI.Presentation.Abstractions;
using Microsoft.UI.Dispatching;

namespace MechanicAI.Desktop.Services;

/// <summary>Marshals view-model updates onto the window's UI thread.</summary>
public sealed class DispatcherService : IDispatcher
{
    private DispatcherQueue? _queue;

    public void Attach(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue?.HasThreadAccess ?? false;

    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var queue = _queue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}
