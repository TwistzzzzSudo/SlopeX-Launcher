using System;
using System.Reflection;

namespace SlopeX.Modules.ModBase
{
    /// <summary>
    /// 启动器版本号。唯一来源是 <c>SlopeX.csproj</c>：<c>&lt;InformationalVersion&gt;</c> 管展示，
    /// <c>&lt;Version&gt;</c> 管程序集版本（NuGet 只认合法 semver，带后缀的写法只能放前者）；
    /// 编译时写进程序集，界面、CLI 与启动参数都从这里取，不再各自写死一份。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>展示用版本，例如 <c>1.0.0</c> 或 <c>1.0.0e</c>（不含 <c>+commit</c> 元数据）。</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>带 v 前缀的展示形式，例如 <c>v1.0.0e</c>。</summary>
        public static string DisplayVersion => "v" + Version;

        private static string ReadVersion()
        {
            // InformationalVersion 可能被 SDK 追加 "+<提交号>"，展示时要截掉
            var info = typeof(AppInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return (plus > 0 ? info.Substring(0, plus) : info).Trim();
            }

            var v = typeof(AppInfo).Assembly.GetName().Version;
            if (v == null) return "0.0.0";
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }
}
