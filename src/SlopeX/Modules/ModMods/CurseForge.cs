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
    /// <summary>
    /// CurseForge 数据源。
    ///
    /// **元数据全部经 MCIM 社区镜像**（<c>mod.mcimirror.top/curseforge/v1</c>）：
    /// 官方 <c>api.curseforge.com</c> 强制 <c>x-api-key</c>，没有 Key 一律 403；
    /// 镜像在服务端替客户端持有 Key 并把响应缓存下来，路径与官方完全一致，
    /// 将来若拿到 Key，只要把 <see cref="Base"/> 换回官方地址即可，解析代码不用动。
    ///
    /// 文件下载**不走镜像**：镜像的文件端点只做 302 回 <c>edge / mediafilez.forgecdn.net</c>，
    /// 多一跳没有收益，所以这里保留 API 给出的原始地址直连。
    ///
    /// 与 Modrinth 的三处差异（都会影响调用方）：
    /// <list type="bullet">
    /// <item>项目 ID 是数字（<c>238222</c>），不是 base62 字符串 —— 两边的 ID 不能互认。</item>
    /// <item>搜索接口没有相关度排序，**默认排序实测全是无关结果**（搜 create 返回水模组），必须显式 <c>sortField=2</c>（热度）。</item>
    /// <item>必需依赖在文件对象里直接给出（<c>relationType = 3</c>），不必再按版本号反查。</item>
    /// </list>
    /// </summary>
    public static class CurseForgeSource
    {
        /// <summary>MCIM 镜像的 CurseForge 入口（路径与官方 <c>api.curseforge.com/v1</c> 一致）。</summary>
        private const string Base = "https://mod.mcimirror.top/curseforge/v1";

        private const int GameId = 432;        // 432 = Minecraft
        private const int ClassMods = 6;

        /// <summary>整合包分类（CF 的 classId）。模组是 6，整合包是 4471。</summary>
        public const int ClassModpacks = 4471;

        /// <summary>写进 <see cref="ModSearchHit.Source"/> 的标记，安装时靠它分派。</summary>
        public const string SourceName = "CurseForge";

        /// <summary>CF 的 <c>modLoaderType</c> 枚举；未装加载器时返回 null（不过滤）。</summary>
        public static int? LoaderType(string loader) => (loader ?? "").ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => (int?)null
        };

        // ---------------- 搜索 ----------------

        /// <summary>
        /// 按关键词搜模组，服务端已按 MC 版本 + 加载器过滤。
        ///
        /// 排序用"热度"而不是默认：实测默认排序（不传 sortField）返回的是完全不相关的结果，
        /// 搜 jei 第一条是 recipegraph、搜 create 第一条是水模组。改成热度之后
        /// jei / create 都能落到第一条附近，再叠加一次"名称或 slug 精确命中提前"。
        /// </summary>
        public static async Task<List<ModSearchHit>> SearchAsync(string query, string mcVersion, string loader,
            int limit = 20, CancellationToken ct = default, int classId = ClassMods)
        {
            var q = new List<string>
            {
                "gameId=" + GameId,
                "classId=" + classId,
                "searchFilter=" + Uri.EscapeDataString(query ?? ""),
                "pageSize=" + Math.Clamp(limit, 1, 50),
                "index=0",
                "sortField=2",     // 2 = Popularity
                "sortOrder=desc"
            };

            if (!string.IsNullOrWhiteSpace(mcVersion))
                q.Add("gameVersion=" + Uri.EscapeDataString(mcVersion.Trim()));

            var lt = LoaderType(loader);
            if (lt.HasValue) q.Add("modLoaderType=" + lt.Value);

            var node = await Net.GetJsonRawAsync(Base + "/mods/search?" + string.Join("&", q), ct)
                .ConfigureAwait(false);

            var hits = (J.Arr(node, "data") ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(ToHit)
                .Where(h => h != null)
                .ToList();

            var key = (query ?? "").Trim();
            if (key.Length == 0) return hits;

            // OrderByDescending 是稳定排序：精确命中的提前，其余保持服务端顺序
            return hits.OrderByDescending(h => IsExact(h, key)).ToList();
        }

        /// <summary>
        /// 按 slug 精确查一个项目——中文搜索从 MC百科 词条拿到的是 CurseForge 的 slug，
        /// 要先换成数字 ID 才能取文件。
        /// </summary>
        public static async Task<ModSearchHit> SearchBySlugAsync(string slug, CancellationToken ct = default,
            int? classId = null)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;

            // 带上 classId 时只在该分类里找：整合包与模组可能撞 slug（如 create）
            var url = Base + "/mods/search?gameId=" + GameId +
                      "&slug=" + Uri.EscapeDataString(slug.Trim()) + "&pageSize=10" +
                      (classId.HasValue ? "&classId=" + classId.Value : "");

            try
            {
                var node = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);

                var candidates = (J.Arr(node, "data") ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(ToHit)
                    .Where(h => h != null)
                    .ToList();

                // slug 查询走的是模糊匹配，而且**同一个 slug 可能对应多个项目**
                // （CF 上 create 同时是 Create 本体和 Create+），
                // 取下载量最高的那条才是用户真要的那个
                var exact = candidates
                    .Where(h => string.Equals(h.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(h => h.Downloads)
                    .ToList();

                return exact.FirstOrDefault() ?? candidates.FirstOrDefault();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"CurseForge 按 slug 查询 {slug} 失败：{ex.Message}");
                return null;
            }
        }

        private static bool IsExact(ModSearchHit hit, string key) =>
            string.Equals(hit.Slug, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hit.Title, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(StripSpaces(hit.Title), StripSpaces(key), StringComparison.OrdinalIgnoreCase);

        private static string StripSpaces(string s) => (s ?? "").Replace(" ", "");

        private static ModSearchHit ToHit(JsonObject o)
        {
            var id = J.Long(o, "id");
            if (id <= 0) return null;

            var hit = new ModSearchHit
            {
                Source = SourceName,
                ProjectId = id.ToString(),
                Slug = J.Str(o, "slug") ?? "",
                Title = J.Str(o, "name") ?? "",
                Description = J.Str(o, "summary") ?? "",
                Author = (J.Arr(o, "authors") ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(a => J.Str(a, "name"))
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "",
                Downloads = J.Long(o, "downloadCount"),
                IconUrl = J.Str(J.Obj(o, "logo"), "thumbnailUrl") ?? ""
            };

            if (DateTime.TryParse(J.Str(o, "dateModified"), out var updated))
                hit.Updated = updated;

            // latestFiles 只是"最新的几个文件"，聚合出来的版本范围是近似值，
            // 用来在列表里给个参考够用；真正的"支不支持这个版本"由搜索接口的 gameVersion 过滤保证
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in J.Arr(o, "latestFiles") ?? new JsonArray())
            {
                foreach (var v in J.Arr(f, "gameVersions") ?? new JsonArray())
                {
                    var s = v?.ToString();
                    if (string.IsNullOrEmpty(s) || IsLoaderLabel(s)) continue;
                    if (seen.Add(s)) hit.GameVersions.Add(s);
                }
            }

            return hit;
        }

        private static bool IsLoaderLabel(string s) => s.ToLowerInvariant() switch
        {
            "forge" => true,
            "fabric" => true,
            "neoforge" => true,
            "quilt" => true,
            "rift" => true,
            "liteloader" => true,
            "cauldron" => true,
            "client" => true,
            "server" => true,
            _ => false
        };

        // ---------------- 文件 ----------------

        /// <summary>
        /// 列出该项目适用于目标 MC 版本 + 加载器的文件（服务端按发布时间倒序）。
        /// 同时把每个文件声明的必需依赖收进 <see cref="ModFileRef.RequiredDepIds"/>。
        /// </summary>
        public static async Task<List<ModFileRef>> ListFilesAsync(string modId, string mcVersion, string loader,
            CancellationToken ct = default)
        {
            var q = new List<string> { "pageSize=50", "index=0" };

            if (!string.IsNullOrWhiteSpace(mcVersion))
                q.Add("gameVersion=" + Uri.EscapeDataString(mcVersion.Trim()));

            var lt = LoaderType(loader);
            if (lt.HasValue) q.Add("modLoaderType=" + lt.Value);

            var url = $"{Base}/mods/{Uri.EscapeDataString(modId)}/files?" + string.Join("&", q);
            var node = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);

            var list = new List<ModFileRef>();

            foreach (var o in (J.Arr(node, "data") ?? new JsonArray()).OfType<JsonObject>())
            {
                var file = await ToFileRefAsync(modId, o, ct).ConfigureAwait(false);
                if (file != null) list.Add(file);
            }

            return list;
        }

        /// <summary>
        /// 取该项目**最新**的一个适配当前版本 + 加载器的文件（<c>pageSize=1</c>，服务端按时间倒序）。
        /// 更新检查用：一次请求就能判断有没有新版本，不必把整个文件列表拉回来。
        /// </summary>
        public static async Task<ModFileRef> LatestFileAsync(string modId, string mcVersion, string loader,
            CancellationToken ct = default)
        {
            var q = new List<string> { "pageSize=1", "index=0" };

            if (!string.IsNullOrWhiteSpace(mcVersion))
                q.Add("gameVersion=" + Uri.EscapeDataString(mcVersion.Trim()));

            var lt = LoaderType(loader);
            if (lt.HasValue) q.Add("modLoaderType=" + lt.Value);

            var url = $"{Base}/mods/{Uri.EscapeDataString(modId)}/files?" + string.Join("&", q);
            var node = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);

            var o = (J.Arr(node, "data") ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault();
            return o == null ? null : await ToFileRefAsync(modId, o, ct).ConfigureAwait(false);
        }

        /// <summary>把一个 CF 文件对象转成内部结构；作者禁止第三方分发又拿不到地址时返回 null。</summary>
        private static async Task<ModFileRef> ToFileRefAsync(string modId, JsonObject o, CancellationToken ct)
        {
            var fileId = J.Long(o, "id");
            if (fileId <= 0) return null;

            // 作者可以禁止第三方分发，这时 downloadUrl 是 null，得走专门的端点再取一次
            var url = J.Str(o, "downloadUrl");
            if (string.IsNullOrEmpty(url))
                url = await ResolveDownloadUrlAsync(modId, fileId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return null;

            var file = new ModFileRef
            {
                Source = SourceName,
                ProjectId = modId,
                VersionLabel = J.Str(o, "displayName") ?? J.Str(o, "fileName") ?? "",
                FileName = J.Str(o, "fileName") ?? "",
                Url = url,
                Sha1 = Sha1Of(o),
                Size = J.Long(o, "fileLength"),
                FileDate = ParseDate(J.Str(o, "fileDate"))
            };

            foreach (var d in J.Arr(o, "dependencies") ?? new JsonArray())
            {
                if (d is not JsonObject dep) continue;
                if (J.Long(dep, "relationType") != 3) continue;   // 3 = RequiredDependency

                var depId = J.Long(dep, "modId");
                if (depId > 0) file.RequiredDepIds.Add(depId.ToString());
            }

            return file;
        }

        private static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, out var d) ? d : (DateTime?)null;

        // ---------------- 指纹 ----------------

        /// <summary>指纹反查的命中结果：本地这个 jar 在 CF 上是哪个项目的哪个文件。</summary>
        public sealed class CfMatch
        {
            /// <summary>CF 的数字项目 ID（与 Modrinth 的 base62 不互通）。</summary>
            public string ProjectId { get; set; } = "";

            public long FileId { get; set; }
            public string FileName { get; set; } = "";
            public DateTime? FileDate { get; set; }
        }

        /// <summary>一次能问多少个指纹。镜像是社区服务，别一次灌太多。</summary>
        public const int FingerprintBatch = 100;

        /// <summary>
        /// 按指纹反查文件。返回的字典以**指纹**为键（无符号 32 位，与 <c>JarFingerprint</c> 同一个口径），
        /// 没认出来的文件不会出现在结果里。
        ///
        /// 这是 CF 侧更新检查的入口：CF 没有"按 SHA1 批量查更新"的接口，只能先用指纹认出
        /// "本地这个 jar 是哪个项目的哪个文件"，再去看该项目有没有更新的文件。
        /// </summary>
        public static async Task<Dictionary<long, CfMatch>> MatchFingerprintsAsync(
            IReadOnlyList<long> fingerprints, CancellationToken ct = default)
        {
            var result = new Dictionary<long, CfMatch>();
            if (fingerprints == null || fingerprints.Count == 0) return result;

            var arr = new JsonArray();
            foreach (var fp in fingerprints) arr.Add(JsonValue.Create(fp));

            var node = await Net.PostJsonAsync(Base + "/fingerprints",
                new JsonObject { ["fingerprints"] = arr }, ct).ConfigureAwait(false);

            var matches = J.Arr(J.Obj(node, "data"), "exactMatches") ?? new JsonArray();

            foreach (var m in matches.OfType<JsonObject>())
            {
                var file = J.Obj(m, "file");
                if (file == null) continue;

                var fileId = J.Long(file, "id");
                if (fileId <= 0) continue;

                // 用响应里回带的指纹做键，避免顺序假设
                var key = J.Long(file, "fileFingerprint");
                if (key == 0) continue;

                result[key] = new CfMatch
                {
                    ProjectId = J.Long(m, "id").ToString(),
                    FileId = fileId,
                    FileName = J.Str(file, "fileName") ?? "",
                    FileDate = ParseDate(J.Str(file, "fileDate"))
                };
            }

            return result;
        }


        private static async Task<string> ResolveDownloadUrlAsync(string modId, long fileId, CancellationToken ct)
        {
            try
            {
                var node = await Net.GetJsonRawAsync($"{Base}/mods/{modId}/files/{fileId}/download-url", ct)
                    .ConfigureAwait(false);
                return J.Str(node, "data") ?? "";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"CurseForge 取下载地址失败 {modId}/{fileId}：{ex.Message}");
                return "";
            }
        }

        /// <summary>CF 的 <c>hashes</c> 里 <c>algo = 1</c> 是 SHA1，<c>2</c> 是 MD5。</summary>
        private static string Sha1Of(JsonObject file)
        {
            foreach (var h in J.Arr(file, "hashes") ?? new JsonArray())
            {
                if (h is not JsonObject ho) continue;
                if (J.Long(ho, "algo") == 1) return J.Str(ho, "value") ?? "";
            }

            return "";
        }

        // ---------------- 依赖 ----------------

        /// <summary>
        /// 取这个文件声明的必需依赖，并一次性补齐名字（界面和日志要显示人话）。
        /// 批量接口漏掉的依赖用 ID 占位返回，至少还能装上。
        /// </summary>
        public static async Task<List<(string Id, string Slug, string Name)>> RequiredDepsAsync(ModFileRef file,
            CancellationToken ct = default)
        {
            var result = new List<(string Id, string Slug, string Name)>();
            if (file == null || file.RequiredDepIds.Count == 0) return result;

            try
            {
                var ids = new JsonArray();
                foreach (var id in file.RequiredDepIds)
                    if (long.TryParse(id, out var n)) ids.Add(n);

                var body = new JsonObject { ["modIds"] = ids };
                var node = await Net.PostJsonAsync(Base + "/mods", body, ct).ConfigureAwait(false);

                foreach (var o in (J.Arr(node, "data") ?? new JsonArray()).OfType<JsonObject>())
                {
                    var id = J.Long(o, "id");
                    if (id <= 0) continue;

                    // slug 通常等于模组的 modid，用于判断「这个前置是不是已经装过」
                    result.Add((id.ToString(), J.Str(o, "slug") ?? "", J.Str(o, "name") ?? ""));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"CurseForge 批量取依赖信息失败：{ex.Message}");
            }

            foreach (var id in file.RequiredDepIds)
            {
                if (!result.Any(r => string.Equals(r.Id, id, StringComparison.Ordinal))) result.Add((id, "", ""));
            }

            return result;
        }

        /// <summary>取项目名（依赖列表里显示用）。</summary>
        public static async Task<string> ProjectTitleAsync(string modId, CancellationToken ct = default)
        {
            try
            {
                var node = await Net.GetJsonRawAsync($"{Base}/mods/{Uri.EscapeDataString(modId)}", ct)
                    .ConfigureAwait(false);
                return J.Str(J.Obj(node, "data"), "name") ?? modId;
            }
            catch (Exception ex)
            {
                Log.Warn($"查询 CurseForge 项目 {modId} 失败：{ex.Message}");
                return modId;
            }
        }
    }
}
