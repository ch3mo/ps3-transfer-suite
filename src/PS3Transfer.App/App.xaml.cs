using System.Windows;
using PS3Transfer.App.Dialogs;
using PS3Transfer.App.Services;

namespace PS3Transfer.App;

public partial class App : Application
{
    private int _reporting;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            AppLog.Write(args.Exception.ToString());
            if (Interlocked.Exchange(ref _reporting, 1) == 1)
                return;
            try
            {
                Prompt.Show(Current.MainWindow, "Something went wrong", args.Exception.Message, "OK", null);
            }
            finally
            {
                Interlocked.Exchange(ref _reporting, 0);
            }
        };
        base.OnStartup(e);
    }
}
