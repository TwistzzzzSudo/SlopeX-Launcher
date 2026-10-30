using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>搜索到的一个模组（来源无关的视图模型）。</summary>
    public sealed class ModSearchHit
    {
        public string Source { get; set; } = "";      // Modrinth / CurseForge
        public string ProjectId { get; set; } = "";   // Modrinth 用字符串 ID，CurseForge 用数字 ID 的字符串形式
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public long Downloads { get; set; }
        public string IconUrl { get; set; } = "";

        public string DownloadsText => Downloads <= 0
            ? ""
            : Downloads >= 1_000_000
                ? $"{Downloads / 1_000_000.0:0.#}M 次下载"
                : Downloads >= 1000
                    ? $"{Downloads / 1000.0:0.#}K 次下载"
                    : $"{Downloads} 次下载";

        public string SubText => string.Join("  ·  ",
            new[] { Author, DownloadsText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>一个可下载的模组文件。</summary>
    public sealed class ModFileRef
    {
        public string Source { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string VersionLabel { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public long Size { get; set; }

        /// <summary>依赖模块用它判断是否已经装过。</summary>
        public string Key => Source + ":" + ProjectId;
    }

    /// <summary>
    /// Modrinth 开放 API（无需 Key，文档 https://docs.modrinth.com）。
    ///
    /// 两条接口就够用：<c>/v2/search</c> 搜项目，<c>/v2/project/{id}/version</c> 取文件。
    /// 依赖信息直接在 version 的 <c>dependencies</c> 里，按 <c>dependency_type = required</c> 递归补齐。
    /// </summary>
    public static class ModrinthSource
    {
        private const string Base = "https://api.modrinth.com/v2";

        /// <summary>加载器名映射到 Modrinth 的 categories facet。</summary>
        public static string LoaderFacet(string loader) => (loader ?? "").ToLowerInvariant() switch
        {
            "fabric" => "fabric",
            "forge" => "forge",
            "neoforge" => "neoforge",
            "quilt" => "quilt",
            _ => ""
        };

        public static async Task<List<ModSearchHit>> SearchAsync(string query, string mcVersion, string loader,
            int limit = 20, int offset = 0, CancellationToken ct = default)
        {
            var facets = new List<string> { "[\"project_type:mod\"]" };
            if (!string.IsNullOrWhiteSpace(mcVersion))
                facets.Add($"[\"versions:{mcVersion}\"]");

            var l = LoaderFacet(loader);
            if (!string.IsNullOrEmpty(l))
                facets.Add($"[\"categories:{l}\"]");

            var url = $"{Base}/search?query={Uri.EscapeDataString(query ?? "")}" +
                      $"&limit={limit}&offset={offset}" +
                      $"&index=relevance&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

            var node = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);
            var hits = new List<ModSearchHit>();

            foreach (var h in J.Arr(node, "hits") ?? new JsonArray())
            {
                if (h is not JsonObject o) continue;

                hits.Add(new ModSearchHit
                {
                    Source = "Modrinth",
                    ProjectId = J.Str(o, "project_id") ?? "",
                    Slug = J.Str(o, "slug") ?? "",
                    Title = J.Str(o, "title") ?? "",
                    Description = J.Str(o, "description") ?? "",
                    Author = J.Str(o, "author") ?? "",
                    Downloads = J.Long(o, "downloads"),
                    IconUrl = J.Str(o, "icon_url") ?? ""
                });
            }

            return hits;
        }

        /// <summary>列出该项目适用于指定 MC 版本 + 加载器的文件（新的在前）。</summary>
        public static async Task<List<ModFileRef>> ListFilesAsync(string projectId, string mcVersion, string loader,
            CancellationToken ct = default)
        {
            var l = LoaderFacet(loader);
            var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(mcVersion))
                q.Add("game_versions=" + Uri.EscapeDataString($"[\"{mcVersion}\"]"));
            if (!string.IsNullOrEmpty(l))
                q.Add("loaders=" + Uri.EscapeDataString($"[\"{l}\"]"));
            if (q.Count > 0) url += "?" + string.Join("&", q);

            var arr = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);
            var list = new List<ModFileRef>();

            foreach (var v in arr as JsonArray ?? new JsonArray())
            {
                if (v is not JsonObject o) continue;
                if (J.Str(o, "version_type") == "alpha") continue;   // 默认不装 alpha

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

                list.Add(new ModFileRef
                {
                    Source = "Modrinth",
                    ProjectId = projectId,
                    VersionLabel = J.Str(o, "version_number") ?? "",
                    FileName = J.Str(picked, "filename") ?? "",
                    Url = url2,
                    Sha1 = J.Str(J.Obj(picked, "hashes"), "sha1"),
                    Size = J.Long(picked, "size")
                });
            }

            return list;
        }

        /// <summary>
        /// 取某个版本声明的必需依赖（只看 required），返回 (projectId, 依赖说明)。
        /// 依赖的具体文件在安装阶段再按 MC 版本 + 加载器解析。
        /// </summary>
        public static async Task<List<(string ProjectId, string Name)>> RequiredDepsAsync(string projectId,
            string versionLabel, string mcVersion, string loader, CancellationToken ct = default)
        {
            var l = LoaderFacet(loader);
            var url = $"{Base}/project/{Uri.EscapeDataString(projectId)}/version";
            if (!string.IsNullOrWhiteSpace(mcVersion))
                url += "?game_versions=" + Uri.EscapeDataString($"[\"{mcVersion}\"]");
            if (!string.IsNullOrEmpty(l))
                url += (url.Contains('?') ? "&" : "?") + "loaders=" + Uri.EscapeDataString($"[\"{l}\"]");

            var arr = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);
            var result = new List<(string, string)>();

            foreach (var v in arr as JsonArray ?? new JsonArray())
            {
                if (v is not JsonObject o) continue;
                if (J.Str(o, "version_number") != versionLabel) continue;

                foreach (var d in J.Arr(o, "dependencies") ?? new JsonArray())
                {
                    if (d is not JsonObject dep) continue;
                    if (!string.Equals(J.Str(dep, "dependency_type"), "required", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pid = J.Str(dep, "project_id");
                    if (string.IsNullOrEmpty(pid)) continue;

                    result.Add((pid, J.Str(dep, "file_name") ?? ""));
                }

                break;
            }

            return result;
        }

        /// <summary>取项目标题（用于依赖列表里显示人话）。</summary>
        public static async Task<string> ProjectTitleAsync(string projectId, CancellationToken ct = default)
        {
            try
            {
                var node = await Net.GetJsonRawAsync($"{Base}/project/{Uri.EscapeDataString(projectId)}", ct)
                    .ConfigureAwait(false);
                return J.Str(node, "title") ?? projectId;
            }
            catch (Exception ex)
            {
                Log.Warn($"查询 Modrinth 项目 {projectId} 失败：{ex.Message}");
                return projectId;
            }
        }
    }
}
