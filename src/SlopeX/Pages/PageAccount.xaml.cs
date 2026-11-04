using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SlopeX.Controls;
using SlopeX.Modules.ModAccount;
using SlopeX.Modules.ModBase;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    /// <summary>
    /// 账号页：登录状态、离线昵称，以及皮肤预览（正版皮肤 / 离线皮肤可切换）。
    ///
    /// 皮肤预览是 3D 的，可以拖动旋转；数据来源分三种——
    /// 正版账号从 Mojang 贴图服务下、离线上传读本地 PNG；都没有就留白，由页面给提示文字。
    /// </summary>
    public partial class PageAccount : UserControl, ILauncherPage
    {
        private SkinLoadResult _offline;
        private SkinLoadResult _premium;

        private bool _showPremium = true;
        private string _premiumLoadedFor;
        private bool _nameLoaded;
        private bool _loadingSkins;

        /// <summary>用户手动点过「正版 / 离线」之后，页面刷新就不再自动帮他改回去。</summary>
        private bool _sourceChosenByUser;

        public PageAccount()
        {
            InitializeComponent();

            // 不预置任何皮肤：没导入就留白，避免让人以为"已经有一张默认皮肤"
            SkinView.SetSkin(null, SkinModel.Classic);
        }

        public void OnShown()
        {
            if (!_nameLoaded)
            {
                InputName.Text = Config.Current.PlayerName;
                _nameLoaded = true;
            }

            UpdateAccountUi();
            _ = LoadSkinsAsync();
        }

        // ---------------- 账号 ----------------

        private void UpdateAccountUi()
        {
            var session = Msa.Current;

            if (session != null)
            {
                AccountStatus.Text = "已登录：" + session.Name;
                BtnLogin.Content = "重新登录";
                BtnLogout.Visibility = Visibility.Visible;
            }
            else
            {
                AccountStatus.Text = "未登录：使用离线账号";
                BtnLogin.Content = "登录微软账号";
                BtnLogout.Visibility = Visibility.Collapsed;
            }

            if (session == null && _showPremium)
            {
                _showPremium = false;
                ApplyPreview();
            }

            UpdateSkinChips();
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new LoginDialog();

            dialog.Finished += session =>
            {
                MainWindow.Current?.HideModal();

                UpdateAccountUi();

                if (session == null) return;

                _showPremium = true;
                _premiumLoadedFor = null;
                _sourceChosenByUser = false;
                MainWindow.Current?.ApplyConfigChanged();
                MainWindow.Current?.SetStatus($"已登录：{session.Name}", null);
                Log.Info($"账号已切换为正版：{session.Name}");

                _ = LoadSkinsAsync();
            };

            MainWindow.Current?.ShowModal(dialog);
            dialog.Start();
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("退出后将以离线账号启动，确定吗？", "退出登录",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            Msa.Logout();
            _premium = null;
            _premiumLoadedFor = null;
            _showPremium = false;
            _sourceChosenByUser = false;

            UpdateAccountUi();
            ApplyPreview();
            MainWindow.Current?.ApplyConfigChanged();
            MainWindow.Current?.SetStatus("已退出正版账号", null);
        }

        private void SaveName_Click(object sender, RoutedEventArgs e)
        {
            var name = (InputName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) name = "Steve";

            Config.Current.PlayerName = name;
            Config.Current.Save();

            MainWindow.Current?.ApplyConfigChanged();
            MainWindow.Current?.ShowToast("玩家名已保存：" + name);
            Log.Info("离线玩家名已改为 " + name);
        }

        // ---------------- 皮肤 ----------------

        private async Task LoadSkinsAsync()
        {
            if (_loadingSkins) return;
            _loadingSkins = true;

            try
            {
                LoadOfflineSkin();

                var session = Msa.Current;

                if (session == null)
                {
                    _premium = null;
                    _premiumLoadedFor = null;
                }
                else if (_premiumLoadedFor != session.Uuid || _premium == null)
                {
                    SkinDetail.Text = "正在读取正版皮肤…";

                    try
                    {
                        var info = await Msa.FetchSkinAsync(session).ConfigureAwait(true);

                        if (info.HasSkin)
                        {
                            _premium = await SkinTexture.FromUrlAsync(info.Url, info.Variant).ConfigureAwait(true);
                        }
                        else
                        {
                            _premium = new SkinLoadResult
                            {
                                Image = null,
                                Model = SkinModel.Classic,
                                Error = "这个账号还没有上传皮肤"
                            };
                        }

                        _premiumLoadedFor = session.Uuid;
                    }
                    catch (Exception ex)
                    {
                        _premium = SkinLoadResult.Fail(ex.Message);
                        _premiumLoadedFor = session.Uuid;
                        Log.Warn("读取正版皮肤失败：" + ex.Message);
                    }
                }

                if (!_sourceChosenByUser && _premium != null && _premium.Ok && _premiumLoadedFor != null)
                    _showPremium = true;

                ApplyPreview();
            }
            finally
            {
                _loadingSkins = false;
            }
        }

        private void LoadOfflineSkin()
        {
            var name = Config.Current.OfflineSkin;

            if (string.IsNullOrWhiteSpace(name))
            {
                _offline = null;   // 没导入过 → 预览留白
                return;
            }

            var path = SkinStore.FullPath(name);
            var result = SkinTexture.FromFile(path);

            if (result.Ok)
            {
                _offline = result;
                return;
            }

            // 文件被删了或坏了：回落到默认，并把配置清干净，免得每次进页面都失败
            Log.Warn($"离线皮肤不可用（{name}）：{result.Error}");
            Config.Current.OfflineSkin = "";
            Config.Current.Save();
            _offline = null;
        }

        private void ApplyPreview()
        {
            var premiumUsable = _premium != null && _premium.Ok;
            if (_showPremium && !premiumUsable) _showPremium = false;

            var current = _showPremium ? _premium : _offline;

            if (current == null || !current.Ok)
                SkinView.SetSkin(null, SkinModel.Classic);   // 留白
            else
                SkinView.SetSkin(current.Image, current.Model);

            SkinDetail.Text = Describe();

            // 上传按钮只在离线皮肤下有意义
            BtnUpload.IsEnabled = !_showPremium;

            UpdateSkinChips();
        }

        private string Describe()
        {
            if (_showPremium)
            {
                if (_premium == null) return "还没有登录正版账号，登录后才能预览正版皮肤。";

                if (!_premium.Ok)
                {
                    // 账号确实没皮肤时 Error 是那句中性的说明；读取失败才是异常
                    return string.Equals(_premium.Error, "这个账号还没有上传皮肤", StringComparison.Ordinal)
                        ? "这个账号还没有上传皮肤，游戏里会显示官方默认外观。"
                        : "读取正版皮肤失败：" + (_premium.Error ?? "未知原因") +
                          "。皮肤由 minecraft.net 管理，这里只能预览。";
                }

                return $"正版皮肤 · {ModelText(_premium.Model)}";
            }

            var custom = Config.Current.OfflineSkin;
            if (string.IsNullOrWhiteSpace(custom) || _offline == null || !_offline.Ok)
                return "还没有选择皮肤。点「选择皮肤文件」挑一张 64×64（或 64×32）的 PNG。";

            return $"离线皮肤 · {custom}（{ModelText(_offline.Model)}）";
        }

        private static string ModelText(SkinModel model) =>
            model == SkinModel.Slim ? "细手臂" : "粗手臂";

        private void UpdateSkinChips()
        {
            var session = Msa.Current;

            ChipPremium.IsEnabled = session != null;
            ChipPremium.IsChecked = _showPremium && session != null;
            ChipOffline.IsChecked = !ChipPremium.IsChecked;
            ChipOffline.IsEnabled = true;
        }

        private void SkinSource_Click(object sender, RoutedEventArgs e)
        {
            _showPremium = ReferenceEquals(sender, ChipPremium);
            _sourceChosenByUser = true;
            ApplyPreview();
        }

        private void Upload_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择皮肤文件",
                Filter = "皮肤图片 (*.png)|*.png|所有图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var result = SkinTexture.FromFile(dialog.FileName);
            if (!result.Ok)
            {
                MessageBox.Show(result.Error ?? "这个文件不能当皮肤用。", "皮肤文件无法使用",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var stored = SkinStore.Save(dialog.FileName);

                Config.Current.OfflineSkin = stored;
                Config.Current.Save();

                _offline = result;
                _showPremium = false;

                ApplyPreview();
                SkinView.ResetView();
                MainWindow.Current?.ShowToast("离线皮肤已更新：" + stored);
                Log.Info($"离线皮肤已设为 {stored}");
            }
            catch (Exception ex)
            {
                Log.Error("保存离线皮肤失败", ex);
                MessageBox.Show("保存失败：" + ex.Message, "SlopeX");
            }
        }
    }
}
