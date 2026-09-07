using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CropPipViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            SettingsService.Log("fatal_dispatcher | " + args.Exception);
            MessageBox.Show(args.Exception.ToString(), "CropPipViewer 실행 오류");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            SettingsService.Log("fatal_appdomain | " + args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SettingsService.Log("fatal_task | " + args.Exception);
            args.SetObserved();
        };

        try
        {
            SettingsService.Log("app_start | v03 | base=" + AppContext.BaseDirectory);
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
            SettingsService.Log("main_window_shown | v03");
        }
        catch (Exception ex)
        {
            SettingsService.Log("fatal_startup | " + ex);
            MessageBox.Show(ex.ToString(), "CropPipViewer 시작 실패");
            Shutdown(1);
        }
    }
}
