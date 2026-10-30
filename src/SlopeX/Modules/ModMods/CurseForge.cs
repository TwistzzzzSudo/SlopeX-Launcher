using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>
    /// CurseForge 模组来源（官方 API，需要用户在设置里填自己的 API Key）。
    ///
    /// 为什么必须给 Key：<c>api.curseforge.com</c> 强制 <c>x-api-key</c>，没有 Key 一律 403；
    /// 网站内部接口被 Cloudflare 挡；第三方镜像 cfwidget 只有"按 ID 查单个模组"、没有搜索，
    /// 且单次响应可达 2–3 MB、实测常要几十秒，不能当搜索用。
    ///
    /// Key 申请地址：https://console.curseforge.com/ → API Keys（免费，需登录）。
    /// </summary>
    public static class CurseForgeSource
    {
        private const string Base = "https://api.curseforge.com/v1";
        private const int MinecraftGameId = 432;
        private const int ModClassId = 6;

        public static bool HasKey => !string.IsNullOrWhiteSpace(Config.Current?.CurseForgeApiKey);

        /// <summary>CurseForge 的 modLoaderType 枚举。</summary>
        public static int LoaderType(string loader) => (loader ?? "").ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => 0
        };

        public static async Task<List<ModSearchHit>> SearchAsync(string query, string mcVersion, string loader,
            int limit = 20, int offset = 0, CancellationToken ct = default)
        {
            if (!HasKey) throw new InvalidOperationException("未配置 CurseForge API Key。");

            var url = $"{Base}/mods/search?gameId={MinecraftGameId}&classId={ModClassId}" +
                      $"&searchFilter={Uri.EscapeDataString(query ?? "")}" +
                      $"&pageSize={limit}&index={offset}&sortField=6&sortOrder=desc";  // 6 = 总下载量

            if (!string.IsNullOrWhiteSpace(mcVersion))
                url += "&gameVersion=" + Uri.EscapeDataString(mcVersion);

            var lt = LoaderType(loader);
            if (lt > 0) url += "&modLoaderType=" + lt;

            var node = await GetAsync(url, ct).ConfigureAwait(false);
            var hits = new List<ModSearchHit>();

            foreach (var m in J.Arr(node, "data") ?? new JsonArray())
            {
                if (m is not JsonObject o) continue;

                hits.Add(new ModSearchHit
                {
                    Source = "CurseForge",
                    ProjectId = J.Long(o, "id").ToString(),
                    Slug = J.Str(o, "slug") ?? "",
                    Title = J.Str(o, "name") ?? "",
                    Description = J.Str(o, "summary") ?? "",
                    Author = (J.Arr(o, "authors")?.FirstOrDefault() as JsonObject) is { } a
                        ? J.Str(a, "name") ?? ""
                        : "",
                    Downloads = J.Long(o, "downloadCount"),
                    IconUrl = J.Str(J.Obj(o, "logo"), "thumbnailUrl") ?? ""
                });
            }

            return hits;
        }

        /// <summary>列出该模组适用于指定 MC 版本 + 加载器的文件（新的在前）。</summary>
        public static async Task<List<ModFileRef>> ListFilesAsync(string modId, string mcVersion, string loader,
            CancellationToken ct = default)
        {
            if (!HasKey) throw new InvalidOperationException("未配置 CurseForge API Key。");

            var url = $"{Base}/mods/{modId}/files?pageSize=50";
            if (!string.IsNullOrWhiteSpace(mcVersion))
                url += "&gameVersion=" + Uri.EscapeDataString(mcVersion);

            var lt = LoaderType(loader);
            if (lt > 0) url += "&modLoaderType=" + lt;

            var node = await GetAsync(url, ct).ConfigureAwait(false);
            var list = new List<ModFileRef>();

            foreach (var f in J.Arr(node, "data") ?? new JsonArray())
            {
                if (f is not JsonObject o) continue;

                var fileName = J.Str(o, "fileName") ?? "";
                var fileId = J.Long(o, "id");
                var url2 = J.Str(o, "downloadUrl");

                // 作者关闭了第三方分发时 downloadUrl 为 null，此时按官方 CDN 规则拼；
                // 这类文件通常也下不动，交给下载阶段报错即可。
                if (string.IsNullOrEmpty(url2) && fileId > 0 && !string.IsNullOrEmpty(fileName))
                    url2 = $"https://mediafilez.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/" +
                           Uri.EscapeDataString(fileName);

                if (string.IsNullOrEmpty(url2)) continue;

                // gameVersions 里混着 "Client"/"Server"/加载器名与 MC 版本号，
                // API 的 gameVersion 过滤有时偏宽松，这里再确认一次它真的支持该版本
                var gameVersions = J.Arr(o, "gameVersions")?
                                       .Select(x => x?.GetValue<string>())
                                       .Where(v => !string.IsNullOrEmpty(v))
                                       .ToList()
                                   ?? new List<string>();

                if (!string.IsNullOrWhiteSpace(mcVersion) && gameVersions.Count > 0 &&
                    !gameVersions.Contains(mcVersion, StringComparer.OrdinalIgnoreCase))
                    continue;

                list.Add(new ModFileRef
                {
                    Source = "CurseForge",
                    ProjectId = modId,
                    VersionLabel = J.Str(o, "displayName") ?? fileName,
                    FileName = fileName,
                    Url = url2,
                    Sha1 = J.Str(J.Obj(o, "hashes"), "sha1"),
                    Size = J.Long(o, "fileLength")
                });
            }

            return list;
        }

        /// <summary>取模组标题（依赖展示用）。</summary>
        public static async Task<string> ModNameAsync(string modId, CancellationToken ct = default)
        {
            try
            {
                var node = await GetAsync($"{Base}/mods/{modId}", ct).ConfigureAwait(false);
                return J.Str(J.Obj(node, "data"), "name") ?? modId;
            }
            catch (Exception ex)
            {
                Log.Warn($"查询 CurseForge 模组 {modId} 失败：{ex.Message}");
                return modId;
            }
        }

        private static Task<JsonNode> GetAsync(string url, CancellationToken ct)
        {
            return Net.GetJsonWithHeadersAsync(url, ct,
                new[] { ("x-api-key", Config.Current?.CurseForgeApiKey ?? "") });
        }
    }
}
