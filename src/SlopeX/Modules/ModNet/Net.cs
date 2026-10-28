using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModNet
{
    /// <summary>HTTP 访问与镜像重写（对应 PCL2 的 ModNet）。</summary>
    public static class Net
    {
        public static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SlopeX/1.0 (Minecraft Launcher)");
            return c;
        }

        /// <summary>国内加速：把 Mojang 官方源改写到 BMCLAPI 镜像。</summary>
        public static string Mirror(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (!Config.Current.UseMirror) return url;

            const string mirror = "https://bmclapi2.bangbang93.com";
            try
            {
                var u = new Uri(url);
                switch (u.Host.ToLowerInvariant())
                {
                    case "launchermeta.mojang.com":
                    case "launcher.mojang.com":
                    case "piston-meta.mojang.com":
                    case "piston-data.mojang.com":
                        return mirror + u.PathAndQuery;
                    case "libraries.minecraft.net":
                        return mirror + "/maven" + u.PathAndQuery;
                    case "resources.download.minecraft.net":
                        return mirror + "/assets" + u.PathAndQuery;
                    default:
                        return url;
                }
            }
            catch
            {
                return url;
            }
        }

        public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            var target = Mirror(url);
            using var resp = await Client.GetAsync(target, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }

        public static async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct = default)
        {
            var text = await GetStringAsync(url, ct);
            return JsonNode.Parse(text);
        }

        public static string Sha1OfFile(string path)
        {
            using var sha = SHA1.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
    }

    public enum JobState { Pending, Running, Done, Skipped, Failed, Canceled }

    public sealed class DownloadJob : ObservableObject
    {
        private long _downloaded;
        private JobState _state = JobState.Pending;
        private string _error;

        public string Url { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string Sha1 { get; set; }

        public long Downloaded
        {
            get => _downloaded;
            set => Set(ref _downloaded, value);
        }

        public JobState State
        {
            get => _state;
            set => Set(ref _state, value);
        }

        public string Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        public double Percent => Size > 0 ? Math.Min(100, _downloaded * 100.0 / Size) : (_state == JobState.Done ? 100 : 0);
    }

    /// <summary>并发下载队列：统一进度、速度、失败重试（对应 PCL2 的下载模块）。</summary>
    public sealed class DownloadQueue : ObservableObject
    {
        public ObservableCollection<DownloadJob> Jobs { get; } = new ObservableCollection<DownloadJob>();

        private long _totalBytes;
        private long _doneBytes;
        private int _finished;
        private int _failed;
        private double _speed;
        private bool _running;
        private string _stage = "";

        public long TotalBytes { get => _totalBytes; private set => Set(ref _totalBytes, value); }
        public long DoneBytes { get => _doneBytes; private set => Set(ref _doneBytes, value); }
        public int Finished { get => _finished; private set => Set(ref _finished, value); }
        public int Failed { get => _failed; private set => Set(ref _failed, value); }
        public double Speed { get => _speed; private set => Set(ref _speed, value); }
        public bool Running { get => _running; private set => Set(ref _running, value); }
        public string Stage { get => _stage; private set => Set(ref _stage, value); }

        public int Total => Jobs.Count;
        public double Percent => _totalBytes > 0 ? Math.Min(100, _doneBytes * 100.0 / _totalBytes) : 0;

        public event Action Changed;

        private long _lastEmit;
        private void Emit(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - _lastEmit < 120) return;
            _lastEmit = now;
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(Total));
            Changed?.Invoke();
        }

        /// <summary>同步入队（在开始下载前调用）。</summary>
        public void Enqueue(DownloadJob job)
        {
            if (job == null) return;
            Jobs.Add(job);
            TotalBytes += Math.Max(0, job.Size);
        }

        public void Clear()
        {
            Jobs.Clear();
            TotalBytes = 0;
            DoneBytes = 0;
            Finished = 0;
            Failed = 0;
            Speed = 0;
            Stage = "";
            Emit(true);
        }

        /// <summary>执行队列。返回失败数量。</summary>
        public async Task<int> RunAsync(int concurrency = 16, CancellationToken ct = default)
        {
            if (Jobs.Count == 0) return 0;

            Running = true;
            Failed = 0;
            Finished = 0;
            DoneBytes = 0;
            Emit(true);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long startDone = 0;

            // 速度采样
            using var speedTimer = new Timer(_ =>
            {
                try
                {
                    var elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed > 0.5)
                    {
                        var delta = Interlocked.Read(ref _doneBytes) - startDone;
                        Speed = delta / elapsed;
                    }
                }
                catch { /* ignored */ }
            }, null, 600, 600);

            var gate = new SemaphoreSlim(concurrency);
            var tasks = new List<Task>();

            foreach (var job in Jobs.ToList())
            {
                if (ct.IsCancellationRequested) break;

                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DownloadOneAsync(job, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        job.State = JobState.Canceled;
                    }
                    catch (Exception ex)
                    {
                        job.State = JobState.Failed;
                        job.Error = ex.Message;
                        Interlocked.Increment(ref _failed);
                        Log.Warn($"下载失败 {job.Name ?? job.Url}：{ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Increment(ref _finished);
                        OnPropertyChanged(nameof(Finished));
                        gate.Release();
                        Emit();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* 已取消 */ }

            speedTimer.Dispose();
            Speed = 0;
            Running = false;
            Emit(true);
            Log.Info($"下载队列结束：成功 {Jobs.Count(j => j.State == JobState.Done || j.State == JobState.Skipped)}，失败 {Failed}");
            return Failed;
        }

        private async Task DownloadOneAsync(DownloadJob job, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(job.Path)) { job.State = JobState.Skipped; return; }

            // 已存在且大小匹配 → 直接跳过
            if (File.Exists(job.Path))
            {
                var len = new FileInfo(job.Path).Length;
                if (job.Size > 0 && len == job.Size)
                {
                    job.State = JobState.Skipped;
                    job.Downloaded = len;
                    Interlocked.Add(ref _doneBytes, job.Size);
                    OnPropertyChanged(nameof(DoneBytes));
                    Emit();
                    return;
                }
            }

            job.State = JobState.Running;
            var dir = System.IO.Path.GetDirectoryName(job.Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = job.Path + ".sxpart";
            Exception last = null;

            // 候选源：优先镜像，镜像失败后回落官方源
            var candidates = new List<string>();
            var mirrorUrl = Net.Mirror(job.Url);
            if (!string.Equals(mirrorUrl, job.Url, StringComparison.OrdinalIgnoreCase)) candidates.Add(mirrorUrl);
            candidates.Add(job.Url);

            foreach (var target in candidates)
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    job.Downloaded = 0;

                    // 单文件超时：按体积估算，避免连接假死时一直挂到全局超时
                    var timeoutMs = job.Size > 0
                        ? (int)Math.Min(600000, Math.Max(60000, job.Size / 1024 / 100 * 1000))
                        : 60000;
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(timeoutMs);
                    var token = timeoutCts.Token;

                    using (var resp = await Net.Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();

                        using (var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            var buffer = new byte[81920];
                            int read;
                            long got = 0;
                            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                            {
                                await dst.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                                got += read;
                                job.Downloaded = got;
                                Interlocked.Add(ref _doneBytes, read);
                                OnPropertyChanged(nameof(DoneBytes));
                                Emit();
                            }
                        }

                        if (job.Size > 0 && new FileInfo(tmp).Length != job.Size)
                            throw new IOException($"大小不符（期望 {job.Size}，实际 {new FileInfo(tmp).Length}）");
                    }

                    // 只对小型文件做 SHA1 校验，避免大文件重复读盘
                    if (!string.IsNullOrEmpty(job.Sha1) && job.Size > 0 && job.Size < 8 * 1024 * 1024)
                    {
                        var actual = Net.Sha1OfFile(tmp);
                        if (!string.Equals(actual, job.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("SHA1 校验失败");
                    }

                    if (File.Exists(job.Path)) File.Delete(job.Path);
                    File.Move(tmp, job.Path);
                    job.State = JobState.Done;
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // 用户主动取消
                }
                catch (Exception ex)
                {
                    // 非用户取消的 OperationCanceledException 即单文件超时，按失败重试
                    last = ex is OperationCanceledException ? new TimeoutException("下载超时") : ex;
                    // 重试前把已计入的字节回滚，避免进度虚高
                    Interlocked.Add(ref _doneBytes, -job.Downloaded);
                    job.Downloaded = 0;
                    Emit(true);
                    if (attempt < 1) await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignored */ }
                }
            }

            throw last ?? new IOException("未知下载错误");
        }
    }

    public static class Format
    {
        public static string Bytes(long value)
        {
            if (value < 0) return "-";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = value;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
        }

        public static string Speed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "—";
            return Bytes((long)bytesPerSecond) + "/s";
        }
    }
}
