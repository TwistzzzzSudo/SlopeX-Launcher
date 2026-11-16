using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 「下载线程」卡片里的子功能：粘贴一个公开直链，用当前线程数（<c>Config.DownloadThreads</c>）下载。
    ///
    /// 只做公开直链：不转发 Cookie / Referer，所以网盘、需要登录的附件、签名几分钟就过期的链接
    /// 都不在覆盖范围内——探测阶段会因为 403 / 不是文件而给出明确提示。
    /// 落盘目录固定 <c>SlopeXData\downloads</c>，跟着启动器目录走。
    /// </summary>
    public partial class QuickDownload : UserControl
    {
        /// <summary>整个下载给一个宽松上限（6 小时）；单条连接的超时由 HttpClient 管。</summary>
        private const int TimeoutMs = 6 * 60 * 60 * 1000;

        private CancellationTokenSource _cts;
        private bool _busy;

        public QuickDownload()
        {
            InitializeComponent();
        }

        /// <summary>只带用户真填了的头；两个都空就返回 null，走普通下载。</summary>
        private Dictionary<string, string> Headers()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var referer = RefererBox.Text?.Trim();
            if (!string.IsNullOrEmpty(referer)) map["Referer"] = referer;

            var cookie = CookieBox.Text?.Trim();
            if (!string.IsNullOrEmpty(cookie)) map["Cookie"] = cookie;

            return map.Count == 0 ? null : map;
        }

        private static string DownloadDir()
        {
            var dir = Path.Combine(Paths.DataDir, "downloads");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void Url_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _busy) return;
            e.Handled = true;
            _ = StartAsync();
        }

        private void Go_Click(object sender, RoutedEventArgs e) => _ = StartAsync();

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            SetStatus("正在取消…");
        }

        private async Task StartAsync()
        {
            if (_busy) return;

            var url = UrlBox.Text?.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                SetStatus("先粘贴一个 http/https 的下载直链。");
                UrlBox.Focus();
                return;
            }

            _busy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            SetBusy(true);
            OpenDirBtn.Visibility = Visibility.Collapsed;
            Bar.Visibility = Visibility.Visible;
            Bar.Value = 0;
            FileText.Visibility = Visibility.Collapsed;

            var headers = Headers();

            try
            {
                SetStatus("正在探测文件…");
                var (headerName, total, supportsRange) = await Downloader.InspectAsync(url, ct, headers);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                var name = SafeName(headerName ?? NameFromUrl(uri));
                var path = UniquePath(DownloadDir(), name);

                FileText.Text = total > 0
                    ? $"{name}  ·  {Format.Bytes(total)}" + (supportsRange ? "" : "  ·  服务器不支持分段，单连接下载")
                    : name;
                FileText.Visibility = Visibility.Visible;
                SetStatus("");

                // sha1 传 null、size 有就传：引擎会自己再探一次，大小不符会报错
                await Downloader.DownloadAsync(url, path, null, total, ct, TimeoutMs, UpdateProgress, headers);

                SetStatus($"已保存到 {path}");
                OpenDirBtn.Visibility = Visibility.Visible;
                Log.Info($"下载工具完成：{name}，{Format.Bytes(total)}，{path}");
                MainWindow.Current?.ShowToast("下载完成：" + name);
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消。");
            }
            catch (Exception ex)
            {
                SetStatus("下载失败：" + Explain(ex));
                Log.Warn($"下载工具失败 {url}：{ex.Message}");
            }
            finally
            {
                _busy = false;
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
                Bar.Visibility = Visibility.Collapsed;
            }
        }

        // ---------------- 进度 ----------------

        private void UpdateProgress(DownloadProgress p)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateProgress(p)));
                return;
            }

            Bar.Value = p.Total > 0 ? p.Percent : 0;
            // 进度直接并到状态行里，卡片里不再多占一行
            SetStatus(p.DetailText);
        }

        private void SetBusy(bool busy)
        {
            GoBtn.IsEnabled = !busy;
            CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            UrlBox.IsEnabled = !busy;
            RefererBox.IsEnabled = !busy;
            CookieBox.IsEnabled = !busy;
        }

        private void SetStatus(string text) => StatusText.Text = text ?? "";

        // ---------------- 小工具 ----------------

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadDir()) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("打开目录失败：" + ex.Message);
            }
        }

        /// <summary>名字优先用 <c>Content-Disposition</c> 的，其次 URL 最后一段；都没有就叫 download。</summary>
        private static string NameFromUrl(Uri uri)
        {
            var last = uri.Segments.LastOrDefault()?.Trim('/');
            if (string.IsNullOrWhiteSpace(last)) return "download";

            try { last = Uri.UnescapeDataString(last); } catch { /* 保留原样 */ }
            return last;
        }

        private static string SafeName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "download";

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            name = name.Trim('.', ' ');
            if (name.Length == 0) return "download";
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }

        /// <summary>同名文件已存在就加 <c> (2)</c>，不覆盖用户已有的东西。</summary>
        private static string UniquePath(string dir, string name)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) return path;

            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);

            for (var i = 2; i < 1000; i++)
            {
                path = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(path)) return path;
            }

            return Path.Combine(dir, $"{stem}-{DateTime.Now:HHmmss}{ext}");
        }

        /// <summary>把常见失败翻成人话——这个工具价值的一半在这里。</summary>
        private static string Explain(Exception ex)
        {
            var msg = ex.Message ?? "";

            if (msg.Contains("403")) return "服务器拒绝了这次请求（403）。试试在下面填 Referer 或 Cookie；都不行说明不是公开直链。";
            if (msg.Contains("404")) return "地址不存在（404）。链接可能已失效，或者少复制了一段。";
            if (msg.Contains("401")) return "需要登录才能下载（401）。";
            if (msg.Contains("文件大小不符")) return msg + "。服务器可能限制了单连接，或者文件在下载过程中变了。";
            if (msg.Contains("timeout") || msg.Contains("超时") || ex is TimeoutException) return "连接超时。检查网络，或者这条链接需要代理。";

            return msg;
        }
    }
}
