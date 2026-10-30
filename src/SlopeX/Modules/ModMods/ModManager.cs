using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>mods 目录里的一个模组文件。</summary>
    public sealed class ModEntry
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";

        /// <summary>true = 已启用；false = 已禁用（文件后缀 .disabled）。</summary>
        public bool Enabled { get; set; }

        /// <summary>从 jar 元数据里读出来的显示名；读不到就用文件名。</summary>
        public string Title { get; set; } = "";
        public string Version { get; set; } = "";

        /// <summary>fabric / forge / neoforge；读不到为空。</summary>
        public string LoaderHint { get; set; } = "";

        public long Size { get; set; }

        public string SizeText => Size <= 0 ? "" : Format.Bytes(Size);

        /// <summary>列表里显示的状态文字。</summary>
        public string StateText => Enabled ? "启用" : "禁用";

        /// <summary>操作按钮上的文字：显示的是点下去之后的动作。</summary>
        public string ToggleText => Enabled ? "禁用" : "启用";
    }

    /// <summary>
    /// 模组文件的本地管理：扫描、启用/禁用、删除。
    ///
    /// 启用/禁用的实现是改文件名（<c>x.jar</c> ↔ <c>x.jar.disabled</c>），
    /// 这是各启动器通用的做法——不移动文件、不破坏 jar，加载器天然忽略带 .disabled 后缀的文件。
    ///
    /// 元数据解析刻意保持轻量：fabric.mod.json 是 JSON（用现成的解析器），
    /// mods.toml / neoforge.mods.toml 是 TOML 但只取 displayName/version 等标量，用正则即可，
    /// 不为这个引入 TOML 依赖。
    /// </summary>
    public static class ModManager
    {
        public static string ModsDir(string gameDir, string versionId) =>
            Path.Combine(Paths.VersionsDir(gameDir), versionId, "mods");

        /// <summary>列出该版本的模组（按文件名排序，禁用的排后面）。</summary>
        public static List<ModEntry> List(string gameDir, string versionId)
        {
            var dir = ModsDir(gameDir, versionId);
            var list = new List<ModEntry>();

            if (!Directory.Exists(dir)) return list;

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    name = name[..^".disabled".Length];

                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                var entry = new ModEntry
                {
                    FileName = name,
                    FullPath = file,
                    Enabled = !file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    Size = new FileInfo(file).Length
                };

                FillMetadata(entry);
                list.Add(entry);
            }

            return list
                .OrderBy(e => !e.Enabled)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>启用一个模组（去掉 .disabled 后缀）。</summary>
        public static void Enable(string path)
        {
            if (!path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                return; // 已经是启用状态

            var target = path[..^".disabled".Length];
            MoveReplacing(path, target);
        }

        /// <summary>禁用一个模组（追加 .disabled 后缀）。</summary>
        public static void Disable(string path)
        {
            if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                return;

            MoveReplacing(path, path + ".disabled");
        }

        /// <summary>删除模组文件。出错直接抛给调用方提示。</summary>
        public static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public static string ToggleResultText(bool nowEnabled, string title) =>
            nowEnabled ? $"已启用 {title}" : $"已禁用 {title}";

        // ---------------- 内部 ----------------

        private static void MoveReplacing(string src, string dst)
        {
            // 目标已存在时：同名启用文件被占用（游戏没关）或上次操作残留。
            // 直接覆盖有风险——那可能是另一个模组——所以报错让用户处理。
            if (File.Exists(dst))
                throw new IOException($"目标文件已存在：{Path.GetFileName(dst)}\n" +
                                      "可能是同名模组，或游戏未关闭导致文件被占用。请先手动处理。");

            File.Move(src, dst);
        }

        /// <summary>从 jar 内读 mod 元数据；解析失败不抛错，保持文件名兜底。</summary>
        private static void FillMetadata(ModEntry entry)
        {
            entry.Title = Path.GetFileNameWithoutExtension(
                entry.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    ? entry.FileName[..^4]
                    : entry.FileName);

            try
            {
                using var zip = ZipFile.OpenRead(entry.FullPath);

                // 1) Fabric：fabric.mod.json（JSON）
                var fabricEntry = FindEntry(zip, "fabric.mod.json");
                if (fabricEntry != null)
                {
                    using var stream = fabricEntry.Open();
                    using var reader = new StreamReader(stream);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())
                               as System.Text.Json.Nodes.JsonObject;

                    var name = J.Str(node, "name");
                    entry.Title = string.IsNullOrWhiteSpace(name)
                        ? J.Str(node, "id") ?? entry.Title
                        : name;
                    entry.Version = J.Str(node, "version") ?? "";
                    entry.LoaderHint = "Fabric";
                    return;
                }

                // 2) Forge / NeoForge：mods.toml（TOML 的标量用正则抽）
                var tomlEntry = FindEntry(zip, "mods.toml", "neoforge.mods.toml");
                if (tomlEntry != null)
                {
                    using var stream = tomlEntry.Open();
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();

                    var displayName = MatchTomlString(text, "displayName");
                    var modId = MatchTomlString(text, "modId");
                    var version = MatchTomlString(text, "version");

                    entry.Title = FirstNonEmpty(displayName, modId, entry.Title);
                    entry.LoaderHint = tomlEntry.FullName.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
                        ? "NeoForge"
                        : "Forge";

                    // Forge/NeoForge 的 mods.toml 常写 version="${file.jarVersion}"，
                    // 这个占位符要等运行时才被替换，TOML 里拿不到真值——改读 jar 清单。
                    if (string.IsNullOrWhiteSpace(version) || version.Contains("${"))
                        version = ReadManifestVersion(zip);

                    entry.Version = version ?? "";
                }
            }
            catch (Exception ex)
            {
                // 元数据读不出来不影响管理功能（文件名兜底）
                Log.Warn($"读取模组元数据失败 {entry.FileName}：{ex.Message}");
            }
        }

        private static ZipArchiveEntry FindEntry(ZipArchive zip, params string[] names)
        {
            foreach (var name in names)
            {
                var hit = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            return null;
        }

        /// <summary>读 MANIFEST.MF 的 Implementation-Version（Forge/NeoForge 生态的版本常放这里）。</summary>
        private static string ReadManifestVersion(ZipArchive zip)
        {
            try
            {
                var manifest = FindEntry(zip, "MANIFEST.MF", "META-INF/MANIFEST.MF");
                if (manifest == null) return null;

                using var stream = manifest.Open();
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();

                var m = Regex.Match(text, @"^Implementation-Version:\s*(.+?)\s*$",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string MatchTomlString(string text, string key)
        {
            // TOML 标量： key = "value"（忽略引号风格与空白）
            var m = Regex.Match(text,
                $@"^\s*{Regex.Escape(key)}\s*=\s*[""'](.+?)[""']",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}
