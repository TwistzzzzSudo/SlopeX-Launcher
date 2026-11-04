using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SlopeX.Modules.ModAccount;

namespace SlopeX.Controls
{
    /// <summary>
    /// 可拖动旋转的角色预览。
    ///
    /// 用 WPF 自带的 <see cref="Viewport3D"/> 拼出六个长方体（头 / 身 / 双臂 / 双腿），
    /// 每个面按皮肤的 UV 布局取样——不引入任何 3D 库。披风是另挂一片 10×16 的薄板，
    /// 有自己的贴图与 UV（和皮肤不是同一张图）。
    ///
    /// 交互：左键拖动改变水平角与俯仰角；双击回到初始视角。
    /// 没有皮肤时画一块极淡的占位色，不会出现空洞。
    /// </summary>
    public sealed class SkinPreview : Grid
    {
        // 角色尺寸（Minecraft 单位：1 = 1 像素）
        private const double HeadSize = 8;
        private const double BodyW = 8, BodyH = 12, BodyD = 4;
        private const double ArmW = 4, ArmH = 12, ArmD = 4;
        private const double LegW = 4, LegH = 12, LegD = 4;

        private const double Fov = 40;

        private readonly Viewport3D _viewport = new();
        private readonly PerspectiveCamera _camera = new() { FieldOfView = Fov };
        private readonly ModelVisual3D _root = new();
        private readonly Model3DGroup _lights = new();

        private double _yaw = 22;
        private double _pitch = 7;
        private double _distance = 48;

        private Point _dragOrigin;
        private bool _dragging;
        private BitmapSource _skin;
        private SkinModel _model = SkinModel.Classic;
        private BitmapSource _cape;

        private BitmapSource _texture;         // 预放大后的贴图
        private BitmapSource _textureSource;   // 上面那张对应哪个原图

        private BitmapSource _capeTexture;
        private BitmapSource _capeTextureSource;

        public SkinPreview()
        {
            ClipToBounds = true;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;

            _camera.UpDirection = new Vector3D(0, 1, 0);
            _camera.NearPlaneDistance = 0.5;
            _camera.FarPlaneDistance = 1000;
            _viewport.Camera = _camera;

            // 环境光负责还原贴图本色，方向光给轮廓做出明暗——
            // 两者相加在正面接近满值，侧面只剩环境光，立体感才出得来
            _lights.Children.Add(new AmbientLight(Color.FromRgb(0xB0, 0xB0, 0xB0)));
            _lights.Children.Add(new DirectionalLight(Color.FromRgb(0x58, 0x58, 0x58),
                new Vector3D(-0.4, -0.55, -1)));

            _viewport.Children.Add(_root);
            _viewport.Children.Add(new ModelVisual3D { Content = _lights });
            Children.Add(_viewport);

            Loaded += (_, __) => UpdateCamera();
            SizeChanged += (_, __) => UpdateCamera();

            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            MouseLeave += OnMouseUp;
        }

        /// <summary>换皮肤。<paramref name="skin"/> 为 null 时回到占位。</summary>
        public void SetSkin(BitmapSource skin, SkinModel model)
        {
            _skin = skin;
            _model = model;
            Rebuild();
        }

        /// <summary>换披风。传 null 表示不显示（没戴、已摘掉，或当前是离线皮肤）。</summary>
        public void SetCape(BitmapSource cape)
        {
            _cape = cape;
            Rebuild();
        }

        public void ResetView()
        {
            _yaw = 22;
            _pitch = 7;
            UpdateCamera();
        }

        /// <summary>
        /// 把预览渲染成 PNG（排障用）。<c>--cli skin-render</c> 走这里。
        ///
        /// 必须在 STA 线程上跑：命令行入口是在后台线程执行的，WPF 的视觉对象不能在 MTA 上建。
        /// </summary>
        public static bool RenderToFile(BitmapSource skin, SkinModel model, string path, int width = 320,
            int height = 480, BitmapSource cape = null, double yaw = 22)
        {
            var ok = false;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var view = new SkinPreview { Width = width, Height = height };

                    // 先布局再设皮肤：UpdateCamera 需要 ActualWidth 才能算对距离
                    view.Measure(new Size(width, height));
                    view.Arrange(new Rect(0, 0, width, height));
                    view.UpdateLayout();

                    view._yaw = yaw;   // 排障用：180 = 从背后看，检查披风
                    view.SetSkin(skin, model);
                    view.SetCape(cape);

                    var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(view);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));

