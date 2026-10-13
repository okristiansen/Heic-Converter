using System.Windows;
using HeicConverter.Services;
using HeicConverter.ViewModels;

namespace HeicConverter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainViewModel(SettingsStore.Load());

        // Files passed on the command line, e.g. dragged onto the .exe or sent via "Send to".
        if (e.Args.Length > 0)
            viewModel.AddPaths(e.Args);

        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }
}
