using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModNet
{
    /// <summary>一次下载的实时进度：已经落盘的字节、文件总大小、当前平均速度。</summary>
    public readonly struct DownloadProgress
    {
        public DownloadProgress(long done, long total, double bytesPerSecond)
        {
            Done = done;
            Total = total;
            BytesPerSecond = bytesPerSecond;
        }

        /// <summary>已下载字节。</summary>
        public long Done { get; }

        /// <summary>文件总大小；0 表示服务器没给（进度条按不确定处理）。</summary>
        public long Total { get; }

        /// <summary>本次下载的平均速度（字节/秒）。</summary>
        public double BytesPerSecond { get; }

        public double Percent => Total > 0 ? Math.Min(100, Done * 100.0 / Total) : 0;

        /// <summary>进度条右侧那行字：<c>42% · 1.2 MB/s</c>。</summary>
        public string CompactText => Total > 0
            ? $"{Percent:0}%  ·  {Format.Speed(BytesPerSecond)}"
            : Format.Speed(BytesPerSecond);

        /// <summary>带体积的完整写法，给 tooltip 或命令行用。</summary>
        public string DetailText => Total > 0
            ? $"{Percent:0}%  ·  {Format.Bytes(Done)} / {Format.Bytes(Total)}  ·  {Format.Speed(BytesPerSecond)}"
            : $"{Format.Bytes(Done)}  ·  {Format.Speed(BytesPerSecond)}";
    }

    /// <summary>
    /// 分段下载引擎。
    ///
    /// 为什么需要它：cdn.modrinth.com 这类源对**单条连接**的限速很低（本机实测 35 KB/s），
    /// 但把同一个文件拆成几十段并发拉，聚合速度能到 2–3 MB/s——浏览器快就是这个原因。
    ///
    /// 策略：
    /// <list type="bullet">
    /// <item>先用 HEAD（失败再 Range 探测）确认总大小与 <c>Accept-Ranges</c>；不支持就退回单流。</item>
    /// <item>小于 1 MB 不拆——握手开销比省下的时间还多。</item>
    /// <item>按 <see cref="Config.DownloadThreads"/> 条连接并发，分片 256 KB–8 MB，按剩余量动态领取。</item>
    /// <item>单段失败从断点续传（已写进文件的偏移不重下），三次仍失败则整体回退单流。</item>
    /// <item>全程写同一个 <c>.sxpart</c> 的绝对偏移（<see cref="RandomAccess"/>），不产生一堆临时分片，也不需要合并。</item>
    /// </list>
    /// </summary>
    internal static class Downloader
    {
        private const long MinChunk = 256 * 1024;
        private const long MaxChunk = 8 * 1024 * 1024;
        private const long SplitThreshold = 1024 * 1024;
        private const int BufferSize = 64 * 1024;

        public static async Task DownloadAsync(string url, string targetPath, string sha1, long size,
            CancellationToken ct, int timeoutMs, Action<DownloadProgress> progress)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
            if (string.IsNullOrEmpty(targetPath)) throw new ArgumentNullException(nameof(targetPath));

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");

            var tmp = targetPath + ".sxpart";

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(Math.Max(30000, timeoutMs));

                var probe = await ProbeAsync(url, timeoutCts.Token).ConfigureAwait(false);
                var total = probe.Total > 0 ? probe.Total : Math.Max(0, size);

                var ok = false;
                if (probe.SupportsRange && total >= SplitThreshold)
                {
                    try
                    {
                        // 打跳转后的真实地址：带 Range 请求打跳转前的域名会被部分 CDN 判 404
                        await DownloadSegmentedAsync(probe.ResolvedUrl, tmp, total, timeoutCts.Token, progress)
                            .ConfigureAwait(false);
                        ok = true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 探测说支持、真下起来又不认（代理改写、CDN 边缘节点差异）；
                        // 与其失败，不如退回单流——慢一点但一定能下完。
                        Log.Warn($"分段下载失败，回退单线程：{Path.GetFileName(targetPath)}（{ex.Message}）");
                        TryDelete(tmp);
                    }
                }

                if (!ok)
                    await DownloadSingleAsync(url, tmp, total, timeoutCts.Token, progress).ConfigureAwait(false);

                if (size > 0)
                {
                    var got = new FileInfo(tmp).Length;
                    if (got != size)
                        throw new IOException($"文件大小不符：期望 {Format.Bytes(size)}，实际 {Format.Bytes(got)}");
                }

                if (!string.IsNullOrEmpty(sha1))
                {
                    var actual = Sha1OfFile(tmp);
                    if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"SHA1 校验失败（期望 {sha1}，实际 {actual}）");
                }

                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tmp, targetPath);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        // ---------------- 探测 ----------------

        private readonly struct ProbeResult
        {
            public ProbeResult(long total, bool supportsRange, string resolvedUrl)
            {
                Total = total;
                SupportsRange = supportsRange;
                ResolvedUrl = resolvedUrl;
            }

            public long Total { get; }
            public bool SupportsRange { get; }

            /// <summary>
            /// 跟随 302 之后的真实地址。分段必须打这个地址：
            /// CurseForge 的 <c>edge.forgecdn.net</c> 带 Range 请求会直接 404，
            /// 而它跳转到的 <c>mediafilez.forgecdn.net</c> 才正常支持分段。
            /// </summary>
            public string ResolvedUrl { get; }
        }

        /// <summary>
        /// 先 HEAD 拿大小与 <c>Accept-Ranges</c>；HEAD 被拒（403/405）或没给 Accept-Ranges 时，
        /// 再用 <c>Range: bytes=0-0</c> 实探一次——有的 CDN 不给头但确实支持分段。
        /// </summary>
        private static async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await Net.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var len = resp.Content.Headers.ContentLength ?? 0;
                    var range = resp.Headers.AcceptRanges.Contains("bytes");
                    var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;

                    if (len > 0 && range) return new ProbeResult(len, true, finalUrl);
                    if (len > 0) return await ProbeRangeAsync(finalUrl, len, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // HEAD 被拒很常见，继续走 Range 探测
            }

            return await ProbeRangeAsync(url, 0, ct).ConfigureAwait(false);
        }

        private static async Task<ProbeResult> ProbeRangeAsync(string url, long knownLength, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(0, 0);

                using var resp = await Net.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    var total = resp.Content.Headers.ContentRange?.Length ?? knownLength;
                    var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
                    return new ProbeResult(total, total > 0, finalUrl);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 探测失败就当不支持分段
            }

            return new ProbeResult(knownLength, false, url);
        }

        // ---------------- 单流 ----------------

        private static async Task DownloadSingleAsync(string url, string tmp, long total, CancellationToken ct,
            Action<DownloadProgress> progress)
        {
            var done = 0L;
            var sw = Stopwatch.StartNew();

            using (var resp = await Net.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                if (total <= 0) total = resp.Content.Headers.ContentLength ?? 0;

                // 总大小要等响应头回来才知道，计时器只能在这之后起
                using var timer = StartReporter(total, () => Interlocked.Read(ref done), sw, progress);

                using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    BufferSize, true);

                var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        Interlocked.Add(ref done, n);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }

            Report(progress, new DownloadProgress(done, total, Speed(done, sw)));
        }

        // ---------------- 分段 ----------------

        private readonly struct Chunk
        {
            public Chunk(long start, long end) { Start = start; End = end; }
            public long Start { get; }
            public long End { get; }
        }

        private static async Task DownloadSegmentedAsync(string url, string tmp, long total, CancellationToken ct,
            Action<DownloadProgress> progress)
        {
            var workers = Math.Clamp(Config.Current.DownloadThreads, 1, 64);
            var chunks = BuildChunks(total, workers);
            if (chunks.Count <= 1)
            {
                await DownloadSingleAsync(url, tmp, total, ct, progress).ConfigureAwait(false);
                return;
            }

            workers = Math.Min(workers, chunks.Count);

            var done = 0L;
            var sw = Stopwatch.StartNew();
            using var timer = StartReporter(total, () => Interlocked.Read(ref done), sw, progress);

            Log.Info($"分段下载 {Path.GetFileName(tmp)}：{Format.Bytes(total)}，{chunks.Count} 段 / {workers} 线程");

            var handle = File.OpenHandle(tmp, FileMode.Create, FileAccess.Write, FileShare.Read,
                FileOptions.Asynchronous);
            try
            {
                RandomAccess.SetLength(handle, total);

                var next = -1;
                var tasks = new List<Task>(workers);
                for (var w = 0; w < workers; w++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        while (true)
                        {
                            var index = Interlocked.Increment(ref next);
                            if (index >= chunks.Count) return;

                            var chunk = chunks[index];
                            await DownloadChunkAsync(url, handle, chunk, ct, n => Interlocked.Add(ref done, n))
                                .ConfigureAwait(false);
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                handle.Dispose();
            }

            Report(progress, new DownloadProgress(total, total, Speed(done, sw)));
        }

        /// <summary>
        /// 切片：目标约 <c>2 × 线程数</c> 段，这样某条连接快一点/慢一点都能被别的线程接走，
        /// 不会出现"15 条连接闲着等最后一段"。
        /// </summary>
        private static List<Chunk> BuildChunks(long total, int workers)
        {
            var wanted = Math.Max(1, workers * 2);
            var chunk = Math.Max(MinChunk, Math.Min(MaxChunk, total / wanted));
            if (chunk <= 0) chunk = MinChunk;

            var list = new List<Chunk>();
            for (long pos = 0; pos < total; pos += chunk)
                list.Add(new Chunk(pos, Math.Min(total, pos + chunk) - 1));

            return list;
        }

        /// <summary>
        /// 下一段。失败从 <c>pos</c> 续传——文件里已经写好的部分不再重下，
        /// 重试也不会把进度条往回拉。
        /// </summary>
        private static async Task DownloadChunkAsync(string url, SafeFileHandle handle, Chunk chunk,
            CancellationToken ct, Action<long> advance)
        {
            var pos = chunk.Start;
            Exception last = null;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    chunkCts.CancelAfter(TimeSpan.FromSeconds(90));   // 单段卡死不要拖住整个文件

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(pos, chunk.End);

                    using var resp = await Net.Client
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, chunkCts.Token)
                        .ConfigureAwait(false);

                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new IOException($"分段请求被拒绝（HTTP {(int)resp.StatusCode}）");

                    using var src = await resp.Content.ReadAsStreamAsync(chunkCts.Token).ConfigureAwait(false);

                    var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int n;
                        while ((n = await src.ReadAsync(buf.AsMemory(0, BufferSize), chunkCts.Token)
                                   .ConfigureAwait(false)) > 0)
                        {
                            if (pos + n > chunk.End + 1)
                                throw new IOException("分段返回的数据超出请求范围");

                            await RandomAccess.WriteAsync(handle, buf.AsMemory(0, n), pos, chunkCts.Token)
                                .ConfigureAwait(false);

                            pos += n;
                            advance(n);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                    }

                    if (pos != chunk.End + 1)
                        throw new IOException($"分段未下完（{pos - chunk.Start}/{chunk.End - chunk.Start + 1} 字节）");

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2)
                        await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }

            throw new IOException("分段下载失败：" + last?.Message, last);
        }

        // ---------------- 进度 ----------------

        /// <summary>每 250 ms 报一次速度；调用方拿到的回调一律吞异常，界面出错不该带崩下载。</summary>
        private static Timer StartReporter(long total, Func<long> done, Stopwatch sw,
            Action<DownloadProgress> progress)
        {
            if (progress == null) return null;

            long lastBytes = 0;
            var lastMs = 0L;

            return new Timer(_ =>
            {
                try
                {
                    var bytes = done();
                    var ms = sw.ElapsedMilliseconds;
                    var elapsed = ms - lastMs;
                    if (elapsed < 200) return;

                    var speed = (bytes - lastBytes) * 1000.0 / elapsed;
                    lastBytes = bytes;
                    lastMs = ms;

                    Report(progress, new DownloadProgress(bytes, total, speed));
                }
                catch
                {
                    // 进度上报失败不影响下载
                }
            }, null, 250, 250);
        }

        private static double Speed(long done, Stopwatch sw) =>
            sw.Elapsed.TotalSeconds > 0.2 ? done / sw.Elapsed.TotalSeconds : 0;

        private static void Report(Action<DownloadProgress> progress, DownloadProgress value)
        {
            if (progress == null) return;
            try { progress(value); }
            catch (Exception ex) { Log.Warn("下载进度回调异常（已忽略）：" + ex.Message); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignored */ }
        }

        private static string Sha1OfFile(string path) => Net.Sha1OfFile(path);
    }
}
