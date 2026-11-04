using System;
using System.Reflection;

namespace SlopeX.Modules.ModBase
{
    /// <summary>
    /// 启动器版本号。唯一来源是 <c>SlopeX.csproj</c> 里的 <c>&lt;Version&gt;</c>，编译时写进程序集；
    /// 界面、CLI 与启动参数都从这里取，不再各自写死一份。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>三段式版本，例如 <c>1.0.0</c>（不带第四位修订号，也不带 <c>+commit</c> 后缀）。</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>带 v 前缀的展示形式，例如 <c>v1.0.0</c>。</summary>
        public static string DisplayVersion => "v" + Version;

        private static string ReadVersion()
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            if (v == null) return "0.0.0";
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }
}