                    using var fs = System.IO.File.Create(path);
                    encoder.Save(fs);
                    ok = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            return ok;
        }

        // ---------------- 模型 ----------------

        /// <summary>
        /// 给 3D 用的贴图：按最近邻预放大过的版本。
        ///
        /// WPF 3D 的纹理采样固定是双线性插值，没有"最近邻"开关。64×64 的像素画铺到
        /// 每个皮肤像素约 10 个屏幕像素时，每条像素边界都会被摊成一条 10 px 宽的渐变带，
        /// 看着就是糊的（实测一条横线上 99 个像素出现 44 种颜色）。
        /// 先把图放大 16 倍，插值就只发生在放大后的相邻纹素之间（1/16 个皮肤像素宽），
        /// 视觉上接近硬边。
        /// </summary>
        private BitmapSource Texture => Cached(ref _texture, ref _textureSource, _skin);

        private BitmapSource CapeTexture => Cached(ref _capeTexture, ref _capeTextureSource, _cape);

        /// <summary>
        /// 按原图缓存预放大结果：同一个控件会反复 Rebuild（换披风 / 换视角），
        /// 原图没变就直接复用上一次的放大图。
        /// </summary>
        private static BitmapSource Cached(ref BitmapSource cache, ref BitmapSource cacheSource, BitmapSource src)
        {
            if (ReferenceEquals(cacheSource, src) && cache != null) return cache;

            cacheSource = src;
            if (src == null)
            {
                cache = null;
                return null;
            }

            // 高清贴图（128×128 起）少放大几倍，避免贴图过大
            var factor = src.PixelWidth switch
            {
                <= 64 => 16,
                <= 128 => 8,
                _ => 4
            };

            cache = UpscaleNearest(src, factor);
            return cache;
        }

        /// <summary>最近邻放大 + alpha 二值化。后者是为了让外衣层的半透明边缘不会混出白边。</summary>
        private static BitmapSource UpscaleNearest(BitmapSource src, int factor)
        {
            var sw = src.PixelWidth;
            var sh = src.PixelHeight;
            var srcPx = new byte[sw * sh * 4];
            src.CopyPixels(srcPx, sw * 4, 0);

            var dw = sw * factor;
            var dh = sh * factor;
            var dst = new byte[dw * dh * 4];

            for (var y = 0; y < dh; y++)
            {
                var sy = y / factor;
                var srcRow = sy * sw;
                var dstRow = y * dw;

                for (var x = 0; x < dw; x++)
                {
                    var si = (srcRow + x / factor) * 4;
                    var di = (dstRow + x) * 4;

                    dst[di] = srcPx[si];
                    dst[di + 1] = srcPx[si + 1];
                    dst[di + 2] = srcPx[si + 2];
                    dst[di + 3] = srcPx[si + 3] < 128 ? (byte)0 : (byte)255;
                }
            }

            var bmp = BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgra32, null, dst, dw * 4);
            bmp.Freeze();
            return bmp;
        }

        private void Rebuild()
        {
            // 没有皮肤就什么都不画：预览区保持空白，由页面自己给提示文字。
            // 不画灰色占位人形——那会让人以为是"已有一张默认皮肤"。
            if (_skin == null)
            {
                _root.Content = null;
                _texture = null;
                _textureSource = null;
                UpdateCamera();
                return;
            }

            var group = new Model3DGroup();
            group.Children.Add(_lights);

            var geometry = new MeshGeometry3D();
            var uvH = _skin.PixelHeight * 2 == _skin.PixelWidth ? 32 : 64;   // 64×32 老皮肤

            // slim 的手臂窄 1 像素，往外挪半格，保持与身体贴合
            var slim = _model == SkinModel.Slim;
            var armW = slim ? 3 : ArmW;
            var armX = BodyW / 2 + armW / 2;

            // 头
            AddCube(geometry, 0, 16 - HeadSize / 2, 0, HeadSize, HeadSize, HeadSize, uvH,
                new FaceUv(8, 0), new FaceUv(16, 0), new FaceUv(0, 8), new FaceUv(8, 8),
                new FaceUv(16, 8), new FaceUv(24, 8));

            // 身体
            AddCube(geometry, 0, 8 - BodyH / 2, 0, BodyW, BodyH, BodyD, uvH,
                new FaceUv(20, 16), new FaceUv(28, 16), new FaceUv(16, 20), new FaceUv(20, 20),
                new FaceUv(28, 20), new FaceUv(32, 20));

            // 右臂（角色的右手，屏幕上在左边）
            AddCube(geometry, -armX, 8 - ArmH / 2, 0, armW, ArmH, ArmD, uvH,
                new FaceUv(44, 16), new FaceUv(48, 16), new FaceUv(40, 20), new FaceUv(44, 20),
                new FaceUv(48, 20), new FaceUv(52, 20));

            // 左臂：64×32 老皮肤没有这块，退回右臂并水平翻转
            var mirrorLeft = uvH <= 32;
            AddCube(geometry, armX, 8 - ArmH / 2, 0, armW, ArmH, ArmD, uvH,
                mirrorLeft ? new FaceUv(44, 16) : new FaceUv(36, 48),
                mirrorLeft ? new FaceUv(48, 16) : new FaceUv(40, 48),
                mirrorLeft ? new FaceUv(40, 20) : new FaceUv(32, 52),
                mirrorLeft ? new FaceUv(44, 20) : new FaceUv(36, 52),
                mirrorLeft ? new FaceUv(48, 20) : new FaceUv(40, 52),
                mirrorLeft ? new FaceUv(52, 20) : new FaceUv(44, 52),
                mirrorLeft);

            // 右腿
            AddCube(geometry, -LegW / 2, -4 - LegH / 2, 0, LegW, LegH, LegD, uvH,
                new FaceUv(4, 16), new FaceUv(8, 16), new FaceUv(0, 20), new FaceUv(4, 20),
                new FaceUv(8, 20), new FaceUv(12, 20));

            // 左腿
            AddCube(geometry, LegW / 2, -4 - LegH / 2, 0, LegW, LegH, LegD, uvH,
                mirrorLeft ? new FaceUv(4, 16) : new FaceUv(20, 48),
                mirrorLeft ? new FaceUv(8, 16) : new FaceUv(24, 48),
                mirrorLeft ? new FaceUv(0, 20) : new FaceUv(16, 52),
                mirrorLeft ? new FaceUv(4, 20) : new FaceUv(20, 52),
                mirrorLeft ? new FaceUv(8, 20) : new FaceUv(24, 52),
                mirrorLeft ? new FaceUv(12, 20) : new FaceUv(28, 52),
                mirrorLeft);

            // ---- 第二层：帽子 / 外衣 / 袖子 / 裤腿 ----
            //
            // 64×32 的老皮肤没有这一层，跳过。外扩 0.5（每边 0.25），
            // 透明像素自然漏出第一层，不透明的地方就盖在上面——这和游戏里的表现一致。
            if (uvH > 32)
            {
                const double g = 0.5;

                AddCube(geometry, 0, 16 - HeadSize / 2, 0, HeadSize + g, HeadSize + g, HeadSize + g, uvH,
                    new FaceUv(40, 0), new FaceUv(48, 0), new FaceUv(32, 8), new FaceUv(40, 8),
                    new FaceUv(48, 8), new FaceUv(56, 8));

                AddCube(geometry, 0, 8 - BodyH / 2, 0, BodyW + g, BodyH + g, BodyD + g, uvH,
                    new FaceUv(20, 32), new FaceUv(28, 32), new FaceUv(16, 36), new FaceUv(20, 36),
                    new FaceUv(28, 36), new FaceUv(32, 36));

                AddCube(geometry, -armX, 8 - ArmH / 2, 0, armW + g, ArmH + g, ArmD + g, uvH,
                    new FaceUv(44, 32), new FaceUv(48, 32), new FaceUv(40, 36), new FaceUv(44, 36),
                    new FaceUv(48, 36), new FaceUv(52, 36));

                AddCube(geometry, armX, 8 - ArmH / 2, 0, armW + g, ArmH + g, ArmD + g, uvH,
                    new FaceUv(52, 48), new FaceUv(56, 48), new FaceUv(48, 52), new FaceUv(52, 52),
                    new FaceUv(56, 52), new FaceUv(60, 52));

                AddCube(geometry, -LegW / 2, -4 - LegH / 2, 0, LegW + g, LegH + g, LegD + g, uvH,
                    new FaceUv(4, 32), new FaceUv(8, 32), new FaceUv(0, 36), new FaceUv(4, 36),
                    new FaceUv(8, 36), new FaceUv(12, 36));

                AddCube(geometry, LegW / 2, -4 - LegH / 2, 0, LegW + g, LegH + g, LegD + g, uvH,
                    new FaceUv(4, 48), new FaceUv(8, 48), new FaceUv(0, 52), new FaceUv(4, 52),
                    new FaceUv(8, 52), new FaceUv(12, 52));
            }

            var brush = new ImageBrush(Texture)
            {
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1)
            };

            var material = new DiffuseMaterial(brush);
            group.Children.Add(new GeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                BackMaterial = material      // 保证任何绕序都不会出现空洞
            });

            if (_cape != null) group.Children.Add(BuildCape());

            _root.Content = group;
            UpdateCamera();
        }

        /// <summary>
        /// 披风：挂一片 10×16×1 的薄板在背后，上沿齐肩、下沿到大腿。
        ///
        /// 贴图布局和皮肤一样是「按方块摊开」，只是原点在左上角 (0,0)：
        /// 顶 (1,0)、底 (11,0)、右 (0,1)、前 (1,1)、左 (11,1)、后 (12,1)。
        /// 图案画在 (1,1) 那块，也就是**朝外**的那一面；朝向身体的那面是内衬，
        /// 所以这里把前后两张对调，不能直接照搬皮肤的顺序。
        /// </summary>
        private GeometryModel3D BuildCape()
        {
            var geometry = new MeshGeometry3D();
            var uvH = _cape.PixelHeight * 2 == _cape.PixelWidth ? 32 : 64;

            // 本体背面在 z = -2，外衣层在 -2.25；披风放到 -3.0（厚 1，占 -3.5 ~ -2.5），
            // 留一条缝，避免两个面贴在一起闪。
            AddCube(geometry, 0, 0, -3.0, 10, 16, 1, uvH,
                new FaceUv(1, 0),    // 顶
                new FaceUv(11, 0),   // 底
                new FaceUv(0, 1),    // 右
                new FaceUv(12, 1),   // 前（朝身体，内衬）
                new FaceUv(11, 1),   // 左
                new FaceUv(1, 1));   // 后（朝外，图案）

            // 注意 ViewportUnits 必须是 Absolute：披风的 UV 只占贴图的左上角一小块，
            // 用 RelativeToBoundingBox 的话 WPF 会把整张图拉伸到这块 UV 的包围盒里，
            // 10×16 的图案会被压掉一半（实测：整幅图挤进来，背面能同时看到正反两面的图案）。
            var brush = new ImageBrush(CapeTexture)
            {
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1)
            };

            var material = new DiffuseMaterial(brush);
            return new GeometryModel3D
            {
                Geometry = geometry,
                Material = material,
                BackMaterial = material
            };
        }

        /// <summary>皮肤里每个面的左上角坐标（64 基准）。宽高由所在立方体的面决定。</summary>
        private readonly struct FaceUv
        {
            public readonly int X, Y;
            public FaceUv(int x, int y) { X = x; Y = y; }
        }

        /// <summary>
        /// 往 mesh 里加一个长方体：六个面各自按 <paramref name="uvH"/> 基准的贴图区域取样。
        /// 参数顺序固定为 顶、底、右、前、左、后。
        /// </summary>
        private static void AddCube(MeshGeometry3D mesh, double cx, double cy, double cz,
            double w, double h, double d, int uvH, FaceUv top, FaceUv bottom, FaceUv right,
            FaceUv front, FaceUv left, FaceUv back, bool flipU = false)
        {
            var hx = w / 2;
            var hy = h / 2;
            var hz = d / 2;

            // 顶：纹理宽对应 X，高对应 Z
            Face(mesh, uvH,
                new Point3D(cx - hx, cy + hy, cz - hz), new Point3D(cx + hx, cy + hy, cz - hz),
                new Point3D(cx + hx, cy + hy, cz + hz), new Point3D(cx - hx, cy + hy, cz + hz),
                top.X, top.Y, (int)w, (int)d, flipU);

            // 底
            Face(mesh, uvH,
                new Point3D(cx - hx, cy - hy, cz + hz), new Point3D(cx + hx, cy - hy, cz + hz),
                new Point3D(cx + hx, cy - hy, cz - hz), new Point3D(cx - hx, cy - hy, cz - hz),
                bottom.X, bottom.Y, (int)w, (int)d, flipU);

            // 右（+X）
            Face(mesh, uvH,
                new Point3D(cx + hx, cy + hy, cz - hz), new Point3D(cx + hx, cy + hy, cz + hz),
                new Point3D(cx + hx, cy - hy, cz + hz), new Point3D(cx + hx, cy - hy, cz - hz),
                right.X, right.Y, (int)d, (int)h, flipU);

            // 前（+Z）
            Face(mesh, uvH,
                new Point3D(cx - hx, cy + hy, cz + hz), new Point3D(cx + hx, cy + hy, cz + hz),
                new Point3D(cx + hx, cy - hy, cz + hz), new Point3D(cx - hx, cy - hy, cz + hz),
                front.X, front.Y, (int)w, (int)h, flipU);

            // 左（-X）
            Face(mesh, uvH,
                new Point3D(cx - hx, cy + hy, cz + hz), new Point3D(cx - hx, cy + hy, cz - hz),
                new Point3D(cx - hx, cy - hy, cz - hz), new Point3D(cx - hx, cy - hy, cz + hz),
                left.X, left.Y, (int)d, (int)h, flipU);

            // 后（-Z）
            Face(mesh, uvH,
                new Point3D(cx + hx, cy + hy, cz - hz), new Point3D(cx - hx, cy + hy, cz - hz),
                new Point3D(cx - hx, cy - hy, cz - hz), new Point3D(cx + hx, cy - hy, cz - hz),
                back.X, back.Y, (int)w, (int)h, flipU);
        }

        /// <summary>
        /// 加一个面。<paramref name="p0"/> 对应纹理区域的左上角，依次 p1 右上、p2 右下、p3 左下。
        ///
        /// 注意：WPF 的 <see cref="MeshGeometry3D.TextureCoordinates"/> 原点在**左上角**
        /// （与 OpenGL 相反），所以这里不能翻 V 轴——翻了会把头和腿的贴图对调。
        /// </summary>
        private static void Face(MeshGeometry3D mesh, int uvH, Point3D p0, Point3D p1, Point3D p2, Point3D p3,
            int tx, int ty, int tw, int th, bool flipU)
        {
            var u0 = tx / 64.0;
            var u1 = (tx + tw) / 64.0;
            var v0 = ty / (double)uvH;
            var v1 = (ty + th) / (double)uvH;

            if (flipU) (u0, u1) = (1 - u0, 1 - u1);

            var index = mesh.Positions.Count;
            mesh.Positions.Add(p0);
            mesh.Positions.Add(p1);
            mesh.Positions.Add(p2);
            mesh.Positions.Add(p3);

            mesh.TextureCoordinates.Add(new Point(u0, v0));
            mesh.TextureCoordinates.Add(new Point(u1, v0));
            mesh.TextureCoordinates.Add(new Point(u1, v1));
            mesh.TextureCoordinates.Add(new Point(u0, v1));

            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 2);
            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 2);
            mesh.TriangleIndices.Add(index + 3);
        }

        // ---------------- 相机 ----------------

        private void UpdateCamera()
        {
            // 让整个角色（高 32、宽 16 左右）在任何控件尺寸下都装得下。
            // 注意 FieldOfView 是**水平**视场角，垂直方向要按宽高比换算，否则竖长控件里角色会缩得很小。
            const double halfH = 17.2;
            const double halfW = 9.2;

            var aspect = ActualWidth > 1 && ActualHeight > 1 ? ActualWidth / ActualHeight : 0.7;
            var tanHalfH = Math.Tan(Fov / 2 * Math.PI / 180);
            var tanHalfV = tanHalfH / Math.Max(0.05, aspect);

            var needH = halfH / tanHalfV;
            var needW = halfW / tanHalfH;

            _distance = Math.Max(needH, needW) * 1.18;

            var yaw = _yaw * Math.PI / 180;
            var pitch = _pitch * Math.PI / 180;

            var x = _distance * Math.Cos(pitch) * Math.Sin(yaw);
            var y = _distance * Math.Sin(pitch);
            var z = _distance * Math.Cos(pitch) * Math.Cos(yaw);

            _camera.Position = new Point3D(x, y, z);
            _camera.LookDirection = new Vector3D(-x, -y, -z);
        }

        // ---------------- 交互 ----------------

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ResetView();
                return;
            }

            _dragOrigin = e.GetPosition(this);
            _dragging = true;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            var p = e.GetPosition(this);
            _yaw -= (p.X - _dragOrigin.X) * 0.45;
            _pitch = Math.Clamp(_pitch + (p.Y - _dragOrigin.Y) * 0.30, -65, 65);
            _dragOrigin = p;
            UpdateCamera();
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            _dragging = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
        }
    }
}
