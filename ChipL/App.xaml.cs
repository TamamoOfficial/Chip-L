using System.Windows;
using System.Windows.Threading;

namespace ChipL;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 命令行自检：ChipL.exe --selftest [--update]
        // 在无界面干预下验证数据库解密、解析与热更新全链路。
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code;
            try
            {
                code = await SelfTest.RunAsync(e.Args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("自检异常: " + ex);
                code = 2;
            }
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ShowFatal(ex);
        };

        new MainWindow().Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            "程序遇到未处理的错误：\n\n" + ex.Message + "\n\n" + ex.GetType().Name,
            "Chip-L 桌面版", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
