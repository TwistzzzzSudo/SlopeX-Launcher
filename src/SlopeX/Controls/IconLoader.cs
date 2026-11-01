using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMods;
using SlopeX.Modules.ModNet;

namespace SlopeX.Controls
{
    /// <summary>
    /// 模组 / 资源包图标的取与转。
    ///
    /// 两个来源：jar 里自带的图（本地模组，最准）与平台的项目图标（搜索结果）。
    /// 图统一以 <c>byte[]</c> 挂在模型上，界面通过 <see cref="IconConverter"/> 转 BitmapImage——
    /// 模型层不碰 WPF 类型，绑定也不会每次求值都重新解码。
    /// </summary>
    public static class IconLoader
    {
        private static readonly ConcurrentDictionary<string, byte[]> UrlCache =
            new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>下载平台图标（带内存缓存）。失败返回 null——图标是装饰，不该影响列表。</summary>
        public static async Task<byte[]> FromUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (UrlCache.TryGetValue(url, out var cached)) return cached;

            try
            {
                var bytes = await Net.GetBytesAsync(url).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;

                UrlCache[url] = bytes;
                return bytes;
            }
            catch (Exception ex)
            {
                Log.Warn($"取图标失败 {url}：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 并发给一批搜索结果补图标（限 8 路）。拿到一个挂一个——
        /// <see cref="ModSearchHit.IconData"/> 会发属性通知，列表自己刷新，不用整表重绑。
        /// 图标只是装饰：超时、失败都只留首字母占位，不抛给调用方。
        /// </summary>
        public static async Task FillAsync(IEnumerable<ModSearchHit> hits, CancellationToken ct = default)
        {
            if (hits == null) return;

            var gate = new SemaphoreSlim(8);
            var tasks = new List<Task>();

            foreach (var hit in hits)
            {
                if (hit == null || string.IsNullOrEmpty(hit.IconUrl)) continue;

                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var data = await FromUrlAsync(hit.IconUrl).ConfigureAwait(false);
                        if (data != null && !ct.IsCancellationRequested) hit.IconData = data;
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户又搜了一次，这批图标作废
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // 单个图标的问题各自吞掉了，这里只是兜底
            }
            finally
            {
                gate.Dispose();
            }
        }

        /// <summary>byte[] → 冻结的 BitmapImage。解码失败返回 null（当成没有图标）。</summary>
        public static BitmapImage ToImage(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                // 列表里图标最大 32 px；按 64 解码，高分屏下不糊
                bmp.DecodePixelWidth = 64;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>byte[] → BitmapImage。空值返回 null，界面上会露出底层的占位块。</summary>
    public sealed class IconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => IconLoader.ToImage(value as byte[]);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
