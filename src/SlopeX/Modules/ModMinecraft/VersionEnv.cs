using System;
using System.IO;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMinecraft
{
    /// <summary>
    /// 版本隔离（<b>强制，不可关闭</b>）：每个版本固定使用 <c>versions\&lt;版本 ID&gt;</c> 作为游戏目录，
    /// mods / resourcepacks / saves / options.txt 各自独立，互不污染。
    ///
    /// 设计约束：
    /// <list type="bullet">
    /// <item>这里<b>没有</b>"共用根目录"的分支——不存在任何能把游戏目录指回根目录的路径。</item>
    /// <item>不提供自动复制、继承公共存档的行为；跨版本共用存档只能由用户自己复制文件。</item>
    /// <item>libraries 与 assets 仍共用根目录——它们与版本无关，按版本复制只会白占几百 MB。</item>
    /// </list>
    /// </summary>
    public static class VersionEnv
    {
        /// <summary>版本隔离目录下会自动创建的子文件夹。</summary>
        public static readonly string[] SubDirs =
        {
            "mods", "resourcepacks", "shaderpacks", "saves",
            "screenshots", "config", "crash-reports", "logs"
        };

        /// <summary>
        /// 算出版本实际使用的游戏目录。<b>恒为</b> <c>versions\&lt;版本 ID&gt;</c>。
        /// 版本 ID 为空时直接抛异常，绝不静默回退到根目录（否则等于悄悄关掉隔离）。
        /// </summary>
        public static string Resolve(string root, string versionId)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("游戏目录未设置", nameof(root));
            if (string.IsNullOrWhiteSpace(versionId))
                throw new ArgumentException("未指定版本 ID：强制隔离下没有可回退的共用目录", nameof(versionId));

            return Path.Combine(Paths.VersionsDir(root), versionId);
        }

        /// <summary>该版本的存档目录。</summary>
        public static string SavesDir(string root, string versionId) =>
            Path.Combine(Resolve(root, versionId), "saves");

        /// <summary>创建隔离目录与它的标准子文件夹。</summary>
        public static void Ensure(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;

            var existed = Directory.Exists(dir);
            Directory.CreateDirectory(dir);

            foreach (var sub in SubDirs)
            {
                try { Directory.CreateDirectory(Path.Combine(dir, sub)); }
                catch (Exception ex) { Log.Warn($"创建 {sub} 目录失败：{ex.Message}"); }
            }

            if (!existed) Log.Info($"已创建版本隔离目录：{dir}");
        }

        /// <summary>
        /// 启动前准备：返回该版本的游戏目录并完成目录创建。
        /// <b>不做任何存档/模组复制</b>——隔离是强制的，各版本从零开始。
        /// </summary>
        public static string Prepare(string root, string versionId)
        {
            var dir = Resolve(root, versionId);
            Ensure(dir);
            return dir;
        }

        /// <summary>
        /// 检测根目录是否还残留旧版"共用模式"留下的存档。
        /// 只用于提示用户，<b>不会</b>自动搬移或复制。
        /// </summary>
        public static bool HasLegacySharedSaves(string root, out int count)
        {
            count = 0;
            try
            {
                var saves = Path.Combine(root, "saves");
                if (!Directory.Exists(saves)) return false;

                foreach (var dir in Directory.GetDirectories(saves))
                {
                    if (File.Exists(Path.Combine(dir, "level.dat"))) count++;
                }

                return count > 0;
            }
            catch
            {
                count = 0;
                return false;
            }
        }
    }
}
