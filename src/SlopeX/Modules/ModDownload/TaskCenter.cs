using System.Collections.ObjectModel;
using System.Linq;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>全局任务中心：下载页发起安装，任务页观察进度。</summary>
    public static class TaskCenter
    {
        public static ObservableCollection<InstallTask> Tasks { get; } = new ObservableCollection<InstallTask>();

        public static InstallTask StartInstall(string versionId)
        {
            var existing = Tasks.FirstOrDefault(t => t.VersionId == versionId && t.Running);
            if (existing != null) return existing;

            var task = new InstallTask { VersionId = versionId };
            Tasks.Insert(0, task);
            _ = task.RunAsync();
            return task;
        }

        /// <summary>
        /// 安装 Fabric 加载器（原版缺失时会先补原版）。
        /// loaderVersion 为空表示交给安装器挑最新稳定版。
        /// </summary>
        public static InstallTask StartFabricInstall(string baseVersion, string loaderVersion = null)
        {
            var existing = Tasks.FirstOrDefault(t =>
                t.Kind == InstallKind.Fabric && t.BaseVersion == baseVersion && t.Running);
            if (existing != null) return existing;

            var task = new InstallTask
            {
                Kind = InstallKind.Fabric,
                BaseVersion = baseVersion,
                LoaderVersion = loaderVersion,
                VersionId = string.IsNullOrEmpty(loaderVersion)
                    ? "fabric-" + baseVersion
                    : FabricInstaller.BuildVersionId(loaderVersion, baseVersion)
            };

            Tasks.Insert(0, task);
            _ = task.RunAsync();
            return task;
        }

        /// <summary>
        /// 安装 Forge（原版缺失时会先补原版）。
        /// loaderVersion 为空表示取 Forge 官方推荐的版本。
        /// </summary>
        public static InstallTask StartForgeInstall(string baseVersion, string loaderVersion = null)
        {
            var existing = Tasks.FirstOrDefault(t =>
                t.Kind == InstallKind.Forge && t.BaseVersion == baseVersion && t.Running);
            if (existing != null) return existing;

            var task = new InstallTask
            {
                Kind = InstallKind.Forge,
                BaseVersion = baseVersion,
                LoaderVersion = loaderVersion,
                VersionId = string.IsNullOrEmpty(loaderVersion)
                    ? "forge-" + baseVersion
                    : ForgeInstaller.BuildVersionId(baseVersion, loaderVersion)
            };

            Tasks.Insert(0, task);
            _ = task.RunAsync();
            return task;
        }

        /// <summary>导入整合包。baseVersion 仅用于没有清单的普通压缩包。</summary>
        public static InstallTask StartModpackInstall(string zipPath, string baseVersion = null)
        {
            var task = new InstallTask
            {
                Kind = InstallKind.Modpack,
                PackPath = zipPath,
                BaseVersionFallback = baseVersion,
                VersionId = System.IO.Path.GetFileNameWithoutExtension(zipPath)
            };

            Tasks.Insert(0, task);
            _ = task.RunAsync();
            return task;
        }

        /// <summary>安装 NeoForge（原版缺失时会先补原版）。loaderVersion 为空表示取最新版。</summary>
        public static InstallTask StartNeoForgeInstall(string baseVersion, string loaderVersion = null)
        {
            var existing = Tasks.FirstOrDefault(t =>
                t.Kind == InstallKind.NeoForge && t.BaseVersion == baseVersion && t.Running);
            if (existing != null) return existing;

            var task = new InstallTask
            {
                Kind = InstallKind.NeoForge,
                BaseVersion = baseVersion,
                LoaderVersion = loaderVersion,
                VersionId = string.IsNullOrEmpty(loaderVersion)
                    ? "neoforge-" + baseVersion
                    : NeoForgeInstaller.BuildVersionId(loaderVersion)
            };

            Tasks.Insert(0, task);
            _ = task.RunAsync();
            return task;
        }

        public static bool IsBusy(string versionId) =>
            Tasks.Any(t => t.VersionId == versionId && t.Running);

        public static int RunningCount => Tasks.Count(t => t.Running);
    }
}
