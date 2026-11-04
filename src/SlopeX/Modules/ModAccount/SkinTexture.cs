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

        /// <summary>
        /// 画一张 Steve 风格的默认皮肤（16 位色块风格，64×64）。
        ///
        /// 为什么是代码画而不是内嵌一张图：Minecraft 的默认皮肤是 Mojang 的资产，
        /// 仓库里也不想放二进制图；这张是自己画的近似款，视觉上能认出是"那个默认角色"。
        /// </summary>
        public static BitmapSource CreateDefault(SkinModel model = SkinModel.Classic)
        {
            const int n = Unit;
            var px = new byte[n * n * 4];   // 全透明起步

            // Steve 的经典配色
            var hair = Rgba(0x33, 0x2A, 0x22);
            var skin = Rgba(0xC6, 0x8B, 0x63);
            var skinDark = Rgba(0xB0, 0x78, 0x53);
            var eyeWhite = Rgba(0xF2, 0xF2, 0xF2);
            var eyeIris = Rgba(0x3B, 0x44, 0x8F);
            var shirt = Rgba(0x00, 0xA8, 0xA8);
            var shirtDark = Rgba(0x00, 0x8B, 0x8B);
            var pants = Rgba(0x3B, 0x3B, 0xAA);
            var shoes = Rgba(0x4A, 0x4A, 0x4A);

            // --- 头 8×8×8 ---
            Fill(px, n, 8, 0, 8, 8, hair);        // 顶
            Fill(px, n, 16, 0, 8, 8, hair);       // 底（脖子外面看不到）
            Fill(px, n, 0, 8, 8, 8, skin);        // 右
            Fill(px, n, 8, 8, 8, 8, skin);        // 前
            Fill(px, n, 16, 8, 8, 8, skin);       // 左
            Fill(px, n, 24, 8, 8, 8, hair);       // 后

            // 头发：前面与两侧各压一行，后面整块
            Fill(px, n, 8, 8, 8, 1, hair);
            Fill(px, n, 0, 8, 8, 1, hair);
            Fill(px, n, 16, 8, 8, 1, hair);
            Fill(px, n, 8, 15, 8, 1, skinDark);   // 下巴阴影

            // 眼睛（正面 8..16, 8..16）：y=12，各 2 像素宽
            Fill(px, n, 9, 12, 2, 1, eyeWhite);
            Fill(px, n, 10, 12, 1, 1, eyeIris);
            Fill(px, n, 13, 12, 2, 1, eyeWhite);
            Fill(px, n, 13, 12, 1, 1, eyeIris);
            // 眉毛
            Fill(px, n, 9, 11, 2, 1, hair);
            Fill(px, n, 13, 11, 2, 1, hair);

            // --- 身体 8×12×4 ---
            Fill(px, n, 20, 16, 8, 4, shirt);     // 顶
            Fill(px, n, 28, 16, 8, 4, shirt);     // 底
            Fill(px, n, 16, 20, 4, 12, shirt);    // 右
            Fill(px, n, 20, 20, 8, 12, shirt);    // 前
            Fill(px, n, 28, 20, 4, 12, shirt);    // 左
            Fill(px, n, 32, 20, 8, 12, shirt);    // 后
            // 下摆深一档，给点层次
            Fill(px, n, 20, 30, 8, 2, shirtDark);
            Fill(px, n, 32, 30, 8, 2, shirtDark);

            // --- 右臂 4×12×4（角色右手，屏幕左侧）---
            Fill(px, n, 44, 16, 4, 4, shirt);
            Fill(px, n, 48, 16, 4, 4, shirt);
            Fill(px, n, 40, 20, 4, 12, shirt);
            Fill(px, n, 44, 20, 4, 12, shirt);
            Fill(px, n, 48, 20, 4, 12, shirt);
            Fill(px, n, 52, 20, 4, 12, shirt);
            // 袖子到手腕，露出手
            Fill(px, n, 40, 28, 4, 4, skin);
            Fill(px, n, 44, 28, 4, 4, skin);
            Fill(px, n, 48, 28, 4, 4, skin);
            Fill(px, n, 52, 28, 4, 4, skin);

            // --- 左臂（32..48, 48..64）---
            Fill(px, n, 36, 48, 4, 4, shirt);
            Fill(px, n, 40, 48, 4, 4, shirt);
            Fill(px, n, 32, 52, 4, 12, shirt);
            Fill(px, n, 36, 52, 4, 12, shirt);
            Fill(px, n, 40, 52, 4, 12, shirt);
            Fill(px, n, 44, 52, 4, 12, shirt);
            Fill(px, n, 32, 60, 4, 4, skin);
            Fill(px, n, 36, 60, 4, 4, skin);
            Fill(px, n, 40, 60, 4, 4, skin);
            Fill(px, n, 44, 60, 4, 4, skin);

            // --- 右腿 4×12×4 ---
            Fill(px, n, 4, 16, 4, 4, pants);
            Fill(px, n, 8, 16, 4, 4, pants);
            Fill(px, n, 0, 20, 4, 12, pants);
            Fill(px, n, 4, 20, 4, 12, pants);
            Fill(px, n, 8, 20, 4, 12, pants);
            Fill(px, n, 12, 20, 4, 12, pants);
            // 鞋
            Fill(px, n, 0, 30, 4, 2, shoes);
            Fill(px, n, 4, 30, 4, 2, shoes);
            Fill(px, n, 8, 30, 4, 2, shoes);
            Fill(px, n, 12, 30, 4, 2, shoes);

            // --- 左腿（16..32, 48..64）---
            Fill(px, n, 20, 48, 4, 4, pants);
            Fill(px, n, 24, 48, 4, 4, pants);
            Fill(px, n, 16, 52, 4, 12, pants);
            Fill(px, n, 20, 52, 4, 12, pants);
            Fill(px, n, 24, 52, 4, 12, pants);
            Fill(px, n, 28, 52, 4, 12, pants);
            Fill(px, n, 16, 62, 4, 2, shoes);
            Fill(px, n, 20, 62, 4, 2, shoes);
            Fill(px, n, 24, 62, 4, 2, shoes);
            Fill(px, n, 28, 62, 4, 2, shoes);

            var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
            bmp.Freeze();
            return bmp;
        }

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

        // ---------------- 像素小工具 ----------------

        private static byte[] Rgba(byte r, byte g, byte b, byte a = 255) => new[] { b, g, r, a };
        private static void Fill(byte[] px, int width, int x0, int y0, int w, int h, byte[] color)
        {
            for (var y = y0; y < y0 + h; y++)
            {
                if (y < 0 || y >= width) continue;
                for (var x = x0; x < x0 + w; x++)
                {
                    if (x < 0 || x >= width) continue;
                    var i = (y * width + x) * 4;
                    px[i] = color[0];
                    px[i + 1] = color[1];
                    px[i + 2] = color[2];
                    px[i + 3] = color[3];
                }
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
