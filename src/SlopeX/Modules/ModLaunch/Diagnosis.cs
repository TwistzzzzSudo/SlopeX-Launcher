using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModLaunch
{
    /// <summary>诊断结果：一句话结论 + 可操作说明 + 可选动作。</summary>
    public sealed class DiagnosisResult
    {
        public bool Matched { get; set; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";

        /// <summary>建议的下一步动作标识：memory / java / mods / account / log。</summary>
        public string Action { get; set; } = "";

        public string Text => string.IsNullOrEmpty(Detail) ? Title : Title + "\n" + Detail;
    }

    /// <summary>
    /// 启动失败诊断。
    ///
    /// 目的很实际：Minecraft 崩溃时抛的是 Java 堆栈，普通用户看不出该怎么办。
    /// 这里只把日志里**有明确特征**的几类问题翻成人话，并给出下一步动作；
    /// 认不出来就老实说认不出来，同时把日志/崩溃报告位置给出来——不编原因。
    ///
    /// 规则顺序从具体到宽泛：内存、Java 版本、缺依赖这类有专有异常名的排前面，
    /// 宽泛的类名匹配（NoClassDefFoundError 等）排后面，避免误判。
    /// </summary>
    public static class Diagnosis
    {
        private static readonly (string Pattern, string Title, string Detail, string Action)[] Rules =
        {
            (@"java\.lang\.OutOfMemoryError|Java heap space|GC overhead limit exceeded",
                "内存不够，游戏被强制结束了",
                "把「设置 → 内存」调大一些（整合包一般建议 6–8 GB），或关掉一些后台程序再试。",
                "memory"),

            (@"UnsupportedClassVersionError|has been compiled by a more recent version",
                "Java 版本太低",
                "这个版本要求的 Java 比当前使用的高。到「设置 → Java 运行时」换更高的版本，或打开自动安装。",
                "java"),

            (@"MissingDependencyException|Missing or unsupported mandatory dependencies|" +
             @"which is missing but required|requires .+ which is missing",
                "缺少前置模组",
                "整合包或某个模组缺依赖。在「模组管理」里对照日志提到的模组名，从 Modrinth 搜索安装。",
                "mods"),

            (@"DuplicateModsFoundException|Duplicate mods found|Found a duplicate mod",
                "装了两个同名模组",
                "同一个模组存在多个 jar（常见于更新时没删旧的）。在「模组管理」里删掉旧的那个。",
                "mods"),

            (@"MixinApplyError|Mixin apply failed|Mixin transformation of .+ failed|" +
             @"mixin\.injection\.throwables",
                "模组之间冲突（Mixin 注入失败）",
                "报错的几个模组互相不兼容，或与游戏版本不匹配。逐个禁用最近新装的模组来定位。",
                "mods"),

            (@"NoSuchMethodError|NoClassDefFoundError|AbstractMethodError|IncompatibleClassChangeError",
                "模组与游戏版本不匹配",
                "某个模组是按别的 Minecraft 版本编译的。检查「模组管理」里是否有明显跨版本的模组。",
                "mods"),

            (@"Failed to verify username|Invalid session|AuthenticationException|" +
             @"requires a premium account|premium account|not authenticated with Minecraft",
                "账号验证没通过",
                "这个整合包要求正版账号。SlopeX 的微软登录还在等 Mojang 白名单审核，" +
                "审核通过前，只能用离线账号玩不校验正版的整合包。",
                "account"),

            (@"GLFW error|Failed to create window|Pixel format not accelerated|" +
             @"WGL: The driver does not appear to support OpenGL",
                "显卡驱动或 OpenGL 有问题",
                "更新显卡驱动；笔记本还要确认游戏用的是独立显卡而不是核显。",
                "log"),

            (@"Address already in use|Failed to bind to port",
                "端口被占用",
                "可能上一个游戏进程还没退干净。在任务管理器结束残留的 java.exe 再试。",
                "log"),
        };

        /// <summary>
        /// 分析一次启动失败。
        /// <paramref name="recentOutput"/> 是启动器捕获的实时输出（可能为空），
        /// 另外会读版本目录下的 latest.log 与最新的 crash-report。
        /// </summary>
        public static DiagnosisResult Analyze(string versionDir, string recentOutput, int exitCode,
            TimeSpan ran, bool userStopped = false)
        {
            if (userStopped)
                return new DiagnosisResult { Matched = false, Title = "已结束游戏" };

            var text = BuildEvidence(versionDir, recentOutput);

            foreach (var (pattern, title, detail, action) in Rules)
            {
                bool hit;
                try
                {
                    hit = Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    continue;   // 规则写错不该让诊断整个失效
                }

                if (!hit) continue;

                Log.Info($"启动失败诊断命中：{title}");
                return new DiagnosisResult { Matched = true, Title = title, Detail = detail, Action = action };
            }

            // 没命中就不猜。给出可核查的信息，让用户能自己往下走。
            var report = NewestCrashReport(versionDir);

            return new DiagnosisResult
            {
                Matched = false,
                Title = $"游戏异常退出（退出码 {exitCode}，运行了 {ran.TotalSeconds:0} 秒）",
                Detail = report != null
                    ? "崩溃报告：" + report + "\n可把这个文件发出来，或到「任务与日志」页看完整输出。"
                    : "没有生成崩溃报告，通常在加载早期就被中断（可能是整合包自带的检查或原生崩溃）。\n" +
                      "可到「任务与日志」页看最近的输出。",
                Action = "log"
            };
        }

        private static string BuildEvidence(string versionDir, string recentOutput)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(recentOutput))
                parts.Add(recentOutput);

            var latestLog = Path.Combine(versionDir, "logs", "latest.log");
            parts.Add(TailFile(latestLog, 400));

            var report = NewestCrashReport(versionDir);
            if (report != null)
                parts.Add(TailFile(report, 400));

            return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        /// <summary>读文件末尾若干行；文件不存在或读不了就返回空，不影响诊断。</summary>
        private static string TailFile(string path, int lines)
        {
            try
            {
                if (!File.Exists(path)) return "";

                // 日志可能几十 MB，用队列按行滑窗，避免整份读进内存
                var window = new Queue<string>(lines);
                using var reader = new StreamReader(path);
                while (reader.ReadLine() is { } line)
                {
                    if (window.Count == lines) window.Dequeue();
                    window.Enqueue(line);
                }

                return string.Join("\n", window);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取日志失败 {path}：{ex.Message}");
                return "";
            }
        }

        /// <summary>最新一个崩溃报告（文件名形如 crash-2026-10-30_23.43.21-client.txt）。</summary>
        public static string NewestCrashReport(string versionDir)
        {
            try
            {
                var dir = Path.Combine(versionDir, "crash-reports");
                if (!Directory.Exists(dir)) return null;

                return Directory.EnumerateFiles(dir, "*.txt")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
