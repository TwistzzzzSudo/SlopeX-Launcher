using System;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModAccount
{
    /// <summary>
    /// 皮肤的取数逻辑，账号页与首页共用。
    ///
    /// 只管"怎么拿到一张贴图"（正版按 UUID 缓存、离线读本地文件），
    /// 不管"该显示哪一张"——那是页面自己的选择（账号页有正版 / 离线切换）。
    /// </summary>
    public static class SkinProvider
    {
        private static SkinLoadResult _premium;
        private static string _premiumFor;

        /// <summary>换了账号或退出登录时调用，避免还拿上一个账号的贴图。</summary>
        public static void InvalidatePremium()
        {
            _premium = null;
            _premiumFor = null;
        }

        public static bool IsPremiumCached(MsaSession session) =>
            session != null && _premium != null && _premiumFor == session.Uuid;

        /// <summary>
        /// 离线皮肤。没导入返回 <c>null</c>（页面据此留白）；文件坏掉时顺手把配置清掉，
        /// 免得每次进页面都失败一次。
        /// </summary>
        public static SkinLoadResult Offline()
        {
            var name = Config.Current.OfflineSkin;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var result = SkinTexture.FromFile(SkinStore.FullPath(name));
            if (result.Ok) return result;

            Log.Warn($"离线皮肤不可用（{name}）：{result.Error}");
            Config.Current.OfflineSkin = "";
            Config.Current.Save();
            return null;
        }

        /// <summary>
        /// 正版皮肤，结果按 UUID 缓存在内存里——两个页面都调它，只下载一次。
        /// 账号没上传皮肤时返回一个 <see cref="SkinLoadResult"/>（<c>Ok</c> 为 false，
        /// <c>Error</c> 是中性的说明），读取真正失败时也走同一条路。
        /// </summary>
        public static async Task<SkinLoadResult> PremiumAsync(MsaSession session)
        {
            if (session == null) return null;
            if (_premium != null && _premiumFor == session.Uuid) return _premium;

            try
            {
                var info = await Msa.FetchSkinAsync(session).ConfigureAwait(true);

                _premium = info.HasSkin
                    ? await SkinTexture.FromUrlAsync(info.Url, info.Variant).ConfigureAwait(true)
                    : new SkinLoadResult
                    {
                        Image = null,
                        Model = SkinModel.Classic,
                        Error = "这个账号还没有上传皮肤"
                    };
            }
            catch (Exception ex)
            {
                _premium = SkinLoadResult.Fail(ex.Message);
                Log.Warn("读取正版皮肤失败：" + ex.Message);
            }

            _premiumFor = session.Uuid;
            return _premium;
        }

        public static string ModelText(SkinModel model) =>
            model == SkinModel.Slim ? "细手臂" : "粗手臂";
    }
}
