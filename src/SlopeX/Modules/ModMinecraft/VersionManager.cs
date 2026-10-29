using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMinecraft
{
    /// <summary>已安装版本（轻量信息，用于列表展示）。</summary>
    public sealed class InstalledVersion
    {
        public string Id { get; set; }
        public string Dir { get; set; }
        public string JsonPath { get; set; }
        public string JarPath { get; set; }
        public string Type { get; set; } = "unknown";
        public string InheritsFrom { get; set; }
        public string ReleaseTime { get; set; } = "";
        public string Loader { get; set; } = "原版";
        public string BaseVersion { get; set; }

        /// <summary>是不是本启动器导入的整合包版本（JSON 里带 slopexPack 标记）。</summary>
        public bool IsPack { get; set; }

        public bool HasJar => !string.IsNullOrEmpty(JarPath) && File.Exists(JarPath);

        public string TypeLabel => Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => Type ?? "未知"
        };

        public DateTime ReleaseDate
        {
            get
            {
                if (DateTime.TryParse(ReleaseTime, out var d)) return d;
                return DateTime.MinValue;
            }
        }

        public string CreatedHint
        {
            get
            {
                var d = ReleaseDate;
                return d == DateTime.MinValue ? "" : d.ToString("yyyy-MM-dd");
            }
        }
    }

    /// <summary>合并继承链之后的版本定义（真正拿去启动的东西）。</summary>
    public sealed class ResolvedVersion
    {
        public string Id { get; set; }
        public string RootId { get; set; }
        public string MainClass { get; set; }
        public string Type { get; set; } = "release";
        public string MinecraftArguments { get; set; }
        public JsonArray GameArgs { get; set; }
        public JsonArray JvmArgs { get; set; }
        public string AssetIndexId { get; set; }
        public string AssetIndexUrl { get; set; }
        public long AssetIndexSize { get; set; }
        public string AssetIndexSha1 { get; set; }
        public string LegacyAssets { get; set; }
        public int JavaMajor { get; set; }
        public List<JsonObject> Libraries { get; } = new List<JsonObject>();
        public string ClientUrl { get; set; }
        public long ClientSize { get; set; }
        public string ClientSha1 { get; set; }
        public string JarPath { get; set; }
        public string LogArgument { get; set; }
        public string LogFileId { get; set; }
        public List<string> Chain { get; set; } = new List<string>();

        public string EffectiveAssetIndex => !string.IsNullOrEmpty(AssetIndexId) ? AssetIndexId : (LegacyAssets ?? "legacy");
    }

    public static class VersionManager
    {
        private static readonly Regex BaseVersionRegex = new Regex(@"(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

        /// <summary>扫描 versions 目录下所有版本。</summary>
        public static List<InstalledVersion> Scan(string gameDir)
        {
            var result = new List<InstalledVersion>();
            if (string.IsNullOrWhiteSpace(gameDir)) return result;

            var versionsDir = Paths.VersionsDir(gameDir);
            if (!Directory.Exists(versionsDir)) return result;

            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue;

                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(json)) as JsonObject;
                    if (root == null) continue;

                    var v = new InstalledVersion
                    {
                        Id = J.Str(root, "id") ?? id,
                        Dir = dir,
                        JsonPath = json,
                        JarPath = Path.Combine(dir, id + ".jar"),
                        Type = J.Str(root, "type") ?? "unknown",
                        InheritsFrom = J.Str(root, "inheritsFrom"),
                        ReleaseTime = J.Str(root, "releaseTime") ?? "",
                        Loader = DetectLoader(root, id)
                    };
                    v.BaseVersion = ResolveBaseVersion(root, id);
                    v.IsPack = root.ContainsKey("slopexPack");   // 由整合包导入写入的标记
                    result.Add(v);
                }
                catch (Exception ex)
                {
                    Log.Warn($"跳过无法解析的版本 {id}：{ex.Message}");
                }
            }

            return result.OrderByDescending(v => v.ReleaseDate).ThenBy(v => v.Id).ToList();
        }

        private static string DetectLoader(JsonObject root, string id)
        {
            // 线索来源：版本 ID、inheritsFrom、自身 libraries。
            // 整合包版本自身没有 libraries，只有 inheritsFrom 指向加载器版本，
            // 只看 ID 与 libraries 会把它误判成"原版"。
            var name = (id + " " + (J.Str(root, "inheritsFrom") ?? "")).ToLowerInvariant();
            var libs = J.Arr(root, "libraries");
            var libText = libs?.ToJsonString().ToLowerInvariant() ?? "";

            if (name.Contains("neoforge") || libText.Contains("net.neoforged")) return "NeoForge";
            if (name.Contains("fabric") || libText.Contains("net.fabricmc")) return "Fabric";
            if (name.Contains("quilt") || libText.Contains("org.quiltmc")) return "Quilt";
            if (name.Contains("forge") || libText.Contains("minecraftforge")) return "Forge";
            if (name.Contains("optifine") || libText.Contains("optifine")) return "OptiFine";
            return "原版";
        }

        private static string ResolveBaseVersion(JsonObject root, string id)
        {
            var cur = root;
            var depth = 0;
            while (cur != null && depth++ < 12)
            {
                var parent = J.Str(cur, "inheritsFrom");
                if (string.IsNullOrEmpty(parent))
                {
                    var ownId = J.Str(cur, "id") ?? id;
                    var m = BaseVersionRegex.Match(ownId);
                    return m.Success ? m.Value : ownId;
                }

                var gameDir = Config.Current.GameDir;
                var path = Path.Combine(Paths.VersionsDir(gameDir), parent, parent + ".json");
                if (!File.Exists(path))
                {
                    var m2 = BaseVersionRegex.Match(parent);
                    return m2.Success ? m2.Value : parent;
                }
                try { cur = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
                catch { break; }
            }

            var fallback = BaseVersionRegex.Match(id);
            return fallback.Success ? fallback.Value : id;
        }

        /// <summary>读取并合并整条继承链。</summary>
        public static ResolvedVersion Resolve(string gameDir, string versionId)
        {
            var chain = new List<JsonObject>();
            var ids = new List<string>();

            var cur = versionId;
            for (int depth = 0; depth < 12 && !string.IsNullOrEmpty(cur); depth++)
            {
                var path = Path.Combine(Paths.VersionsDir(gameDir), cur, cur + ".json");
                if (!File.Exists(path))
                {
                    if (depth == 0) throw new FileNotFoundException($"找不到版本 JSON：{path}");
                    Log.Warn($"继承链在 {cur} 处断开，继续使用已读取的部分");
                    break;
                }

                chain.Add(JsonNode.Parse(File.ReadAllText(path)) as JsonObject);
                ids.Add(cur);

                var parent = J.Str(chain[chain.Count - 1], "inheritsFrom");
                if (string.IsNullOrEmpty(parent) || ids.Contains(parent)) break;
                cur = parent;
            }

            chain.Reverse();
            ids.Reverse();

            var rv = new ResolvedVersion { Id = versionId, Chain = ids };
            rv.RootId = ids.FirstOrDefault();

            var libIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var gameArgs = new JsonArray();
            var jvmArgs = new JsonArray();

            foreach (var node in chain)
            {
                if (!(node is JsonObject obj)) continue;

                var main = J.Str(obj, "mainClass");
                if (!string.IsNullOrEmpty(main)) rv.MainClass = main;

                var type = J.Str(obj, "type");
                if (!string.IsNullOrEmpty(type)) rv.Type = type;

                var mArgs = J.Str(obj, "minecraftArguments");
                if (!string.IsNullOrEmpty(mArgs)) rv.MinecraftArguments = mArgs;

                var legacyAssets = J.Str(obj, "assets");
                if (!string.IsNullOrEmpty(legacyAssets)) rv.LegacyAssets = legacyAssets;

                var ai = J.Obj(obj, "assetIndex");
                if (ai != null)
                {
                    var aiId = J.Str(ai, "id");
                    if (!string.IsNullOrEmpty(aiId))
                    {
                        rv.AssetIndexId = aiId;
                        rv.AssetIndexUrl = J.Str(ai, "url");
                        rv.AssetIndexSize = J.Long(ai, "size");
                        rv.AssetIndexSha1 = J.Str(ai, "sha1");
                    }
                }

                var jv = J.Obj(obj, "javaVersion");
                var major = (int)J.Long(jv, "majorVersion");
                if (major > 0) rv.JavaMajor = major;

                var logging = J.Obj(J.Obj(obj, "logging"), "client");
                if (logging != null)
                {
                    var logArg = J.Str(logging, "argument");
                    var logId = J.Str(J.Obj(logging, "file"), "id");
                    if (!string.IsNullOrEmpty(logArg)) rv.LogArgument = logArg;
                    if (!string.IsNullOrEmpty(logId)) rv.LogFileId = logId;
                }

                var downloads = J.Obj(obj, "downloads");
                var client = J.Obj(downloads, "client");
                if (client != null)
                {
                    var url = J.Str(client, "url");
                    if (!string.IsNullOrEmpty(url))
                    {
                        rv.ClientUrl = url;
                        rv.ClientSize = J.Long(client, "size");
                        rv.ClientSha1 = J.Str(client, "sha1");
                    }
                }

                // 参数拼接：父在前、子在后
                var argsNode = J.Obj(obj, "arguments");
                if (argsNode != null)
                {
                    if (argsNode["game"] is JsonArray g)
                        foreach (var item in g.ToList()) gameArgs.Add(item?.DeepClone());
                    if (argsNode["jvm"] is JsonArray j)
                        foreach (var item in j.ToList()) jvmArgs.Add(item?.DeepClone());
                }

                // 库：同 GA 由子版本覆盖，其余追加
                var libs = J.Arr(obj, "libraries");
                if (libs == null) continue;
                foreach (var libNode in libs)
                {
                    if (!(libNode is JsonObject lib)) continue;
                    var name = J.Str(lib, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    var parts = name.Split(':');
                    // 去重键必须带上 classifier：lwjgl 本体与 natives-windows 是不同坐标
                    var ga = parts.Length >= 2 ? parts[0] + ":" + parts[1] : name;
                    if (parts.Length >= 4) ga += ":" + parts[3];
                    var clone = (JsonObject)lib.DeepClone();

                    if (libIndex.TryGetValue(ga, out var idx))
                        rv.Libraries[idx] = clone;
                    else
                    {
                        libIndex[ga] = rv.Libraries.Count;
                        rv.Libraries.Add(clone);
                    }
                }
            }

            if (gameArgs.Count > 0) rv.GameArgs = gameArgs;
            if (jvmArgs.Count > 0) rv.JvmArgs = jvmArgs;

            // 找客户端 jar：从子版本往上找第一个存在的
            foreach (var cid in Enumerable.Reverse(ids))
            {
                var jar = Path.Combine(Paths.VersionsDir(gameDir), cid, cid + ".jar");
                if (File.Exists(jar)) { rv.JarPath = jar; break; }
            }

            if (rv.Libraries.Count == 0 && string.IsNullOrEmpty(rv.MainClass))
                throw new InvalidDataException("版本 JSON 缺少 libraries 与 mainClass，可能文件已损坏");

            return rv;
        }
    }
}
