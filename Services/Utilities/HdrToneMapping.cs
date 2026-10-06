using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace EmbyCredits.Services.Utilities
{
    public sealed class VideoColorInfo
    {
        public string PixelFormat { get; set; } = string.Empty;
        public string Transfer { get; set; } = string.Empty;
        public int? DolbyVisionProfile { get; set; }
        public int? DolbyVisionCompatibilityId { get; set; }

        public bool IsHdrTransfer =>
            Transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
            Transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);

        public bool IsDolbyVisionWithoutHdrBase => DolbyVisionProfile.HasValue && DolbyVisionCompatibilityId == 0;

        public bool CanToneMap => IsHdrTransfer && !IsDolbyVisionWithoutHdrBase;

        public bool IsHighBitDepth =>
            PixelFormat.IndexOf("10", StringComparison.Ordinal) >= 0 ||
            PixelFormat.IndexOf("12", StringComparison.Ordinal) >= 0 ||
            PixelFormat.IndexOf("16", StringComparison.Ordinal) >= 0;
    }

    public static class HdrToneMapping
    {
        private const int MaxCacheEntries = 2000;
        private static readonly ConcurrentDictionary<string, VideoColorInfo> _colorInfoCache =
            new ConcurrentDictionary<string, VideoColorInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _filterCheckLock = new object();
        private static bool? _filtersAvailable;

        public static VideoColorInfo Probe(string videoPath, PluginConfiguration? configuration, ILogger? logger = null)
        {
            if (_colorInfoCache.TryGetValue(videoPath, out var cached))
                return cached;

            var info = ProbeUncached(videoPath, configuration, logger);
            if (_colorInfoCache.Count >= MaxCacheEntries)
                _colorInfoCache.Clear();
            _colorInfoCache[videoPath] = info;
            return info;
        }

        private static VideoColorInfo ProbeUncached(string videoPath, PluginConfiguration? configuration, ILogger? logger)
        {
            var info = new VideoColorInfo();
            try
            {
                var inputPath = FFmpegHelper.ResolveInputPath(videoPath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = FFmpegHelper.GetFfprobePath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList().Add("-v");
                startInfo.ArgumentList().Add("error");
                FFmpegHelper.AddStrmNetworkResilienceArgs(startInfo, videoPath, inputPath, configuration);
                startInfo.ArgumentList().Add("-select_streams");
                startInfo.ArgumentList().Add("v:0");
                startInfo.ArgumentList().Add("-show_entries");
                startInfo.ArgumentList().Add("stream=pix_fmt,color_transfer:stream_side_data=dv_profile,dv_bl_signal_compatibility_id");
                startInfo.ArgumentList().Add("-of");
                startInfo.ArgumentList().Add("default=noprint_wrappers=1");
                startInfo.ArgumentList().Add(inputPath);

                using var process = new Process { StartInfo = startInfo };
                startInfo.ApplyArgumentList();
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 20000))
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    logger?.Warn($"ffprobe colour check timed out for {System.IO.Path.GetFileName(videoPath)}");
                    return info;
                }
                process.WaitForExit(5000);

                foreach (var rawLine in stdoutTask.Result.Split('\n'))
                {
                    var line = rawLine.Trim();
                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;

                    var key = line.Substring(0, separator);
                    var value = line.Substring(separator + 1);
                    switch (key)
                    {
                        case "pix_fmt":
                            info.PixelFormat = value;
                            break;
                        case "color_transfer":
                            info.Transfer = value;
                            break;
                        case "dv_profile":
                            if (int.TryParse(value, out var profile))
                                info.DolbyVisionProfile = profile;
                            break;
                        case "dv_bl_signal_compatibility_id":
                            if (int.TryParse(value, out var compatibility))
                                info.DolbyVisionCompatibilityId = compatibility;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"Could not read colour information for {System.IO.Path.GetFileName(videoPath)}: {ex.Message}");
            }

            return info;
        }

        public static bool AreFiltersAvailable(ILogger? logger = null)
        {
            lock (_filterCheckLock)
            {
                if (_filtersAvailable.HasValue)
                    return _filtersAvailable.Value;

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = FFmpegHelper.GetFfmpegPath(),
                        Arguments = "-hide_banner -filters",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        _filtersAvailable = false;
                        return false;
                    }

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 20000);
                    process.WaitForExit(5000);

                    var output = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
                    _filtersAvailable = output.IndexOf(" zscale ", StringComparison.Ordinal) >= 0 &&
                                        output.IndexOf(" tonemap ", StringComparison.Ordinal) >= 0;
                }
                catch (Exception ex)
                {
                    logger?.Warn($"Could not check FFmpeg tone mapping filters: {ex.Message}");
                    _filtersAvailable = false;
                }

                if (_filtersAvailable == false)
                    logger?.Warn("The FFmpeg build in use has no zscale/tonemap filters - HDR tone mapping is unavailable and frames are used as-is");

                return _filtersAvailable.Value;
            }
        }

        public static string BuildToneMapFilter(VideoColorInfo info)
        {
            var transfer = info.Transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase) ? "arib-std-b67" : "smpte2084";
            return $"zscale=tin={transfer}:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100," +
                   "format=gbrpf32le," +
                   "zscale=p=bt709," +
                   "tonemap=tonemap=hable:desat=0," +
                   "zscale=t=bt709:m=bt709:r=pc," +
                   "format=yuvj420p";
        }
    }
}
