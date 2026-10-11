using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public sealed class PostCreditsCheckResult
    {
        public double MarkerSeconds { get; set; }
        public bool SkipCreditsMarker { get; set; }
        public bool ManagesChapter { get; set; }
        public double? ChapterSeconds { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    public class EpisodeProcessor
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager? _libraryManager;
        private DetectionCoordinator _detectionCoordinator;
        private readonly ChapterMarkerService _chapterMarkerService;
        private readonly DebugLogger _debugLogger;
        private readonly PluginConfiguration _configuration;
        private readonly CpuThrottler _cpuThrottler;
        private readonly RuleMatchingService _ruleMatchingService;
        private readonly VideoValidator _videoValidator;
        private readonly TheIntroDbService? _theIntroDbService;
        private const int StrmProbeTimeoutSeconds = 20;
        public const string RemovedDuringDetectionReason = "Removed from library during detection";

        public EpisodeProcessor(
            ILogger logger,
            ILibraryManager? libraryManager,
            DetectionCoordinator detectionCoordinator,
            ChapterMarkerService chapterMarkerService,
            DebugLogger debugLogger,
            PluginConfiguration configuration,
            RuleMatchingService ruleMatchingService,
            TheIntroDbService? theIntroDbService = null)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _detectionCoordinator = detectionCoordinator;
            _chapterMarkerService = chapterMarkerService;
            _debugLogger = debugLogger;
            _configuration = configuration;
            _cpuThrottler = new CpuThrottler(configuration, logger);
            _ruleMatchingService = ruleMatchingService;
            _videoValidator = new VideoValidator(logger, configuration);
            _theIntroDbService = theIntroDbService;
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence, string methodName, string detectionReason)> ProcessEpisode(
            Episode episode,
            bool isDryRun,
            bool isBatchMode,
            System.Collections.Concurrent.ConcurrentDictionary<string, List<(string method, double timestamp)>> batchDetectionCache)
        {
            var episodeId = episode.Id.ToString();

            var workStart = _cpuThrottler.BeginWork();

            DetectionCoordinator? localCoordinator = null;
            try
            {
                _debugLogger.LogInfo($"Processing episode: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);

                if (string.IsNullOrEmpty(normalizedPath))
                {
                    _debugLogger.LogWarn($"Episode file path is empty: {episode.Path}");
return (false, 0, "File not found", 0, string.Empty, string.Empty);
                }

                try
                {
                    if (!File.Exists(normalizedPath))
                    {
                        _debugLogger.LogWarn($"Episode file not found: {episode.Path}");
return (false, 0, "File not found", 0, string.Empty, string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger.LogWarn($"Could not check file existence for {episode.Path}: {ex.Message}");
                }

                double duration = 0;
                if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                {
                    duration = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                }
                else
                {
                    duration = await GetVideoDuration(normalizedPath);
                }

                if (duration <= 0)
                {
                    _debugLogger.LogWarn($"Could not determine video duration for {episode.Name}");
return (false, 0, "Could not determine video duration", 0, string.Empty, string.Empty);
                }

                if (_configuration.EnableVideoValidation)
                {
                    var validationResult = await _videoValidator.ValidateVideo(normalizedPath, CancellationToken.None);
                    
                    if (!validationResult.isValid)
                    {
                        _debugLogger.LogWarn($"Video validation failed for {episode.Name}: {validationResult.errorMessage}");
                        MarkEpisodeFailed(episode, isDryRun);
                        return (false, 0, $"Video validation failed: {validationResult.errorMessage}", 0, string.Empty, string.Empty);
                    }
                }

                _debugLogger.LogInfo("Running credits detection");
                
                var series = episode.Series;
                var seriesId = series?.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber;
                var episodeNumber = episode.IndexNumber;
                var effectiveConfig = _ruleMatchingService.GetEffectiveConfiguration(episode);

                if (effectiveConfig.DisableDetection)
                {
                    _debugLogger.LogInfo($"Detection disabled by rule for {episode.Name}, skipping");
                    return (false, 0, "Detection disabled by rule", 0, string.Empty, string.Empty);
                }

                if (_configuration.EnableTheIntroDB && _theIntroDbService != null)
                {
                    CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Querying TheIntroDB...");
                    var introDbTimestamp = await _theIntroDbService.GetCreditsTimestamp(episode).ConfigureAwait(false);
                    if (introDbTimestamp.HasValue && introDbTimestamp.Value > 0)
                    {
                        double introDbCreditsStart = introDbTimestamp.Value;
                        double introDbFinalTimestamp = introDbCreditsStart + effectiveConfig.TimestampOffsetSeconds;

                        if (introDbFinalTimestamp < 0)
                        {
                            _debugLogger.LogWarn($"[TheIntroDB] Final timestamp with offset ({FormatTime(introDbFinalTimestamp)}) is negative for {episode.Name}");
                        }
                        else if (introDbFinalTimestamp >= duration)
                        {
                            _debugLogger.LogWarn($"[TheIntroDB] Final timestamp with offset ({FormatTime(introDbFinalTimestamp)}) exceeds video duration for {episode.Name}");
                        }
                        else
                        {
                            var introDbReason = "Community database timestamp";
                            var introDbPostCredits = await CheckPostCreditsSceneAsync(episode, normalizedPath, introDbCreditsStart, introDbFinalTimestamp, duration, isDryRun).ConfigureAwait(false);
                            introDbFinalTimestamp = introDbPostCredits.MarkerSeconds;
                            introDbReason = AppendNote(introDbReason, introDbPostCredits.Note);

                            if (!isDryRun && !SaveDetectionMarkers(episode, introDbFinalTimestamp, introDbPostCredits, out var introDbSaveError))
                            {
                                _debugLogger.LogWarn($"✗ [TheIntroDB] Could not save credits marker for {episode.Name}: {introDbSaveError}");
                                return (false, 0, introDbSaveError, 0, string.Empty, string.Empty);
                            }

                            ClearFailureMarker(episode, isDryRun);

                            _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] [TheIntroDB] Credits at {FormatTime(introDbCreditsStart)}, saved at {FormatTime(introDbFinalTimestamp)} for {episode.Name}");
                            return (true, introDbCreditsStart, string.Empty, 1.0, "TheIntroDB", introDbReason);
                        }
                    }
                    else
                    {
                        CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "TheIntroDB: no data, falling back to detection");
                    }
                }
                
                DetectionCoordinator coordinatorForEpisode;
                if (effectiveConfig != _configuration)
                {
                    _debugLogger.LogInfo("Using rule-based configuration for this episode");
                    localCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                    coordinatorForEpisode = localCoordinator;
                }
                else
                {
                    coordinatorForEpisode = _detectionCoordinator;
                }
                
                var result = !string.IsNullOrEmpty(seriesId) 
                    ? await coordinatorForEpisode.DetectCreditsWithContext(normalizedPath, duration, episodeId, seriesId!, seasonNumber, episodeNumber)
                    : await coordinatorForEpisode.DetectCredits(normalizedPath, duration, episodeId);
                double creditsStart = result.timestamp;
                string failureReason = result.failureReason;
                double confidence = result.confidence;
                string methodName = result.methodName;
                string detectionReason = result.detectionReason;

                if (creditsStart > 0)
                {
                    var containerStartTime = await GetVideoContainerStartTime(normalizedPath);
                    if (containerStartTime > 0)
                    {
                        _debugLogger.LogInfo($"Applying container start_time correction of {containerStartTime:F3}s to {episode.Name}");
                    }
                    double finalTimestamp = creditsStart + containerStartTime + effectiveConfig.TimestampOffsetSeconds;
                    
                    if (finalTimestamp < 0)
                    {
                        _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) is negative for {episode.Name}");
                        MarkEpisodeFailed(episode, isDryRun);
                        return (false, 0, $"Final timestamp with offset is negative: {finalTimestamp:F1}s", 0, string.Empty, string.Empty);
                    }
                    
                    if (finalTimestamp >= duration)
                    {
                        _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                        MarkEpisodeFailed(episode, isDryRun);
                        return (false, 0, $"Final timestamp with offset exceeds duration: {finalTimestamp:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
                    }

                    var postCredits = await CheckPostCreditsSceneAsync(episode, normalizedPath, creditsStart, finalTimestamp, duration, isDryRun).ConfigureAwait(false);
                    finalTimestamp = postCredits.MarkerSeconds;
                    detectionReason = AppendNote(detectionReason, postCredits.Note);

                    if (!isDryRun && !SaveDetectionMarkers(episode, finalTimestamp, postCredits, out var saveError))
                    {
                        _debugLogger.LogWarn($"✗ Could not save credits marker for {episode.Name}: {saveError}");
                        return (false, 0, saveError, 0, string.Empty, string.Empty);
                    }

                    ClearFailureMarker(episode, isDryRun);

                    if (effectiveConfig.TimestampOffsetSeconds != 0)
                    {
                        _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)}, saved at {FormatTime(finalTimestamp)} (offset: {effectiveConfig.TimestampOffsetSeconds:+0;-0}s) for {episode.Name} (confidence: {confidence:F2})");
                    }
                    else
                    {
                        _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)}, saved at {FormatTime(finalTimestamp)} for {episode.Name} (confidence: {confidence:F2})");
                    }

                    return (true, creditsStart, string.Empty, confidence, methodName, detectionReason);
                }
                else
                {
                    _debugLogger.LogWarn($"✗ No clear credits detected for {episode.Name}");
                    MarkEpisodeFailed(episode, isDryRun);
                    return (false, 0, failureReason, 0, string.Empty, string.Empty);
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error processing episode {episode.Name}", ex);
                if (ex is not OperationCanceledException && ex is not IOException)
                    MarkEpisodeFailed(episode, isDryRun);
                return (false, 0, $"Exception: {ex.Message}", 0, string.Empty, string.Empty);
            }
            finally
            {
                localCoordinator?.Dispose();
                await _cpuThrottler.EndWork(workStart, CreditsDetectionService.CurrentCancellationToken).ConfigureAwait(false);

                if (_configuration?.DelayBetweenEpisodesMs > 0)
                {
                    _debugLogger.LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    try { await Task.Delay(_configuration.DelayBetweenEpisodesMs, CreditsDetectionService.CurrentCancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence, string methodName, string detectionReason)> ProcessEpisodeWithBatchResult(
            Episode episode,
            bool isDryRun,
            double? batchDetectedTime,
            DetectionMethods.ChromaprintDetection? chromaprintMethod = null,
            DetectionCoordinator? overrideCoordinator = null)
        {
            var episodeId = episode.Id.ToString();

            var workStart = _cpuThrottler.BeginWork();

            DetectionCoordinator? localFallbackCoordinator = null;
            try
            {
                _debugLogger.LogInfo($"Processing episode with batch result: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                if (Utilities.ItemLookupHelper.IsRemovedFromLibrary(_libraryManager, episode))
                {
                    _debugLogger.LogWarn($"Episode was removed before detection: {episode.Path}");
                    return (false, 0, RemovedDuringDetectionReason, 0, string.Empty, string.Empty);
                }

                var effectiveConfig = _ruleMatchingService.GetEffectiveConfiguration(episode);
                if (effectiveConfig.DisableDetection)
                {
                    _debugLogger.LogInfo($"Detection disabled by rule for {episode.Name}, skipping");
                    return (false, 0, "Detection disabled by rule", 0, string.Empty, string.Empty);
                }

                bool ocrIsPrimary = effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback;

                if (batchDetectedTime.HasValue && batchDetectedTime.Value > 0 && !ocrIsPrimary)
                {
                    return await SaveBatchChromaprintResult(episode, isDryRun, batchDetectedTime.Value, chromaprintMethod);
                }
                else
                {
                    _debugLogger.LogDebug(ocrIsPrimary
                        ? $"Trying OCR first for {episode.Name} before falling back to the batch chromaprint result"
                        : $"No batch result for {episode.Name}, trying other detection methods");
                    
                    var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
                    if (string.IsNullOrEmpty(normalizedPath))
                    {
                        _debugLogger.LogWarn($"Path normalization failed for: {episode.Path}");
return (false, 0, "Path normalization failed", 0, string.Empty, string.Empty);
                    }

                    double duration = 0;
                    if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                    {
                        duration = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                    }
                    else
                    {
                        _debugLogger.LogWarn($"No valid duration for episode {episode.Name}");
return (false, 0, "No valid duration", 0, string.Empty, string.Empty);
                    }

                    if (_configuration.EnableVideoValidation)
                    {
                        _debugLogger.LogDebug("Validating video file integrity");
                        var validationResult = await _videoValidator.ValidateVideo(normalizedPath, CancellationToken.None);
                        
                        if (!validationResult.isValid)
                        {
                            _debugLogger.LogWarn($"Video validation failed for {episode.Name}: {validationResult.errorMessage}");
                            MarkEpisodeFailed(episode, isDryRun);
                            return (false, 0, $"Video validation failed: {validationResult.errorMessage}", 0, string.Empty, string.Empty);
                        }
                        
                        _debugLogger.LogDebug("Video validation passed");
                    }

                    DetectionCoordinator coordinatorToUse;
                    if (overrideCoordinator != null)
                    {
                        coordinatorToUse = overrideCoordinator;
                    }
                    else if (effectiveConfig != _configuration)
                    {
                        _debugLogger.LogInfo("Using rule-based configuration for fallback detection");
                        localFallbackCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                        coordinatorToUse = localFallbackCoordinator;
                    }
                    else
                    {
                        coordinatorToUse = _detectionCoordinator;
                    }
                    var detectionResult = await coordinatorToUse.DetectCreditsWithContext(
                        normalizedPath,
                        duration,
                        episode.Id.ToString(),
                        episode.Series?.Id.ToString() ?? string.Empty,
                        episode.ParentIndexNumber,
                        episode.IndexNumber);

                    if (detectionResult.timestamp > 0)
                    {
                        double creditsStart = detectionResult.timestamp;
                        var containerStartTime = await GetVideoContainerStartTime(normalizedPath).ConfigureAwait(false);
                        if (containerStartTime > 0)
                        {
                            _debugLogger.LogInfo($"Applying container start_time correction of {containerStartTime:F3}s to {episode.Name}");
                        }
                        double finalTimestamp = creditsStart + containerStartTime + effectiveConfig.TimestampOffsetSeconds;

                        if (finalTimestamp < 0)
                        {
                            _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) is negative for {episode.Name}");
                            MarkEpisodeFailed(episode, isDryRun);
                            return (false, 0, $"Final timestamp with offset is negative: {finalTimestamp:F1}s", 0, string.Empty, string.Empty);
                        }
                        
                        if (finalTimestamp >= duration)
                        {
                            _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                            MarkEpisodeFailed(episode, isDryRun);
                            return (false, 0, $"Final timestamp with offset exceeds duration: {finalTimestamp:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
                        }

                        var postCredits = await CheckPostCreditsSceneAsync(episode, normalizedPath, creditsStart, finalTimestamp, duration, isDryRun).ConfigureAwait(false);
                        finalTimestamp = postCredits.MarkerSeconds;
                        var batchDetectionReason= AppendNote(detectionResult.detectionReason, postCredits.Note);

                        if (!isDryRun)
                        {
                            if (effectiveConfig.TimestampOffsetSeconds != 0)
                            {
                                _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {effectiveConfig.TimestampOffsetSeconds:+0;-0}s)");
                            }
                            else
                            {
                                _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(creditsStart)}");
                            }
                            if (!SaveDetectionMarkers(episode, finalTimestamp, postCredits, out var saveError))
                            {
                                _debugLogger.LogWarn($"✗ Could not save credits marker for {episode.Name}: {saveError}");
                                return (false, 0, saveError, 0, string.Empty, string.Empty);
                            }
                            _debugLogger.LogInfo(postCredits.SkipCreditsMarker ? "No credits marker saved - post-credits scene runs to the end" : "Successfully added chapter marker");
                        }
                        else
                        {
                            if (effectiveConfig.TimestampOffsetSeconds != 0)
                            {
                                _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {effectiveConfig.TimestampOffsetSeconds:+0;-0}s, method: {detectionResult.methodName}, confidence: {detectionResult.confidence:F2})");
                            }
                            else
                            {
                                _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(creditsStart)} (method: {detectionResult.methodName}, confidence: {detectionResult.confidence:F2})");
                            }
                        }
                        
                        ClearFailureMarker(episode, isDryRun);

                        return (true, detectionResult.timestamp, string.Empty, detectionResult.confidence, detectionResult.methodName, batchDetectionReason);
                    }
                    else
                    {
                        if (ocrIsPrimary && batchDetectedTime.HasValue && batchDetectedTime.Value > 0)
                        {
                            _debugLogger.LogInfo($"OCR failed for {episode.Name}, using precomputed Chromaprint batch result as Hash fallback");
                            return await SaveBatchChromaprintResult(episode, isDryRun, batchDetectedTime.Value, chromaprintMethod);
                        }

                        var durationStr = episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0
                            ? $" ({FormatTime(episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond)})"
                            : string.Empty;
                        _debugLogger.LogWarn($"Failed to detect credits for {episode.Name}{durationStr}: {detectionResult.failureReason}");
                        MarkEpisodeFailed(episode, isDryRun);
                        return (false, 0, detectionResult.failureReason, 0, string.Empty, string.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error processing episode: {ex.Message}", ex);
                if (ex is not OperationCanceledException && ex is not IOException)
                    MarkEpisodeFailed(episode, isDryRun);
                return (false, 0, $"Error: {ex.Message}", 0, string.Empty, string.Empty);
            }
            finally
            {
                localFallbackCoordinator?.Dispose();
                await _cpuThrottler.EndWork(workStart, CreditsDetectionService.CurrentCancellationToken).ConfigureAwait(false);

                if (_configuration?.DelayBetweenEpisodesMs > 0)
                {
                    _debugLogger.LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    try { await Task.Delay(_configuration.DelayBetweenEpisodesMs, CreditsDetectionService.CurrentCancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
        }

        public List<DetectionMethods.IDetectionMethod> GetDetectionMethods()
        {
            return _detectionCoordinator.GetAllDetectionMethods();
        }

        private void ClearFailureMarker(Episode episode, bool isDryRun)
        {
            if (isDryRun || episode.ProviderIds == null || !episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                return;

            try
            {
                episode.ProviderIds.Remove("EmbyCredits.Fail");
                _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
            }
            catch (Exception ex)
            {
                _debugLogger.LogWarn($"Failed to clear failure marker for {episode.Name}: {ex.Message}");
            }
        }

        private void MarkEpisodeFailed(Episode episode, bool isDryRun)
        {
            if (isDryRun || !_configuration.SkipPreviouslyFailedEpisodes || CreditsDetectionService.IsCancellationRequested)
                return;

            if (Utilities.ItemLookupHelper.IsRemovedFromLibrary(_libraryManager, episode))
            {
                _debugLogger.LogInfo($"Not marking {episode.Name} as failed - it was removed during detection");
                return;
            }

            try
            {
                if (episode.ProviderIds == null)
                    episode.ProviderIds = new MediaBrowser.Model.Entities.ProviderIdDictionary();
                else if (episode.ProviderIds.TryGetValue("EmbyCredits.Fail", out var existing) && existing == "true")
                    return;

                episode.ProviderIds["EmbyCredits.Fail"] = "true";
                _debugLogger.LogInfo($"Marked episode {episode.Name} as failed for future skipping");
                _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
            }
            catch (Exception ex)
            {
                _debugLogger.LogWarn($"Failed to save failure marker for {episode.Name}: {ex.Message}");
            }
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence, string methodName, string detectionReason)> SaveBatchChromaprintResult(
            Episode episode,
            bool isDryRun,
            double batchCreditsStart,
            DetectionMethods.ChromaprintDetection? chromaprintMethod)
        {
            var timestampOffset = _ruleMatchingService.GetEffectiveConfiguration(episode).TimestampOffsetSeconds;
            double creditsStart = batchCreditsStart;
            var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
            var containerStartTime = await GetVideoContainerStartTime(normalizedPath).ConfigureAwait(false);
            if (containerStartTime > 0)
                _debugLogger.LogInfo($"Applying container start_time correction of {containerStartTime:F3}s to {episode.Name}");
            double finalTimestamp = creditsStart + containerStartTime + timestampOffset;

            double duration = episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0
                ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                : 0;

            if (finalTimestamp < 0)
            {
                _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) is negative for {episode.Name}");
                MarkEpisodeFailed(episode, isDryRun);
                return (false, 0, $"Final timestamp with offset is negative: {finalTimestamp:F1}s", 0, string.Empty, string.Empty);
            }

            if (duration > 0 && finalTimestamp >= duration)
            {
                _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                MarkEpisodeFailed(episode, isDryRun);
                return (false, 0, $"Final timestamp with offset exceeds duration: {finalTimestamp:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
            }

            var postCredits = await CheckPostCreditsSceneAsync(episode, normalizedPath, creditsStart, finalTimestamp, duration, isDryRun).ConfigureAwait(false);
            finalTimestamp = postCredits.MarkerSeconds;

            if (!isDryRun)
            {
                if (timestampOffset != 0)
                {
                    _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {timestampOffset:+0;-0}s)");
                }
                else
                {
                    _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(finalTimestamp)}");
                }
                if (!SaveDetectionMarkers(episode, finalTimestamp, postCredits, out var saveError))
                {
                    _debugLogger.LogWarn($"✗ Could not save credits marker for {episode.Name}: {saveError}");
                    return (false, 0, saveError, 0, string.Empty, string.Empty);
                }
                _debugLogger.LogInfo(postCredits.SkipCreditsMarker ? "No credits marker saved - post-credits scene runs to the end" : "Successfully added chapter marker");
            }
            else
            {
                var batchConfidence= chromaprintMethod?.GetBatchConfidence(episode.Id.ToString()) ?? Plugin.Instance?.Configuration.ChromaprintMinConfidence ?? 0.85;
                if (timestampOffset != 0)
                {
                    _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {timestampOffset:+0;-0}s, method: Chromaprint Audio Fingerprint Detection, confidence: {batchConfidence:F2})");
                }
                else
                {
                    _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(creditsStart)} (method: Chromaprint Audio Fingerprint Detection, confidence: {batchConfidence:F2})");
                }
            }

            ClearFailureMarker(episode, isDryRun);

            double confidence = chromaprintMethod?.GetBatchConfidence(episode.Id.ToString()) ?? Plugin.Instance?.Configuration.ChromaprintMinConfidence ?? 0.85;
            return (true, creditsStart, string.Empty, confidence, "Chromaprint Audio Fingerprint Detection", AppendNote("Batch comparison", postCredits.Note));
        }

        public async Task<PostCreditsCheckResult> CheckPostCreditsSceneAsync(Episode episode, string videoPath, double detectedCreditsStart, double markerSeconds, double duration, bool isDryRun = false)
        {
            var postCreditsConfig = _ruleMatchingService.GetEffectiveConfiguration(episode);
            var movesMarker = postCreditsConfig.PostCreditsSceneAction != PostCreditsSceneAction.Flag;
            var result = new PostCreditsCheckResult { MarkerSeconds = markerSeconds };
            if (!postCreditsConfig.EnablePostCreditsSceneDetection || detectedCreditsStart <= 0 || duration <= 0 || string.IsNullOrEmpty(videoPath))
                return result;

            try
            {
                var detector = new DetectionMethods.PostCreditsSceneDetector(_logger, postCreditsConfig);
                var analysis = await detector.DetectAsync(videoPath, detectedCreditsStart, duration, CreditsDetectionService.CurrentCancellationToken).ConfigureAwait(false);
                var markerShift = markerSeconds - detectedCreditsStart;
                var timelineShift = markerShift - postCreditsConfig.TimestampOffsetSeconds;
                var analyzedRange = $"{FormatTime(analysis.AnalyzedFrom + timelineShift)}-{FormatTime(analysis.AnalyzedTo + timelineShift)}";
                var decoding = analysis.UsedHardwareDecoding ? "hardware decoding" : "software decoding";

                if (!string.IsNullOrEmpty(analysis.Error))
                {
                    result.Note = $"Post-credits check skipped ({analysis.Error})";
                    _debugLogger.LogWarn($"[PostCredits] {episode.Name}: check skipped ({analysis.Error}), analysed {analyzedRange}");
                    return result;
                }

                result.ManagesChapter = movesMarker;

                var scene = analysis.Scene;
                if (scene == null)
                {
                    result.Note = "No post-credits scene found";
                    _debugLogger.LogInfo($"[PostCredits] {episode.Name}: no post-credits scene in {analyzedRange} ({analysis.SampleCount} frames, {decoding})");
                    return result;
                }

                var sceneStart = scene.Start + timelineShift;
                var sceneText = $"{FormatTime(sceneStart)}-{FormatTime(scene.End + timelineShift)}";

                if (movesMarker)
                {
                    var movedMarker = scene.End + markerShift;
                    if (duration - movedMarker < 30)
                    {
                        result.SkipCreditsMarker = true;
                        result.ChapterSeconds = sceneStart;
                        result.Note = isDryRun
                            ? $"Post-credits scene at {sceneText} runs to the end, no credits marker would be saved and a '{ChapterMarkerService.PostCreditsChapterName}' chapter would be added at {FormatTime(sceneStart)}"
                            : $"Post-credits scene at {sceneText} runs to the end, no credits marker saved and a '{ChapterMarkerService.PostCreditsChapterName}' chapter added at {FormatTime(sceneStart)}";
                    }
                    else
                    {
                        result.MarkerSeconds = movedMarker;
                        result.Note = $"Post-credits scene at {sceneText}, marker moved to {FormatTime(movedMarker)}";
                    }
                }
                else
                {
                    result.Note = $"\u26a0 Possible post-credits scene at {sceneText}";
                }

                _debugLogger.LogInfo($"[PostCredits] {episode.Name}: {result.Note} (analysed {analyzedRange}, {analysis.SampleCount} frames, {decoding})");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Note = "Post-credits check failed";
                _debugLogger.LogWarn($"[PostCredits] Post-credits scene check failed for {episode.Name}: {ex.Message}");
            }

            return result;
        }

        public bool SaveDetectionMarkers(Episode episode, double creditsMarkerSeconds, PostCreditsCheckResult? postCredits, out string failureReason)
        {
            failureReason = string.Empty;

            if (Utilities.ItemLookupHelper.IsRemovedFromLibrary(_libraryManager, episode))
            {
                failureReason = RemovedDuringDetectionReason;
                return false;
            }

            if (postCredits?.SkipCreditsMarker == true)
            {
                if (!_chapterMarkerService.TryRemoveCreditsMarkers(episode, out failureReason))
                    return false;
                CreditsDetectionService.RegisterEpisodeWithoutCreditsMarker(episode.Id.ToString());
            }
            else if (!_chapterMarkerService.TrySaveCreditsMarker(episode, creditsMarkerSeconds, out failureReason))
            {
                return false;
            }

            if (postCredits != null)
                ApplyPostCreditsChapter(episode, postCredits, isDryRun: false);

            return true;
        }

        public void ApplyPostCreditsChapter(Episode episode, PostCreditsCheckResult postCredits, bool isDryRun)
        {
            if (isDryRun || !postCredits.ManagesChapter)
                return;

            if (!_chapterMarkerService.TrySetPostCreditsChapter(episode, postCredits.ChapterSeconds, out var chapterError))
                _debugLogger.LogWarn($"[PostCredits] {episode.Name}: {chapterError}");
        }

        private static string AppendNote(string reason, string note)
        {
            if (string.IsNullOrEmpty(note))
                return reason;
            return string.IsNullOrEmpty(reason) ? note : $"{reason} | {note}";
        }

        private async Task<double> GetVideoContainerStartTime(string filePath)
        {
            try
            {
                var normalizedFilePath = Utilities.FFmpegHelper.ResolveInputPath(filePath);
                var applyStrmResilience = Utilities.FFmpegHelper.ShouldApplyStrmNetworkResilience(filePath, normalizedFilePath, _configuration);

                var ffprobeStartInfo = new ProcessStartInfo
                {
                    FileName = Utilities.FFmpegHelper.GetFfprobePath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                ffprobeStartInfo.ArgumentList().Add("-v");
                ffprobeStartInfo.ArgumentList().Add("error");
                Utilities.FFmpegHelper.AddStrmNetworkResilienceArgs(ffprobeStartInfo, filePath, normalizedFilePath, _configuration);
                ffprobeStartInfo.ArgumentList().Add("-show_entries");
                ffprobeStartInfo.ArgumentList().Add("format=start_time");
                ffprobeStartInfo.ArgumentList().Add("-of");
                ffprobeStartInfo.ArgumentList().Add("default=noprint_wrappers=1:nokey=1");
                ffprobeStartInfo.ArgumentList().Add(normalizedFilePath);

                using (var process = new Process { StartInfo = ffprobeStartInfo })
                {
                    ffprobeStartInfo.ApplyArgumentList();
                    process.Start();
                    CpuThrottler.SetProcessPriority(process, _configuration);

                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var outputTask = process.StandardOutput.ReadToEndAsync();

                    if (applyStrmResilience)
                    {
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(StrmProbeTimeoutSeconds));
                        var completed = await Task.WhenAny(outputTask, timeoutTask).ConfigureAwait(false);
                        if (completed == timeoutTask)
                        {
                            _debugLogger.LogWarn($"ffprobe container start_time probe timed out after {StrmProbeTimeoutSeconds}s for strm episode: {filePath}");
                            try { if (!process.HasExited) process.Kill(); } catch { }
                            return 0;
                        }
                    }

                    var output = await outputTask.ConfigureAwait(false);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);

                    var trimmed = output.Trim();
                    if (trimmed == "N/A" || string.IsNullOrEmpty(trimmed))
                        return 0;

                    if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var startTime))
                    {
                        return startTime > 0 ? startTime : 0;
                    }

                    return 0;
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error getting container start_time: {ex.Message}", ex);
                return 0;
            }
        }

        private async Task<double> GetVideoDuration(string filePath)
        {
            try
            {
                _debugLogger.LogDebug($"Getting video duration for: {filePath}");

                var normalizedFilePath = Utilities.FFmpegHelper.ResolveInputPath(filePath);
                var applyStrmResilience = Utilities.FFmpegHelper.ShouldApplyStrmNetworkResilience(filePath, normalizedFilePath, _configuration);

                var ffprobeStartInfo = new ProcessStartInfo
                {
                    FileName = Utilities.FFmpegHelper.GetFfprobePath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                ffprobeStartInfo.ArgumentList().Add("-v");
                ffprobeStartInfo.ArgumentList().Add("error");
                Utilities.FFmpegHelper.AddStrmNetworkResilienceArgs(ffprobeStartInfo, filePath, normalizedFilePath, _configuration);
                ffprobeStartInfo.ArgumentList().Add("-show_entries");
                ffprobeStartInfo.ArgumentList().Add("format=duration");
                ffprobeStartInfo.ArgumentList().Add("-of");
                ffprobeStartInfo.ArgumentList().Add("default=noprint_wrappers=1:nokey=1");
                ffprobeStartInfo.ArgumentList().Add(normalizedFilePath);

                using (var process = new Process { StartInfo = ffprobeStartInfo })
                {
                    ffprobeStartInfo.ApplyArgumentList();
                    process.Start();
                    CpuThrottler.SetProcessPriority(process, _configuration);
                    
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var outputTask = process.StandardOutput.ReadToEndAsync();

                    if (applyStrmResilience)
                    {
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(StrmProbeTimeoutSeconds));
                        var completed = await Task.WhenAny(outputTask, timeoutTask).ConfigureAwait(false);
                        if (completed == timeoutTask)
                        {
                            _debugLogger.LogWarn($"ffprobe duration probe timed out after {StrmProbeTimeoutSeconds}s for strm episode: {filePath}");
                            try { if (!process.HasExited) process.Kill(); } catch { }
                            return 0;
                        }
                    }

                    var output = await outputTask.ConfigureAwait(false);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);

                    if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, 
                        System.Globalization.CultureInfo.InvariantCulture, out var duration))
                    {
                        _debugLogger.LogDebug($"Duration result: {duration} seconds");
                        return duration;
                    }

                    _debugLogger.LogError("Failed to parse duration output", null);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error getting video duration: {ex.Message}", ex);
                return 0;
            }
        }

        private string FormatTime(double seconds) => Utilities.ItemLookupHelper.FormatTime(seconds);
    }
}
