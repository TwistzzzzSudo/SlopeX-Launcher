using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SlopeX.Modules.ModAccount;
using SlopeX.Modules.ModBase;

namespace SlopeX.Controls
{
    /// <summary>
    /// 微软登录对话框：展示设备码 → 等用户在浏览器确认 → 回报结果。
    /// 取消、失败、成功都通过 <see cref="Finished"/> 回传（失败/取消传 null）。
    /// </summary>
    public partial class LoginDialog : UserControl
    {
        private CancellationTokenSource _cts;
        private string _verificationUri = "";
        private bool _finished;

        /// <summary>登录结束：成功带上会话，取消或失败为 null。</summary>
        public event Action<MsaSession> Finished;

        public LoginDialog()
        {
            InitializeComponent();
        }

        /// <summary>开始流程（由宿主在显示对话框之后调用）。</summary>
        public async void Start()
        {
            _cts = new CancellationTokenSource();

            try
            {
                var code = await Msa.RequestDeviceCodeAsync(_cts.Token);
                if (_finished) return;

                CodeText.Text = code.UserCode;
                _verificationUri = code.VerificationUri;
                LinkText.Text = code.VerificationUri;
                StatusText.Text = "等待你在浏览器中确认…";

                var progress = new Progress<string>(text =>
                {
                    if (!_finished) StatusText.Text = text;
                });

                var session = await Msa.WaitForLoginAsync(code, progress, _cts.Token);
                if (_finished) return;

                _finished = true;
                StatusText.Text = "登录成功：" + session.Name;
                Finished?.Invoke(session);
            }
            catch (OperationCanceledException)
            {
                // 用户取消，界面已经关了，什么都不用做
            }
            catch (Exception ex)
            {
                if (_finished) return;

                // 网络层错误常常是"加速器没开/线路断了"，原始异常信息对用户没有意义
                var message = ex.Message;
                var isNetwork = ex is System.Net.Http.HttpRequestException ||
                                ex.InnerException is System.Net.Http.HttpRequestException;

                if (isNetwork)
                    message = "网络连接失败，无法访问微软或 Mojang 的服务器。\n" +
                              "请检查网络，或开启加速器后重试。\n" +
                              "也可以先用「离线登录」进入游戏。";
                else if (ex is System.TimeoutException || ex is System.Threading.Tasks.TaskCanceledException)
                    message = "连接超时。请检查网络后重试。";

                StatusText.Text = "登录失败：" + message;
                Log.Warn("微软登录失败：" + ex.Message);
            }
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_verificationUri)) return;
                Process.Start(new ProcessStartInfo(_verificationUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开浏览器，请手动访问：" + _verificationUri + "（" + ex.Message + "）";
            }
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(CodeText.Text) && CodeText.Text != "········")
                    Clipboard.SetText(CodeText.Text);
                StatusText.Text = "代码已复制";
            }
            catch (Exception ex)
            {
                StatusText.Text = "复制失败，请手动输入：" + CodeText.Text + "（" + ex.Message + "）";
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_finished) return;
            _finished = true;
            try { _cts?.Cancel(); } catch { /* 忽略 */ }
            Finished?.Invoke(null);
        }
    }
}
