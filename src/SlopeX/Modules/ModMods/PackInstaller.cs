using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>资源包的两种类型——决定落到哪个目录、以及要不要提示光影加载器。</summary>
    public enum PackKind
    {
        Shader,
        ResourcePack
    }

    /// <summary>某个资源包的一个可下载版本。</summary>
    public sealed class PackVersionRef
    {
        public string VersionNumber { get; set; } = "";
        public string VersionType { get; set; } = "";     // release / beta / alpha
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public long Size { get; set; }
        public DateTime Published { get; set; }

        public List<string> GameVersions { get; } = new List<string>();

        public string SizeText => Size > 0 ? Format.Bytes(Size) : "";

        public string PublishedText => Published == DateTime.MinValue
            ? ""
            : Published.ToLocalTime().ToString("yyyy-MM-dd");

        /// <summary>声明支持的 MC 版本，太长时只给范围。</summary>
        public string McText => VersionRange.Text(GameVersions);

        /// <summary>是否声明支持这个 MC 版本。空条件视为不限制。</summary>
        public bool MatchesMc(string mcVersion) =>
            string.IsNullOrEmpty(mcVersion) ||
            GameVersions.Any(v => string.Equals(v, mcVersion, StringComparison.OrdinalIgnoreCase));

        /// <summary>列表里的第二行：版本号 · 支持范围 · 大小 · 日期。</summary>
        public string SubText => string.Join("  ·  ",
            new[] { VersionNumber, McText, SizeText, PublishedText }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// 光影 / 材质包的版本查询与落盘。
    ///
    /// 与模组的区别（都是刻意的）：
    /// <list type="bullet">
    /// <item>搜索与安装都<b>不按 MC 版本、不按加载器过滤</b>；版本由用户在界面里自己挑。</item>
    /// <item>没有依赖概念，不往 <c>mods\</c> 里放任何东西。</item>
    /// <item>落盘到版本隔离目录下的 <c>shaderpacks\</c> / <c>resourcepacks\</c>。</item>
    /// </list>
    /// </summary>
    public static class PackInstaller
    {
        public static string FolderName(PackKind kind) =>
            kind == PackKind.Shader ? "shaderpacks" : "resourcepacks";

        /// <summary>目标目录：<c>.minecraft\versions\&lt;版本 ID&gt;\shaderpacks|resourcepacks</c>。</summary>
        public static string Dir(string gameDir, string versionId, PackKind kind) =>
            Path.Combine(VersionEnv.Resolve(gameDir, versionId), FolderName(kind));

        /// <summary>
        /// 列出该项目的**全部**版本（新的在前），不做版本 / 加载器过滤。
        /// </summary>
        public static async Task<List<PackVersionRef>> ListVersionsAsync(string projectId,
            CancellationToken ct = default)
        {
            var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version";
            var arr = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);

            var list = new List<PackVersionRef>();

            foreach (var node in arr as JsonArray ?? new JsonArray())
            {
                if (node is not JsonObject o) continue;

                var files = J.Arr(o, "files");
                if (files == null) continue;

                JsonObject picked = null;
                foreach (var f in files)
                {
                    if (f is JsonObject fo && J.Bool(fo, "primary")) { picked = fo; break; }
                }
                picked ??= files.FirstOrDefault() as JsonObject;
                if (picked == null) continue;

                var url2 = J.Str(picked, "url");
                if (string.IsNullOrEmpty(url2)) continue;

                var version = new PackVersionRef
                {
                    VersionNumber = J.Str(o, "version_number") ?? "",
                    VersionType = J.Str(o, "version_type") ?? "",
                    FileName = J.Str(picked, "filename") ?? "",
                    Url = url2,
                    Sha1 = J.Str(J.Obj(picked, "hashes"), "sha1"),
                    Size = J.Long(picked, "size")
                };

                if (DateTime.TryParse(J.Str(o, "date_published"), out var published))
                    version.Published = published;

                foreach (var v in J.Arr(o, "game_versions") ?? new JsonArray())
                    if (v != null) version.GameVersions.Add(v.ToString());

                list.Add(version);
            }

            // 接口本身按发布时间倒序，这里再明确一次，避免依赖服务端顺序
            return list.OrderByDescending(v => v.Published).ToList();
        }

        /// <summary>
        /// 下载一个资源包版本到目标目录。返回落地的完整路径；已存在同名文件时直接返回该路径并标记跳过。
        /// </summary>
        public static async Task<(string Path, bool Skipped)> InstallAsync(string gameDir, string versionId,
            PackKind kind, PackVersionRef version, CancellationToken ct = default, Action<string> progress = null)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));

            var dir = Dir(gameDir, versionId, kind);
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, version.FileName);
            if (File.Exists(target)) return (target, true);

            // 与模组安装同一套口径：国内网络经代理常只有几十 KB/s，按体积给足时间
            var timeoutMs = version.Size > 0
                ? (int)Math.Min(900000, Math.Max(300000, version.Size / 1024 / 20 * 1000))
                : 600000;

            try
            {
                progress?.Invoke($"下载 {version.FileName}（{version.SizeText}）");
                await Net.DownloadToFileAsync(version.Url, target, version.Sha1, version.Size, ct, timeoutMs)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"资源包下载失败 {version.FileName}：{ex.Message}");
                throw;
            }

            Log.Info($"资源包已安装：{target}（{kind} {version.VersionNumber}）");
            return (target, false);
        }

        /// <summary>该目录下已有的文件数（用于「打开目录」与空状态提示）。</summary>
        public static int Count(string gameDir, string versionId, PackKind kind)
        {
            try
            {
                var dir = Dir(gameDir, versionId, kind);
                return Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
