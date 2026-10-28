using System;
using System.IO;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMinecraft
{
    /// <summary>
    /// 版本隔离：启用后每个版本使用独立游戏目录 <c>versions/&lt;版本 ID&gt;</c>，
    /// mods / resourcepacks / saves / config / options.txt 各自独立，互不污染。
    /// libraries 与 assets 仍共用根目录——它们与版本无关，按版本复制只会白占几百 MB。
    /// </summary>
    public static class VersionEnv
    {
        /// <summary>版本隔离目录下会自动创建的子文件夹。</summary>
        public static readonly string[] SubDirs =
        {
            "mods", "resourcepacks", "shaderpacks", "saves",
            "screenshots", "config", "crash-reports", "logs"
        };

        /// <summary>首次启用隔离时从公共目录继承的内容。</summary>
        public static readonly string[] SharedItems =
        {
            "mods", "resourcepacks", "shaderpacks", "saves",
            "screenshots", "config", "options.txt", "servers.dat"
        };

        /// <summary>算出版本实际使用的游戏目录（未开启隔离时就是根目录）。</summary>
        public static string Resolve(string root, string versionId, bool isolation)
        {
            if (!isolation || string.IsNullOrWhiteSpace(versionId)) return root;
            return Path.Combine(Paths.VersionsDir(root), versionId);
        }

        /// <summary>该目录是否已是一个独立的版本目录。</summary>
        public static bool IsIsolated(string root, string dir) =>
            !string.IsNullOrWhiteSpace(dir) && !Paths.SamePath(root, dir);

        /// <summary>目录里是否已经有玩家数据（任何子文件夹里有文件，或存在 options.txt）。</summary>
        public static bool HasContent(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, "options.txt"))) return true;

                foreach (var sub in SubDirs)
                {
                    var d = Path.Combine(dir, sub);
                    if (!Directory.Exists(d)) continue;
                    if (Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).GetEnumerator().MoveNext())
                        return true;
                }
            }
            catch { /* 权限问题按空处理 */ }
            return false;
        }

        /// <summary>创建隔离目录与它的标准子文件夹。</summary>
        public static void Ensure(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            foreach (var sub in SubDirs)
            {
                try { Directory.CreateDirectory(Path.Combine(dir, sub)); }
                catch (Exception ex) { Log.Warn($"创建 {sub} 目录失败：{ex.Message}"); }
            }
        }

        /// <summary>
        /// 启动前准备：返回该版本实际使用的游戏目录，并完成目录创建。
        /// 首次启用隔离时，把公共目录里的存档 / 材质包 / 模组复制一份到该版本，避免"存档消失"。
        /// </summary>
        public static string Prepare(string root, string versionId)
        {
            var cfg = Config.Current;
            if (!cfg.VersionIsolation) return root;

            var dir = Resolve(root, versionId, true);
            var fresh = !HasContent(dir);
            Ensure(dir);

            if (fresh && !cfg.IsolationSeeded)
            {
                var r = CopyShared(root, dir);
                cfg.IsolationSeeded = true;
                cfg.Save();

                if (r.Files > 0)
                    Log.Info($"版本隔离首次启用：已从公共目录复制 {r.Files} 个文件（{FormatSize(r.Bytes)}）到 {dir}");
                else
                    Log.Info($"版本隔离已启用：{dir}");
            }

            return dir;
        }

        /// <summary>把公共目录的内容复制到隔离目录（已存在的项跳过，不覆盖任何文件）。</summary>
        public static (int Files, long Bytes) CopyShared(string root, string target)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(target)) return (0, 0);
            if (Paths.SamePath(root, target)) return (0, 0);
            if (!Directory.Exists(root)) return (0, 0);

            int files = 0;
            long bytes = 0;
            try { Directory.CreateDirectory(target); }
            catch (Exception ex)
            {
                Log.Warn($"创建隔离目录失败：{ex.Message}");
                return (0, 0);
            }

            foreach (var item in SharedItems)
            {
                var src = Path.Combine(root, item);
                var dst = Path.Combine(target, item);
                try
                {
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) continue;
                        File.Copy(src, dst, false);
                        files++;
                        bytes += new FileInfo(dst).Length;
                    }
                    else if (Directory.Exists(src))
                    {
                        CopyDir(src, dst, ref files, ref bytes);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"复制 {item} 到隔离目录失败：{ex.Message}");
                }
            }

            return (files, bytes);
        }

        private static void CopyDir(string src, string dst, ref int files, ref long bytes)
        {
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.GetFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                try
                {
                    File.Copy(file, target, false);
                    files++;
                    bytes += new FileInfo(target).Length;
                }
                catch (Exception ex)
                {
                    Log.Warn($"复制文件失败 {file}：{ex.Message}");
                }
            }

            foreach (var dir in Directory.GetDirectories(src))
                CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)), ref files, ref bytes);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }
    }
}
