using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModCli;
using SlopeX.Windows;

namespace SlopeX
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- 无界面模式 ----
            if (e.Args.Any(a => string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase)))
            {
                // 注意：必须在后台线程执行，否则 async 续体会被派回已成阻塞状态的 UI 调度器而死锁
                var code = System.Threading.Tasks.Task.Run(() => Cli.Run(e.Args)).GetAwaiter().GetResult();
                Shutdown(code);
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                Log.Error("未捕获异常", args.ExceptionObject as Exception);

            Log.Info("SlopeX Launcher 启动");
            Log.Info($"游戏目录：{Config.Current.GameDir}");

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面线程异常", e.Exception);
            try
            {
                MessageBox.Show(e.Exception.Message, "SlopeX Launcher 出现了一个错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* ignored */ }
            e.Handled = true;
        }
    }
}
