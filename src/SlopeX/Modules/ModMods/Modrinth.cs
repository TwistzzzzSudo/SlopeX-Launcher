using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>搜索到的一个模组（来源无关的视图模型）。</summary>
    public sealed class ModSearchHit : INotifyPropertyChanged
    {
        /// <summary>下载来源：<c>Modrinth</c> 或 <c>CurseForge</c>。安装时靠它分派接口。</summary>
        public string Source { get; set; } = "Modrinth";

        /// <summary>
        /// 来源侧的数值 ID。Modrinth 是 base62 的项目 ID，CurseForge 是数字 modId
        /// —— **两边不能互认**，跨源时不要拿一个去查另一个。
        /// </summary>
        public string ProjectId { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public long Downloads { get; set; }
        public string IconUrl { get; set; } = "";

        /// <summary>
        /// 已经下载好的图标字节（<see cref="IconUrl"/> 的内容）。搜索结果先出列表、
        /// 图标陆续补，补到就挂在这里；界面用 <c>IconConverter</c> 转图。
        /// 只有这个属性会发通知——列表要的是「图标陆续出现」，不想整表重绑。
        /// </summary>
        public byte[] IconData
        {
            get => _iconData;
            set
            {
                _iconData = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconData)));
            }
        }

        private byte[] _iconData;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>没有图标时显示的占位字符（取标题首字）。</summary>
        public string Initial
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Title)) return "?";

                var t = Title.Trim();
                return char.IsHighSurrogate(t[0]) && t.Length > 1 ? t.Substring(0, 2) : t.Substring(0, 1);
            }
        }

        /// <summary>项目声明支持的 MC 版本（搜索结果里就有，不用再请求一次）。</summary>
        public List<string> GameVersions { get; } = new List<string>();

        /// <summary>最后更新时间（<c>date_modified</c>），取不到时是 <see cref="DateTime.MinValue"/>。</summary>
        public DateTime Updated { get; set; }

        /// <summary>「1.7.10 – 1.21.8」这种支持范围；只有一个版本时就是那个版本。</summary>
        public string VersionSupportText => VersionRange.Text(GameVersions);

        /// <summary>来自 MC百科 的那条结果才有：百科的模组 ID（class/xxxx.html 里的 xxxx）。</summary>
        public string McModId { get; set; } = "";

        /// <summary>
        /// 百科词条的类型段：模组是 <c>class</c>，整合包是 <c>modpack</c>。
        /// 两种词条的地址只差这一段，链接与查询都跟着它走。
        /// </summary>
        public string McModKind { get; set; } = "class";

        public bool HasMcMod => !string.IsNullOrEmpty(McModId);

        /// <summary>百科词条地址，给"百科"按钮用。</summary>
        public string McModUrl => HasMcMod
            ? McModSource.Host + "/" + (McModKind == "modpack" ? "modpack" : "class") + "/" + McModId + ".html"
            : "";

        public string DownloadsText => Downloads <= 0
            ? ""
            : Downloads >= 1_000_000
                ? $"{Downloads / 1_000_000.0:0.#}M 次下载"
                : Downloads >= 1000
                    ? $"{Downloads / 1000.0:0.#}K 次下载"
                    : $"{Downloads} 次下载";

        /// <summary>结果行上的来源标签——两个源混在一起时必须能一眼分出来。</summary>
        public string SourceText => string.Equals(Source, "CurseForge", StringComparison.OrdinalIgnoreCase)
            ? "CurseForge"
            : "Modrinth";

        /// <summary>「全部」模式下同一个模组两边都有，靠这个去重。</summary>
        public string DedupeKey => (Title ?? "").Replace(" ", "").ToLowerInvariant();

        /// <summary>
        /// 这条结果是从 MCIM 镜像查到的。安装 / 取依赖要沿用同一个出口，
        /// 否则会出现"搜得到、装不上"（官方接口在部分网络下不通）。
        /// </summary>
        public bool Mirror { get; set; }

        public string SubText => string.Join("  ·  ", new[]
        {
            HasMcMod ? "MC百科" : "",
            Author,
            DownloadsText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>Modrinth 项目的基本信息（中文搜索靠它核对版本与加载器）。</summary>
    public sealed class ModrinthProject
    {
        public string Id { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public string ProjectType { get; set; } = "";
        public long Downloads { get; set; }

        public List<string> GameVersions { get; } = new List<string>();
        public List<string> Loaders { get; } = new List<string>();

        /// <summary>这个项目对目标版本 + 加载器有没有内容。空条件视为不限制。</summary>
        public bool Supports(string mcVersion, string loader)
        {
            if (!string.IsNullOrEmpty(mcVersion) &&
                !GameVersions.Any(v => string.Equals(v, mcVersion, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (!string.IsNullOrEmpty(loader) &&
                !Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }
    }

    /// <summary>一个可下载的模组文件。</summary>
    public sealed class ModFileRef
    {
        public string Source { get; set; } = "";
        public string ProjectId { get; set; } = "";

        /// <summary>
        /// Modrinth 的版本 ID。<c>ListFilesAsync</c> 填，CurseForge 侧留空。
        /// 有了它就能直接 <c>/version/{id}</c> 取依赖，不必按版本号反查整个列表；
        /// 依赖声明里带版本号时也靠它精确锁定（见 <c>ModInstaller</c>）。
        /// </summary>
        public string VersionId { get; set; } = "";

        public string VersionLabel { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public long Size { get; set; }

        /// <summary>
        /// 文件发布时间。只有 CurseForge 侧会填（Modrinth 留空）。
        /// 更新检查拿它做"新旧"的硬判据：项目最新文件比本地这份还早，就说明本地已是最新，
        /// 光比 fileId 不够——作者补传旧分支时排序会变。
        /// </summary>
        public DateTime? FileDate { get; set; }

        /// <summary>
        /// CurseForge 在文件里直接给出的必需依赖（数字 modId）。Modrinth 用不到，留空，
        /// 装的时候还按版本号反查（见 <c>ModrinthSource.RequiredDepsAsync</c>）。
        /// </summary>
        public List<string> RequiredDepIds { get; } = new List<string>();

        /// <summary>依赖模块用它判断是否已经装过。</summary>
        public string Key => Source + ":" + ProjectId;
    }

    /// <summary>把一组 MC 版本号压成「最低 – 最高」的展示文本。</summary>
    public static class VersionRange
    {
        public static string Text(IReadOnlyList<string> versions)
        {
            if (versions == null || versions.Count == 0) return "";

            var sorted = versions.OrderBy(v => Key(v)).ToList();
            var low = sorted[0];
            var high = sorted[sorted.Count - 1];

            return string.Equals(low, high, StringComparison.OrdinalIgnoreCase) ? low : low + " – " + high;
        }

        /// <summary>「1.20.1」→ (1, 20, 1)，用于排序；解析不出来的排到最后。</summary>
        private static (int, int, int) Key(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return (int.MaxValue, 0, 0);

            var parts = version.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            int Part(int index) => index < parts.Length && int.TryParse(parts[index], out var n) ? n : 0;

            return (Part(0), Part(1), Part(2));
        }
    }

    /// <summary>
    /// Modrinth 开放 API（无需 Key，文档 https://docs.modrinth.com）。
    ///
    /// 两条接口就够用：<c>/v2/search</c> 搜项目，<c>/v2/project/{id}/version</c> 取文件。
    /// 依赖信息直接在 version 的 <c>dependencies</c> 里，按 <c>dependency_type = required</c> 递归补齐。
    /// </summary>
    public static class ModrinthSource
    {
        /// <summary>官方地址。Modrinth 侧只走这里，镜像已废（原因见下面的 <c>BaseUrl</c>）。</summary>
        private const string Official = "https://api.modrinth.com/v2";

        /// <summary>
        /// Modrinth 一律走官方接口，<paramref name="mirror"/> 只在 CurseForge 侧生效。
        ///
        /// 曾经在「全部」来源下让 Modrinth 也走 MCIM 镜像统一出口，但 2026-11-01 核出
        /// **镜像的版本数据严重滞后**：Iris 官方最新已到 1.7.6（2025-03-03），
        /// 镜像最新只到 1.7.2（2024-06-29），差八个多月。后果是「全部」来源下
        /// 点安装会装到旧版本，且依赖解析也跟着退化（Sodium 只到 0.5.11）。
        ///
        /// 镜像本来就是为绕开 CurseForge 官方 API 的 Key 限制才引入的，
        /// Modrinth 官方接口不需要 Key、国内可达，没有走镜像的理由。
        /// </summary>
        private static string BaseUrl(bool mirror) => Official;

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
            int limit = 20, int offset = 0, CancellationToken ct = default, bool mirror = false,
            string index = "relevance")
        {
            var facets = new List<string> { "[\"project_type:mod\"]" };
            if (!string.IsNullOrWhiteSpace(mcVersion))
                facets.Add($"[\"versions:{mcVersion}\"]");

            var l = LoaderFacet(loader);
            if (!string.IsNullOrEmpty(l))
                facets.Add($"[\"categories:{l}\"]");

            return await QueryAsync(query, facets, limit, offset, mirror, ct, index).ConfigureAwait(false);
        }

        /// <summary>
        /// 按项目类型搜索，**不带版本 / 加载器过滤**。
        ///
        /// 光影与材质包的版本约束比模组松得多（一个光影常常同时支持 1.7.10 到 1.21），
        /// 硬过滤会漏掉大半结果 —— 所以这两类由用户自己在结果里挑包版本。
        /// </summary>
        /// <param name="projectType">Modrinth 的 <c>project_type</c>：<c>shader</c> / <c>resourcepack</c> / <c>mod</c> …</param>
        /// <param name="index">排序方式：<c>relevance</c> / <c>downloads</c> / <c>newest</c> / <c>updated</c>；
        /// 关键词为空时用 <c>downloads</c> 就能拿到「下载量最高的」。</param>
        public static async Task<List<ModSearchHit>> SearchByTypeAsync(string query, string projectType,
            int limit = 20, int offset = 0, CancellationToken ct = default, string index = "relevance")
        {
            var facets = new List<string> { $"[\"project_type:{projectType}\"]" };
            return await QueryAsync(query, facets, limit, offset, false, ct, index).ConfigureAwait(false);
        }

        private static async Task<List<ModSearchHit>> QueryAsync(string query, List<string> facets,
            int limit, int offset, bool mirror, CancellationToken ct, string index = "relevance")
        {
            var url = $"{BaseUrl(mirror)}/search?query={Uri.EscapeDataString(query ?? "")}" +
                      $"&limit={limit}&offset={offset}" +
                      $"&index={Uri.EscapeDataString(string.IsNullOrWhiteSpace(index) ? "relevance" : index)}" +
                      $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

            var node = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);
            var hits = new List<ModSearchHit>();

            foreach (var h in J.Arr(node, "hits") ?? new JsonArray())
            {
                if (h is not JsonObject o) continue;

                var hit = new ModSearchHit
                {
                    Source = "Modrinth",
                    ProjectId = J.Str(o, "project_id") ?? "",
                    Slug = J.Str(o, "slug") ?? "",
                    Title = J.Str(o, "title") ?? "",
                    Description = J.Str(o, "description") ?? "",
                    Author = J.Str(o, "author") ?? "",
                    Downloads = J.Long(o, "downloads"),
                    IconUrl = J.Str(o, "icon_url") ?? "",
                    Mirror = mirror
                };

                // 支持版本与更新时间只在搜索结果里就有，不必为每一条再请求一次项目详情
                foreach (var v in J.Arr(o, "versions") ?? new JsonArray())
                    if (v != null) hit.GameVersions.Add(v.ToString());

                if (DateTime.TryParse(J.Str(o, "date_modified"), out var updated))
                    hit.Updated = updated;

                hits.Add(hit);
            }

            return hits;
        }

        /// <summary>列出该项目适用于指定 MC 版本 + 加载器的文件（新的在前）。</summary>
        public static async Task<List<ModFileRef>> ListFilesAsync(string projectId, string mcVersion, string loader,
            CancellationToken ct = default, bool mirror = false)
        {
            var l = LoaderFacet(loader);
            var url = $"{BaseUrl(mirror)}/project/{Uri.EscapeDataString(projectId)}/version";
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
                    VersionId = J.Str(o, "id") ?? "",
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
        /// 取某个版本声明的必需依赖（只看 required），返回 (projectId, versionId, 依赖说明)。
        /// <para>
        /// <c>versionId</c> 是依赖项里声明的<b>具体版本</b>：模组作者要求「Flywheel 0.6.10」时这里就有值，
        /// 安装阶段据此精确落盘，而不是装该前置的最新版——后者可能已经超出作者验证过的范围。
        /// 只标了 project 没标版本的依赖，这里为空，安装阶段回落到「最新适配版」。
        /// </para>
        /// 依赖的具体文件在安装阶段再解析。
        /// </summary>
        public static async Task<List<(string ProjectId, string VersionId, string Name)>> RequiredDepsAsync(
            ModFileRef file, string mcVersion, string loader, CancellationToken ct = default,
            bool mirror = false)
        {
            var result = new List<(string, string, string)>();
            if (file == null) return result;

            // 首选：直接按 version_id 取这一个版本（一次请求，且不会因版本号重复而认错）
            JsonObject o = null;
            if (!string.IsNullOrEmpty(file.VersionId))
            {
                o = await Net.GetJsonRawAsync(
                        $"{BaseUrl(mirror)}/version/{Uri.EscapeDataString(file.VersionId)}", ct)
                    .ConfigureAwait(false) as JsonObject;
            }

            // 兜底：没有 version_id（旧数据 / 其它来源）时，仍按版本号在项目版本列表里找
            if (o == null)
            {
                var l = LoaderFacet(loader);
                var url = $"{BaseUrl(mirror)}/project/{Uri.EscapeDataString(file.ProjectId)}/version";
                if (!string.IsNullOrWhiteSpace(mcVersion))
                    url += "?game_versions=" + Uri.EscapeDataString($"[\"{mcVersion}\"]");
                if (!string.IsNullOrEmpty(l))
                    url += (url.Contains('?') ? "&" : "?") + "loaders=" + Uri.EscapeDataString($"[\"{l}\"]");

                var arr = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);
                foreach (var v in arr as JsonArray ?? new JsonArray())
                {
                    if (v is not JsonObject candidate) continue;
                    if (J.Str(candidate, "version_number") != file.VersionLabel) continue;
                    o = candidate;
                    break;
                }
            }

            if (o == null) return result;

            foreach (var d in J.Arr(o, "dependencies") ?? new JsonArray())
            {
                if (d is not JsonObject dep) continue;
                if (!string.Equals(J.Str(dep, "dependency_type"), "required", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pid = J.Str(dep, "project_id");
                if (string.IsNullOrEmpty(pid)) continue;

                result.Add((pid, J.Str(dep, "version_id"), J.Str(dep, "file_name") ?? ""));
            }

            return result;
        }

        /// <summary>
        /// 按版本 ID 精确取文件。依赖声明里带了 <c>version_id</c> 时用它，把版本钉死。
        /// <para>
        /// 声明的版本若不适配当前 MC 版本 / 加载器，返回 null 并记一条日志，
        /// 由调用方回落到「最新适配版」——宁可装新版，也不要装一个明确不兼容的文件。
        /// </para>
        /// </summary>
        public static async Task<ModFileRef> FileOfVersionAsync(string versionId, string projectId,
            string mcVersion, string loader, CancellationToken ct = default, bool mirror = false)
        {
            if (string.IsNullOrWhiteSpace(versionId)) return null;

            JsonObject o;
            try
            {
                o = await Net.GetJsonRawAsync(
                        $"{BaseUrl(mirror)}/version/{Uri.EscapeDataString(versionId)}", ct)
                    .ConfigureAwait(false) as JsonObject;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"取版本 {versionId} 失败：{ex.Message}");
                return null;
            }

            if (o == null) return null;

            var label = J.Str(o, "version_number") ?? versionId;

            var games = StrArr(o, "game_versions");
            if (!string.IsNullOrEmpty(mcVersion) && games.Count > 0 &&
                !games.Contains(mcVersion, StringComparer.OrdinalIgnoreCase))
            {
                Log.Warn($"依赖声明的版本 {label} 不含 MC {mcVersion}（支持：{VersionRange.Text(games)}）");
                return null;
            }

            var want = LoaderFacet(loader);
            var loaders = StrArr(o, "loaders");
            if (!string.IsNullOrEmpty(want) && loaders.Count > 0 &&
                !loaders.Contains(want, StringComparer.OrdinalIgnoreCase))
            {
                Log.Warn($"依赖声明的版本 {label} 不含加载器 {loader}（支持：{string.Join("/", loaders)}）");
                return null;
            }

            var files = J.Arr(o, "files");
            if (files == null) return null;

            JsonObject picked = null;
            foreach (var f in files)
            {
                if (f is JsonObject fo && J.Bool(fo, "primary")) { picked = fo; break; }
            }
            picked ??= files.FirstOrDefault() as JsonObject;
            if (picked == null) return null;

            var url = J.Str(picked, "url");
            if (string.IsNullOrEmpty(url)) return null;

            return new ModFileRef
            {
                Source = "Modrinth",
                ProjectId = projectId ?? J.Str(o, "project_id") ?? "",
                VersionId = versionId,
                VersionLabel = label,
                FileName = J.Str(picked, "filename") ?? "",
                Url = url,
                Sha1 = J.Str(J.Obj(picked, "hashes"), "sha1"),
                Size = J.Long(picked, "size")
            };
        }

        /// <summary>
        /// 批量取项目 slug（一次请求）。slug 通常等于模组的 modid，
        /// 用来判断「这个前置是不是已经装过」——不必下载下来读 jar。
        /// 返回 projectId → slug；查不到的项目不出现在结果里。
        /// </summary>
        public static async Task<Dictionary<string, string>> SlugsAsync(IReadOnlyList<string> projectIds,
            CancellationToken ct = default, bool mirror = false)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (projectIds == null || projectIds.Count == 0) return map;

            var arr = new JsonArray();
            foreach (var id in projectIds.Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                arr.Add(JsonValue.Create(id));
            }

            if (arr.Count == 0) return map;

            try
            {
                var url = $"{BaseUrl(mirror)}/projects?ids=" + Uri.EscapeDataString(arr.ToJsonString());
                var node = await Net.GetJsonRawAsync(url, ct).ConfigureAwait(false);

                foreach (var o in (node as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var id = J.Str(o, "id");
                    var slug = J.Str(o, "slug");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(slug)) map[id] = slug;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"批量查项目 slug 失败：{ex.Message}");
            }

            return map;
        }

        /// <summary>
        /// 按文件 SHA1 批量查「有没有更新的版本」。
        /// <para>
        /// 接口语义要小心：**已经是最新时也会把当前版本返回给你**，所以调用方必须自己比对
        /// 「返回的主文件 sha1 是否等于请求的那个」，相等才算「已是最新」（见 <c>ModUpdater</c>）。
        /// </para>
        /// 返回：请求的 hash → 该项目在给定 MC 版本 / 加载器下的最新版本对象。
        /// </summary>
        public static async Task<Dictionary<string, JsonObject>> CheckUpdatesAsync(
            IReadOnlyList<string> hashes, string mcVersion, string loader, CancellationToken ct = default,
            bool mirror = false)
        {
            var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            if (hashes == null || hashes.Count == 0) return result;

            var arr = new JsonArray();
            foreach (var h in hashes)
            {
                if (!string.IsNullOrWhiteSpace(h)) arr.Add(JsonValue.Create(h));
            }
            if (arr.Count == 0) return result;

            var body = new JsonObject
            {
                ["hashes"] = arr,
                ["algorithm"] = "sha1"
            };

            var l = LoaderFacet(loader);
            if (!string.IsNullOrEmpty(l))
            {
                var loaders = new JsonArray();
                loaders.Add(JsonValue.Create(l));
                body["loaders"] = loaders;
            }

            if (!string.IsNullOrWhiteSpace(mcVersion))
            {
                var games = new JsonArray();
                games.Add(JsonValue.Create(mcVersion));
                body["game_versions"] = games;
            }

            var node = await Net.PostJsonAsync($"{BaseUrl(mirror)}/version_files/update", body, ct)
                .ConfigureAwait(false);

            foreach (var pair in node as JsonObject ?? new JsonObject())
            {
                if (pair.Value is JsonObject v) result[pair.Key] = v;
            }

            return result;
        }

        /// <summary>把 JSON 数组里的字符串元素取出来（读 <c>game_versions</c> / <c>loaders</c> 用）。</summary>
        private static List<string> StrArr(JsonNode node, string key)
        {
            var arr = J.Arr(node, key);
            var list = new List<string>();
            if (arr == null) return list;

            foreach (var n in arr)
            {
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    list.Add(s);
            }

            return list;
        }

        /// <summary>取项目标题（用于依赖列表里显示人话）。</summary>
        public static async Task<string> ProjectTitleAsync(string projectId, CancellationToken ct = default,
            bool mirror = false)
        {
            try
            {
                var node = await Net.GetJsonRawAsync(
                        $"{BaseUrl(mirror)}/project/{Uri.EscapeDataString(projectId)}", ct)
                    .ConfigureAwait(false);
                return J.Str(node, "title") ?? projectId;
            }
            catch (Exception ex)
            {
                Log.Warn($"查询 Modrinth 项目 {projectId} 失败：{ex.Message}");
                return projectId;
            }
        }

        /// <summary>
        /// 按 slug / 项目 ID 取项目详情。
        ///
        /// 中文搜索要用它核对两件事：这个百科词条对应的 Modrinth 项目到底存不存在，
        /// 以及它对目标 MC 版本 + 加载器有没有内容 —— 否则列表里会出现点安装必然失败的项。
        /// </summary>
        public static async Task<ModrinthProject> ProjectAsync(string idOrSlug, CancellationToken ct = default,
            bool mirror = false)
        {
            if (string.IsNullOrWhiteSpace(idOrSlug)) return null;

            try
            {
                var node = await Net.GetJsonRawAsync(
                        $"{BaseUrl(mirror)}/project/{Uri.EscapeDataString(idOrSlug.Trim())}", ct)
                    .ConfigureAwait(false);

                if (node is not JsonObject o) return null;

                var project = new ModrinthProject
                {
                    Id = J.Str(o, "id") ?? "",
                    Slug = J.Str(o, "slug") ?? "",
                    Title = J.Str(o, "title") ?? "",
                    Description = J.Str(o, "description") ?? "",
                    IconUrl = J.Str(o, "icon_url") ?? "",
                    ProjectType = J.Str(o, "project_type") ?? "",
                    Downloads = J.Long(o, "downloads")
                };

                foreach (var v in J.Arr(o, "game_versions") ?? new JsonArray())
                    if (v != null) project.GameVersions.Add(v.ToString());

                foreach (var l in J.Arr(o, "loaders") ?? new JsonArray())
                    if (l != null) project.Loaders.Add(l.ToString());

                return project;
            }
            catch (Exception ex)
            {
                // 404 很常见：百科登记的 Modrinth 项目可能已被作者删除
                Log.Warn($"查询 Modrinth 项目 {idOrSlug} 失败：{ex.Message}");
                return null;
            }
        }
    }
}
