using System.Windows;
using System.Windows.Threading;
using GameOp.Services;

namespace GameOp;

public partial class App : Application
{
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Warn($"Unexpected error: {e.Exception.Message}");
        System.Windows.MessageBox.Show(e.Exception.Message, "GameOp", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
