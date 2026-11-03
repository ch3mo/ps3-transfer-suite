using System.Windows.Threading;
using PS3Transfer.Core;

namespace PS3Transfer.App.Mvvm;

public sealed class UiProgress : IProgress<TransferProgress>
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<TransferProgress> _handler;

    public UiProgress(Dispatcher dispatcher, Action<TransferProgress> handler)
    {
        _dispatcher = dispatcher;
        _handler = handler;
    }

    public void Report(TransferProgress value)
    {
        if (_dispatcher.CheckAccess())
            _handler(value);
        else
            _dispatcher.Invoke(() => _handler(value));
    }
}
