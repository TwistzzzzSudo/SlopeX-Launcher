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

        public static bool IsBusy(string versionId) =>
            Tasks.Any(t => t.VersionId == versionId && t.Running);

        public static int RunningCount => Tasks.Count(t => t.Running);
    }
}
