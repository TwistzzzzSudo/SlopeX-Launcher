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
            Log.Info($"数据目录：{Paths.DataDir}");
            Log.Info($"Java 运行时目录：{Paths.RuntimeDir}");
            Log.Info($"游戏目录：{Config.Current.GameDir}");

            // 主题要在建窗口之前定下来，避免先白后黑的闪烁
            SlopeX.Themes.ThemeService.Initialize();
            Log.Info($"外观：{Config.Current.Appearance}（当前{(SlopeX.Themes.ThemeService.IsDark ? "深色 极简墨纸" : "浅色 极简白纸")}）");

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
