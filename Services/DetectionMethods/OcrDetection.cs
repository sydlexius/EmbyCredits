using MediaBrowser.Model.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{

    public class OcrDetection : BaseDetectionMethod
    {
        public override string MethodName => "OCR Detection";

        private static readonly AsyncLocal<double?> _calculatedConfidenceState = new AsyncLocal<double?>();
        private double _calculatedConfidence
        {
            get => _calculatedConfidenceState.Value ?? 0.95;
            set => _calculatedConfidenceState.Value = value;
        }
        public override double Confidence => _calculatedConfidence;
        
        private readonly bool _isForAnime;
        
        public override int Priority => Configuration.OcrDetectionPriority;
        public override bool IsEnabled => Configuration.DetectionMode == DetectionMode.OcrOnly || 
                                          Configuration.DetectionMode == DetectionMode.OcrWithHashFallback ||
                                          Configuration.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                          (_isForAnime && Configuration.AnimeDetectionMethod == AnimeDetectionMethod.Ocr);

        private static readonly AsyncLocal<List<double>?> _ocrTextConfidencesState = new AsyncLocal<List<double>?>();
        private List<double> _ocrTextConfidences
        {
            get
            {
                if (_ocrTextConfidencesState.Value == null)
                    _ocrTextConfidencesState.Value = new List<double>();
                return _ocrTextConfidencesState.Value;
            }
            set => _ocrTextConfidencesState.Value = value;
        }

        private static readonly AsyncLocal<int> _totalKeywordMatchesState = new AsyncLocal<int>();
        private int _totalKeywordMatches
        {
            get => _totalKeywordMatchesState.Value;
            set => _totalKeywordMatchesState.Value = value;
        }

        private static readonly AsyncLocal<int> _totalFramesProcessedState = new AsyncLocal<int>();
        private int _totalFramesProcessed
        {
            get => _totalFramesProcessedState.Value;
            set => _totalFramesProcessedState.Value = value;
        }

        private static readonly AsyncLocal<int?> _lastProcessedFrameIndexState = new AsyncLocal<int?>();
        private int _lastProcessedFrameIndex
        {
            get => _lastProcessedFrameIndexState.Value ?? -1;
            set => _lastProcessedFrameIndexState.Value = value;
        }

        private static readonly AsyncLocal<DateTime?> _lastFrameProgressTimeState = new AsyncLocal<DateTime?>();
        private DateTime _lastFrameProgressTime
        {
            get => _lastFrameProgressTimeState.Value ?? DateTime.UtcNow;
            set => _lastFrameProgressTimeState.Value = value;
        }

        private static readonly AsyncLocal<int> _stuckRetryCountState = new AsyncLocal<int>();
        private int _stuckRetryCount
        {
            get => _stuckRetryCountState.Value;
            set => _stuckRetryCountState.Value = value;
        }
        private const int StuckDetectionTimeoutSeconds = 20;
        private const int MaxStuckRetries = 1;

        private static readonly Lazy<Services.Http.IHttpClientWrapper> _httpClient = new Lazy<Services.Http.IHttpClientWrapper>(() => 
            Services.Http.HttpClientPool.Instance.GetClient()
        );

        private static readonly Regex _multipleWhitespaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NormalizedKeyword[]> _normalizedKeywordsByString
            = new System.Collections.Concurrent.ConcurrentDictionary<string, NormalizedKeyword[]>(StringComparer.Ordinal);
        private const int MaxNormalizedKeywordCacheEntries = 64;
        private const int ShortKeywordLength = 4;

        private NormalizedKeyword[] _normalizedKeywordCache = Array.Empty<NormalizedKeyword>();

        private readonly struct NormalizedKeyword
        {
            public NormalizedKeyword(string lower, string lowerCollapsed)
            {
                Lower = lower;
                LowerCollapsed = lowerCollapsed;
                RequireStartBoundary = UsesWordSeparatedScript(lower);
                RequireEndBoundary = RequireStartBoundary && lower.Length <= ShortKeywordLength;
                WordCount = lower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            }

            public string Lower { get; }
            public string LowerCollapsed { get; }
            public bool RequireStartBoundary { get; }
            public bool RequireEndBoundary { get; }
            public int WordCount { get; }
        }

        private static readonly DetectionTimestampCache _cache = new DetectionTimestampCache();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _av1VideoCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private const int MaxAv1CacheEntries = 2000;

        public OcrDetection(ILogger logger, PluginConfiguration configuration, bool isForAnime = false)
            : base(logger, configuration)
        {
            _isForAnime = isForAnime;
        }

        public static void ClearSeriesCache(string seriesId, int seasonNumber) =>
            _cache.ClearSeries(seriesId, seasonNumber);

        public static void ClearAllCache() =>
            _cache.ClearAll();

        public async Task<double> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken = default)
        {
            if (!Configuration.OcrEnableEpisodeComparison || string.IsNullOrEmpty(seriesId))
            {
                return await DetectCredits(videoPath, duration, cancellationToken).ConfigureAwait(false);
            }

            var cacheKey = DetectionTimestampCache.MakeCacheKey(seriesId, seasonNumber);
            _cache.TouchAccess(cacheKey);
            _cache.EnsureCleanedIfNeeded();

            double result;
            
            double averageTimestamp = 0;
            double standardDeviation = 0;
            bool cacheReady = false;
            if (_cache.TryGetTimestamps(cacheKey, out var cachedTimestamps))
            {
                lock (cachedTimestamps)
                {
                    if (cachedTimestamps.Count >= Configuration.OcrEpisodeComparisonMinimumEpisodes)
                    {
                        cacheReady = true;
                        averageTimestamp = cachedTimestamps.Average();
                        var variance = cachedTimestamps.Select(t => Math.Pow(t - averageTimestamp, 2)).Average();
                        standardDeviation = Math.Sqrt(variance);
                    }
                }
            }

            if (cacheReady)
            {

                var tolerance = Math.Max(Configuration.OcrEpisodeComparisonTolerance, standardDeviation * 4);
                var narrowStartTime = Math.Max(0, averageTimestamp - tolerance);
                var narrowEndTime = Math.Min(duration, averageTimestamp + tolerance);
                
                var searchUnit = (Configuration.OcrSearchStartUnit ?? "minutes").ToLowerInvariant();
                var searchValue = Configuration.OcrSearchStartValue;
                double userConfiguredStartTime;
                
                if (searchUnit == "minutes")
                {
                    userConfiguredStartTime = Math.Max(0, duration - (searchValue * 60));
                }
                else if (searchUnit == "percentage")
                {
                    var searchStartPercentage = searchValue / 100.0;
                    userConfiguredStartTime = duration * searchStartPercentage;
                }
                else
                {
                    userConfiguredStartTime = Math.Max(0, duration - (searchValue * 60));
                }
                
                if (narrowStartTime < userConfiguredStartTime)
                {
                    LogDebug($"OCR Episode comparison wanted to start at {FormatTime(narrowStartTime)}, but respecting user's search start setting of {FormatTime(userConfiguredStartTime)}");
                    narrowStartTime = userConfiguredStartTime;
                }
                
                var narrowDuration = narrowEndTime - narrowStartTime;

                LogInfo($"OCR Episode comparison for {cacheKey} E{episodeNumber:D2}: Using {cachedTimestamps.Count} episodes, avg={FormatTime(averageTimestamp)}, stdDev={standardDeviation:F1}s");
                LogInfo($"OCR Narrowing search to {FormatTime(narrowStartTime)} - {FormatTime(narrowEndTime)} (window: {narrowDuration:F0}s, tolerance: ±{tolerance:F0}s)");

                result = await DetectCreditsNarrowed(videoPath, duration, narrowStartTime, narrowDuration, cancellationToken).ConfigureAwait(false);

                if (result > 0)
                {
                    _calculatedConfidence = 0.95;
                    
                    if (!string.IsNullOrEmpty(DetectionReason))
                    {
                        DetectionReason = $"Episode Comparison (based on {cachedTimestamps.Count} episodes, avg={FormatTime(averageTimestamp)}) | {DetectionReason}";
                    }
                    else
                    {
                        DetectionReason = $"Episode Comparison (based on {cachedTimestamps.Count} episodes, avg={FormatTime(averageTimestamp)})";
                    }
                }
                else
                {
                    LogWarn($"OCR Episode comparison failed to detect credits in narrowed window for {cacheKey} E{episodeNumber:D2}");
                    LogInfo($"Retrying with full search window (keeping cache)...");
                    
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retrying with full window");
                    
                    result = await DetectCredits(videoPath, duration, cancellationToken).ConfigureAwait(false);
                    
                    if (result > 0)
                    {
                        LogInfo($"OCR Retry successful - credits found at {FormatTime(result)} (was outside comparison window)");
                        LogInfo($"Adding new timestamp to cache - this will widen tolerance for future episodes");
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retry successful");
                    }
                }
            }
            else
            {
                result = await DetectCredits(videoPath, duration, cancellationToken).ConfigureAwait(false);
            }

            if (result > 0)
            {
                var episodeTimestamps = _cache.GetOrAddList(cacheKey);
                lock (episodeTimestamps)
                {
                    episodeTimestamps.Add(result);
                    
                    if (episodeTimestamps.Count > 1)
                    {
                        var newAvg = episodeTimestamps.Average();
                        var newStdDev = Math.Sqrt(episodeTimestamps.Average(t => Math.Pow(t - newAvg, 2)));
                        LogInfo($"Stored OCR timestamp {FormatTime(result)} for {cacheKey} E{episodeNumber:D2} (total: {episodeTimestamps.Count} episodes, new avg: {FormatTime(newAvg)}, stdDev: {newStdDev:F1}s)");
                    }
                    else
                    {
                        LogInfo($"Stored OCR timestamp {FormatTime(result)} for {cacheKey} E{episodeNumber:D2} (first episode in cache)");
                    }
                }
            }

            return result;
        }

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            return await DetectCreditsInternal(videoPath, duration, null, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task<double> DetectCreditsNarrowed(string videoPath, double duration, double narrowStartTime, double narrowDuration, CancellationToken cancellationToken)
        {
            return await DetectCreditsInternal(videoPath, duration, narrowStartTime, narrowDuration, cancellationToken).ConfigureAwait(false);
        }

        private async Task<double> DetectCreditsInternal(string videoPath, double duration, double? overrideStartTime, double? overrideDuration, CancellationToken cancellationToken)
        {
            LastError = string.Empty;
            
            _ocrTextConfidences = new List<double>();
            _totalKeywordMatches = 0;
            _totalFramesProcessed = 0;
            _calculatedConfidence = 0.95;
            
            _lastProcessedFrameIndex = -1;
            _lastFrameProgressTime = DateTime.UtcNow;
            _stuckRetryCount = 0;
            
            try
            {
                if (Configuration.OcrEngine == OcrEngine.LocalTesseract)
                {
                    try { LocalTesseractService.ResolveBinaryPath(Configuration.LocalTesseractPath); }
                    catch (FileNotFoundException ex)
                    {
                        LastError = ex.Message;
                        LogWarn(ex.Message);
                        return 0;
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(Configuration.OcrEndpoint))
                    {
                        LastError = "OCR endpoint not configured";
                        LogWarn("OCR endpoint not configured. Please set the OCR API URL in settings.");
                        return 0;
                    }

                    var endpointAvailable = await TestOcrEndpoint(cancellationToken).ConfigureAwait(false);
                    if (!endpointAvailable)
                    {
                        LastError = $"OCR endpoint {Configuration.OcrEndpoint} is not accessible";
                        LogWarn($"OCR endpoint {Configuration.OcrEndpoint} is not accessible. Skipping OCR detection.");
                        return 0;
                    }
                }

                LogDebug("Analyzing video for OCR-based credits detection...");
                UpdateProgress(5, "Starting OCR detection");

                var keywords = ParseKeywords(Configuration.OcrDetectionKeywords);
                if (keywords.Count == 0)
                {
                    LastError = "No OCR keywords configured";
                    LogWarn("No keywords configured for OCR detection");
                    return 0;
                }

                LogDebug($"OCR searching for {keywords.Count} keywords: {string.Join(", ", keywords.Take(5))}{(keywords.Count > 5 ? "..." : "")}");

                double startTime;
                double analysisDuration;

                if (overrideStartTime.HasValue && overrideDuration.HasValue)
                {
                    startTime = overrideStartTime.Value;
                    analysisDuration = overrideDuration.Value;
                    LogDebug($"OCR using narrowed search window: {FormatTime(startTime)} to {FormatTime(startTime + analysisDuration)} (duration: {analysisDuration:F0}s)");
                }
                else
                {
                    var searchUnit = (Configuration.OcrSearchStartUnit ?? "minutes").ToLowerInvariant();
                    var searchValue = Configuration.OcrSearchStartValue;

                    if (searchUnit == "minutes")
                    {
                        startTime = Math.Max(0, duration - (searchValue * 60));
                        LogDebug($"OCR starting {searchValue} minutes from end at {FormatTime(startTime)}");
                    }
                    else if (searchUnit == "percentage")
                    {
                        var searchStartPercentage = searchValue / 100.0;
                        startTime = duration * searchStartPercentage;
                        LogDebug($"OCR starting at {searchValue}% ({FormatTime(startTime)})");
                    }
                    else
                    {
                        startTime = Math.Max(0, duration - (searchValue * 60));
                        LogDebug($"OCR starting {searchValue} minutes from end at {FormatTime(startTime)} (invalid unit '{searchUnit}', defaulting to minutes)");
                    }

                    analysisDuration = duration - startTime;
                }

                if (Configuration.OcrStopSecondsFromEnd > 0)
                {
                    var stopTime = duration - Configuration.OcrStopSecondsFromEnd;
                    if (startTime + analysisDuration > stopTime)
                    {
                        analysisDuration = Math.Max(0, stopTime - startTime);
                        LogDebug($"Stopping analysis {Configuration.OcrStopSecondsFromEnd} seconds before end at {FormatTime(stopTime)}");
                    }
                }

                if (Configuration.OcrMaxAnalysisDuration > 0 && analysisDuration > Configuration.OcrMaxAnalysisDuration)
                {
                    analysisDuration = Configuration.OcrMaxAnalysisDuration;
                    LogDebug($"Limiting OCR analysis to {Configuration.OcrMaxAnalysisDuration} seconds (video has {duration - startTime:F0}s remaining)");
                }

                var tempDir = Configuration.OcrUseDirectMemoryPipeline
                    ? null
                    : Path.Combine(FFmpegHelper.GetTempPath(), $"ocr_frames_{Guid.NewGuid()}");

                if (tempDir != null)
                    Directory.CreateDirectory(tempDir);

                try
                {
                    var fps = Configuration.OcrFrameRate > 0 ? Configuration.OcrFrameRate : 1.0;
                    var recentTextFrames = new List<(double timestamp, string text)>();
                    
                    if (Configuration.OcrUseDirectMemoryPipeline)
                    {
                        LogInfo("Using direct memory pipeline (no disk I/O) for frame extraction");
                        if (Configuration.OcrAdaptiveSamplingEnabled && analysisDuration > Configuration.OcrAdaptiveCoarseIntervalSeconds * 4)
                        {
                            LogInfo($"Adaptive sampling: coarse interval {Configuration.OcrAdaptiveCoarseIntervalSeconds:F0}s, refinement radius ±{Configuration.OcrAdaptiveRefinementRadiusSeconds:F0}s");
                            var result = await ProcessFramesWithAdaptiveSampling(videoPath, duration, startTime, analysisDuration, fps, keywords, recentTextFrames, cancellationToken).ConfigureAwait(false);
                            return result;
                        }
                        else
                        {
                            var result = await ProcessFramesDirectFromMemory(videoPath, duration, startTime, analysisDuration, fps, keywords, recentTextFrames, cancellationToken).ConfigureAwait(false);
                            return result;
                        }
                    }
                    else
                    {
                        LogInfo("Using disk-based frame extraction (legacy method)");
                        var result = await ProcessFramesFromDisk(videoPath, duration, startTime, analysisDuration, fps, keywords, tempDir!, recentTextFrames, cancellationToken).ConfigureAwait(false);
                        return result;
                    }
                }
                catch (OperationCanceledException)
                {
                    LastError = "OCR detection cancelled";
                    LogInfo("OCR detection cancelled");
                    return 0;
                }
                catch (Exception ex)
                {
                    LastError = $"OCR detection error: {ex.Message}";
                    LogError("Error in OCR detection", ex);
                    return 0;
                }
                finally
                {
                    if (tempDir != null && Directory.Exists(tempDir))
                    {
                        try
                        {
                            Directory.Delete(tempDir, true);
                            LogDebug($"Cleaned up temp directory: {tempDir}");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Could not cleanup temp directory: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LastError = "OCR detection cancelled";
                LogInfo("OCR detection cancelled");
                return 0;
            }
            catch (Exception ex)
            {
                LastError = $"OCR detection error: {ex.Message}";
                LogError("Error in OCR detection", ex);
                return 0;
            }
        }

        private Task ProcessOcrResult(
            string ocrText,
            double timestamp,
            int frameIndex,
            double analysisDuration,
            double fps,
            List<string> keywords,
            List<(double timestamp, int score, string matchedText)> detectionScores,
            List<(double timestamp, int charCount)> characterDensityHistory,
            List<(double timestamp, string text)> recentTextFrames,
            int maxFramesToProcess)
        {
            var estimatedTotal = Math.Min(maxFramesToProcess, (int)(analysisDuration * fps));
            var ocrProgress = estimatedTotal > 0 ? (double)(frameIndex + 1) / estimatedTotal : 0;
            var overallProgress = 15 + (ocrProgress * 80);
            UpdateProgress(overallProgress, $"OCR: {frameIndex + 1} frames ({ocrProgress:P0})");

            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                recentTextFrames.Add((timestamp, ocrText));
                if (recentTextFrames.Count > Configuration.OcrScrollingMinFrames + 5)
                {
                    recentTextFrames.RemoveAt(0);
                }

                var charCount = CountMeaningfulCharacters(ocrText);
                characterDensityHistory.Add((timestamp, charCount));

                var matchedKeywords = Configuration.OcrEnableFuzzyMatching 
                    ? FindKeywordMatchesFuzzy(ocrText, keywords, Configuration.OcrFuzzyMatchMaxDistance)
                    : FindKeywordMatches(ocrText, keywords);

                var densityDetected = false;
                if (Configuration.OcrEnableCharacterDensityDetection)
                {
                    densityDetected = CheckCharacterDensity(characterDensityHistory, detectionScores, timestamp, charCount, ocrText);
                }

                var scrollingDetected = false;
                if (Configuration.OcrEnableScrollingDetection && recentTextFrames.Count >= Configuration.OcrScrollingMinFrames)
                {
                    scrollingDetected = OcrOptimizations.DetectScrollingPattern(
                        recentTextFrames.TakeLast(Configuration.OcrScrollingMinFrames).ToList(),
                        Configuration.OcrScrollingMinFrames,
                        Configuration.OcrScrollingOverlapThreshold);
                }

                var structureDetected = false;
                if (Configuration.OcrEnableCreditStructureDetection)
                {
                    structureDetected = DetectCreditStructure(ocrText, Configuration.OcrMinimumStructureLines);
                }

                bool frameIndicatesCredits = false;
                if (Configuration.OcrCharacterDensityPrimaryMethod)
                {
                    frameIndicatesCredits = densityDetected || matchedKeywords.Count > 0 || scrollingDetected || structureDetected;
                }
                else
                {
                    frameIndicatesCredits = matchedKeywords.Count > 0 || scrollingDetected || structureDetected;
                }

                if (matchedKeywords.Count > 0)
                    _totalKeywordMatches += matchedKeywords.Count;

                if (frameIndicatesCredits)
                {
                    var matchReasons = new List<string>();
                    if (matchedKeywords.Count > 0) matchReasons.Add(string.Join(", ", matchedKeywords));
                    if (densityDetected && matchedKeywords.Count == 0) matchReasons.Add("density");
                    if (scrollingDetected) matchReasons.Add("scrolling");
                    if (structureDetected) matchReasons.Add("structure");
                    
                    var matchedText = matchReasons.Count > 0 ? string.Join(" | ", matchReasons) : "density";
                    var matchScore = matchedKeywords.Count > 0 ? matchedKeywords.Count : 1;
                    if (scrollingDetected) matchScore += 1;
                    if (structureDetected) matchScore += 1;
                    
                    detectionScores.Add((timestamp, matchScore, matchedText));
                }
            }
            else
            {
                characterDensityHistory.Add((timestamp, 0));
            }
            
            return Task.CompletedTask;
        }

        private async Task<double> ProcessFramesDirectFromMemory(
            string videoPath, 
            double duration, 
            double startTime, 
            double analysisDuration, 
            double fps, 
            List<string> keywords,
            List<(double timestamp, string text)> recentTextFrames,
            CancellationToken cancellationToken)
        {
            var normalizedVideoPath = FFmpegHelper.ResolveInputPath(videoPath);
            var preInputArgs = BuildPreInputArgs(videoPath);
            var threadArgs = BuildThreadArgs();
            var filterChain = BuildFilterChain(fps, videoPath);

            var vcodec = "mjpeg";
            var codecArgs = $"-q:v {FFmpegHelper.ToMjpegQScale(Configuration.OcrJpegQuality)}";

            UpdateProgress(10, "Starting direct memory frame extraction");

            var ocrMemStartInfo = new ProcessStartInfo
            {
                FileName = FFmpegHelper.GetFfmpegPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = null
            };
            AddTokenizedArgs(ocrMemStartInfo, preInputArgs);
            ocrMemStartInfo.ArgumentList().Add("-ss");
            ocrMemStartInfo.ArgumentList().Add(startTime.ToString(CultureInfo.InvariantCulture));
            FFmpegHelper.AddStrmNetworkResilienceArgs(ocrMemStartInfo, videoPath, normalizedVideoPath, Configuration);
            ocrMemStartInfo.ArgumentList().Add("-i");
            ocrMemStartInfo.ArgumentList().Add(normalizedVideoPath);
            AddTokenizedArgs(ocrMemStartInfo, threadArgs);
            ocrMemStartInfo.ArgumentList().Add("-t");
            ocrMemStartInfo.ArgumentList().Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
            ocrMemStartInfo.ArgumentList().Add("-vf");
            ocrMemStartInfo.ArgumentList().Add(filterChain);
            ocrMemStartInfo.ArgumentList().Add("-f");
            ocrMemStartInfo.ArgumentList().Add("image2pipe");
            ocrMemStartInfo.ArgumentList().Add("-vcodec");
            ocrMemStartInfo.ArgumentList().Add(vcodec);
            AddTokenizedArgs(ocrMemStartInfo, codecArgs);
            ocrMemStartInfo.ArgumentList().Add("pipe:1");

            using (var process = new Process { StartInfo = ocrMemStartInfo })
            {
                try
                {
                    ocrMemStartInfo.ApplyArgumentList();
                    process.Start();
                    Utilities.FFmpegHelper.RegisterProcess(process, $"OCR Memory: {System.IO.Path.GetFileName(videoPath)}");
                    ActiveFfmpegProcessId = process.Id;
                    Utilities.CpuThrottler.SetProcessPriority(process, Configuration);

                    var defaultTimeoutMinutes = Configuration.OcrMaxAnalysisDuration > 0 
                    ? (Configuration.OcrMaxAnalysisDuration / 60) + 5
                    : 30;
                var resolvedTimeout = Utilities.FFmpegHelper.ResolveOperationTimeout(Configuration, defaultTimeoutMinutes);
                var timeoutMinutes = resolvedTimeout?.TotalMinutes ?? 0;
                using var timeoutCts = new CancellationTokenSource(resolvedTimeout ?? Timeout.InfiniteTimeSpan);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var effectiveToken = linkedCts.Token;

                var detectionScores = new List<(double timestamp, int matchCount, string matchedKeywords)>();
                var characterDensityHistory = new List<(double timestamp, int charCount)>();
                int frameIndex = 0;
                int maxFramesToProcess = Configuration.OcrMaxFramesToProcess > 0 ? Configuration.OcrMaxFramesToProcess : int.MaxValue;
                double creditsTimestamp = 0;

                var ffmpegErrorTask = Task.Run(async () =>
                {
                    try
                    {
                        using (var errorReader = process.StandardError)
                        {
                            var ffmpegError = await errorReader.ReadToEndAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(ffmpegError))
                            {
                                if (CreditsDetectionService.IsDebugMode)
                                    CreditsDetectionService.LogToDebug("DEBUG", $"[OCR Detection] FFmpeg stderr: {Utilities.FFmpegHelper.RemoveRepetitiveFfmpegNoise(ffmpegError)}");
                                else if (ffmpegError.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         ffmpegError.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                                    LogDebug($"FFmpeg output: {ffmpegError}");
                            }
                            return ffmpegError;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Error reading FFmpeg output: {ex.Message}");
                        return string.Empty;
                    }
                });

                JpegFrameSplitter? splitter = null;
                List<(byte[] data, double timestamp, int index)>? frameQueue = null;
                byte[]? readBuffer = null;

                try
                {
                    var stdoutStream = process.StandardOutput.BaseStream;

                    const int MaxBufferSize = 20 * 1024 * 1024;
                    splitter = new JpegFrameSplitter(MaxBufferSize);
                    readBuffer = ArrayPool<byte>.Shared.Rent(65536);
                    frameQueue = new List<(byte[] data, double timestamp, int index)>();
                    var batchSize = Configuration.OcrEnableParallelProcessing ? Configuration.OcrParallelBatchSize : 1;
                    var endOfStream = false;

                    while (!endOfStream && !effectiveToken.IsCancellationRequested && frameIndex < maxFramesToProcess)
                    {
                        int bytesRead = await stdoutStream.ReadAsync(readBuffer, 0, 65536, effectiveToken).ConfigureAwait(false);

                        if (bytesRead == 0)
                            endOfStream = true;
                        else
                            splitter.Append(readBuffer, bytesRead);

                        while (!effectiveToken.IsCancellationRequested && frameIndex < maxFramesToProcess && splitter.TryReadFrame(out var frameData))
                        {
                            if (frameData.Length < 1024)
                            {
                                frameIndex++;
                                continue;
                            }

                            var timestamp = startTime + (frameIndex / fps);
                            frameQueue.Add((frameData, timestamp, frameIndex));
                            frameIndex++;

                            if (frameQueue.Count >= batchSize)
                            {
                                await ProcessFrameBatch(frameQueue, keywords, detectionScores, characterDensityHistory,
                                    recentTextFrames, analysisDuration, fps, maxFramesToProcess, effectiveToken).ConfigureAwait(false);
                                frameQueue.Clear();
                            }
                        }

                        if (splitter.DiscardedOversizedData)
                        {
                            LogWarn($"Frame buffer exceeded {MaxBufferSize / (1024 * 1024)}MB, clearing to prevent memory leak");
                            splitter.ResetDiscardedFlag();
                        }
                    }

                    if (frameQueue.Count > 0 && !effectiveToken.IsCancellationRequested)
                    {
                        await ProcessFrameBatch(frameQueue, keywords, detectionScores, characterDensityHistory,
                            recentTextFrames, analysisDuration, fps, maxFramesToProcess, effectiveToken).ConfigureAwait(false);
                        frameQueue.Clear();
                    }

                    splitter = null;
                    if (readBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(readBuffer);
                        readBuffer = null;
                    }
                    if (frameQueue != null)
                    {
                        frameQueue.Clear();
                        frameQueue.TrimExcess();
                    }
                    frameQueue = null;

                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill();
                            LogDebug("FFmpeg process terminated");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Error killing FFmpeg process: {ex.Message}");
                        }
                    }

                    await ffmpegErrorTask.ConfigureAwait(false);
                    var memPipelineStderr = ffmpegErrorTask.Result;
                    if (timeoutCts.IsCancellationRequested && !string.IsNullOrWhiteSpace(memPipelineStderr))
                    {
                        LogWarn($"FFmpeg timed out after {timeoutMinutes:F1} min - stderr: {memPipelineStderr}");
                    }
                    if (process.HasExited && process.ExitCode != 0)
                    {
                        var hwType = Configuration.OcrEnableHardwareAcceleration && !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType) && Configuration.OcrHardwareAccelerationType != "none"
                            ? Configuration.OcrHardwareAccelerationType : null;
                        CreditsDetectionService.LogToDebug(hwType != null ? "WARN" : "DEBUG",
                            hwType != null
                                ? $"[OCR Detection] FFmpeg exited with code {process.ExitCode} - hardware acceleration ({hwType}) may have failed"
                                : $"[OCR Detection] FFmpeg exited with code {process.ExitCode}");
                    }
                }
                catch (OperationCanceledException)
                {
                    LogInfo("Frame extraction cancelled");

                    splitter = null;
                    if (readBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(readBuffer);
                        readBuffer = null;
                    }
                    if (frameQueue != null)
                    {
                        frameQueue.Clear();
                        frameQueue.TrimExcess();
                        frameQueue = null;
                    }
                    if (detectionScores != null)
                    {
                        detectionScores.Clear();
                        detectionScores.TrimExcess();
                    }
                    if (characterDensityHistory != null)
                    {
                        characterDensityHistory.Clear();
                        characterDensityHistory.TrimExcess();
                    }
                    if (recentTextFrames != null)
                    {
                        recentTextFrames.Clear();
                        recentTextFrames.TrimExcess();
                    }
                    
                    return 0;
                }

                UpdateProgress(95, $"Analyzing {detectionScores.Count} OCR detections");

                var endTime = startTime + analysisDuration;
                
                if (detectionScores.Count == 0)
                {
                    var currentEngine = Configuration.OcrEngine == OcrEngine.PaddleOCR 
                        ? "PaddleOCR" 
                        : "Tesseract";
                        
                    LogDebug("=== OCR Detection Failed - No Keywords Found ===");
                    LogDebug($"  Frames analyzed: {_totalFramesProcessed}");
                    LogDebug($"  Time range analyzed: {FormatTime(startTime)} to {FormatTime(endTime)}");
                    LogDebug($"  Search criteria: {keywords.Count} keywords configured");
                    LogDebug($"  OCR engine: {currentEngine}");
                    LogDebug(Configuration.OcrEngine == OcrEngine.LocalTesseract
                        ? $"  Local tesseract path: {(string.IsNullOrWhiteSpace(Configuration.LocalTesseractPath) ? "(auto from PATH)" : Configuration.LocalTesseractPath)}"
                        : $"  OCR endpoint: {Configuration.OcrEndpoint}");
                    LogDebug($"  Possible reasons:");
                    LogDebug($"    - No text matching configured keywords was found in the analyzed frames");
                    LogDebug($"    - Credits may start outside the analyzed time range");
                    LogDebug($"    - Text may be too small, stylized, or low contrast for OCR to detect");
                    LogDebug($"    - Keywords may not match the language or format of the credits");
                    LogDebug($"  Suggestions:");
                    LogDebug($"    - Try adjusting 'Search Start' settings to analyze a different time range");
                    LogDebug($"    - Check if keywords match the actual credits text language");
                    LogDebug($"    - Consider enabling image preprocessing options for better text detection");
                    LogDebug("=== End OCR Detection ===");
                    
                    LastError = $"No credits keywords detected via OCR (analyzed {_totalFramesProcessed} frames from {FormatTime(startTime)} to {FormatTime(endTime)})";
                    LogWarn("OCR completed but found no matching keywords");
                    
                    detectionScores.Clear();
                    detectionScores.TrimExcess();
                    characterDensityHistory.Clear();
                    characterDensityHistory.TrimExcess();
                    recentTextFrames.Clear();
                    recentTextFrames.TrimExcess();
                    
                    return 0;
                }

                creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);

                if (creditsTimestamp > 0)
                {
                    CalculateDynamicConfidence(detectionScores.Count);
                    
                    DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                    UpdateProgress(100, $"Credits detected at {FormatTime(creditsTimestamp)}");
                    LogInfo($"✓ OCR detected credits starting at {FormatTime(creditsTimestamp)}");
                    LogInfo($"  Detection based on {detectionScores.Count} keyword matches");
                    LogInfo($"  Confidence: {_calculatedConfidence:F2} ({(_calculatedConfidence * 100):F0}%)");
                    
                    detectionScores.Clear();
                    detectionScores.TrimExcess();
                    characterDensityHistory.Clear();
                    characterDensityHistory.TrimExcess();
                    recentTextFrames.Clear();
                    recentTextFrames.TrimExcess();
                    
                    return creditsTimestamp;
                }
                else
                {
                    LogDebug("=== OCR Detection Failed - No Clear Start Point ===");
                    LogDebug($"  Total keyword matches found: {detectionScores.Count}");
                    LogDebug($"  Frames with matches: {detectionScores.Count}");
                    LogDebug($"  Time range searched: {FormatTime(startTime)} to {FormatTime(endTime)}");
                    LogDebug($"  Possible reasons:");
                    LogDebug($"    - Keyword matches were too scattered or inconsistent");
                    LogDebug($"    - No clear temporal clustering of credits text");
                    LogDebug($"    - Matches may be from other text (episode title, production logos, etc.)");
                    if (Configuration.OcrEnableCharacterDensityDetection)
                    {
                        LogDebug($"    - Character density requirements not met (threshold: {Configuration.OcrCharacterDensityThreshold})");
                    }
                    LogDebug($"  Suggestions:");
                    LogDebug($"    - Review character density settings if enabled");
                    LogDebug($"    - Try adjusting keyword list to be more specific to end credits");
                    LogDebug($"    - Check if temporal consistency requirements are too strict");
                    LogDebug("=== End OCR Detection ===");
                    
                    LastError = $"No significant credits pattern found (found {detectionScores.Count} keyword matches but no clear start point)";
                    LogWarn("OCR found keywords but no clear credits start point");
                    
                    detectionScores.Clear();
                    detectionScores.TrimExcess();
                    characterDensityHistory.Clear();
                    characterDensityHistory.TrimExcess();
                    recentTextFrames.Clear();
                    recentTextFrames.TrimExcess();
                    
                    return 0;
                }
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        LogDebug($"Error during process cleanup: {cleanupEx.Message}");
                    }
                    finally
                    {
                        Utilities.FFmpegHelper.UnregisterProcess(process);
                        ActiveFfmpegProcessId = null;
                    }
                }
            }
        }

        private async Task ProcessFrameBatch(
            List<(byte[] data, double timestamp, int index)> frameQueue,
            List<string> keywords,
            List<(double timestamp, int matchCount, string matchedKeywords)> detectionScores,
            List<(double timestamp, int charCount)> characterDensityHistory,
            List<(double timestamp, string text)> recentTextFrames,
            double analysisDuration,
            double fps,
            int maxFramesToProcess,
            CancellationToken cancellationToken)
        {
            if (frameQueue.Count == 0) return;

            if (Configuration.OcrEnableParallelProcessing && frameQueue.Count > 1)
            {
                LogDebug($"Processing {frameQueue.Count} frames in parallel from memory");
                
                var ocrTasks = frameQueue.Select(async frame =>
                {
                    try
                    {
                        var (ocrText, ocrConfidence) = await PerformOcrOnFrameData(frame.data, cancellationToken).ConfigureAwait(false);
                        return (frame.timestamp, frame.index, ocrText, ocrConfidence);
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Error processing frame {frame.index}: {ex.Message}");
                        return (frame.timestamp, frame.index, string.Empty, 0.0);
                    }
                }).ToList();

                var results = await Task.WhenAll(ocrTasks).ConfigureAwait(false);

                foreach (var (timestamp, index, ocrText, ocrConfidence) in results.OrderBy(r => r.timestamp))
                {
                    if (ocrConfidence > 0)
                        _ocrTextConfidences.Add(ocrConfidence);
                    await ProcessOcrResult(ocrText, timestamp, index, analysisDuration, fps, keywords,
                        detectionScores, characterDensityHistory, recentTextFrames, maxFramesToProcess).ConfigureAwait(false);
                    _totalFramesProcessed++;
                }
                
                ocrTasks.Clear();
                ocrTasks = null;
                
                if (Configuration.OcrDelayBetweenBatchesMs > 0)
                {
                    await Task.Delay(Configuration.OcrDelayBetweenBatchesMs, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var frame in frameQueue)
                {
                    try
                    {
                        var (ocrText, ocrConfidence) = await PerformOcrOnFrameData(frame.data, cancellationToken).ConfigureAwait(false);
                        
                        if (ocrConfidence > 0)
                        {
                            _ocrTextConfidences.Add(ocrConfidence);
                        }
                        
                        await ProcessOcrResult(ocrText, frame.timestamp, frame.index, analysisDuration, fps, keywords,
                            detectionScores, characterDensityHistory, recentTextFrames, maxFramesToProcess).ConfigureAwait(false);
                        _totalFramesProcessed++;
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Error processing frame {frame.index}: {ex.Message}");
                    }
                }
            }
        }

        private async Task<double> ProcessFramesWithAdaptiveSampling(
            string videoPath,
            double duration,
            double startTime,
            double analysisDuration,
            double fps,
            List<string> keywords,
            List<(double timestamp, string text)> recentTextFrames,
            CancellationToken cancellationToken)
        {
            var coarseInterval = Configuration.OcrAdaptiveCoarseIntervalSeconds;
            var coarseFps = 1.0 / coarseInterval;
            var refinementRadius = Configuration.OcrAdaptiveRefinementRadiusSeconds;
            var searchEnd = startTime + analysisDuration;

            LogInfo($"Adaptive sampling phase 1: scanning {FormatTime(startTime)} to {FormatTime(searchEnd)} at 1 frame/{coarseInterval:F0}s");
            UpdateProgress(10, "Adaptive: coarse scan");

            var hitTimestamps = await ScanCoarseFrames(videoPath, startTime, analysisDuration, coarseFps, keywords, cancellationToken).ConfigureAwait(false);

            if (hitTimestamps.Count == 0)
            {
                LogInfo("Adaptive sampling: no signals found in coarse pass, falling back to full scan");
                UpdateProgress(20, "Adaptive: no coarse hits, full scan");
                return await ProcessFramesDirectFromMemory(videoPath, duration, startTime, analysisDuration, fps, keywords, recentTextFrames, cancellationToken).ConfigureAwait(false);
            }

            LogInfo($"Adaptive sampling: coarse pass found {hitTimestamps.Count} signal(s) at [{string.Join(", ", hitTimestamps.Select(FormatTime))}]");

            var windows = BuildRefinementWindows(hitTimestamps, refinementRadius, startTime, searchEnd);
            var totalWindowDuration = windows.Sum(w => w.end - w.start);

            if (totalWindowDuration >= analysisDuration * 0.5)
            {
                LogInfo($"Adaptive sampling: coarse hits cover {totalWindowDuration:F0}s of {analysisDuration:F0}s (too widespread to refine efficiently, likely subtitles/on-screen text) - running a single full-rate scan instead");
                UpdateProgress(20, "Adaptive: hits too widespread, full scan");
                return await ProcessFramesDirectFromMemory(videoPath, duration, startTime, analysisDuration, fps, keywords, recentTextFrames, cancellationToken).ConfigureAwait(false);
            }

            LogInfo($"Adaptive sampling phase 2: refining {windows.Count} window(s) at {fps} fps");

            double earliest = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var (winStart, winEnd) = windows[i];
                var winDuration = winEnd - winStart;
                if (winDuration <= 0)
                    continue;

                LogInfo($"Adaptive sampling: fine scan window {i + 1}/{windows.Count}: {FormatTime(winStart)} - {FormatTime(winEnd)} ({winDuration:F0}s)");
                UpdateProgress(30 + (i * 60 / windows.Count), $"Adaptive: refining window {i + 1}/{windows.Count}");

                var windowResult = await ProcessFramesDirectFromMemory(
                    videoPath, duration, winStart, winDuration, fps, keywords,
                    new List<(double, string)>(), cancellationToken).ConfigureAwait(false);

                if (windowResult > 0 && (earliest == 0 || windowResult < earliest))
                    earliest = windowResult;
            }

            if (earliest > 0)
                LogInfo($"Adaptive sampling: credits pinned at {FormatTime(earliest)} after {windows.Count} refinement window(s)");
            else
                LogWarn("Adaptive sampling: fine scan found no matches; returning 0");

            return earliest;
        }

        private async Task<List<double>> ScanCoarseFrames(
            string videoPath,
            double startTime,
            double analysisDuration,
            double coarseFps,
            List<string> keywords,
            CancellationToken cancellationToken)
        {
            var hitTimestamps = new List<double>();
            var pendingBatch = new List<(byte[] data, double timestamp)>();
            var coarseBatchSize = Configuration.OcrEnableParallelProcessing ? Math.Max(1, Configuration.OcrParallelBatchSize) : 1;
            var normalizedPath = FFmpegHelper.ResolveInputPath(videoPath);
            var preInputArgs = BuildPreInputArgs(videoPath);
            var threadArgs = BuildThreadArgs();
            var filterChain = BuildFilterChain(coarseFps, videoPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = FFmpegHelper.GetFfmpegPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = null
            };
            AddTokenizedArgs(startInfo, preInputArgs);
            startInfo.ArgumentList().Add("-ss");
            startInfo.ArgumentList().Add(startTime.ToString(CultureInfo.InvariantCulture));
            FFmpegHelper.AddStrmNetworkResilienceArgs(startInfo, videoPath, normalizedPath, Configuration);
            startInfo.ArgumentList().Add("-i");
            startInfo.ArgumentList().Add(normalizedPath);
            AddTokenizedArgs(startInfo, threadArgs);
            startInfo.ArgumentList().Add("-t");
            startInfo.ArgumentList().Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList().Add("-vf");
            startInfo.ArgumentList().Add(filterChain);
            startInfo.ArgumentList().Add("-f");
            startInfo.ArgumentList().Add("image2pipe");
            startInfo.ArgumentList().Add("-vcodec");
            startInfo.ArgumentList().Add("mjpeg");
            AddTokenizedArgs(startInfo, $"-q:v {FFmpegHelper.ToMjpegQScale(Configuration.OcrJpegQuality)}");
            startInfo.ArgumentList().Add("pipe:1");

            using var process = new Process { StartInfo = startInfo };
            try
            {
                startInfo.ApplyArgumentList();
                process.Start();
                Utilities.FFmpegHelper.RegisterProcess(process, $"OCR Coarse: {System.IO.Path.GetFileName(videoPath)}");
                ActiveFfmpegProcessId = process.Id;
                Utilities.CpuThrottler.SetProcessPriority(process, Configuration);

                var defaultTimeoutMinutes = (analysisDuration / 60) + 5;
                var resolvedTimeout = Utilities.FFmpegHelper.ResolveOperationTimeout(Configuration, defaultTimeoutMinutes);
                var timeoutMinutes = resolvedTimeout?.TotalMinutes ?? 0;
                using var timeoutCts = new CancellationTokenSource(resolvedTimeout ?? Timeout.InfiniteTimeSpan);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var token = linkedCts.Token;

                var stderrTask = Task.Run(async () =>
                {
                    try
                    {
                        var ffmpegError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ffmpegError))
                        {
                            if (CreditsDetectionService.IsDebugMode)
                                CreditsDetectionService.LogToDebug("DEBUG", $"[OCR Detection] FFmpeg coarse scan stderr: {Utilities.FFmpegHelper.RemoveRepetitiveFfmpegNoise(ffmpegError)}");
                            else if (ffmpegError.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     ffmpegError.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                                LogDebug($"FFmpeg coarse scan output: {ffmpegError}");
                        }
                        return ffmpegError;
                    }
                    catch { return string.Empty; }
                });

                var splitter = new JpegFrameSplitter(20 * 1024 * 1024);
                var readBuffer = ArrayPool<byte>.Shared.Rent(65536);
                int frameIndex = 0;

                try
                {
                    var stdoutStream = process.StandardOutput.BaseStream;
                    var endOfStream = false;

                    while (!endOfStream && !token.IsCancellationRequested)
                    {
                        int bytesRead = await stdoutStream.ReadAsync(readBuffer, 0, 65536, token).ConfigureAwait(false);

                        if (bytesRead == 0)
                            endOfStream = true;
                        else
                            splitter.Append(readBuffer, bytesRead);

                        while (!token.IsCancellationRequested && splitter.TryReadFrame(out var frameData))
                        {
                            if (frameData.Length >= 1024)
                            {
                                var timestamp = startTime + (frameIndex / coarseFps);
                                pendingBatch.Add((frameData, timestamp));

                                if (pendingBatch.Count >= coarseBatchSize)
                                {
                                    await FlushCoarseBatch(pendingBatch, keywords, hitTimestamps, token).ConfigureAwait(false);
                                    if (Configuration.OcrDelayBetweenBatchesMs > 0)
                                        await Task.Delay(Configuration.OcrDelayBetweenBatchesMs, token).ConfigureAwait(false);
                                }
                            }
                            frameIndex++;
                        }
                    }

                    await FlushCoarseBatch(pendingBatch, keywords, hitTimestamps, token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(readBuffer);

                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch { }
                    }
                    var coarseStderr = await stderrTask.ConfigureAwait(false);
                    if (timeoutCts.IsCancellationRequested && !string.IsNullOrWhiteSpace(coarseStderr))
                    {
                        LogWarn($"FFmpeg coarse scan timed out after {timeoutMinutes:F1} min - stderr: {coarseStderr}");
                    }
                    if (process.HasExited && process.ExitCode != 0)
                    {
                        var hwType = Configuration.OcrEnableHardwareAcceleration && !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType) && Configuration.OcrHardwareAccelerationType != "none"
                            ? Configuration.OcrHardwareAccelerationType : null;
                        CreditsDetectionService.LogToDebug(hwType != null ? "WARN" : "DEBUG",
                            hwType != null
                                ? $"[OCR Detection] FFmpeg coarse scan exited with code {process.ExitCode} - hardware acceleration ({hwType}) may have failed"
                                : $"[OCR Detection] FFmpeg coarse scan exited with code {process.ExitCode}");
                    }
                }
            }
            finally
            {
                Utilities.FFmpegHelper.UnregisterProcess(process);
                ActiveFfmpegProcessId = null;
            }

            return hitTimestamps;
        }

        private async Task FlushCoarseBatch(
            List<(byte[] data, double timestamp)> batch,
            List<string> keywords,
            List<double> hitTimestamps,
            CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
                return;

            var tasks = batch.Select(async item =>
            {
                var isHit = await HasCreditSignal(item.data, keywords, cancellationToken).ConfigureAwait(false);
                return (item.timestamp, isHit);
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            batch.Clear();

            foreach (var (timestamp, isHit) in results.OrderBy(r => r.timestamp))
            {
                if (isHit)
                {
                    hitTimestamps.Add(timestamp);
                    LogDebug($"Adaptive coarse hit at {FormatTime(timestamp)}");
                }
            }
        }

        private async Task<bool> HasCreditSignal(byte[] frameData, List<string> keywords, CancellationToken cancellationToken)
        {
            var (ocrText, _) = await PerformOcrOnFrameData(frameData, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(ocrText))
                return false;

            var matches = Configuration.OcrEnableFuzzyMatching
                ? FindKeywordMatchesFuzzy(ocrText, keywords, Configuration.OcrFuzzyMatchMaxDistance)
                : FindKeywordMatches(ocrText, keywords);

            if (matches.Count > 0)
                return true;

            if (Configuration.OcrEnableCharacterDensityDetection &&
                CountMeaningfulCharacters(ocrText) >= Configuration.OcrCharacterDensityThreshold)
                return true;

            if (Configuration.OcrEnableCreditStructureDetection &&
                DetectCreditStructure(ocrText, Configuration.OcrMinimumStructureLines))
                return true;

            return false;
        }

        private static List<(double start, double end)> BuildRefinementWindows(
            List<double> hitTimestamps, double radius, double searchStart, double searchEnd)
        {
            var sorted = hitTimestamps.OrderBy(t => t).ToList();
            var windows = new List<(double start, double end)>();

            double wStart = sorted[0] - radius;
            double wEnd   = sorted[0] + radius;

            for (int i = 1; i < sorted.Count; i++)
            {
                double t = sorted[i];
                if (t - radius <= wEnd)
                {
                    wEnd = Math.Max(wEnd, t + radius);
                }
                else
                {
                    windows.Add((Math.Max(searchStart, wStart), Math.Min(searchEnd, wEnd)));
                    wStart = t - radius;
                    wEnd   = t + radius;
                }
            }
            windows.Add((Math.Max(searchStart, wStart), Math.Min(searchEnd, wEnd)));

            return windows;
        }

        private async Task<double> ProcessFramesFromDisk(
            string videoPath, 
            double duration, 
            double startTime, 
            double analysisDuration, 
            double fps, 
            List<string> keywords,
            string tempDir,
            List<(double timestamp, string text)> recentTextFrames,
            CancellationToken cancellationToken)
        {
            try
            {
                var imageExtension = "jpg";

                var qualityParam = $"-q:v {FFmpegHelper.ToMjpegQScale(Configuration.OcrJpegQuality)}";

                var frameOutputPath = $"{tempDir.Replace("\\", "/")}/frame_%04d.{imageExtension}";

                var ffmpegTempDir = tempDir.Replace("\\", "/");
                var ffmpegFramePath = $"{ffmpegTempDir}/frame_%04d.{imageExtension}";
                
                var normalizedVideoPathDisk = FFmpegHelper.ResolveInputPath(videoPath);

                var preInputArgs = BuildPreInputArgs(videoPath);
                var threadArgs = BuildThreadArgs();
                var filterChain = BuildFilterChain(fps, videoPath);

                LogDebug($"Extracting frames from {FormatTime(startTime)} at {fps} fps (JPG Q{Configuration.OcrJpegQuality}) for OCR analysis");
                LogDebug($"FFmpeg command: {FFmpegHelper.GetFfmpegPath()} -ss {startTime} -i <path> -t {analysisDuration} -vf ...");
                UpdateProgress(10, "Starting frame extraction and OCR processing");

                var ocrDiskStartInfo = new ProcessStartInfo
                {
                    FileName = FFmpegHelper.GetFfmpegPath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                AddTokenizedArgs(ocrDiskStartInfo, preInputArgs);
                ocrDiskStartInfo.ArgumentList().Add("-ss");
                ocrDiskStartInfo.ArgumentList().Add(startTime.ToString(CultureInfo.InvariantCulture));
                FFmpegHelper.AddStrmNetworkResilienceArgs(ocrDiskStartInfo, videoPath, normalizedVideoPathDisk, Configuration);
                ocrDiskStartInfo.ArgumentList().Add("-i");
                ocrDiskStartInfo.ArgumentList().Add(normalizedVideoPathDisk);
                AddTokenizedArgs(ocrDiskStartInfo, threadArgs);
                ocrDiskStartInfo.ArgumentList().Add("-t");
                ocrDiskStartInfo.ArgumentList().Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
                ocrDiskStartInfo.ArgumentList().Add("-vf");
                ocrDiskStartInfo.ArgumentList().Add(filterChain);
                AddTokenizedArgs(ocrDiskStartInfo, qualityParam);
                ocrDiskStartInfo.ArgumentList().Add("-f");
                ocrDiskStartInfo.ArgumentList().Add("image2");
                ocrDiskStartInfo.ArgumentList().Add(ffmpegFramePath);

                using (var process = new Process { StartInfo = ocrDiskStartInfo })
                {
                    ocrDiskStartInfo.ApplyArgumentList();
                    process.Start();
                    Utilities.FFmpegHelper.RegisterProcess(process, $"OCR Disk: {System.IO.Path.GetFileName(videoPath)}");
                    ActiveFfmpegProcessId = process.Id;
                    try
                    {
                    Utilities.CpuThrottler.SetProcessPriority(process, Configuration);

                    var defaultTimeoutMinutes = Configuration.OcrMaxAnalysisDuration > 0 
                        ? (Configuration.OcrMaxAnalysisDuration / 60) + 5
                        : 30;
                    var resolvedTimeout = Utilities.FFmpegHelper.ResolveOperationTimeout(Configuration, defaultTimeoutMinutes);
                    var timeoutMinutes = resolvedTimeout?.TotalMinutes ?? 0;
                    using var timeoutCts = new CancellationTokenSource(resolvedTimeout ?? Timeout.InfiniteTimeSpan);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    var effectiveToken = linkedCts.Token;

                    var detectionScores = new List<(double timestamp, int matchCount, string matchedKeywords)>();
                    var characterDensityHistory = new List<(double timestamp, int charCount)>();
                    bool loggedFirstFrame = false;
                    int frameIndex = 0;
                    int maxFramesToProcess = Configuration.OcrMaxFramesToProcess > 0 ? Configuration.OcrMaxFramesToProcess : int.MaxValue;
                    bool creditsFound = false;
                    double creditsTimestamp = 0;

                    var ffmpegTask = Task.Run(async () =>
                    {
                        try
                        {
                            using (var errorReader = process.StandardError)
                            {
                                var ffmpegError = await errorReader.ReadToEndAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(ffmpegError))
                                {
                                    if (CreditsDetectionService.IsDebugMode)
                                        CreditsDetectionService.LogToDebug("DEBUG", $"[OCR Detection] FFmpeg disk pipeline stderr: {Utilities.FFmpegHelper.RemoveRepetitiveFfmpegNoise(ffmpegError)}");
                                    else if (ffmpegError.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             ffmpegError.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                                        LogDebug($"FFmpeg output: {ffmpegError}");
                                }
                                return ffmpegError;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"Error reading FFmpeg output: {ex.Message}");
                            return string.Empty;
                        }
                    });

                    var processingTask = Task.Run(async () =>
                    {
                        var lastFrameCount = 0;
                        var noNewFramesCount = 0;
                        var waitingForFirstFrame = true;
                        const int maxNoNewFramesIterations = 100;
                        const int maxWaitForFirstFrameIterations = 600;
                        var totalWaitIterations = 0;

                        var consecutiveMatches = 0;
                        var recentMatches = new List<(double timestamp, int matchCount)>();
                        var frameSkip = 1;
                        var stuckDetected = false;

                        while (!creditsFound && frameIndex < maxFramesToProcess && !stuckDetected)
                        {

                            if (effectiveToken.IsCancellationRequested)
                            {
                                if (timeoutCts.IsCancellationRequested)
                                {
                                    LastError = $"OCR detection timed out after {timeoutMinutes} minutes";
                                    LogError($"OCR detection timed out after {timeoutMinutes} minutes");
                                }
                                else
                                {
                                    LogInfo("OCR detection cancelled");
                                }
                                break;
                            }

                            if (!Directory.Exists(tempDir))
                            {
                                await Task.Delay(50, effectiveToken).ConfigureAwait(false);
                                continue;
                            }

                            var currentFrames = Directory.GetFiles(tempDir, $"frame_*.{imageExtension}")
                                .OrderBy(f => f)
                                .Skip(frameIndex)
                                .ToList();

                            if (currentFrames.Count == 0)
                            {
                                if (process.HasExited)
                                {
                                    if (frameIndex == 0 || noNewFramesCount > 10)
                                    {
                                        break;
                                    }
                                }

                                if (lastFrameCount == frameIndex)
                                {
                                    noNewFramesCount++;
                                    totalWaitIterations++;

                                    if (waitingForFirstFrame)
                                    {
                                        if (totalWaitIterations > maxWaitForFirstFrameIterations)
                                        {
                                            LogWarn($"Timeout waiting for first frame after {totalWaitIterations * 50}ms");
                                            break;
                                        }

                                        if (totalWaitIterations % 40 == 0)
                                        {
                                            LogDebug($"Waiting for FFmpeg to generate first frame... ({totalWaitIterations * 50 / 1000}s elapsed)");
                                        }
                                    }
                                    else
                                    {
                                        if (noNewFramesCount > maxNoNewFramesIterations)
                                        {
                                            LogDebug($"No new frames for {noNewFramesCount * 50}ms, stopping frame processing");
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    noNewFramesCount = 0;
                                }

                                lastFrameCount = frameIndex;
                                await Task.Delay(50, effectiveToken).ConfigureAwait(false);
                                continue;
                            }

                            noNewFramesCount = 0;
                            waitingForFirstFrame = false;

                            if (Configuration.OcrEnableParallelProcessing && currentFrames.Count > 1)
                            {
                                var batchSize = Math.Min(Configuration.OcrParallelBatchSize, currentFrames.Count);
                                var frameBatch = currentFrames.Take(batchSize).ToList();
                                var frameBatchWithTimestamps = frameBatch.Select((f, i) => (f, startTime + ((frameIndex + i) / fps))).ToList();

                                var batchResults = await OcrOptimizations.ProcessFramesBatch(
                                    frameBatchWithTimestamps,
                                    async (path) => await PerformOcr(path, effectiveToken).ConfigureAwait(false),
                                    batchSize,
                                    effectiveToken
                                ).ConfigureAwait(false);

                                foreach (var (framePath, ocrText, ocrConfidence, timestamp) in batchResults)
                                {
                                    if (ocrConfidence > 0)
                                    {
                                        _ocrTextConfidences.Add(ocrConfidence);
                                    }
                                    
                                    var estimatedTotal = Math.Min(maxFramesToProcess, (int)(analysisDuration * fps));
                                    var ocrProgress = estimatedTotal > 0 ? (double)(frameIndex + 1) / estimatedTotal : 0;
                                    var overallProgress = 15 + (ocrProgress * 80);
                                    UpdateProgress(overallProgress, $"OCR: {frameIndex + 1} frames ({ocrProgress:P0})");

                                    if (!string.IsNullOrWhiteSpace(ocrText))
                                    {
                                        var charCount = CountMeaningfulCharacters(ocrText);
                                        characterDensityHistory.Add((timestamp, charCount));

                                        LogDebug(() =>
                                        {
                                            var textPreview = ocrText.Length > 100 ? ocrText.Substring(0, 100) + "..." : ocrText;
                                            var textOneLine = textPreview.Replace("\n", " ").Replace("\r", "");
                                            return $"Frame at {FormatTime(timestamp)}: OCR detected {charCount} chars: \"{textOneLine}\"";
                                        });

                                        var matchedKeywords = FindKeywordMatches(ocrText, keywords);

                                        var densityDetected = false;
                                        if (Configuration.OcrEnableCharacterDensityDetection)
                                        {
                                            densityDetected = CheckCharacterDensity(characterDensityHistory, detectionScores, timestamp, charCount, ocrText);
                                        }

                                        bool frameIndicatesCredits = false;
                                        if (Configuration.OcrCharacterDensityPrimaryMethod)
                                        {

                                            frameIndicatesCredits = densityDetected || matchedKeywords.Count > 0;
                                            if (densityDetected)
                                            {
                                                LogDebug(() =>
                                                {
                                                    var keywordBonus = matchedKeywords.Count > 0 ? $" + {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}" : "";
                                                    return $"Frame at {FormatTime(timestamp)}: ✓ MATCH - High text density ({charCount} chars){keywordBonus}";
                                                });
                                            }
                                        }
                                        else
                                        {

                                            frameIndicatesCredits = matchedKeywords.Count > 0;
                                        }

                                        if (frameIndicatesCredits)
                                        {
                                            var matchedText = matchedKeywords.Count > 0 ? string.Join(", ", matchedKeywords) : "density";
                                            detectionScores.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1, matchedText));
                                            consecutiveMatches++;
                                            recentMatches.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1));

                                            if (matchedKeywords.Count > 0)
                                            {
                                                _totalKeywordMatches += matchedKeywords.Count;
                                            }

                                            if (matchedKeywords.Count > 0 && !densityDetected)
                                            {
                                                LogDebug(() => $"Frame at {FormatTime(timestamp)}: ✓ MATCH - Found {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}");
                                            }
                                        }
                                        else
                                        {
                                            consecutiveMatches = 0;
                                            recentMatches.Add((timestamp, 0));
                                        }
                                    }
                                    else
                                    {
                                        characterDensityHistory.Add((timestamp, 0));
                                    }

                                    _totalFramesProcessed++;
                                    frameIndex++;
                                }

                                if (Configuration.OcrConsecutiveMatchesForEarlyStop > 0)
                                {
                                    if (OcrOptimizations.ShouldTerminateEarly(recentMatches, Configuration.OcrConsecutiveMatchesForEarlyStop))
                                    {
                                        creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                        if (creditsTimestamp > 0)
                                        {
                                            DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                            creditsFound = true;
                                            UpdateProgress(98, $"Credits found via consecutive matches! Processed {frameIndex} frames");
                                            LogInfo($"Early termination: {Configuration.OcrConsecutiveMatchesForEarlyStop} consecutive matches at {FormatTime(creditsTimestamp)}");

                                            try
                                            {
                                                if (!process.HasExited)
                                                {
                                                    process.Kill();
                                                    LogDebug("FFmpeg process terminated after early stop");
                                                }
                                            }
                                            catch (Exception killEx)
                                            {
                                                LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                            }

                                            break;
                                        }
                                    }
                                }

                                if (Configuration.OcrEnableSmartFrameSkipping && consecutiveMatches > 0)
                                {
                                    frameSkip = OcrOptimizations.CalculateSmartSkip(consecutiveMatches);
                                    if (frameSkip > 1)
                                    {
                                        LogDebug($"Smart skipping: jumping {frameSkip} frames ahead");
                                        frameIndex += (frameSkip - batchSize);
                                    }
                                }

                                if (detectionScores.Count >= Configuration.OcrMinimumMatches)
                                {
                                    creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                    if (creditsTimestamp > 0)
                                    {
                                        DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                        creditsFound = true;
                                        UpdateProgress(98, $"Credits found! Processed {frameIndex} frames");
                                        LogInfo($"Credits detected at {FormatTime(creditsTimestamp)} via OCR keyword matching");

                                        try
                                        {
                                            if (!process.HasExited)
                                            {
                                                process.Kill();
                                                LogDebug("FFmpeg process terminated after credits detected");
                                            }
                                        }
                                        catch (Exception killEx)
                                        {
                                            LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                        }

                                        break;
                                    }
                                }

                                continue;
                            }

                            foreach (var frameFile in currentFrames)
                            {
                                if (creditsFound || frameIndex >= maxFramesToProcess)
                                {
                                    break;
                                }

                                var timestamp = startTime + (frameIndex / fps);

                                {
                                    if (!loggedFirstFrame)
                                    {
                                        LogInfo($"Processing first frame: {frameFile}");
                                        loggedFirstFrame = true;
                                    }

                                    var estimatedTotal = Math.Min(maxFramesToProcess, (int)(analysisDuration * fps));
                                    var ocrProgress = estimatedTotal > 0 ? (double)(frameIndex + 1) / estimatedTotal : 0;
                                    var overallProgress = 15 + (ocrProgress * 80);
                                    UpdateProgress(overallProgress, $"OCR: {frameIndex + 1} frames ({ocrProgress:P0})");

                                    if (frameIndex > 0 && frameIndex % 50 == 0)
                                    {
                                        LogDebug($"OCR progress: {frameIndex} frames processed");
                                    }

                                    if (frameIndex != _lastProcessedFrameIndex)
                                    {
                                        _lastProcessedFrameIndex = frameIndex;
                                        _lastFrameProgressTime = DateTime.UtcNow;
                                    }
                                    else
                                    {
                                        var timeSinceLastProgress = (DateTime.UtcNow - _lastFrameProgressTime).TotalSeconds;
                                        if (timeSinceLastProgress > StuckDetectionTimeoutSeconds)
                                        {
                                            if (_stuckRetryCount < MaxStuckRetries)
                                            {
                                                _stuckRetryCount++;
                                                LogWarn($"Detection stuck on frame {frameIndex + 1} for {timeSinceLastProgress:F0}s. Retry attempt {_stuckRetryCount}/{MaxStuckRetries}");
                                                _lastFrameProgressTime = DateTime.UtcNow;
                                                await Task.Delay(1000, effectiveToken).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                LastError = $"Detection stuck on frame {frameIndex + 1} for {timeSinceLastProgress:F0}s after {MaxStuckRetries} retry attempt(s). OCR server may be unresponsive.";
                                                LogError(LastError);
                                                
                                                try
                                                {
                                                    if (!process.HasExited)
                                                    {
                                                        process.Kill();
                                                        LogDebug("FFmpeg process terminated due to stuck detection");
                                                    }
                                                }
                                                catch (Exception killEx)
                                                {
                                                    LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                                }
                                                
                                                stuckDetected = true;
                                                break;
                                            }
                                        }
                                    }

                                    LogDebug(() => $"Sending frame {frameIndex + 1} to OCR API: {frameFile}");
                                    var (ocrText, ocrConfidence) = await PerformOcr(frameFile, effectiveToken).ConfigureAwait(false);
                                    
                                    if (ocrConfidence > 0)
                                    {
                                        _ocrTextConfidences.Add(ocrConfidence);
                                    }
                                    
                                    LogDebug(() => $"OCR response for frame {frameIndex + 1}: {(string.IsNullOrWhiteSpace(ocrText) ? "empty" : $"{ocrText.Length} chars")}");

                                    if (!string.IsNullOrWhiteSpace(ocrText))
                                    {
                                        var charCount = CountMeaningfulCharacters(ocrText);
                                        characterDensityHistory.Add((timestamp, charCount));

                                        LogDebug(() =>
                                        {
                                            var textPreview = ocrText.Length > 100 ? ocrText.Substring(0, 100) + "..." : ocrText;
                                            var textOneLine = textPreview.Replace("\n", " ").Replace("\r", "");
                                            return $"Frame at {FormatTime(timestamp)}: OCR detected {charCount} chars: \"{textOneLine}\"";
                                        });

                                        var matchedKeywords = FindKeywordMatches(ocrText, keywords);

                                        var densityDetected = false;
                                        if (Configuration.OcrEnableCharacterDensityDetection)
                                        {
                                            densityDetected = CheckCharacterDensity(characterDensityHistory, detectionScores, timestamp, charCount, ocrText);
                                        }

                                        bool frameIndicatesCredits = false;
                                        if (Configuration.OcrCharacterDensityPrimaryMethod)
                                        {

                                            frameIndicatesCredits = densityDetected || matchedKeywords.Count > 0;
                                            if (densityDetected)
                                            {
                                                LogDebug(() =>
                                                {
                                                    var keywordBonus = matchedKeywords.Count > 0 ? $" + {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}" : "";
                                                    return $"Frame at {FormatTime(timestamp)}: ✓ MATCH - High text density ({charCount} chars){keywordBonus}";
                                                });
                                            }
                                        }
                                        else
                                        {

                                            frameIndicatesCredits = matchedKeywords.Count > 0;
                                        }

                                        if (frameIndicatesCredits)
                                        {
                                            var matchedText = matchedKeywords.Count > 0 ? string.Join(", ", matchedKeywords) : "density";
                                            detectionScores.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1, matchedText));
                                            consecutiveMatches++;
                                            recentMatches.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1));

                                            if (matchedKeywords.Count > 0 && !densityDetected)
                                            {
                                                LogDebug(() => $"Frame at {FormatTime(timestamp)}: ✓ MATCH - Found {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}");
                                            }

                                            if (Configuration.OcrConsecutiveMatchesForEarlyStop > 0)
                                            {
                                                if (OcrOptimizations.ShouldTerminateEarly(recentMatches, Configuration.OcrConsecutiveMatchesForEarlyStop))
                                                {
                                                    creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                                    if (creditsTimestamp > 0)
                                                    {
                                                        DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                                        creditsFound = true;
                                                        UpdateProgress(98, $"Credits found via consecutive matches! Processed {frameIndex + 1} frames");
                                                        LogInfo($"Early termination: {Configuration.OcrConsecutiveMatchesForEarlyStop} consecutive matches at {FormatTime(creditsTimestamp)}");

                                                        try
                                                        {
                                                            if (!process.HasExited)
                                                            {
                                                                process.Kill();
                                                                LogDebug("FFmpeg process terminated after early stop");
                                                            }
                                                        }
                                                        catch (Exception killEx)
                                                        {
                                                            LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                                        }

                                                        break;
                                                    }
                                                }
                                            }

                                            if (detectionScores.Count >= Configuration.OcrMinimumMatches)
                                            {
                                                creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                                if (creditsTimestamp > 0)
                                                {
                                                    DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                                    creditsFound = true;
                                                    UpdateProgress(98, $"Credits found! Processed {frameIndex + 1} frames");
                                                    LogInfo($"Credits detected at {FormatTime(creditsTimestamp)} via OCR keyword matching");
                                                    LogInfo($"OCR processing stopped early after finding credits (processed {frameIndex + 1} frames, FFmpeg extraction stopped)");

                                                    try
                                                    {
                                                        if (!process.HasExited)
                                                        {
                                                            process.Kill();
                                                            LogDebug("FFmpeg process terminated after credits detected");
                                                        }
                                                    }
                                                    catch (Exception killEx)
                                                    {
                                                        LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                                    }

                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            consecutiveMatches = 0;
                                            recentMatches.Add((timestamp, 0));
                                        }
                                    }
                                    else
                                    {
                                        characterDensityHistory.Add((timestamp, 0));
                                        consecutiveMatches = 0;
                                        recentMatches.Add((timestamp, 0));
                                        LogDebug(() => $"Frame at {FormatTime(timestamp)}: No text detected");
                                    }
                                }

                                frameIndex++;
                                _totalFramesProcessed++;

                                if (Configuration.OcrEnableSmartFrameSkipping && consecutiveMatches > 0)
                                {
                                    frameSkip = OcrOptimizations.CalculateSmartSkip(consecutiveMatches);
                                    if (frameSkip > 1)
                                    {
                                        LogDebug($"Smart skipping: jumping {frameSkip - 1} frames ahead");
                                        var framesToSkip = Math.Min(frameSkip - 1, currentFrames.Count - 1);
                                        for (int i = 0; i < framesToSkip; i++)
                                        {
                                            frameIndex++;
                                        }
                                        break;
                                    }
                                }

                                if (Configuration.OcrDelayBetweenFramesMs > 0)
                                {
                                    await Task.Delay(Configuration.OcrDelayBetweenFramesMs, effectiveToken).ConfigureAwait(false);
                                }
                            }
                        }
                    });

                    await processingTask.ConfigureAwait(false);

                    if (creditsFound)
                    {
                        return creditsTimestamp;
                    }

                    var ffmpegError = await ffmpegTask.ConfigureAwait(false);

                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill();
                            LogDebug("Terminated FFmpeg process");
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"Error terminating FFmpeg process: {ex.Message}");
                        }
                    }

                    await process.WaitForExitAsync().ConfigureAwait(false);

                    if (process.ExitCode != 0 && !creditsFound)
                    {
                        var hwType = Configuration.OcrEnableHardwareAcceleration && !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType) && Configuration.OcrHardwareAccelerationType != "none"
                            ? Configuration.OcrHardwareAccelerationType : null;
                        LastError = $"FFmpeg frame extraction failed (exit code {process.ExitCode})";
                        LogError(hwType != null
                            ? $"FFmpeg frame extraction failed with exit code {process.ExitCode} - hardware acceleration ({hwType}) may have failed"
                            : $"FFmpeg frame extraction failed with exit code {process.ExitCode}");
                        CreditsDetectionService.LogToDebug("ERROR",
                            hwType != null
                                ? $"[OCR Detection] FFmpeg frame extraction failed with exit code {process.ExitCode} - hardware acceleration ({hwType}) may have failed"
                                : $"[OCR Detection] FFmpeg frame extraction failed with exit code {process.ExitCode}");
                        if (!string.IsNullOrWhiteSpace(ffmpegError))
                        {
                            LogError($"FFmpeg error output: {ffmpegError}");
                            CreditsDetectionService.LogToDebug("ERROR", $"[OCR Detection] FFmpeg error output: {ffmpegError}");
                        }
                        return 0;
                    }

                    if (frameIndex == 0)
                    {
                        LastError = "No frames extracted for OCR analysis";
                        LogWarn("No frames extracted for OCR analysis");
                        return 0;
                    }

                    LogDebug($"Extracted and processed {frameIndex} frames for OCR analysis");
                    UpdateProgress(95, $"OCR: {frameIndex} frames (100%)");

                    LogDebug($"OCR analysis complete: Found {detectionScores.Count} frames with keyword matches");
                    UpdateProgress(98, "Analyzing results");

                    if (detectionScores.Count > 0)
                    {
                        var creditsStart = FindCreditsStartFromOcrScores(detectionScores, duration);
                        if (creditsStart > 0)
                        {
                            DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsStart);
                            LogInfo($"Credits detected at {FormatTime(creditsStart)} via OCR keyword matching");
                            
                            detectionScores.Clear();
                            detectionScores.TrimExcess();
                            characterDensityHistory.Clear();
                            characterDensityHistory.TrimExcess();
                            recentTextFrames.Clear();
                            recentTextFrames.TrimExcess();
                            
                            return creditsStart;
                        }
                    }

                    LastError = $"No OCR keywords found in {frameIndex} frames analyzed";
                    LogDebug("No sustained keyword matches found for credits");
                    
                    detectionScores.Clear();
                    detectionScores.TrimExcess();
                    characterDensityHistory.Clear();
                    characterDensityHistory.TrimExcess();
                    recentTextFrames.Clear();
                    recentTextFrames.TrimExcess();
                    
                    return 0;
                    }
                    finally
                    {
                        Utilities.FFmpegHelper.UnregisterProcess(process);
                        ActiveFfmpegProcessId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"OCR detection error: {ex.Message}";
                LogError("Error in disk-based frame extraction", ex);
                return 0;
            }
        }

        private async Task<(string text, double confidence)> PerformOcrOnFrameData(byte[] frameData, CancellationToken cancellationToken)
        {
            if (Configuration.OcrEngine == OcrEngine.PaddleOCR)
                return await PerformPaddleOcrOnFrameData(frameData, cancellationToken).ConfigureAwait(false);

            if (Configuration.OcrEngine == OcrEngine.LocalTesseract)
                return await LocalTesseractService.RunOcrAsync(
                    frameData,
                    Configuration.OcrLanguages,
                    Configuration.OcrPageSegmentationMode,
                    Configuration.OcrEngineMode,
                    Configuration.LocalTesseractPath,
                    Configuration.OcrUseDirectMemoryPipeline,
                    Logger,
                    cancellationToken).ConfigureAwait(false);

            return await PerformTesseractOcrOnFrameData(frameData, cancellationToken).ConfigureAwait(false);
        }

        private async Task<(string text, double confidence)> PerformTesseractOcrOnFrameData(byte[] frameData, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/') + "/tesseract";
                
                using (var content = new MultipartFormDataContent())
                using (var imageContent = new ByteArrayContent(frameData))
                {
                    var contentType = "image/jpeg";
                    var filename = "frame.jpg";
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                    content.Add(imageContent, "file", filename);
                    
                    var languages = Configuration.OcrLanguages.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                                    .Select(l => l.Trim())
                                                                    .ToArray();
                    
                    var optionsDict = new Dictionary<string, object>
                    {
                        { "languages", languages },
                        { "dpi", 300 },
                        { "pageSegmentationMethod", Configuration.OcrPageSegmentationMode },
                        { "ocrEngineMode", Configuration.OcrEngineMode }
                    };
                    
                    if (Configuration.OcrPreserveInterwordSpaces)
                    {
                        optionsDict["configParams"] = new Dictionary<string, string>
                        {
                            { "preserve_interword_spaces", "1" }
                        };
                    }
                    
                    var options = SimpleJson.Serialize(optionsDict);
                    using (var optionsContent = new StringContent(options))
                    {
                        content.Add(optionsContent, "options");

                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                    {
                        using (var response = await _httpClient.Value.PostAsync(endpoint, content, linkedCts.Token).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                LogDebug($"OCR response: {jsonResponse}");
                                var ocrResult = SimpleJson.Deserialize<OcrResponse>(jsonResponse);

                                if (ocrResult?.Data?.Stdout != null)
                                {
                                    var ocrText = ocrResult.Data.Stdout.Trim();
                                    var (parsedText, confidence) = ParseOcrResponse(jsonResponse);
                                    return (ocrText, confidence);
                                }
                                else
                                {
                                    LogDebug("OCR result Data.Stdout was null or empty");
                                }
                            }
                            else
                            {
                                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                LogDebug($"OCR request failed with status: {response.StatusCode}, body: {errorContent}");
                            }
                        }
                    }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"OCR request error: {ex.Message}");
            }

            return (string.Empty, 0);
        }

        private class OcrResponse
        {
            public OcrData? Data { get; set; }
        }

        private class OcrData
        {
            public string? Stdout { get; set; }
            public string? Stderr { get; set; }
        }

        private async Task<(string text, double confidence)> PerformPaddleOcrOnFrameData(byte[] frameData, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/') + "/ocr/file";
                
                using (var content = new MultipartFormDataContent())
                using (var imageContent = new ByteArrayContent(frameData))
                {
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    content.Add(imageContent, "file", "frame.jpg");
                    
                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                    {
                        using (var response = await _httpClient.Value.PostAsync(endpoint, content, linkedCts.Token).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                LogDebug($"PaddleOCR response: {jsonResponse}");
                                
                                var ocrResult = SimpleJson.Deserialize<OcrResponse>(jsonResponse);

                                if (ocrResult?.Data?.Stdout != null)
                                {
                                    var ocrText = ocrResult.Data.Stdout.Trim();
                                    var sanitized = SanitizeOcrText(ocrText);
                                    var confidence = CalculateSyntheticConfidence(sanitized);
                                    return (sanitized, confidence);
                                }
                                else
                                {
                                    LogDebug("PaddleOCR result Data.Stdout was null or empty");
                                }
                            }
                            else
                            {
                                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                LogDebug($"PaddleOCR request failed with status: {response.StatusCode}, body: {errorContent}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"PaddleOCR request error: {ex.Message}");
            }

            return (string.Empty, 0);
        }

        private bool IsAv1Video(string videoPath)
        {
            if (_av1VideoCache.Count >= MaxAv1CacheEntries)
                _av1VideoCache.Clear();
            return _av1VideoCache.GetOrAdd(videoPath, path =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = FFmpegHelper.GetFfprobePath(),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList().Add("-v");
                    psi.ArgumentList().Add("error");
                    psi.ArgumentList().Add("-select_streams");
                    psi.ArgumentList().Add("v:0");
                    psi.ArgumentList().Add("-show_entries");
                    psi.ArgumentList().Add("stream=codec_name");
                    psi.ArgumentList().Add("-of");
                    psi.ArgumentList().Add("default=noprint_wrappers=1:nokey=1");
                    psi.ArgumentList().Add(FFmpegHelper.ResolveInputPath(path));

                    using var process = new Process { StartInfo = psi };
                    psi.ApplyArgumentList();
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    return output.Trim().Equals("av1", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }

        private string BuildPreInputArgs(string? videoPath = null)
        {
            var args = new List<string>(5);

            if (Configuration.OcrEnableHardwareAcceleration && 
                !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType))
            {
                var hwAccelType = Configuration.OcrHardwareAccelerationType.ToLowerInvariant();
                
                if (hwAccelType == "nvdec" || hwAccelType == "cuda")
                {
                    int threadLimit;
                    if (Configuration.OcrFfmpegThreads > 0)
                    {
                        threadLimit = Math.Min(Configuration.OcrFfmpegThreads, 8);
                        if (Configuration.OcrFfmpegThreads > 8)
                        {
                            LogWarn($"OcrFfmpegThreads={Configuration.OcrFfmpegThreads} exceeds NVDEC safe limit (32 decode surfaces). Capping at 8 threads.");
                        }
                    }
                    else
                    {
                        threadLimit = 4;
                    }
                    args.Add($"-threads {threadLimit}");
                    LogDebug($"NVIDIA hardware - limiting to {threadLimit} threads (max 32 decode surfaces)");
                }
                else if (Configuration.OcrFfmpegThreads > 0)
                {
                    args.Add($"-threads {Configuration.OcrFfmpegThreads}");
                    LogDebug($"Limiting FFmpeg to {Configuration.OcrFfmpegThreads} threads");
                }
            }
            else if (Configuration.OcrFfmpegThreads > 0)
            {
                args.Add($"-threads {Configuration.OcrFfmpegThreads}");
                LogDebug($"Limiting FFmpeg to {Configuration.OcrFfmpegThreads} threads");
            }

            if (!string.IsNullOrWhiteSpace(Configuration.OcrFfmpegPreInputArgs))
            {
                args.Add(Configuration.OcrFfmpegPreInputArgs.Trim());
                LogDebug($"Using custom FFmpeg pre-input args: {Configuration.OcrFfmpegPreInputArgs}");
            }
            else if (Configuration.OcrEnableHardwareAcceleration && 
                     !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType) && 
                     Configuration.OcrHardwareAccelerationType != "none")
            {
                var hwAccelType = Configuration.OcrHardwareAccelerationType.ToLowerInvariant();
                
                switch (hwAccelType)
                {
                    case "vaapi":
                        args.Add("-hwaccel vaapi");
                        var vaapiDevice = string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice) 
                            ? "/dev/dri/renderD128" 
                            : Configuration.OcrHardwareDevice;
                        args.Add($"-vaapi_device {vaapiDevice}");
                        if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                        {
                            args.Add("-c:v av1_vaapi");
                            LogDebug("AV1 source detected - using av1_vaapi hardware decoder");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format vaapi");
                            LogDebug($"Using VAAPI hardware acceleration with device: {vaapiDevice} (output format: vaapi)");
                        }
                        else
                        {
                            LogDebug($"Using VAAPI hardware acceleration with device: {vaapiDevice} (output format: software)");
                        }
                        break;
                    
                    case "qsv":
                        var isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                            System.Runtime.InteropServices.OSPlatform.Linux);
                        if (isLinux)
                        {
                            var qsvDevice = string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice)
                                ? "/dev/dri/renderD128"
                                : Configuration.OcrHardwareDevice;
                            args.Add($"-init_hw_device vaapi=va:{qsvDevice}");
                            args.Add("-init_hw_device qsv=qs@va");
                            args.Add("-hwaccel qsv");
                            args.Add("-hwaccel_device qs");
                            args.Add("-filter_hw_device qs");
                            if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                            {
                                args.Add("-c:v av1_qsv");
                                LogDebug("AV1 source detected - using av1_qsv hardware decoder");
                            }
                            if (Configuration.OcrUseHardwareOutputFormat)
                            {
                                args.Add("-hwaccel_output_format qsv");
                                LogDebug($"Using Intel Quick Sync Video (QSV) hardware acceleration on Linux via VAAPI (device: {qsvDevice}, output format: qsv)");
                            }
                            else
                            {
                                LogDebug($"Using Intel Quick Sync Video (QSV) hardware acceleration on Linux via VAAPI (device: {qsvDevice}, output format: software)");
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                            {
                                args.Add($"-init_hw_device qsv=qs:{Configuration.OcrHardwareDevice}");
                                args.Add("-hwaccel qsv");
                                args.Add("-hwaccel_device qs");
                                args.Add("-filter_hw_device qs");
                            }
                            else
                            {
                                args.Add("-hwaccel qsv");
                            }
                            if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                            {
                                args.Add("-c:v av1_qsv");
                                LogDebug("AV1 source detected - using av1_qsv hardware decoder");
                            }
                            if (Configuration.OcrUseHardwareOutputFormat)
                            {
                                args.Add("-hwaccel_output_format qsv");
                                LogDebug("Using Intel Quick Sync Video (QSV) hardware acceleration (output format: qsv)");
                            }
                            else
                            {
                                LogDebug("Using Intel Quick Sync Video (QSV) hardware acceleration (output format: software)");
                            }
                        }
                        break;
                    
                    case "cuda":
                        args.Add("-hwaccel cuda");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-hwaccel_device {Configuration.OcrHardwareDevice}");
                        }
                        if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                        {
                            args.Add("-c:v av1_cuvid");
                            LogDebug("AV1 source detected - using av1_cuvid hardware decoder");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format cuda");
                            LogDebug("Using NVIDIA CUDA hardware acceleration (output format: cuda)");
                        }
                        else
                        {
                            LogDebug("Using NVIDIA CUDA hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "nvdec":
                        args.Add("-hwaccel nvdec");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-hwaccel_device {Configuration.OcrHardwareDevice}");
                        }
                        if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                        {
                            args.Add("-c:v av1_cuvid");
                            LogDebug("AV1 source detected - using av1_cuvid hardware decoder");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format cuda");
                            LogDebug("Using NVIDIA NVDEC hardware acceleration (output format: cuda)");
                        }
                        else
                        {
                            LogDebug("Using NVIDIA NVDEC hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "d3d11va":
                        args.Add("-hwaccel d3d11va");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-hwaccel_device {Configuration.OcrHardwareDevice}");
                        }
                        if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                        {
                            args.Add("-c:v av1");
                            LogDebug("AV1 source detected - using av1 decoder with D3D11VA acceleration");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format d3d11");
                            LogDebug("Using Direct3D 11 hardware acceleration (output format: d3d11)");
                        }
                        else
                        {
                            LogDebug("Using Direct3D 11 hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "dxva2":
                        args.Add("-hwaccel dxva2");
                        if (!string.IsNullOrWhiteSpace(videoPath) && IsAv1Video(videoPath!))
                        {
                            LogWarn("AV1 source detected but DXVA2 does not support AV1 hardware decoding - falling back to software decoder");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format dxva2_vld");
                            LogDebug("Using DXVA2 hardware acceleration (output format: dxva2_vld)");
                        }
                        else
                        {
                            LogDebug("Using DXVA2 hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "videotoolbox":
                        args.Add("-hwaccel videotoolbox");
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format videotoolbox_vld");
                            LogDebug("Using macOS VideoToolbox hardware acceleration (output format: videotoolbox_vld)");
                        }
                        else
                        {
                            LogDebug("Using macOS VideoToolbox hardware acceleration (output format: software)");
                        }
                        break;
                    
                    default:
                        LogWarn($"Unknown hardware acceleration type: {Configuration.OcrHardwareAccelerationType}");
                        break;
                }
            }

            var effectiveArgs = args.Count > 0 ? string.Join(" ", args) + " " : "";
            if (!string.IsNullOrWhiteSpace(effectiveArgs))
                CreditsDetectionService.LogToDebug("DEBUG", $"[OCR Detection] FFmpeg pre-input args: {effectiveArgs.Trim()}");
            return effectiveArgs;
        }

        private static void AddTokenizedArgs(ProcessStartInfo psi, string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return;
            bool inQuotes = false;
            var token = new System.Text.StringBuilder();
            foreach (var c in args)
            {
                if (c == '"') { inQuotes = !inQuotes; }
                else if (c == ' ' && !inQuotes)
                {
                    if (token.Length > 0) { psi.ArgumentList().Add(token.ToString()); token.Clear(); }
                }
                else { token.Append(c); }
            }
            if (token.Length > 0) psi.ArgumentList().Add(token.ToString());
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _toneMapPipelineChecks
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private string HardwareType =>
            Configuration.OcrEnableHardwareAcceleration && !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType)
                ? Configuration.OcrHardwareAccelerationType.ToLowerInvariant()
                : "none";

        private bool HardwareFramesStayOnGpu =>
            HardwareType != "none" &&
            Configuration.OcrUseHardwareOutputFormat &&
            string.IsNullOrWhiteSpace(Configuration.OcrFfmpegPreInputArgs);

        private bool UseHardwareFilters => HardwareFramesStayOnGpu && Configuration.OcrUseHardwareFilters;

        private int MaxFrameHeight =>
            Configuration.OcrMaxResolutionHeight > 0 && Configuration.OcrMaxResolutionHeight < 4320
                ? Configuration.OcrMaxResolutionHeight
                : 0;

        private string BuildFilterChain(double fps, string? videoPath = null)
        {
            VideoColorInfo? colorInfo = null;
            if (!string.IsNullOrWhiteSpace(videoPath) && (Configuration.OcrEnableHdrToneMapping || HardwareFramesStayOnGpu))
                colorInfo = HdrToneMapping.Probe(videoPath!, Configuration, Logger);

            var toneMap = colorInfo != null && ShouldToneMap(videoPath!, colorInfo);
            return ComposeFilterChain(fps, colorInfo, toneMap);
        }

        private bool ShouldToneMap(string videoPath, VideoColorInfo colorInfo)
        {
            if (!Configuration.OcrEnableHdrToneMapping || !colorInfo.IsHdrTransfer)
                return false;

            if (colorInfo.IsDolbyVisionWithoutHdrBase)
            {
                LogInfo($"Dolby Vision profile {colorInfo.DolbyVisionProfile} without an HDR10 base layer - OCR frames are used without tone mapping");
                return false;
            }

            if (!HdrToneMapping.AreFiltersAvailable(Logger))
                return false;

            var pipelineKey = string.Join("|",
                HardwareType,
                Configuration.OcrUseHardwareOutputFormat,
                Configuration.OcrUseHardwareFilters,
                string.IsNullOrWhiteSpace(Configuration.OcrFfmpegPreInputArgs),
                colorInfo.IsHighBitDepth,
                MaxFrameHeight);

            var works = _toneMapPipelineChecks.GetOrAdd(pipelineKey, _ => TestToneMapPipeline(videoPath, colorInfo));
            if (works)
                LogDebug($"HDR source ({colorInfo.Transfer}) - tone mapping OCR frames");
            return works;
        }

        private bool TestToneMapPipeline(string videoPath, VideoColorInfo colorInfo)
        {
            try
            {
                var inputPath = FFmpegHelper.ResolveInputPath(videoPath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = FFmpegHelper.GetFfmpegPath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList().Add("-hide_banner");
                startInfo.ArgumentList().Add("-loglevel");
                startInfo.ArgumentList().Add("error");
                AddTokenizedArgs(startInfo, BuildPreInputArgs(videoPath));
                startInfo.ArgumentList().Add("-ss");
                startInfo.ArgumentList().Add("30");
                FFmpegHelper.AddStrmNetworkResilienceArgs(startInfo, videoPath, inputPath, Configuration);
                startInfo.ArgumentList().Add("-i");
                startInfo.ArgumentList().Add(inputPath);
                startInfo.ArgumentList().Add("-frames:v");
                startInfo.ArgumentList().Add("1");
                startInfo.ArgumentList().Add("-an");
                startInfo.ArgumentList().Add("-sn");
                startInfo.ArgumentList().Add("-vf");
                startInfo.ArgumentList().Add(ComposeFilterChain(1, colorInfo, toneMap: true));
                startInfo.ArgumentList().Add("-f");
                startInfo.ArgumentList().Add("null");
                startInfo.ArgumentList().Add("-");

                using var process = new Process { StartInfo = startInfo };
                startInfo.ApplyArgumentList();
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 60000))
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    LogWarn("HDR tone mapping test timed out - OCR frames are used without tone mapping");
                    return false;
                }

                process.WaitForExit(5000);
                if (process.ExitCode == 0)
                {
                    LogInfo($"HDR tone mapping works with the current OCR settings (hardware: {HardwareType})");
                    return true;
                }

                var error = stderrTask.Result.Trim();
                LogWarn($"HDR tone mapping does not work with the current OCR hardware settings ({HardwareType}) - OCR frames are used without tone mapping. FFmpeg said: {(error.Length > 300 ? error.Substring(0, 300) : error)}");
                return false;
            }
            catch (Exception ex)
            {
                LogWarn($"HDR tone mapping test failed - OCR frames are used without tone mapping: {ex.Message}");
                return false;
            }
        }

        private string ComposeFilterChain(double fps, VideoColorInfo? colorInfo, bool toneMap)
        {
            var filters = new List<string>(12);
            var highBitDepth = colorInfo?.IsHighBitDepth == true;
            var downloadFormat = highBitDepth ? "p010le" : "nv12";
            var maxHeight = MaxFrameHeight;
            var softwareScale = maxHeight > 0 ? $"scale=-2:'min({maxHeight},ih)':flags=lanczos" : null;
            var needsSoftwareScale = true;

            filters.Add($"fps={fps.ToString(CultureInfo.InvariantCulture)}");

            if (UseHardwareFilters)
            {
                var hwAccelType = HardwareType;
                string? hardwareScale = null;
                var hardwareFormat = "nv12";
                var sizeArgs = maxHeight > 0 ? $"w=-2:h='min({maxHeight},ih)':" : string.Empty;

                switch (hwAccelType)
                {
                    case "vaapi":
                        hardwareScale = $"scale_vaapi={sizeArgs}format=nv12{(toneMap ? string.Empty : ":out_range=pc")}";
                        break;
                    case "qsv":
                        hardwareScale = $"scale_qsv={sizeArgs}format=nv12";
                        break;
                    case "cuda":
                    case "nvdec":
                        hardwareFormat = toneMap && highBitDepth ? "p010le" : "nv12";
                        hardwareScale = $"scale_cuda={sizeArgs}format={hardwareFormat}";
                        break;
                }

                if (hardwareScale != null)
                {
                    filters.Add(hardwareScale);
                    filters.Add("hwdownload");
                    filters.Add($"format={hardwareFormat}");
                    needsSoftwareScale = false;
                    LogDebug($"Hardware filters: fps -> {hwAccelType} scale ({hardwareFormat}) -> hwdownload -> software filters");
                }
                else
                {
                    filters.Add("hwdownload");
                    filters.Add($"format={downloadFormat}");
                    LogDebug($"Hardware filters: no {hwAccelType} scaler available, downloading frames ({downloadFormat}) and scaling in software");
                }
            }
            else if (HardwareFramesStayOnGpu)
            {
                filters.Add("hwdownload");
                filters.Add($"format={downloadFormat}");
                LogDebug($"Hardware output format without hardware filters: downloading frames ({downloadFormat}) before software filters");
            }

            if (needsSoftwareScale && softwareScale != null)
            {
                filters.Add(toneMap ? softwareScale : softwareScale + ":out_range=pc");
                if (!toneMap)
                {
                    LogDebug($"Resolution limiting: Using software scaling to max height {maxHeight}px");
                }
            }

            if (toneMap && colorInfo != null)
            {
                filters.Add(HdrToneMapping.BuildToneMapFilter(colorInfo));
            }
            else if (!needsSoftwareScale || softwareScale == null)
            {
                filters.Add("scale=out_range=pc");
            }

            if (Configuration.OcrEnableRoiDetection && !string.IsNullOrWhiteSpace(Configuration.OcrRoiRegion))
            {
                var roi = Configuration.OcrRoiRegion.ToLowerInvariant();
                switch (roi)
                {
                    case "bottom_third":
                        filters.Add("crop=iw:ih/3:0:ih*2/3");
                        LogDebug("ROI: Processing bottom third of frame");
                        break;
                    case "bottom_half":
                        filters.Add("crop=iw:ih/2:0:ih/2");
                        LogDebug("ROI: Processing bottom half of frame");
                        break;
                    case "center":
                        filters.Add("crop=iw:ih*0.6:0:ih*0.2");
                        LogDebug("ROI: Processing center 60% of frame");
                        break;
                    case "top_third":
                        filters.Add("crop=iw:ih/3:0:0");
                        LogDebug("ROI: Processing top third of frame");
                        break;
                    case "full":
                    default:
                        break;
                }
            }

            if (Configuration.OcrEnableImagePreprocessing)
            {
                if (Configuration.OcrContrastEnhancement != 1.0 || Configuration.OcrBrightnessAdjustment != 0.0)
                {
                    filters.Add($"eq=contrast={Configuration.OcrContrastEnhancement.ToString(CultureInfo.InvariantCulture)}:brightness={Configuration.OcrBrightnessAdjustment.ToString(CultureInfo.InvariantCulture)}");
                }

                if (Configuration.OcrEnableSharpening)
                {
                    filters.Add($"unsharp=5:5:{Configuration.OcrSharpenAmount.ToString(CultureInfo.InvariantCulture)}");
                }
            }

            return string.Join(",", filters);
        }

        private string BuildThreadArgs()
        {
            var args = new List<string>(3);

            if (Configuration.OcrFfmpegFilterThreads > 0)
            {
                args.Add($"-filter_threads {Configuration.OcrFfmpegFilterThreads}");
                LogDebug($"Limiting FFmpeg filter threads to {Configuration.OcrFfmpegFilterThreads}");
            }

            return args.Count > 0 ? string.Join(" ", args) + " " : "";
        }

        private bool IsValidOcrEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            try
            {
                var uri = new Uri(endpoint);
                
                if (uri.Scheme != "http" && uri.Scheme != "https")
                    return false;

                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                    return true;

                if (System.Net.IPAddress.TryParse(host, out var ipAddress))
                {
                    var bytes = ipAddress.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                LogWarn($"OCR endpoint is public/remote: {endpoint}. Ensure this is intentional.");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestOcrEndpoint(CancellationToken cancellationToken = default)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/');

                using (var response = await _httpClient.Value.GetAsync(endpoint, cancellationToken).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        LogDebug($"OCR endpoint {endpoint} is accessible");
                        return true;
                    }
                    else
                    {
                        LogWarn($"OCR endpoint {endpoint} returned status: {response.StatusCode}");
                        return false;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                LogWarn($"Cannot connect to OCR endpoint {Configuration.OcrEndpoint}: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException)
            {
                LogWarn($"OCR endpoint {Configuration.OcrEndpoint} timed out");
                return false;
            }
            catch (Exception ex)
            {
                LogWarn($"Error testing OCR endpoint: {ex.Message}");
                return false;
            }
        }

        private async Task<(string text, double confidence)> PerformOcr(string imagePath, CancellationToken cancellationToken = default)
        {
            var maxRetries = Configuration.OcrRetryAttempts;
            var retryDelay = Configuration.OcrRetryDelayMs;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var (text, confidence) = await PerformOcrInternal(imagePath, cancellationToken).ConfigureAwait(false);
                    if (attempt > 1)
                    {
                        LogInfo($"OCR succeeded on attempt {attempt}/{maxRetries}");
                    }
                    return (text, confidence);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < maxRetries)
                    {
                        LogWarn($"OCR attempt {attempt}/{maxRetries} failed: {ex.Message}. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogError($"OCR failed after {maxRetries} attempts: {ex.Message}", ex);
                        return (string.Empty, 0);
                    }
                }
                catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LogWarn("OCR request cancelled");
                    return (string.Empty, 0);
                }
                catch (TaskCanceledException)
                {
                    if (attempt < maxRetries)
                    {
                        LogWarn($"OCR attempt {attempt}/{maxRetries} timed out. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogError($"OCR timed out after {maxRetries} attempts");
                        return (string.Empty, 0);
                    }
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        LogWarn($"OCR attempt {attempt}/{maxRetries} encountered error: {ex.Message}. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogError($"OCR failed after {maxRetries} attempts: {ex.Message}", ex);
                        return (string.Empty, 0);
                    }
                }
            }

            return (string.Empty, 0);
        }

        private async Task<(string text, double confidence)> PerformOcrInternal(string imagePath, CancellationToken cancellationToken = default)
        {
            LogDebug($"Reading image file: {imagePath}");
            var imageBytes = File.ReadAllBytes(imagePath);
            LogDebug($"Image size: {imageBytes.Length} bytes");

            return await PerformOcrOnFrameData(imageBytes, cancellationToken).ConfigureAwait(false);
        }

        private (string text, double confidence) ParseOcrResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return (string.Empty, 0);

            double confidence = 0;

            if (response.TrimStart().StartsWith("{"))
            {
                try
                {
                    var confidenceStart = response.IndexOf("\"confidence\"", StringComparison.OrdinalIgnoreCase);
                    if (confidenceStart >= 0)
                    {
                        var colonIndex = response.IndexOf(":", confidenceStart);
                        var valueStart = colonIndex + 1;
                        var valueEnd = valueStart;

                        while (valueEnd < response.Length && (char.IsDigit(response[valueEnd]) || response[valueEnd] == '.'))
                        {
                            valueEnd++;
                        }

                        if (valueEnd > valueStart)
                        {
                            var confStr = response.Substring(valueStart, valueEnd - valueStart).Trim();
                            if (double.TryParse(confStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var conf))
                            {
                                confidence = conf;
                                if (confidence > 1)
                                {
                                    confidence = confidence / 100.0;
                                }
                            }
                        }
                    }

                    var stdoutStart = response.IndexOf("\"stdout\"", StringComparison.OrdinalIgnoreCase);
                    if (stdoutStart >= 0)
                    {
                        var colonIndex = response.IndexOf(":", stdoutStart);
                        var quoteStart = response.IndexOf("\"", colonIndex + 1);

                        if (quoteStart >= 0)
                        {
                            var quoteEnd = quoteStart + 1;
                            while (quoteEnd < response.Length)
                            {
                                if (response[quoteEnd] == '"' && response[quoteEnd - 1] != '\\')
                                {
                                    break;
                                }
                                quoteEnd++;
                            }

                            if (quoteEnd < response.Length)
                            {
                                var text = response.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                                text = text.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
                                text = SanitizeOcrText(text);
                                
                                if (confidence == 0 && !string.IsNullOrWhiteSpace(text))
                                {
                                    confidence = CalculateSyntheticConfidence(text);
                                    LogDebug($"OCR server provided no confidence, calculated synthetic: {confidence:F2}");
                                }
                                else if (confidence > 0)
                                {
                                    LogDebug($"OCR server provided confidence: {confidence:F2}");
                                }
                                
                                return (text, confidence);
                            }
                        }
                    }
                }
                catch (SimpleJsonException ex)
                {
                    LogWarn($"Failed to parse OCR JSON response: {ex.Message}. Attempting fallback parsing.");

                }
            }

            var sanitized = SanitizeOcrText(response.Trim());
            
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                var syntheticConf = CalculateSyntheticConfidence(sanitized);
                LogDebug($"Using fallback parsing with synthetic confidence: {syntheticConf:F2}");
                return (sanitized, syntheticConf);
            }
            
            return (sanitized, 0);
        }
        
        private double CalculateSyntheticConfidence(string text)
        {
            double conf = 0.85;
            
            int length = text.Length;
            if (length < 5)
                conf -= 0.15;
            else if (length < 20)
                conf -= 0.05;
            else if (length > 200)
                conf += 0.05;
            
            var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3)
                conf += 0.03;
            if (words.Length >= 10)
                conf += 0.02;
            
            int letterCount = text.Count(c => char.IsLetter(c));
            double letterRatio = length > 0 ? (double)letterCount / length : 0;
            if (letterRatio > 0.7)
                conf += 0.03;
            else if (letterRatio < 0.3)
                conf -= 0.10;
            
            var lowerText = text.ToLowerInvariant();
            var creditIndicators = new[] { "directed", "produced", "written", "cast", "music", "editor", "starring" };
            if (creditIndicators.Any(w => lowerText.Contains(w)))
                conf += 0.05;
            
            int specialCount = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            double specialRatio = length > 0 ? (double)specialCount / length : 0;
            if (specialRatio > 0.3)
                conf -= 0.10;
            
            return Math.Max(0.50, Math.Min(0.95, conf));
        }
        
        private string SanitizeOcrText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            
            var cleaned = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!char.IsControl(c) || c == '\n' || c == '\r' || c == '\t')
                {
                    cleaned.Append(c);
                }
            }
            
            return cleaned.ToString().Trim();
        }
        
        private int CountMeaningfulCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            
            return text.Count(c => !char.IsWhiteSpace(c));
        }

        private List<string> ParseKeywords(string keywordString)
        {
            if (string.IsNullOrWhiteSpace(keywordString))
            {
                _normalizedKeywordCache = Array.Empty<NormalizedKeyword>();
                return new List<string>();
            }

            var keywords = keywordString
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct()
                .ToList();

            _normalizedKeywordCache = _normalizedKeywordsByString.GetOrAdd(keywordString, _ =>
            {
                if (_normalizedKeywordsByString.Count >= MaxNormalizedKeywordCacheEntries)
                    _normalizedKeywordsByString.Clear();
                return keywords.Select(k =>
                {
                    var lower = k.ToLowerInvariant();
                    var lowerCollapsed = _multipleWhitespaceRegex.Replace(lower, "");
                    return new NormalizedKeyword(lower, lowerCollapsed);
                }).ToArray();
            });

            return keywords;
        }

        private List<string> FindKeywordMatches(string text, List<string> keywords)
        {
            if (keywords.Count == 0)
                return new List<string>();
                
            var matches = new List<string>(keywords.Count);
            var lowerText = text.ToLowerInvariant();
            string? textCollapsedSpaces = null;

            for (int i = 0; i < keywords.Count; i++)
            {
                var keyword = _normalizedKeywordCache[i];

                if (ContainsKeyword(lowerText, keyword.Lower, keyword))
                {
                    matches.Add(keywords[i]);
                }
                else
                {
                    if (textCollapsedSpaces == null)
                        textCollapsedSpaces = _multipleWhitespaceRegex.Replace(text, "").ToLowerInvariant();

                    if (ContainsKeyword(textCollapsedSpaces, keyword.LowerCollapsed, keyword))
                    {
                        matches.Add(keywords[i]);
                    }
                }
            }

            return matches;
        }

        private static bool UsesWordSeparatedScript(string keyword)
        {
            var hasLetter = false;
            foreach (var c in keyword)
            {
                if (!char.IsLetter(c))
                    continue;

                hasLetter = true;
                if (c >= 0x2E80)
                    return false;
            }
            return hasLetter;
        }

        private static bool ContainsKeyword(string text, string keyword, NormalizedKeyword options)
        {
            if (string.IsNullOrEmpty(keyword))
                return false;

            if (!options.RequireStartBoundary)
                return text.IndexOf(keyword, StringComparison.Ordinal) >= 0;

            var index = text.IndexOf(keyword, StringComparison.Ordinal);
            while (index >= 0)
            {
                var startOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                var end = index + keyword.Length;
                var endOk = !options.RequireEndBoundary || end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (startOk && endOk)
                    return true;

                index = text.IndexOf(keyword, index + 1, StringComparison.Ordinal);
            }

            return false;
        }

        private static int GetAllowedFuzzyDistance(string keyword, int maxDistance)
        {
            var letters = keyword.Count(char.IsLetter);
            if (letters <= ShortKeywordLength)
                return 0;
            if (letters <= 7)
                return Math.Min(1, maxDistance);
            return maxDistance;
        }
        
        private bool IsJapaneseText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            
            foreach (char c in text)
            {
                if ((c >= 0x3040 && c <= 0x309F) ||
                    (c >= 0x30A0 && c <= 0x30FF) ||
                    (c >= 0x4E00 && c <= 0x9FFF) ||
                    (c >= 0x3400 && c <= 0x4DBF))
                {
                    return true;
                }
            }
            return false;
        }

        private List<string> FindKeywordMatchesFuzzy(string text, List<string> keywords, int maxDistance = 2)
        {
            var matches = new List<string>();
            var lowerText = text.ToLowerInvariant();
            var textCollapsedSpaces = _multipleWhitespaceRegex.Replace(text, "").ToLowerInvariant();
            var words = lowerText.Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ';', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < keywords.Count; i++)
            {
                var keyword = _normalizedKeywordCache[i];

                if (ContainsKeyword(lowerText, keyword.Lower, keyword))
                {
                    matches.Add(keywords[i]);
                    continue;
                }

                if (ContainsKeyword(textCollapsedSpaces, keyword.LowerCollapsed, keyword))
                {
                    matches.Add(keywords[i]);
                    continue;
                }

                var allowedDistance = GetAllowedFuzzyDistance(keyword.Lower, maxDistance);
                if (allowedDistance <= 0 || keyword.WordCount == 0 || words.Length < keyword.WordCount)
                    continue;

                for (int w = 0; w + keyword.WordCount <= words.Length; w++)
                {
                    var candidate = keyword.WordCount == 1
                        ? words[w]
                        : string.Join(" ", words, w, keyword.WordCount);
                    var distance = OcrOptimizations.LevenshteinDistance(candidate, keyword.Lower);
                    if (distance <= allowedDistance)
                    {
                        matches.Add(keywords[i]);
                        LogDebug(() => $"Fuzzy match: '{candidate}' ≈ '{keywords[i]}' (distance: {distance})");
                        break;
                    }
                }
            }

            return matches.Distinct().ToList();
        }

        private bool DetectCreditStructure(string text, int minimumLines = 4)
        {
            var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            if (lines.Length < minimumLines)
                return false;

            int upperCaseLines = 0;
            int mixedCaseLines = 0;

            foreach (var line in lines)
            {
                var letters = line.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) continue;

                var trimmedLine = line.Trim();
                if (trimmedLine.Length < 3) continue;

                if (letters.All(char.IsUpper))
                    upperCaseLines++;
                else if (letters.Any(char.IsUpper) && letters.Any(char.IsLower))
                    mixedCaseLines++;
            }

            return (upperCaseLines >= 2 && mixedCaseLines >= 2) || 
                   (upperCaseLines >= minimumLines / 2);
        }

        private bool CheckCharacterDensity(List<(double timestamp, int charCount)> history, List<(double timestamp, int matchCount, string matchedKeywords)> detectionScores, double currentTimestamp, int currentCharCount, string currentText)
        {
            if (!Configuration.OcrEnableCharacterDensityDetection)
                return false;

            var threshold = Configuration.OcrCharacterDensityThreshold;
            var consecutiveRequired = Configuration.OcrCharacterDensityConsecutiveFrames;

            if (currentCharCount < threshold)
                return false;

            var recentFrames = history
                .Where(h => h.timestamp >= currentTimestamp - Configuration.OcrStopSecondsFromEnd)
                .OrderBy(h => h.timestamp)
                .ToList();

            if (recentFrames.Count < consecutiveRequired)
                return false;

            var consecutiveCount = 0;
            for (int i = recentFrames.Count - 1; i >= 0; i--)
            {
                if (recentFrames[i].charCount >= threshold)
                {
                    consecutiveCount++;
                    if (consecutiveCount >= consecutiveRequired)
                    {
                        if (Configuration.OcrDensityRequireKeyword && !CheckKeywordRequirement(detectionScores, currentTimestamp))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but no keywords found within {Configuration.OcrDensityKeywordWindowSeconds}s window - rejected");
                            return false;
                        }
                        
                        if (Configuration.OcrDensityRequireTemporalConsistency && !CheckTemporalConsistency(history, currentTimestamp))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but temporal consistency requirement not met (need {Configuration.OcrDensityMinimumDurationSeconds}s sustained) - rejected");
                            return false;
                        }
                        
                        if (Configuration.OcrDensityRequireStyleConsistency && !CheckStyleConsistency(history, currentTimestamp, currentCharCount))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but style consistency check failed - rejected");
                            return false;
                        }
                        
                        return true;
                    }
                }
                else
                {

                    break;
                }
            }

            return false;
        }
        
        private bool CheckKeywordRequirement(List<(double timestamp, int matchCount, string matchedKeywords)> detectionScores, double currentTimestamp)
        {
            var windowSeconds = Configuration.OcrDensityKeywordWindowSeconds;
            
            for (int i = 0; i < detectionScores.Count; i++)
            {
                var score = detectionScores[i];
                if (Math.Abs(score.timestamp - currentTimestamp) <= windowSeconds && 
                    score.matchedKeywords != "density" && 
                    !string.IsNullOrEmpty(score.matchedKeywords))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private bool CheckTemporalConsistency(List<(double timestamp, int charCount)> history, double currentTimestamp)
        {
            var minDuration = Configuration.OcrDensityMinimumDurationSeconds;
            var threshold = Configuration.OcrCharacterDensityThreshold;
            
            var relevantFrames = history
                .Where(h => h.timestamp <= currentTimestamp && h.timestamp >= currentTimestamp - minDuration)
                .OrderBy(h => h.timestamp)
                .ToList();
            
            if (relevantFrames.Count == 0)
                return false;
            
            var timeSpan = currentTimestamp - relevantFrames.First().timestamp;
            
            if (timeSpan < minDuration * 0.8)
                return false;
            
            var framesAboveThreshold = relevantFrames.Count(f => f.charCount >= threshold);
            
            var requiredRatio = 0.6;
            var actualRatio = (double)framesAboveThreshold / relevantFrames.Count;
            
            return actualRatio >= requiredRatio;
        }
        
        private bool CheckStyleConsistency(List<(double timestamp, int charCount)> history, double currentTimestamp, int currentCharCount)
        {
            var lookbackSeconds = 10.0;
            var threshold = Configuration.OcrCharacterDensityThreshold;
            
            var recentFrames = history
                .Where(h => h.timestamp <= currentTimestamp && 
                           h.timestamp >= currentTimestamp - lookbackSeconds &&
                           h.charCount >= threshold)
                .ToList();
            
            if (recentFrames.Count < 3)
                return false;
            
            var charCounts = recentFrames.Select(f => (double)f.charCount).ToList();
            var mean = charCounts.Average();
            
            if (mean == 0)
                return false;
            
            var variance = charCounts.Select(x => Math.Pow(x - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            var coefficientOfVariation = stdDev / mean;
            
            var maxAllowedCV = Configuration.OcrDensityStyleConsistencyThreshold;
            
            return coefficientOfVariation <= maxAllowedCV;
        }

        private string BuildDetectionReason(List<(double timestamp, int matchCount, string matchedKeywords)> scores, List<(double timestamp, int charCount)> densityHistory, double detectedTimestamp)
        {
            var reasonParts = new List<string>(3);

            var relevantScores = scores
                .Where(s => Math.Abs(s.timestamp - detectedTimestamp) <= 10.0)
                .OrderBy(s => s.timestamp)
                .ToList();

            var densityMatches = 0;
            var keywordMatches = 0;
            foreach (var score in relevantScores)
            {
                if (score.matchedKeywords == "density")
                    densityMatches++;
                else
                    keywordMatches++;
            }

            if (Configuration.OcrEnableCharacterDensityDetection && densityMatches > 0)
            {

                var densityAtDetection = densityHistory
                    .Where(d => Math.Abs(d.timestamp - detectedTimestamp) <= 5.0)
                    .OrderByDescending(d => d.charCount)
                    .FirstOrDefault();

                if (densityAtDetection.charCount > 0)
                {
                    reasonParts.Add($"Character density: {densityAtDetection.charCount} chars/frame (threshold: {Configuration.OcrCharacterDensityThreshold})");
                }
            }

            if (keywordMatches > 0)
            {

                var allKeywords = relevantScores
                    .Where(s => s.matchedKeywords != "density")
                    .SelectMany(s => s.matchedKeywords.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                    .Distinct()
                    .ToList();

                if (allKeywords.Count > 0)
                {
                    reasonParts.Add($"Keywords: {string.Join(", ", allKeywords)} ({keywordMatches} matches)");
                }
            }

            if (reasonParts.Count == 0)
            {
                return $"OCR detection ({relevantScores.Count} frames)";
            }

            return string.Join(" | ", reasonParts);
        }

        private double FindCreditsStartFromOcrScores(List<(double timestamp, int matchCount, string matchedKeywords)> scores, double duration)
        {
            if (scores.Count == 0)
                return 0;

            var sortedScores = scores.OrderBy(s => s.timestamp).ToList();

            var minMatches = Configuration.OcrMinimumMatches;
            var windowSeconds = 10.0; 

            for (int i = 0; i < sortedScores.Count; i++)
            {
                var matchesInWindow = sortedScores
                    .Where(s => s.timestamp >= sortedScores[i].timestamp && 
                               s.timestamp <= sortedScores[i].timestamp + windowSeconds)
                    .ToList();

                if (matchesInWindow.Count >= minMatches)
                {
                    LogInfo($"Found sustained keyword detection: {matchesInWindow.Count} matches within {windowSeconds}s starting at {FormatTime(sortedScores[i].timestamp)}");
                    return sortedScores[i].timestamp;
                }
            }

            if (sortedScores[0].matchCount >= minMatches)
            {
                LogInfo($"Single strong match with {sortedScores[0].matchCount} keywords at {FormatTime(sortedScores[0].timestamp)}");
                return sortedScores[0].timestamp;
            }

            return 0;
        }

        private void CalculateDynamicConfidence(int detectionMatches)
        {
            double confidence = 0.70;

            if (_ocrTextConfidences.Count > 0)
            {
                var avgOcrConfidence = _ocrTextConfidences.Average();
                confidence += avgOcrConfidence * 0.15;
                LogDebug($"OCR text confidence contribution: {avgOcrConfidence:F2} -> +{(avgOcrConfidence * 0.15):F2}");
            }

            if (_totalFramesProcessed > 0)
            {
                var matchDensity = Math.Min(1.0, (double)_totalKeywordMatches / _totalFramesProcessed);
                confidence += matchDensity * 0.10;
                LogDebug($"Keyword match density: {matchDensity:F2} ({_totalKeywordMatches}/{_totalFramesProcessed}) -> +{(matchDensity * 0.10):F2}");
            }

            var matchFactor = Math.Min(1.0, detectionMatches / 10.0);
            confidence += matchFactor * 0.05;
            LogDebug($"Detection matches: {detectionMatches} -> +{(matchFactor * 0.05):F2}");

            _calculatedConfidence = Math.Max(0.70, Math.Min(0.98, confidence));
            
            LogInfo($"Dynamic confidence calculated: {_calculatedConfidence:F2} (Base: 0.70, OCR: {(_ocrTextConfidences.Count > 0 ? _ocrTextConfidences.Average() : 0):F2}, Density: {(_totalFramesProcessed > 0 ? (double)_totalKeywordMatches / _totalFramesProcessed : 0):F2}, Matches: {detectionMatches})");
        }
    }
}
