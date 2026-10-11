using MediaBrowser.Controller.Entities;
using System;
using System.IO;

namespace EmbyCredits.Services.Utilities
{
    public static class FileWriteGuard
    {
        public const int QuietPeriodSeconds = 60;

        public static bool IsBeingWritten(BaseItem item)
        {
            if (item == null)
                return false;

            return IsBeingRecorded(item) || WasModifiedRecently(item.Path);
        }

        public static bool WasModifiedRecently(string? path)
        {
            try
            {
                var normalizedPath = FFmpegHelper.NormalizeFilePath(path ?? string.Empty);
                if (string.IsNullOrEmpty(normalizedPath) || normalizedPath.Contains("://") || !File.Exists(normalizedPath))
                    return false;

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(normalizedPath);
                return Math.Abs(age.TotalSeconds) < QuietPeriodSeconds;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsBeingRecorded(BaseItem item)
        {
            var liveTv = Plugin.LiveTvManager;
            if (liveTv == null || item == null)
                return false;

            try
            {
                if (!liveTv.HasActiveRecording)
                    return false;

                return liveTv.GetActiveRecordingInfo(item) != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasActiveRecordingInFolderOf(BaseItem item)
        {
            var liveTv = Plugin.LiveTvManager;
            if (liveTv == null || item == null || string.IsNullOrEmpty(item.Path))
                return false;

            try
            {
                if (!liveTv.HasActiveRecording)
                    return false;

                var directory = Path.GetDirectoryName(item.Path);
                return !string.IsNullOrEmpty(directory) && liveTv.HasActiveRecordingInDirectory(directory);
            }
            catch
            {
                return false;
            }
        }
    }
}
