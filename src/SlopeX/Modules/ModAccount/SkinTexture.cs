using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModAccount
{
    /// <summary>皮肤的模型类型。粗手臂（Classic）与细手臂（Slim，即 Alex 那种）的纹理布局不同。</summary>
    public enum SkinModel
    {
        Classic,
        Slim
    }

    /// <summary>一次皮肤加载的结果。失败时 <see cref="Error"/> 有可读原因，<see cref="Image"/> 为 null。</summary>
    public sealed class SkinLoadResult
    {
        public BitmapSource Image { get; set; }
        public SkinModel Model { get; set; } = SkinModel.Classic;
        public string Error { get; set; }

        public bool Ok => Image != null;

        public static SkinLoadResult Fail(string why) => new SkinLoadResult { Error = why };
    }

    /// <summary>
    /// 皮肤贴图的加载、校验与默认皮肤生成。
    ///
    /// 三个来源：
    /// <list type="bullet">
    /// <item>正版账号：<c>api.minecraftservices.com/minecraft/profile</c> 的 <c>skins[0].url</c>，
    ///       变体直接给 <c>SLIM</c> / <c>CLASSIC</c>，不用猜。</item>
    /// <item>离线自定义：用户从本地选的 PNG，模型类型靠像素透明度猜。</item>
    /// <item>离线默认：代码画的一张 Steve 风格贴图，不依赖任何外部文件。</item>
    /// </list>
    ///
    /// 所有返回的 <see cref="BitmapSource"/> 都已 <c>Freeze()</c>，可以跨线程传递。
    /// </summary>
    public static class SkinTexture
    {
        /// <summary>皮肤图的标准边长。老皮肤是 64×32，现代皮肤 64×64，高清皮肤是其整数倍。</summary>
        public const int Unit = 64;

        // ---------------- 加载 ----------------

        /// <summary>从字节流解码。宽高比必须是 1:1（64×64）或 2:1（64×32），否则当不是皮肤贴图。</summary>
        public static SkinLoadResult FromBytes(byte[] data, string variantHint = null)
        {
            if (data == null || data.Length == 0)
                return SkinLoadResult.Fail("文件是空的");

            BitmapSource frame;
            try
            {
                using var ms = new MemoryStream(data);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    return SkinLoadResult.Fail("图片里没有可用的画面");

                // 统一转 Bgra32：后面要看像素判断模型，也方便 3D 贴图取样
                var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                frame = converted;
            }
            catch (Exception ex)
            {
                return SkinLoadResult.Fail("不是有效的图片：" + ex.Message);
            }

            var w = frame.PixelWidth;
            var h = frame.PixelHeight;

            // 64×64（含 128×128 等整数倍）或 64×32
            var square = w == h && w % Unit == 0;
            var legacy = w == Unit && h == Unit / 2;

            if (!square && !legacy)
                return SkinLoadResult.Fail($"尺寸不对：{w}×{h}，皮肤应为 64×64 或 64×32");

            var model = ResolveModel(frame, variantHint);
            return new SkinLoadResult { Image = frame, Model = model };
        }

        public static SkinLoadResult FromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return SkinLoadResult.Fail("文件不存在");
                return FromBytes(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                return SkinLoadResult.Fail("读取失败：" + ex.Message);
            }
        }

        /// <summary>从 Mojang 的贴图地址下载。地址形如 <c>http://textures.minecraft.net/texture/&lt;hash&gt;</c>。</summary>
        public static async Task<SkinLoadResult> FromUrlAsync(string url, string variantHint = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                return SkinLoadResult.Fail("没有皮肤地址");

            try
            {
                // 官方贴图服务只认 https，历史上返回的是 http 链接
                var safe = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                var bytes = await Net.GetBytesAsync(safe, ct).ConfigureAwait(false);
                return FromBytes(bytes, variantHint);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SkinLoadResult.Fail("下载皮肤失败：" + ex.Message);
            }
        }

        // ---------------- 模型判定 ----------------

        /// <summary>
        /// 平台给了 <c>SLIM</c> / <c>CLASSIC</c> 就直接用；本地文件没有这个信息，
        /// 靠 slim 布局特有的两列透明像素来判断。
        /// </summary>
        private static SkinModel ResolveModel(BitmapSource image, string hint)
        {
            if (!string.IsNullOrWhiteSpace(hint))
            {
                if (hint.IndexOf("slim", StringComparison.OrdinalIgnoreCase) >= 0) return SkinModel.Slim;
                if (hint.IndexOf("classic", StringComparison.OrdinalIgnoreCase) >= 0) return SkinModel.Classic;
            }

            return GuessSlim(image) ? SkinModel.Slim : SkinModel.Classic;
        }

        /// <summary>
        /// slim 皮肤的手臂只有 3 像素宽，右臂纹理区最右边那一列（换算到 64 基准是 x=54..55）
        /// 和左臂的同位置整列透明。classic 皮肤这两列是有内容的。判不准就当 classic。
        /// </summary>
        private static bool GuessSlim(BitmapSource image)
        {
            try
            {
                var scale = image.PixelWidth / (double)Unit;
                var stride = image.PixelWidth * 4;
                var pixels = new byte[stride * image.PixelHeight];
                image.CopyPixels(pixels, stride, 0);

                // 右臂「外侧」那一列：x = 54，y = 20..31
                var right = ColumnEmpty(pixels, stride, image.PixelWidth,
                    (int)(54 * scale), (int)(20 * scale), (int)(12 * scale));

                return right;
            }
            catch
            {
                return false;
            }
        }

        private static bool ColumnEmpty(byte[] pixels, int stride, int width, int x, int y0, int height)
        {
            if (x >= width) return false;

            for (var y = y0; y < y0 + height; y++)
            {
                var idx = y * stride + x * 4;
                if (idx + 3 >= pixels.Length) return false;
                if (pixels[idx + 3] != 0) return false;   // 有像素 → 不是 slim
            }

            return true;
        }

        // ---------------- 默认皮肤 ----------------

        // ---------------- 头像裁切 ----------------

        /// <summary>
        /// 从皮肤里裁出正脸（8..16, 8..16）做小头像，用最近邻放大保持像素风。
        /// 拿不到就返回 null，调用方退回首字母占位。
        /// </summary>
        public static BitmapSource CropFace(BitmapSource skin, int size = 32)
        {
            if (skin == null) return null;

            try
            {
                var scale = skin.PixelWidth / (double)Unit;
                var rect = new System.Windows.Int32Rect(
                    (int)Math.Round(8 * scale), (int)Math.Round(8 * scale),
                    Math.Max(1, (int)Math.Round(8 * scale)), Math.Max(1, (int)Math.Round(8 * scale)));

                var face = new CroppedBitmap(skin, rect);
                var scaled = new TransformedBitmap(face,
                    new ScaleTransform(size / (double)rect.Width, size / (double)rect.Height));
                var frozen = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                frozen.Freeze();
                return frozen;
            }
            catch
            {
                return null;
            }
        }

    }

    /// <summary>
    /// 离线皮肤的存放目录（<c>SlopeXData\skins\</c>）。
    ///
    /// 只存文件名、不存完整路径，配合 <see cref="Config.OfflineSkin"/>：
    /// 用户把整个 SlopeX 文件夹搬走之后皮肤依然有效。
    /// </summary>
    public static class SkinStore
    {
        public static string Dir
        {
            get
            {
                var dir = Path.Combine(Paths.DataDir, "skins");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string FullPath(string fileName) =>
            string.IsNullOrWhiteSpace(fileName) ? null : Path.Combine(Dir, fileName);

        /// <summary>
        /// 把用户选的皮肤复制进来，返回存放的文件名。
        /// 先校验能当皮肤用再落盘——不能让一个坏文件把预览搞成空白。
        /// </summary>
        public static string Save(string sourcePath)
        {
            var check = SkinTexture.FromFile(sourcePath);
            if (!check.Ok) throw new InvalidDataException(check.Error ?? "不是可用的皮肤文件");

            // 只留文件名，挡掉 ../ 这类路径穿越
            var name = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(name)) name = "skin.png";
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name) + ".png";

            var target = Path.Combine(Dir, name);
            File.Copy(sourcePath, target, true);
            return name;
        }

        public static void Delete(string fileName)
        {
            try
            {
                var path = FullPath(fileName);
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn("删除离线皮肤失败：" + ex.Message);
            }
        }
    }
}
