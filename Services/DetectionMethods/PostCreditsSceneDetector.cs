using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{
    public sealed class PostCreditsScene
    {
        public PostCreditsScene(double start, double end)
        {
            Start = start;
            End = end;
        }

        public double Start { get; }
        public double End { get; }
        public double Duration => End - Start;
    }

    public sealed class PostCreditsAnalysis
    {
        public PostCreditsScene? Scene { get; set; }
        public double AnalyzedFrom { get; set; }
        public double AnalyzedTo { get; set; }
        public int SampleCount { get; set; }
        public bool UsedHardwareDecoding { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    public sealed class PostCreditsSceneDetector
    {
        private const double SampleFramesPerSecond = 2.0;
        private const int ContentMaxBlackPercent = 50;
        private const double MinimumCreditsBeforeSceneSeconds = 20.0;
        private const double MinimumDarkGapBeforeSceneSeconds = 6.0;
        private const double MaximumGapInsideSceneSeconds = 1.0;
        private const double MaximumAnalysisSeconds = 20 * 60;

        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;

        public PostCreditsSceneDetector(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        private readonly struct FrameSample
        {
            public FrameSample(double time, int blackPercent)
            {
                Time = time;
                BlackPercent = blackPercent;
            }

            public double Time { get; }
            public int BlackPercent { get; }

            public bool LooksLikeContent => BlackPercent < ContentMaxBlackPercent;
        }

        public async Task<PostCreditsAnalysis> DetectAsync(string videoPath, double creditsStart, double duration, CancellationToken cancellationToken)
        {
            var analysis = new PostCreditsAnalysis
            {
                AnalyzedFrom = creditsStart,
                AnalyzedTo = creditsStart,
                UsedHardwareDecoding = FFmpegHelper.IsHardwareDecodeEnabled(FFmpegHelper.GetSharedHardwareDecode(_configuration).Type)
            };

            if (creditsStart <= 0 || duration <= 0 || creditsStart >= duration)
            {
                analysis.Error = "credits start is outside the video";
                return analysis;
            }

            var analysisLength = Math.Min(duration - creditsStart, MaximumAnalysisSeconds);
            analysis.AnalyzedTo = creditsStart + analysisLength;
            if (analysisLength < MinimumCreditsBeforeSceneSeconds + MinimumSceneSeconds)
            {
                analysis.Error = "too little video after the credits start to hold a scene";
                return analysis;
            }

            var samples = await SampleFramesAsync(videoPath, creditsStart, analysisLength, analysis, cancellationToken).ConfigureAwait(false);
            analysis.SampleCount = samples.Count;
            if (samples.Count == 0)
            {
                if (string.IsNullOrEmpty(analysis.Error))
                    analysis.Error = "no frames could be read";
                return analysis;
            }

            analysis.Scene = FindScene(samples, creditsStart);
            return analysis;
        }

        private double MinimumSceneSeconds => Math.Max(3, _configuration.PostCreditsMinSceneSeconds);

        private PostCreditsScene? FindScene(List<FrameSample> samples, double creditsStart)
        {
            var frameStep = 1.0 / SampleFramesPerSecond;
            var earliestSceneStart = creditsStart + MinimumCreditsBeforeSceneSeconds;

            var gapStart = samples[0].Time;
            double? lastLongContentEnd = null;
            var index = 0;
            while (index < samples.Count)
            {
                var sample = samples[index];
                if (!sample.LooksLikeContent)
                {
                    index++;
                    continue;
                }

                var runStart = sample.Time;
                var runEnd = sample.Time;
                var lastContentIndex = index;
                var cursor = index + 1;
                while (cursor < samples.Count)
                {
                    if (samples[cursor].LooksLikeContent)
                    {
                        runEnd = samples[cursor].Time;
                        lastContentIndex = cursor;
                    }
                    else if (samples[cursor].Time - runEnd > MaximumGapInsideSceneSeconds)
                    {
                        break;
                    }
                    cursor++;
                }

                var runLength = runEnd - runStart + frameStep;
                if (runLength >= MinimumSceneSeconds)
                {
                    var gapBefore = runStart - (lastLongContentEnd ?? gapStart);
                    if (runStart >= earliestSceneStart && gapBefore >= MinimumDarkGapBeforeSceneSeconds)
                    {
                        return new PostCreditsScene(runStart, runEnd + frameStep);
                    }

                    lastLongContentEnd = runEnd + frameStep;
                }

                index = lastContentIndex + 1;
            }

            return null;
        }

        private async Task<List<FrameSample>> SampleFramesAsync(string videoPath, double start, double length, PostCreditsAnalysis analysis, CancellationToken cancellationToken)
        {
            var samples = new List<FrameSample>();
            var inputPath = FFmpegHelper.ResolveInputPath(videoPath);
            var blackThreshold = Math.Max(0, Math.Min(255, _configuration.BlackFrameThreshold));

            var startInfo = new ProcessStartInfo
            {
                FileName = FFmpegHelper.GetFfmpegPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList().Add("-hide_banner");
            startInfo.ArgumentList().Add("-nostats");
            if (_configuration.BlackFrameFfmpegThreads > 0)
            {
                startInfo.ArgumentList().Add("-threads");
                startInfo.ArgumentList().Add(_configuration.BlackFrameFfmpegThreads.ToString(CultureInfo.InvariantCulture));
            }
            var (hardwareType, hardwareDevice) = FFmpegHelper.GetSharedHardwareDecode(_configuration);
            FFmpegHelper.AddHardwareDecodeArgs(startInfo, hardwareType, hardwareDevice, _logger);
            startInfo.ArgumentList().Add("-ss");
            startInfo.ArgumentList().Add(start.ToString(CultureInfo.InvariantCulture));
            FFmpegHelper.AddStrmNetworkResilienceArgs(startInfo, videoPath, inputPath, _configuration);
            startInfo.ArgumentList().Add("-i");
            startInfo.ArgumentList().Add(inputPath);
            startInfo.ArgumentList().Add("-t");
            startInfo.ArgumentList().Add(length.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList().Add("-an");
            startInfo.ArgumentList().Add("-sn");
            startInfo.ArgumentList().Add("-dn");
            startInfo.ArgumentList().Add("-vf");
            startInfo.ArgumentList().Add(string.Format(CultureInfo.InvariantCulture,
                "fps={0},scale=320:-2,crop=iw*0.6:ih*0.5,blackframe=amount=0:threshold={1}",
                SampleFramesPerSecond, blackThreshold));
            startInfo.ArgumentList().Add("-f");
            startInfo.ArgumentList().Add("null");
            startInfo.ArgumentList().Add("-");

            using var process = new Process { StartInfo = startInfo };
            startInfo.ApplyArgumentList();
            process.Start();
            FFmpegHelper.RegisterProcess(process, $"Post-credits check: {Path.GetFileName(videoPath)}");
            CpuThrottler.SetProcessPriority(process, _configuration);

            var timeout = FFmpegHelper.ResolveOperationTimeout(_configuration, defaultMinutes: 5.0);
            using var timeoutCts = new CancellationTokenSource(timeout ?? Timeout.InfiniteTimeSpan);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var errorLines = new List<string>();

            try
            {
                var reader = process.StandardError;
                using (linkedCts.Token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch { }
                }))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (TryParseBlackFrameLine(line, out var time, out var blackPercent))
                        {
                            samples.Add(new FrameSample(start + time, blackPercent));
                        }
                        else if (errorLines.Count < 5 && line.IndexOf("rror", StringComparison.Ordinal) >= 0)
                        {
                            errorLines.Add(line.Trim());
                        }
                    }
                }

                if (linkedCts.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    analysis.Error = "timed out";
                    _logger.Warn($"Post-credits scene check timed out for {Path.GetFileName(videoPath)}");
                    return new List<FrameSample>();
                }

                process.WaitForExit(5000);
                if (samples.Count == 0 && errorLines.Count > 0)
                    analysis.Error = string.Join(" | ", errorLines);

                return samples;
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }

                try { await stdoutTask.ConfigureAwait(false); } catch { }
                FFmpegHelper.UnregisterProcess(process);
            }
        }

        private static bool TryParseBlackFrameLine(string line, out double time, out int blackPercent)
        {
            time = 0;
            blackPercent = 0;

            var blackIndex = line.IndexOf("pblack:", StringComparison.Ordinal);
            if (blackIndex < 0)
                return false;

            var timeIndex = line.IndexOf(" t:", blackIndex, StringComparison.Ordinal);
            if (timeIndex < 0)
                return false;

            return int.TryParse(ReadToken(line, blackIndex + 7), NumberStyles.Integer, CultureInfo.InvariantCulture, out blackPercent) &&
                   double.TryParse(ReadToken(line, timeIndex + 3), NumberStyles.Float, CultureInfo.InvariantCulture, out time);
        }

        private static string ReadToken(string line, int start)
        {
            var end = line.IndexOf(' ', start);
            return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
        }
    }
}
