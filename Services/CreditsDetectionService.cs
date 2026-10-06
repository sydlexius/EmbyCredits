using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public static class CreditsDetectionService
    {
        private static ILogger? _logger;
        private static IApplicationPaths? _appPaths;
        private static PluginConfiguration? _configuration;
        private static ILibraryManager? _libraryManager;
        private static IItemRepository? _itemRepository;
        private static IFfmpegManager? _ffmpegManager;
        private static readonly System.Reflection.PropertyInfo? _markerTypeProp =
            typeof(ChapterInfo).GetProperty("MarkerType");
        private static volatile bool _isRunning;
        private static ConcurrentDictionary<string, DateTime> _processedEpisodes = new ConcurrentDictionary<string, DateTime>();
        private static EpisodeQueue _processingQueue = new EpisodeQueue();
        private static SemaphoreSlim? _processingSemaphore = new SemaphoreSlim(1, 1);
        private static volatile bool _isProcessing = false;
        private static volatile bool _cancellationRequested = false;
        private static CancellationTokenSource? _cancellationTokenSource = null;

        public static bool IsCancellationRequested => _cancellationRequested;

        public static CancellationToken CurrentCancellationToken
        {
            get
            {
                try
                {
                    return Volatile.Read(ref _cancellationTokenSource)?.Token ?? CancellationToken.None;
                }
                catch (ObjectDisposedException)
                {
                    return CancellationToken.None;
                }
            }
        }

        private static readonly ConcurrentDictionary<string, bool> _dryRunEpisodeIds = new ConcurrentDictionary<string, bool>();
        private static volatile HashSet<string>? _activeRunDryIds;
        [ThreadStatic]
        private static bool _queueingDryRun;

        private static readonly object _queueStateLock = new object();
        private static bool _startPending;

        private static readonly ConcurrentQueue<IDisposable> _retiredServices = new ConcurrentQueue<IDisposable>();

        private static readonly ConcurrentDictionary<string, bool> _pendingItemUpdateChecks = new ConcurrentDictionary<string, bool>();
        private const int ItemUpdateDebounceSeconds = 5;

        private static readonly ConcurrentDictionary<string, bool> _episodesWithoutCreditsMarker = new ConcurrentDictionary<string, bool>();

        public static void RegisterEpisodeWithoutCreditsMarker(string episodeId)
        {
            _episodesWithoutCreditsMarker[episodeId] = true;
        }

        private const int DispatchRetryDelayMs = 60000;
        private const int MaxDispatchRetries = 30;
        private static volatile bool _isManualDetectionRun = false;
        private static bool _previousRestoreAfterScanState = false;
        private static bool _previousItemAddedHandlerState = false;

        private const int MaxProcessedEpisodesCache = 10000;
        private const int MaxBatchDetectionCacheSize = 5000;
        private const int MaxEpisodeStatusMessagesCache = 1000;

        private static volatile DetectionCoordinator? _detectionCoordinator;
        private static DebugLogger? _debugLogger;
        private static ChapterMarkerService? _chapterMarkerService;
        private static EpisodeProcessor? _episodeProcessor;
        private static PluginCoordinationService? _pluginCoordination;
        private static TheIntroDbService? _theIntroDbService;

        private static ConcurrentDictionary<string, DateTime> _recentlyRestoredEpisodes = new ConcurrentDictionary<string, DateTime>();
        private const int RestoreGuardSeconds = 120;
        private static ConcurrentDictionary<string, bool> _pendingDeferredRestoreChecks = new ConcurrentDictionary<string, bool>();
        private const int DeferredRestoreDelaySeconds = 45;

        private static ConcurrentDictionary<string, List<(string method, double timestamp)>> _batchDetectionCache = new ConcurrentDictionary<string, List<(string method, double timestamp)>>();
        private static ConcurrentDictionary<string, DateTime> _batchDetectionCacheInsertTime = new ConcurrentDictionary<string, DateTime>();
        private static bool _isBatchMode = false;

        private static ConcurrentDictionary<string, bool> _singleEpisodeTargets = new ConcurrentDictionary<string, bool>();
        private static volatile HashSet<string>? _activeRunTargets;
        
        private static ConcurrentDictionary<string, ConcurrentQueue<string>> _episodeStatusMessages = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

        private static RuleMatchingService? _cachedRuleMatchingService;
        private static Timer? _cacheCleanupTimer;
        private static readonly ConcurrentDictionary<string, SeasonDispatchState> _pendingSeasonDispatches =
            new ConcurrentDictionary<string, SeasonDispatchState>(StringComparer.Ordinal);

        private static void LogInfo(string message)
        {
            _debugLogger?.LogInfo(message);
        }

        private static void LogDebug(string message)
        {
            _debugLogger?.LogDebug(message);
        }

        private static void LogWarn(string message)
        {
            _debugLogger?.LogWarn(message);
        }

        private static void LogError(string message, Exception? ex = null)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
            {
                _debugLogger?.LogInfo($"{message} (Task was canceled)");
                return;
            }
            _debugLogger?.LogError(message, ex);
        }

        public static void LogToDebug(string level, string message)
        {
            _debugLogger?.LogToDebug(level, message);
        }
        
        public static void AddEpisodeStatusMessage(string episodeId, string message)
        {
            var messages = _episodeStatusMessages.GetOrAdd(episodeId, _ => new ConcurrentQueue<string>());

            messages.Enqueue(message);
            while (messages.Count > 50)
                messages.TryDequeue(out _);

            if (_episodeStatusMessages.Count > MaxEpisodeStatusMessagesCache)
                CleanupEpisodeStatusMessages();
        }
        
        private static List<string> GetAndClearEpisodeStatusMessages(string episodeId)
        {
            if (_episodeStatusMessages.TryRemove(episodeId, out var messages))
            {
                return messages.ToList();
            }
            return new List<string>();
        }

        public static bool IsDebugMode => _debugLogger?.IsDebugMode ?? false;

        public static void Start(ILogger logger, IApplicationPaths appPaths, PluginConfiguration configuration)
        {
            _logger = logger;
            _appPaths = appPaths;
            _configuration = configuration;
            _isRunning = true;

            _detectionCoordinator?.Dispose();
            _detectionCoordinator = new DetectionCoordinator(_logger, _configuration);

            if (_debugLogger == null)
                _debugLogger = new DebugLogger(_logger, configuration);
            else
                _debugLogger.SetConfiguration(configuration);
            _pluginCoordination?.Dispose();
            _pluginCoordination = new PluginCoordinationService(_logger, configuration);
            _cachedRuleMatchingService = new RuleMatchingService(logger, configuration);
            
            if (_itemRepository != null)
            {
                _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                _theIntroDbService?.Dispose();
                _theIntroDbService = configuration.EnableTheIntroDB ? new TheIntroDbService(_logger, configuration) : null;
                _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                    _chapterMarkerService, _debugLogger, configuration, _cachedRuleMatchingService!, _theIntroDbService);
            }

            LogInfo("Credits Detection Service started");

            _previousItemAddedHandlerState = configuration.EnableAutoDetection || configuration.EnableTracerMode || configuration.OnlyProcessNewEpisodes;
            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                if (_previousItemAddedHandlerState)
                {
                    _libraryManager.ItemAdded += OnItemAdded;
                    LogInfo("ItemAdded event handler registered (auto-detection, tracer, or OnlyProcessNewEpisodes enabled)");
                }

                _libraryManager.ItemUpdated -= OnItemUpdated;
                _previousRestoreAfterScanState = configuration.EnableAutoRestoreAfterScan;
                if (configuration.EnableAutoRestoreAfterScan)
                {
                    _libraryManager.ItemUpdated += OnItemUpdated;
                    LogInfo("Auto-restore enabled: ItemUpdated event handler registered");
                }
            }

            _cacheCleanupTimer?.Dispose();
            _cacheCleanupTimer = new Timer(_ =>
            {
                try
                {
                    if (!_isProcessing) { CleanupBatchDetectionCache(); CleanupOldProcessedEpisodes(); }
                }
                catch (Exception ex)
                {
                    LogError("Cache cleanup failed", ex);
                }
            }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        }

        public static void UpdateConfiguration(PluginConfiguration configuration)
        {
            var previousAutoDetectionState = _configuration?.EnableAutoDetection ?? false;
            _configuration = configuration;
            LogInfo("Credits Detection Service configuration updated");

            if (_logger != null && _appPaths != null)
            {
                var oldDetectionCoordinator = _detectionCoordinator;
                _detectionCoordinator = new DetectionCoordinator(_logger, configuration);

                if (_debugLogger == null)
                    _debugLogger = new DebugLogger(_logger, configuration);
                else
                    _debugLogger.SetConfiguration(configuration);

                var oldPluginCoordination = _pluginCoordination;
                _pluginCoordination = new PluginCoordinationService(_logger, configuration);
                _cachedRuleMatchingService = new RuleMatchingService(_logger, configuration);

                var oldChapterMarkerService = _chapterMarkerService;
                var oldEpisodeProcessor = _episodeProcessor;
                var oldTheIntroDbService = _theIntroDbService;
                
                if (_itemRepository != null)
                {
                    _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                    _theIntroDbService = configuration.EnableTheIntroDB ? new TheIntroDbService(_logger, configuration) : null;
                    _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                        _chapterMarkerService, _debugLogger, configuration, _cachedRuleMatchingService!, _theIntroDbService);
                }

                RetireService(oldDetectionCoordinator);
                RetireService(oldPluginCoordination);
                if (!ReferenceEquals(oldTheIntroDbService, _theIntroDbService))
                    RetireService(oldTheIntroDbService);
                DisposeRetiredServicesIfIdle();
            }

            if (_libraryManager != null && _isRunning)
            {
                var needsItemAddedHandler = configuration.EnableAutoDetection || configuration.EnableTracerMode || configuration.OnlyProcessNewEpisodes;
                if (needsItemAddedHandler != _previousItemAddedHandlerState)
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                    _previousItemAddedHandlerState = needsItemAddedHandler;
                    if (needsItemAddedHandler)
                    {
                        _libraryManager.ItemAdded += OnItemAdded;
                        LogInfo("ItemAdded event handler registered (auto-detection, tracer, or OnlyProcessNewEpisodes enabled)");
                    }
                    else
                    {
                        LogInfo("ItemAdded event handler unregistered (auto-detection, tracer, and OnlyProcessNewEpisodes all disabled)");
                    }
                }

                var previousRestoreState = _previousRestoreAfterScanState;
                _previousRestoreAfterScanState = configuration.EnableAutoRestoreAfterScan;
                if (configuration.EnableAutoRestoreAfterScan != previousRestoreState)
                {
                    _libraryManager.ItemUpdated -= OnItemUpdated;

                    if (configuration.EnableAutoRestoreAfterScan)
                    {
                        _libraryManager.ItemUpdated += OnItemUpdated;
                        LogInfo("Auto-restore enabled: ItemUpdated event handler registered");
                    }
                    else
                    {
                        LogInfo("Auto-restore disabled: ItemUpdated event handler unregistered");
                    }
                }
            }
        }

        private static void RetireService(IDisposable? service)
        {
            if (service != null)
                _retiredServices.Enqueue(service);
        }

        private static void DisposeRetiredServicesIfIdle()
        {
            lock (_queueStateLock)
            {
                if (_isProcessing || _startPending)
                    return;
            }

            DisposeRetiredServices();
        }

        private static void DisposeRetiredServices()
        {
            while (_retiredServices.TryDequeue(out var service))
            {
                try
                {
                    service.Dispose();
                }
                catch (Exception ex)
                {
                    LogDebug($"Error disposing retired service: {ex.Message}");
                }
            }
        }

        private static int DrainProcessingQueue()
        {
            var drained = 0;
            while (_processingQueue.TryDequeue(out var queued))
            {
                drained++;
                if (queued != null)
                    _dryRunEpisodeIds.TryRemove(queued.Id.ToString(), out _);
            }
            return drained;
        }

        private static void MarkTarget(string episodeId)
        {
            _singleEpisodeTargets[episodeId] = true;
            if (_queueingDryRun)
                _dryRunEpisodeIds[episodeId] = true;
            else
                _dryRunEpisodeIds.TryRemove(episodeId, out _);
        }

        private static bool IsDryRun(string episodeId)
        {
            var dryIds = _activeRunDryIds;
            return dryIds != null && dryIds.Contains(episodeId);
        }

        private static void MarkPendingResolved(Episode episode)
        {
            Plugin.PendingEpisodesService?.MarkProcessed(episode.Id.ToString());
        }

        private static void ReleaseSemaphore(SemaphoreSlim? semaphore)
        {
            try
            {
                semaphore?.Release();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private static void StartProcessQueueTask(string failureMessage)
        {
            _ = Task.Run(ProcessQueue).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    LogError(failureMessage, t.Exception.GetBaseException());
                }
            }, TaskScheduler.Default);
        }

        public static void ClearCache()
        {
            ClearCaches(drainQueue: true);
        }

        private static void ClearCaches(bool drainQueue)
        {
            try
            {
                _detectionCoordinator?.ClearCache();
                
                if (_batchDetectionCache.Count > MaxBatchDetectionCacheSize)
                {
                    System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
                    System.Threading.Interlocked.Exchange(ref _batchDetectionCacheInsertTime, new ConcurrentDictionary<string, DateTime>());
                }
                else
                {
                    _batchDetectionCache.Clear();
                    _batchDetectionCacheInsertTime.Clear();
                }
                
                CleanupOldProcessedEpisodes();

                if (drainQueue)
                    DrainProcessingQueue();

                _episodeStatusMessages.Clear();
                System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, ConcurrentQueue<string>>());
                
                LogInfo("Cleared in-memory batch detection cache and processing queue for fresh detection");
            }
            catch (Exception ex)
            {
                LogError("Error clearing cache", ex);
            }
        }

        private static void CleanupOldProcessedEpisodes()
        {
            if (_processedEpisodes.Count <= MaxProcessedEpisodesCache)
                return;

            var cutoffTime = DateTime.UtcNow.AddDays(-7);
            
            foreach (var kvp in _processedEpisodes)
            {
                if (kvp.Value < cutoffTime)
                {
                    _processedEpisodes.TryRemove(kvp.Key, out _);
                }
            }

            if (_processedEpisodes.Count > MaxProcessedEpisodesCache)
            {
                var oldestEntries = _processedEpisodes
                    .OrderBy(kvp => kvp.Value)
                    .Take(_processedEpisodes.Count - MaxProcessedEpisodesCache)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldestEntries)
                {
                    _processedEpisodes.TryRemove(key, out _);
                }
            }
        }

        private static void CleanupBatchDetectionCache()
        {
            if (_batchDetectionCache.Count <= MaxBatchDetectionCacheSize)
                return;

            var entriesToRemove = _batchDetectionCache.Count - MaxBatchDetectionCacheSize;

            var oldest = _batchDetectionCacheInsertTime
                .OrderBy(kvp => kvp.Value)
                .Take(entriesToRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            var removed = 0;
            foreach (var key in oldest)
            {
                if (_batchDetectionCache.TryRemove(key, out _))
                {
                    _batchDetectionCacheInsertTime.TryRemove(key, out _);
                    removed++;
                }
            }

            if (removed < entriesToRemove)
            {
                foreach (var kvp in _batchDetectionCache)
                {
                    if (removed >= entriesToRemove) break;
                    if (!_batchDetectionCacheInsertTime.ContainsKey(kvp.Key) && _batchDetectionCache.TryRemove(kvp.Key, out _))
                        removed++;
                }
            }

            LogDebug($"Cleaned up batch detection cache: removed {removed} entries");
        }
        
        private static void CleanupEpisodeStatusMessages()
        {
            if (_episodeStatusMessages.Count <= MaxEpisodeStatusMessagesCache)
                return;

            var removed = 0;
            var entriesToRemove = _episodeStatusMessages.Count - MaxEpisodeStatusMessagesCache;

            foreach (var kvp in _episodeStatusMessages)
            {
                if (removed >= entriesToRemove) break;
                if (kvp.Value.IsEmpty && _episodeStatusMessages.TryRemove(kvp.Key, out _))
                    removed++;
            }

            foreach (var kvp in _episodeStatusMessages)
            {
                if (removed >= entriesToRemove) break;
                if (_episodeStatusMessages.TryRemove(kvp.Key, out _))
                    removed++;
            }

            LogDebug($"Cleaned up episode status messages cache: removed {removed} entries");
        }

        public static void Stop()
        {
            _isRunning = false;
            _isProcessing = false;

            _cacheCleanupTimer?.Dispose();
            _cacheCleanupTimer = null;

            if (_libraryManager != null)
            {
                try
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                    _libraryManager.ItemUpdated -= OnItemUpdated;
                }
                catch (Exception ex)
                {
                    LogError("Error unregistering library event handlers", ex);
                }
            }

            _detectionCoordinator?.Dispose();
            _detectionCoordinator = null;

            try
            {
                var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
                if (cts != null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                var oldSemaphore = Interlocked.Exchange(ref _processingSemaphore, new SemaphoreSlim(1, 1));
                oldSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("Error disposing semaphore", ex);
            }
            
            _debugLogger?.Dispose();
            _debugLogger = null;

            _pluginCoordination?.Dispose();
            _pluginCoordination = null;

            DisposeRetiredServices();

            _episodeProcessor = null;
            _chapterMarkerService = null;
            _cachedRuleMatchingService = null;

            _theIntroDbService?.Dispose();
            _theIntroDbService = null;

            while (_processingQueue.TryDequeue(out _)) { }
            _processingQueue.Dispose();
            _processingQueue = new EpisodeQueue();

            _batchDetectionCache.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
            _batchDetectionCacheInsertTime.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCacheInsertTime, new ConcurrentDictionary<string, DateTime>());
            
            _processedEpisodes.Clear();
            System.Threading.Interlocked.Exchange(ref _processedEpisodes, new ConcurrentDictionary<string, DateTime>());
            
            _episodeStatusMessages.Clear();
            System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, ConcurrentQueue<string>>());

            _recentlyRestoredEpisodes.Clear();
            _pendingDeferredRestoreChecks.Clear();
            _pendingItemUpdateChecks.Clear();
            _singleEpisodeTargets.Clear();
            _dryRunEpisodeIds.Clear();

            foreach (var s in _pendingSeasonDispatches.Values)
                s.Dispose();
            _pendingSeasonDispatches.Clear();

            LogInfo("Credits Detection Service stopped");
        }

        private static void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (!_isRunning || _configuration == null)
                return;

            var autoDetect = _configuration.EnableAutoDetection;
            var tracerEnabled = _configuration.EnableTracerMode;
            var trackPending = _configuration.OnlyProcessNewEpisodes;

            if (!autoDetect && !tracerEnabled && !trackPending)
                return;

            try
            {
                if (e.Item is Episode episode)
                {
                    if (episode.IsVirtualItem)
                    {
                        LogDebug($"Skipping virtual item: {episode.SeriesName} - {episode.Name}");
                        return;
                    }

                    if (episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0)
                    {
                        LogDebug($"Skipping TV special: {episode.SeriesName} - {episode.Name} (Season {episode.ParentIndexNumber})");
                        return;
                    }

                    if (tracerEnabled && Plugin.TracerService != null)
                        Plugin.TracerService.TrackEpisode(episode);

                    if (trackPending && Plugin.PendingEpisodesService != null)
                        Plugin.PendingEpisodesService.TrackEpisode(episode);

                    var libraryIds = _configuration.LibraryIds ?? Array.Empty<string>();
                    if (libraryIds.Length > 0 && _libraryManager != null)
                    {
                        string? matchedLibrary = null;
                        if (!string.IsNullOrEmpty(episode.Path))
                            matchedLibrary = RuleMatchingService.FindLibraryNameForPath(episode.Path, libraryIds, _libraryManager);

                        LogDebug($"Episode {episode.Name} - library match={matchedLibrary != null} (library={matchedLibrary ?? "none"}), Configured: [{string.Join(", ", libraryIds)}]");

                        if (matchedLibrary == null)
                        {
                            LogDebug($"Skipping auto-detection for {episode.Name} - not in configured libraries");
                            return;
                        }
                    }

                    LogInfo($"New episode detected: {episode.SeriesName} - {episode.Name}");

                    if (!autoDetect)
                        return;

                    var seasonKey = $"{episode.SeriesId}_{episode.ParentIndexNumber!.Value}";
                    var dispatchState = _pendingSeasonDispatches.GetOrAdd(seasonKey, _ => new SeasonDispatchState());
                    dispatchState.EpisodeIds.TryAdd(episode.Id, 0);
                    LogInfo($"Registered '{episode.Name}' in dispatch window for '{episode.SeriesName}' Season {episode.ParentIndexNumber}");
                    dispatchState.ResetTimer(20000, () => DispatchPendingSeason(seasonKey));
                }
            }
            catch (Exception ex)
            {
                LogError($"Error handling ItemAdded event for {e.Item?.Name}", ex);
            }
        }

        private static void DispatchPendingSeason(string seasonKey)
        {
            if (!_pendingSeasonDispatches.TryRemove(seasonKey, out var state))
                return;
            state.Dispose();

            if (!_isRunning || _configuration == null || !_configuration.EnableAutoDetection)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    var episodeIds = state.EpisodeIds.Keys.ToList();
                    if (episodeIds.Count == 0 || _libraryManager == null)
                        return;

                    var validEpisodes = new List<Episode>();
                    var notReadyIds = new List<Guid>();
                    foreach (var id in episodeIds)
                    {
                        var ep = _libraryManager.GetItemById(id) as Episode;
                        if (ep == null || string.IsNullOrEmpty(ep.Path)) continue;

                        if (ep.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            ep.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            if (!File.Exists(ep.Path))
                            {
                                LogDebug($"File not available yet: {ep.Name}");
                                notReadyIds.Add(id);
                                continue;
                            }
                            var fi = new FileInfo(ep.Path);
                            using (fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                            if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < 5)
                            {
                                LogDebug($"File still being modified: {ep.Name}");
                                notReadyIds.Add(id);
                                continue;
                            }
                            validEpisodes.Add(ep);
                        }
                        catch (IOException)
                        {
                            LogDebug($"File is locked and still being written: {ep.Name}");
                            notReadyIds.Add(id);
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Skipping {ep.Name} due to file access error: {ex.Message}");
                        }
                    }

                    if (notReadyIds.Count > 0)
                    {
                        var nextAttempt = state.RetryCount + 1;
                        if (nextAttempt <= MaxDispatchRetries)
                        {
                            LogInfo($"{notReadyIds.Count} episode(s) in dispatch '{seasonKey}' are still being written - retrying the season in {DispatchRetryDelayMs / 1000}s (attempt {nextAttempt}/{MaxDispatchRetries})");
                            var retryState = _pendingSeasonDispatches.GetOrAdd(seasonKey, _ => new SeasonDispatchState());
                            foreach (var id in episodeIds)
                                retryState.EpisodeIds.TryAdd(id, 0);
                            retryState.RetryCount = Math.Max(retryState.RetryCount, nextAttempt);
                            retryState.ResetTimer(DispatchRetryDelayMs, () => DispatchPendingSeason(seasonKey));
                            return;
                        }

                        LogWarn($"Gave up waiting for {notReadyIds.Count} episode(s) in dispatch '{seasonKey}' after {MaxDispatchRetries} attempts - they will be picked up by the next scheduled run");
                    }

                    if (validEpisodes.Count == 0)
                    {
                        LogWarn($"No valid files ready for season dispatch '{seasonKey}'");
                        return;
                    }

                    var representative = validEpisodes[0];
                    LogInfo($"Dispatching {validEpisodes.Count} episode(s) for '{representative.SeriesName}' Season {representative.ParentIndexNumber}");

                    if (representative.Series != null && SeasonUsesChromaprintBatch(representative))
                    {
                        LogInfo($"Hash detection for '{representative.SeriesName}' — queuing season with {validEpisodes.Count} target(s)");
                        foreach (var ep in validEpisodes)
                            MarkTarget(ep.Id.ToString());
                        QueueSeasonForEpisode(representative, isManualDetection: false);
                        return;
                    }

                    foreach (var ep in validEpisodes)
                    {
                        LogInfo($"Processing episode after delay: {ep.SeriesName} - {ep.Name}");
                        QueueEpisode(ep);
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Error dispatching pending season '{seasonKey}'", ex);
                }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    LogError($"Season dispatch task failed for '{seasonKey}'", t.Exception.GetBaseException());
            }, TaskScheduler.Default);
        }

        private static bool SeasonUsesChromaprintBatch(Episode episode)
        {
            if (_configuration == null || _logger == null || episode.Series == null)
                return false;

            var ruleMatchingService = _cachedRuleMatchingService ?? new RuleMatchingService(_logger, _configuration);
            var effectiveConfig = ruleMatchingService.GetEffectiveConfiguration(episode.Series);

            bool usesHashDetection = effectiveConfig.DetectionMode == DetectionMode.HashOnly ||
                                     effectiveConfig.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                     effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback;
            if (!usesHashDetection || effectiveConfig.DisableDetection)
                return false;

            if (effectiveConfig.EnableAnimeDetection && CheckIfAnime(episode.Series.Id.ToString()))
                return false;

            return true;
        }

        public static bool SeasonNeedsReferenceEpisodes(Episode episode)
        {
            return SeasonUsesChromaprintBatch(episode);
        }

        public static List<Episode> GetSeasonEpisodes(Episode episode)
        {
            if (_libraryManager == null || episode.Series == null || !episode.ParentIndexNumber.HasValue)
                return new List<Episode>();

            var seasonNumber = episode.ParentIndexNumber.Value;
            var seasonItem = episode.ParentId != 0 ? _libraryManager.GetItemById(episode.ParentId) : null;
            long ancestorId = seasonItem?.InternalId ?? episode.Series.InternalId;

            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                HasPath = true,
                AncestorIds = new[] { ancestorId }
            }).OfType<Episode>()
              .Where(e => e.ParentIndexNumber == seasonNumber)
              .OrderBy(e => e.IndexNumber)
              .ToList();
        }

        private static void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (!_isRunning || _isProcessing || _configuration == null || !_configuration.EnableAutoRestoreAfterScan)
                return;

            if (string.IsNullOrWhiteSpace(_configuration.BackupFolderPath))
                return;

            if (e.Item is not Episode episode)
                return;

            if (episode.IsVirtualItem || episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0)
                return;

            var episodeId = episode.Id.ToString();

            if (_recentlyRestoredEpisodes.TryGetValue(episodeId, out var restoredAt) &&
                (DateTime.UtcNow - restoredAt).TotalSeconds < RestoreGuardSeconds)
            {
                return;
            }

            if (!_pendingItemUpdateChecks.TryAdd(episodeId, true))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ItemUpdateDebounceSeconds)).ConfigureAwait(false);
                    _pendingItemUpdateChecks.TryRemove(episodeId, out _);
                    await HandleItemUpdatedAsync(episode, episodeId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _pendingItemUpdateChecks.TryRemove(episodeId, out _);
                    LogError($"Error in OnItemUpdated for '{episode.Name}'", ex);
                }
            });
        }

        private static async Task HandleItemUpdatedAsync(Episode episode, string episodeId)
        {
            var configuration = _configuration;
            if (!_isRunning || _isProcessing || configuration == null || !configuration.EnableAutoRestoreAfterScan)
                return;

            var backupFolder = configuration.BackupFolderPath;
            if (string.IsNullOrWhiteSpace(backupFolder))
                return;

            if (_itemRepository == null)
                return;

            var libraryIds = configuration.LibraryIds ?? Array.Empty<string>();
            if (libraryIds.Length > 0 && _libraryManager != null && !string.IsNullOrEmpty(episode.Path) &&
                RuleMatchingService.FindLibraryNameForPath(episode.Path, libraryIds, _libraryManager) == null)
            {
                return;
            }

            var chapters = _itemRepository.GetChapters(episode);
            var hasCreditsMarker = chapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;

            if (hasCreditsMarker)
            {
                if (Plugin.CreditsBackupService != null &&
                    Plugin.CreditsBackupService.HasFileChanged(episode, backupFolder))
                {
                    LogInfo($"File changed since last detection for '{episode.Name}' — queuing fresh detection instead of trusting existing marker");
                    if (configuration.EnableAutoDetection)
                        QueueEpisode(episode, isManualDetection: true);
                    return;
                }

                ScheduleDeferredRestoreCheck(episode, episodeId);
                return;
            }

            var backupService = Plugin.CreditsBackupService;
            if (backupService == null)
                return;

            if (backupService.HasFileChanged(episode, backupFolder))
            {
                LogInfo($"File changed since last backup for '{episode.Name}' — queuing fresh detection instead of restoring stale marker");
                if (configuration.EnableAutoDetection)
                    QueueEpisode(episode, isManualDetection: true);
                return;
            }

            var restored = await backupService.RestoreEpisodeMarkerFromBackup(episode, backupFolder).ConfigureAwait(false);
            if (restored)
            {
                _recentlyRestoredEpisodes[episodeId] = DateTime.UtcNow;
                var cutoff = DateTime.UtcNow.AddSeconds(-RestoreGuardSeconds * 2);
                foreach (var key in _recentlyRestoredEpisodes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
                    _recentlyRestoredEpisodes.TryRemove(key, out _);
                if (Plugin.Instance != null)
                    Plugin.Progress.IncrementBackupRestored();
                ScheduleDeferredRestoreCheck(episode, episodeId);
            }
        }

        private static void ScheduleDeferredRestoreCheck(Episode episode, string episodeId)
        {
            if (!_pendingDeferredRestoreChecks.TryAdd(episodeId, true))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(DeferredRestoreDelaySeconds)).ConfigureAwait(false);
                    _pendingDeferredRestoreChecks.TryRemove(episodeId, out _);

                    if (!_isRunning || _configuration == null || !_configuration.EnableAutoRestoreAfterScan)
                        return;

                    if (string.IsNullOrWhiteSpace(_configuration.BackupFolderPath))
                        return;

                    if (_itemRepository == null || Plugin.CreditsBackupService == null)
                        return;

                    var chapters = _itemRepository.GetChapters(episode);
                    var stillHasMarker = chapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;

                    if (!stillHasMarker)
                    {
                        if (Plugin.CreditsBackupService.HasFileChanged(episode, _configuration.BackupFolderPath))
                        {
                            LogInfo($"Deferred restore check: file changed since last backup for '{episode.Name}' — queuing fresh detection instead of restoring stale marker");
                            if (_configuration.EnableAutoDetection)
                                QueueEpisode(episode, isManualDetection: true);
                            return;
                        }

                        LogInfo($"Deferred restore check: credits marker was removed for '{episode.Name}', restoring from backup...");
                        var restored = await Plugin.CreditsBackupService.RestoreEpisodeMarkerFromBackup(episode, _configuration.BackupFolderPath).ConfigureAwait(false);
                        if (restored)
                        {
                            _recentlyRestoredEpisodes[episodeId] = DateTime.UtcNow;
                            var cutoff = DateTime.UtcNow.AddSeconds(-RestoreGuardSeconds * 2);
                            foreach (var key in _recentlyRestoredEpisodes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
                                _recentlyRestoredEpisodes.TryRemove(key, out _);
                            if (Plugin.Instance != null)
                                Plugin.Progress.IncrementBackupRestored();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pendingDeferredRestoreChecks.TryRemove(episodeId, out _);
                    LogError($"Error in deferred restore check for '{episode.Name}'", ex);
                }
            });
        }

        public static void SetLibraryManager(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public static void SetItemRepository(IItemRepository itemRepository)
        {
            _itemRepository = itemRepository;
        }

        public static void SetFfmpegManager(IFfmpegManager ffmpegManager)
        {
            _ffmpegManager = ffmpegManager;
        }

        public static void QueueEpisode(Episode episode, bool isManualDetection = false)
        {
            QueueEpisodeCore(episode, isManualDetection, allowSeasonExpansion: true);
        }

        private static void QueueEpisodeCore(Episode episode, bool isManualDetection, bool allowSeasonExpansion)
        {
            _cancellationRequested = false;

            var episodeId = episode.Id.ToString();
            LogDebug($"QueueEpisode called for: {episode.Name} (ID: {episodeId}), IsManual: {isManualDetection}");
            LogDebug($"Already processed: {_processedEpisodes.ContainsKey(episodeId)}, IsDryRun: {_queueingDryRun}, IsProcessing: {_isProcessing}");

            if (episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0)
            {
                LogDebug($"Skipping TV special: {episode.SeriesName} - {episode.Name} (Season {episode.ParentIndexNumber})");
                return;
            }

            if (allowSeasonExpansion && !_isBatchMode && episode.Series != null && SeasonUsesChromaprintBatch(episode))
            {
                LogInfo($"Hash detection enabled for '{episode.Series?.Name}' - queueing entire season instead of single episode");
                MarkTarget(episodeId);
                QueueSeasonForEpisode(episode, isManualDetection);
                return;
            }

            if (!isManualDetection)
            {
                if (_configuration != null && _itemRepository != null)
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var hasCreditsMarker = chapters.Any(c =>
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart";
                    });

                    LogDebug($"Episode has existing credits marker: {hasCreditsMarker}, ScheduledTaskOnlyProcessMissing: {_configuration.ScheduledTaskOnlyProcessMissing}");

                    if (hasCreditsMarker && _configuration.ScheduledTaskOnlyProcessMissing)
                    {
                        LogInfo($"Skipping episode {episode.Name} - already has credits marker (ScheduledTaskOnlyProcessMissing is enabled)");

                        if (Plugin.Instance != null)
                        {
                            var series = episode.Series;
                            var episodeKey = series != null
                                ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                                : episode.Name;
                            Plugin.Progress.SkipReasons[episodeKey] = "Already has credits marker";
                            Plugin.Progress.SkippedItems++;
                        }

                        return;
                    }

                    if (_configuration.UseEmbeddedChapterMarkersScheduled && _chapterMarkerService != null)
                    {
                        var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                        if (imported)
                        {
                            LogInfo($"Credits marker imported from embedded chapter for {episode.Name}");

                            if (Plugin.Instance != null)
                            {
                                var series = episode.Series;
                                var episodeKey = series != null
                                    ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                                    : episode.Name;
                                Plugin.Progress.SuccessDetails[episodeKey] = "(imported from embedded chapter)";
                                Plugin.Progress.EpisodeIds[episodeKey] = episode.Id.ToString();
                                Plugin.Progress.SuccessfulItems++;
                                Plugin.Progress.ProcessedItems++;
                            }

                            _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                            Plugin.TracerService?.MarkDetected(episodeId);
                            Plugin.PendingEpisodesService?.MarkProcessed(episodeId);

                            return;
                        }
                    }
                }
            }
            else
            {
                if (_processedEpisodes.ContainsKey(episodeId))
                {
                    _processedEpisodes.TryRemove(episodeId, out _);
                    LogDebug($"Manual detection: Cleared {episode.Name} from processed episodes cache");
                }
            }

            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes)
            {
                var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                
                bool shouldSkip = hasFailed && (!isManualDetection || !_configuration.IgnoreFailureMarkers);
                
                if (shouldSkip)
                {
                    var skipReason = isManualDetection 
                        ? "previously failed detection - enable 'Allow retry of failed episodes' in Settings to override"
                        : "previously failed detection (SkipPreviouslyFailedEpisodes is enabled)";
                    
                    LogInfo($"Skipping episode {episode.Name} - {skipReason}");
                    
                    if (Plugin.Instance != null)
                    {
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                        Plugin.Progress.SkippedItems++;
                    }
                    
                    return;
                }
                else if (hasFailed && isManualDetection && _configuration.IgnoreFailureMarkers)
                {
                    LogInfo($"Retrying previously failed episode {episode.Name} (IgnoreFailureMarkers is enabled)");
                }
            }

            if (!isManualDetection &&
                _configuration != null &&
                _configuration.SkipDetectionIfFileUnchanged &&
                !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                Plugin.CreditsBackupService != null)
            {
                if (!Plugin.CreditsBackupService.HasFileChanged(episode, _configuration.BackupFolderPath))
                {
                    LogInfo($"Skipping {episode.Name} — file unchanged since last detection");
                    if (Plugin.Instance != null)
                    {
                        var skipSeries = episode.Series;
                        var skipKey = skipSeries != null
                            ? $"{skipSeries.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SkipReasons[skipKey] = "File unchanged since last detection";
                        Plugin.Progress.SkippedItems++;
                    }
                    return;
                }
            }

            MarkTarget(episodeId);
            if (!_processingQueue.TryEnqueue(episode))
            {
                LogWarn($"Failed to enqueue episode: {episode.Name}");
                return;
            }
            LogInfo($"Queued episode: {episode.Name} (Queue size: {_processingQueue.Count})");

            if (Plugin.Instance == null)
            {
                LogWarn($"Episode queued but not starting processing: isProcessing={_isProcessing}, PluginInstance=False");
                return;
            }

            var startNow = false;
            lock (_queueStateLock)
            {
                if (!_isProcessing && !_startPending)
                {
                    Plugin.Progress.Reset();
                    Plugin.Progress.IsRunning = true;
                    Plugin.Progress.TotalItems = 1;
                    Plugin.Progress.StartTime = DateTime.Now;
                    _startPending = true;
                    startNow = true;
                }
                else
                {
                    Plugin.Progress.TotalItems++;
                }
            }

            if (startNow)
            {
                LogInfo("Starting ProcessQueue task");
                StartProcessQueueTask("ProcessQueue task failed");
            }
            else
            {
                LogInfo($"Added to existing processing queue (total: {Plugin.Progress.TotalItems})");
            }
        }

        private static void QueueSeasonForEpisode(Episode episode, bool isManualDetection)
        {
            if (_libraryManager == null || episode.Series == null || !episode.ParentIndexNumber.HasValue)
            {
                LogWarn($"Cannot queue season for episode - missing required data");
                QueueEpisodeCore(episode, isManualDetection, allowSeasonExpansion: false);
                return;
            }

            var series = episode.Series;
            var seasonNumber = episode.ParentIndexNumber.Value;

            LogInfo($"Fetching all episodes from {series.Name} Season {seasonNumber} for hash detection");

            var seasonEpisodes = GetSeasonEpisodes(episode);

            if (seasonEpisodes.Count == 0)
            {
                LogWarn($"No episodes found in season - queueing the single episode without reference episodes");
                QueueEpisodeCore(episode, isManualDetection, allowSeasonExpansion: false);
                return;
            }

            LogInfo($"Found {seasonEpisodes.Count} episodes in {series.Name} Season {seasonNumber} - queueing all for hash detection");
            
            if (isManualDetection)
            {
                QueueSeriesManual(seasonEpisodes, skipExistingMarkers: false, preserveTargets: true);
            }
            else
            {
                QueueSeries(seasonEpisodes, clearTargets: false);
            }
        }

        public static void QueueSeries(List<Episode> episodes, bool clearTargets = true)
        {
            var validEpisodes = episodes.Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0).ToList();
            var specialCount = episodes.Count - validEpisodes.Count;

            if (specialCount > 0)
            {
                LogInfo($"Filtered out {specialCount} specials from processing queue");
            }

            if (validEpisodes.Count == 0)
            {
                LogInfo("No valid episodes to process after filtering specials");
                return;
            }

            bool deferToRunningJob;
            lock (_queueStateLock)
            {
                deferToRunningJob = _isProcessing || _startPending;
                if (!deferToRunningJob)
                {
                    _startPending = true;

                    ClearCaches(drainQueue: false);

                    _batchDetectionCache.Clear();
                    System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
                    _batchDetectionCacheInsertTime.Clear();
                    System.Threading.Interlocked.Exchange(ref _batchDetectionCacheInsertTime, new ConcurrentDictionary<string, DateTime>());

                    if (clearTargets)
                    {
                        _singleEpisodeTargets.Clear();
                        _dryRunEpisodeIds.Clear();
                    }

                    _cancellationRequested = false;

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.Reset();
                        Plugin.Progress.IsRunning = true;
                        Plugin.Progress.TotalItems = validEpisodes.Count + _processingQueue.Count;
                        Plugin.Progress.StartTime = DateTime.Now;
                    }
                }
            }

            if (deferToRunningJob)
            {
                var deferredCount = 0;
                foreach (var episode in validEpisodes)
                {
                    var episodeId = episode.Id.ToString();
                    if (_processedEpisodes.ContainsKey(episodeId))
                    {
                        _processedEpisodes.TryRemove(episodeId, out _);
                    }
                    if (clearTargets)
                        MarkTarget(episodeId);
                    if (_processingQueue.TryEnqueue(episode))
                    {
                        deferredCount++;
                    }
                    else
                    {
                        LogWarn($"Failed to defer episode {episode.Name} - queue is full ({_processingQueue.Count})");
                    }
                }

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.TotalItems += deferredCount;
                }

                LogInfo($"Detection already in progress - deferred {deferredCount} episode(s) to run after the current job finishes");
                return;
            }

            if (_detectionCoordinator == null && _logger != null && _configuration != null)
            {
                LogInfo("Initializing DetectionCoordinator");
                _detectionCoordinator = new DetectionCoordinator(_logger, _configuration);
            }

            var queuedCount = 0;
            foreach (var episode in validEpisodes)
            {
                var episodeId = episode.Id.ToString();
                if (_processedEpisodes.ContainsKey(episodeId))
                {
                    _processedEpisodes.TryRemove(episodeId, out _);
                }
                if (clearTargets)
                    MarkTarget(episodeId);
                if (_processingQueue.TryEnqueue(episode))
                {
                    queuedCount++;
                }
                else
                {
                    LogWarn($"Failed to enqueue episode {episode.Name} - queue is full ({_processingQueue.Count})");
                }
            }

            if (queuedCount < validEpisodes.Count)
            {
                LogWarn($"Only {queuedCount} of {validEpisodes.Count} episodes were queued - {validEpisodes.Count - queuedCount} were dropped");
            }

            LogInfo($"Queued {queuedCount} episodes for processing (forced reprocess). Queue size: {_processingQueue.Count}");
            LogInfo($"Service running: {_isRunning}, Already processing: {_isProcessing}");
            StartProcessQueueTask("ProcessQueue task failed");
        }

        public static void CancelProcessing()
        {
            LogInfo("Cancellation requested for credits detection");

            _cancellationRequested = true;
            
            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            DetectionCoordinator.CancelAllDetections();
            
            var killedProcesses = Utilities.FFmpegHelper.KillHungProcesses(0);
            if (killedProcesses > 0)
            {
                LogInfo($"Killed {killedProcesses} running ffmpeg processes during cancellation");
            }

            var clearedCount = DrainProcessingQueue();

            _processedEpisodes.Clear();
            System.Threading.Interlocked.Exchange(ref _processedEpisodes, new ConcurrentDictionary<string, DateTime>());
            
            _episodeStatusMessages.Clear();
            System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, ConcurrentQueue<string>>());
            
            EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

            LogInfo($"Queue cleared: {clearedCount} items removed, processed cache cleared");
            ResetProgressToCancelling();
        }

        public static int ClearQueue()
        {
            LogInfo("Clearing processing queue");

            var clearedCount = DrainProcessingQueue();

            _isProcessing = false;
            _cancellationRequested = false;

            LogInfo($"Queue cleared: {clearedCount} items removed, flags reset");

            return clearedCount;
        }

        private const int PlaybackPollSeconds = 15;
        private const string PausedForPlaybackText = "Paused while media is playing - detection resumes automatically";
        private static int _playbackWaiters;

        public static bool IsPlaybackActive()
        {
            var sessionManager = Plugin.SessionManager;
            if (sessionManager == null)
                return false;

            try
            {
                return sessionManager.Sessions.Any(s =>
                    s.NowPlayingItem != null &&
                    ((s.PlayState != null && !s.PlayState.IsPaused) || s.TranscodingInfo != null));
            }
            catch (Exception ex)
            {
                LogDebug($"Could not read playback sessions: {ex.Message}");
                return false;
            }
        }

        public static async Task WaitWhilePlaybackActiveAsync(CancellationToken cancellationToken)
        {
            if (_configuration == null || !_configuration.PauseDetectionDuringPlayback || !IsPlaybackActive())
                return;

            var pausedAt = DateTime.UtcNow;
            if (Interlocked.Increment(ref _playbackWaiters) == 1)
                LogInfo("Media playback detected - pausing credits detection until playback stops");

            try
            {
                if (Plugin.Instance != null)
                    Plugin.Progress.CurrentItem = PausedForPlaybackText;

                while (_isRunning &&
                       !_cancellationRequested &&
                       !cancellationToken.IsCancellationRequested &&
                       _configuration?.PauseDetectionDuringPlayback == true &&
                       IsPlaybackActive())
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(PlaybackPollSeconds), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _playbackWaiters) == 0)
                {
                    LogInfo($"Playback stopped - resuming credits detection (paused for {(DateTime.UtcNow - pausedAt).TotalMinutes:F1} minute(s))");
                    if (Plugin.Instance != null && Plugin.Progress.CurrentItem == PausedForPlaybackText)
                        Plugin.Progress.CurrentItem = "Resuming...";
                }
            }
        }

        public static async Task WaitForOtherPluginsIfNeeded()
        {
            await WaitWhilePlaybackActiveAsync(CurrentCancellationToken).ConfigureAwait(false);

            if (_pluginCoordination == null)
                return;

            try
            {
                await _pluginCoordination.WaitForOtherPlugins().ConfigureAwait(false);
            }
            catch (Exception coordEx)
            {
                LogDebug($"Plugin coordination check failed: {coordEx.Message}");
            }
        }

        public static void ResetForScheduledTask()
        {
            _cancellationRequested = false;
            _activeRunDryIds = null;
            _activeRunTargets = null;
            var newCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cancellationTokenSource, newCts)?.Dispose();
            LogInfo("Cancellation state reset for scheduled task run");
        }

        public static void SetActiveRunTargets(IEnumerable<string>? episodeIds)
        {
            _activeRunTargets = episodeIds == null ? null : new HashSet<string>(episodeIds);
        }

        public static bool TryBeginScheduledRun()
        {
            lock (_queueStateLock)
            {
                if (_processingSemaphore == null || !_processingSemaphore.Wait(0))
                    return false;

                _isProcessing = true;
            }

            if (_configuration != null && _configuration.EnableLogToFile && !IsDebugMode)
                StartDebugMode();

            return true;
        }

        public static void EndScheduledRun()
        {
            _activeRunTargets = null;

            var startFollowUp = false;
            lock (_queueStateLock)
            {
                _isProcessing = false;
                ReleaseSemaphore(_processingSemaphore);

                if (_processingQueue.Count > 0 && _isRunning)
                {
                    _startPending = true;
                    startFollowUp = true;
                }
            }

            if (IsDebugMode)
                ScheduleDebugLogCleanup();

            if (startFollowUp)
            {
                LogInfo($"Queue received {_processingQueue.Count} episode(s) during scheduled run - starting follow-up cycle");
                StartProcessQueueTask("ProcessQueue follow-up after scheduled task failed");
            }
            else
            {
                DisposeRetiredServicesIfIdle();
            }
        }

        private static void ResetProgressToCancelling()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = "Cancelling...";
            }
        }

        private static void RunAsDryRunRequest(Action queueAction)
        {
            var previous = _queueingDryRun;
            _queueingDryRun = true;
            try
            {
                queueAction();
            }
            finally
            {
                _queueingDryRun = previous;
            }
        }

        public static void QueueEpisodeManual(Episode episode, bool skipExistingMarkers = false)
        {
            var series = episode.Series;
            if (series != null && episode.ParentIndexNumber.HasValue)
            {
                var seriesId = series.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber.Value;
                EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (manual detection)");
            }

            _isManualDetectionRun = true;

            if (_configuration?.UseEmbeddedChapterMarkersManual == true && _chapterMarkerService != null)
            {
                var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                if (imported)
                {
                    LogInfo($"Skipping episode {episode.Name} - credits marker imported from embedded chapter");

                    if (Plugin.Instance != null)
                    {
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SuccessDetails[episodeKey] = "(imported from embedded chapter)";
                        Plugin.Progress.SuccessfulItems++;
                    }

                    return;
                }
            }

            if (skipExistingMarkers && _itemRepository != null)
            {
                var chapters = _itemRepository.GetChapters(episode);
                var hasCreditsMarker = chapters.Any(c => 
                {
                    var markerType = GetMarkerType(c);
                    return markerType == "CreditsStart";
                });

                if (hasCreditsMarker)
                {
                    LogInfo($"Skipping episode {episode.Name} - already has credits marker (manual skip enabled)");
                    
                    if (Plugin.Instance != null)
                    {
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SuccessDetails[episodeKey] = "(already exists)";
                        Plugin.Progress.SuccessfulItems++;
                    }
                    
                    return;
                }
            }

            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
            {
                var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                if (hasFailed)
                {
                    LogInfo($"Skipping episode {episode.Name} - previously failed detection");
                    
                    if (Plugin.Instance != null)
                    {
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                        Plugin.Progress.SkippedItems++;
                    }
                    
                    return;
                }
            }

            QueueEpisode(episode, isManualDetection: true);
        }

        public static void QueueSeriesManual(List<Episode> episodes, bool skipExistingMarkers = false, bool? ignoreFailureMarkers = null, bool preserveTargets = false)
        {
            if (episodes.Count > 0)
            {
                var firstEpisode = episodes.First();
                var series = firstEpisode.Series;
                if (series != null && firstEpisode.ParentIndexNumber.HasValue)
                {
                    var seriesId = series.Id.ToString();
                    var seasonNumber = firstEpisode.ParentIndexNumber.Value;
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                    LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (manual series detection)");
                }
            }

            var episodesToQueue = episodes.ToList();
            var skippedEpisodes = new List<string>();
            var preQueueSkips = new List<KeyValuePair<string, string>>();
            var preQueueImports = new List<string>();

            _isManualDetectionRun = true;

            if (_configuration?.UseEmbeddedChapterMarkersManual == true && _chapterMarkerService != null)
            {
                var remainingAfterImport = new List<Episode>();
                foreach (var episode in episodesToQueue)
                {
                    var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                    if (imported)
                    {
                        var series2 = episode.Series;
                        var episodeKey = series2 != null
                            ? $"{series2.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        LogInfo($"Skipping episode {episode.Name} - credits marker imported from embedded chapter");
                        skippedEpisodes.Add($"{episodeKey} (imported from embedded chapter)");
                        preQueueImports.Add(episodeKey);
                    }
                    else
                    {
                        remainingAfterImport.Add(episode);
                    }
                }
                episodesToQueue = remainingAfterImport;
            }

            if (skipExistingMarkers && _itemRepository != null)
            {
                episodesToQueue = episodesToQueue.Where(episode =>
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var hasCreditsMarker = chapters.Any(c => 
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart";
                    });

                    if (hasCreditsMarker)
                    {
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        skippedEpisodes.Add($"{episodeKey} (has marker)");
                        preQueueSkips.Add(new KeyValuePair<string, string>(episodeKey, "Already has credits marker"));
                        return false;
                    }
                    return true;
                }).ToList();
            }

            var effectiveIgnoreFailureMarkers = ignoreFailureMarkers ?? _configuration?.IgnoreFailureMarkers ?? false;
            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !effectiveIgnoreFailureMarkers)
            {
                var filteredByFailure = new List<Episode>();
                
                foreach (var episode in episodesToQueue)
                {
                    var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                    
                    if (hasFailed)
                    {
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        skippedEpisodes.Add($"{episodeKey} (previously failed)");
                        preQueueSkips.Add(new KeyValuePair<string, string>(episodeKey, "Previously failed detection"));
                    }
                    else
                    {
                        filteredByFailure.Add(episode);
                    }
                }
                
                episodesToQueue = filteredByFailure;
            }
            else if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && effectiveIgnoreFailureMarkers)
            {
                var retryCount = episodesToQueue.Count(e => 
                    e.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true");
                
                if (retryCount > 0)
                {
                    LogInfo($"Retrying {retryCount} previously failed episodes (IgnoreFailureMarkers is enabled)");
                }
            }

            if (skippedEpisodes.Count > 0)
            {
                LogInfo($"Skipped {skippedEpisodes.Count} episode(s):");
                foreach (var skipped in skippedEpisodes)
                {
                    LogInfo($"  - {skipped}");
                }
            }

            if (episodesToQueue.Count == 0)
            {
                LogInfo("No episodes to process - all filtered out by skip settings");
                if (Plugin.Instance != null && !_isProcessing)
                {
                    Plugin.Progress.Reset();
                    Plugin.Progress.StartTime = DateTime.Now;
                    Plugin.Progress.EndTime = DateTime.Now;
                    Plugin.Progress.CurrentItem = "Complete";
                    RecordPreQueueResults(preQueueSkips, preQueueImports);
                }
                return;
            }

            QueueSeries(episodesToQueue, clearTargets: !preserveTargets);
            RecordPreQueueResults(preQueueSkips, preQueueImports);
        }

        private static void RecordPreQueueResults(List<KeyValuePair<string, string>> skips, List<string> imports)
        {
            if (Plugin.Instance == null)
                return;

            foreach (var skip in skips)
                Plugin.Progress.SkipReasons[skip.Key] = skip.Value;
            foreach (var key in imports)
                Plugin.Progress.SuccessDetails[key] = "(imported from embedded chapter)";

            Plugin.Progress.SkippedItems += skips.Count;
            Plugin.Progress.SuccessfulItems += imports.Count;
        }

        public static void QueueEpisodeDryRun(Episode episode, bool skipExistingMarkers = false)
        {
            _isManualDetectionRun = true;
            
            var series = episode.Series;
            if (series != null && episode.ParentIndexNumber.HasValue)
            {
                var seriesId = series.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber.Value;
                EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run)");
            }
            
            if (skipExistingMarkers)
            {
                RunAsDryRunRequest(() => QueueEpisodeManual(episode, skipExistingMarkers: true));
            }
            else
            {
                RunAsDryRunRequest(() => QueueEpisode(episode, isManualDetection: true));
            }
        }

        public static void QueueSeriesDryRun(List<Episode> episodes, bool skipExistingMarkers = false)
        {
            _isManualDetectionRun = true;
            
            if (episodes.Count > 0)
            {
                var firstEpisode = episodes.First();
                var series = firstEpisode.Series;
                if (series != null && firstEpisode.ParentIndexNumber.HasValue)
                {
                    var seriesId = series.Id.ToString();
                    var seasonNumber = firstEpisode.ParentIndexNumber.Value;
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                    LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run series)");
                }
            }
            
            if (skipExistingMarkers)
            {
                RunAsDryRunRequest(() => QueueSeriesManual(episodes, skipExistingMarkers: true));
            }
            else
            {
                RunAsDryRunRequest(() => QueueSeries(episodes));
            }
        }

        public static void QueueEpisodeDryRunDebug(Episode episode, bool skipExistingMarkers = false)
        {
            _isManualDetectionRun = true;
            StartDebugMode();
            
            var series = episode.Series;
            if (series != null && episode.ParentIndexNumber.HasValue)
            {
                var seriesId = series.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber.Value;
                EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run debug)");
            }
            
            if (skipExistingMarkers)
            {
                RunAsDryRunRequest(() => QueueEpisodeManual(episode, skipExistingMarkers: true));
            }
            else
            {
                RunAsDryRunRequest(() => QueueEpisode(episode, isManualDetection: true));
            }
        }

        public static void QueueSeriesDryRunDebug(List<Episode> episodes, bool skipExistingMarkers = false)
        {
            _isManualDetectionRun = true;
            StartDebugMode();
            
            if (episodes.Count > 0)
            {
                var firstEpisode = episodes.First();
                var series = firstEpisode.Series;
                if (series != null && firstEpisode.ParentIndexNumber.HasValue)
                {
                    var seriesId = series.Id.ToString();
                    var seasonNumber = firstEpisode.ParentIndexNumber.Value;
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                    LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run debug series)");
                }
            }
            
            if (skipExistingMarkers)
            {
                RunAsDryRunRequest(() => QueueSeriesManual(episodes, skipExistingMarkers: true));
            }
            else
            {
                RunAsDryRunRequest(() => QueueSeries(episodes));
            }
        }

        private static void StartDebugMode()
        {
            _debugLogger?.StartDebugMode();
        }

        public static string GetDebugLog()
        {
            return _debugLogger?.GetDebugLog() ?? "No debug log available. Debug mode was not enabled.";
        }

        public static void CleanupDebugLog()
        {
            _debugLogger?.Cleanup();
        }

        private static void ScheduleDebugLogCleanup()
        {
            _debugLogger?.ScheduleDebugLogCleanup();
        }

        public static List<EpisodeMarkerInfo> GetSeriesMarkers(System.Collections.Generic.List<Episode> episodes)
        {
            return _chapterMarkerService?.GetSeriesMarkers(episodes) ?? new List<EpisodeMarkerInfo>();
        }

        public static ChapterMarkerService? GetChapterMarkerService()
        {
            return _chapterMarkerService;
        }

        private static async Task ProcessQueue()
        {
            LogInfo($"ProcessQueue started. Queue count: {_processingQueue.Count}");

            var semaphore = _processingSemaphore;
            if (semaphore == null || !await semaphore.WaitAsync(0))
            {
                lock (_queueStateLock)
                {
                    _startPending = false;
                }
                LogInfo("ProcessQueue: already processing, skipping");
                return;
            }

            lock (_queueStateLock)
            {
                _isProcessing = true;
                _startPending = false;
            }
            LogInfo("ProcessQueue: acquired semaphore, starting processing");

            if (_configuration != null && _configuration.EnableLogToFile && !IsDebugMode)
                StartDebugMode();

            var isManualRun = _isManualDetectionRun;
            _isManualDetectionRun = false;

            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = "Preparing...";
            }

            var newCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cancellationTokenSource, newCts)?.Dispose();

            if (Plugin.Instance != null)
            {
                Plugin.Progress.FailureReasons?.Clear();
            }

            var processedCount = 0;
            var lastCleanupProcessedCount = 0;
            var cycleIsDryRun = false;
            var startFollowUp = false;
            try
            {
                var allEpisodes = new List<Episode>();
                while (_processingQueue.TryDequeue(out var episode))
                {
                    if (episode != null)
                    {
                        allEpisodes.Add(episode);
                    }
                }

                LogInfo($"Collected {allEpisodes.Count} episodes from queue");

                var uniqueEpisodes = allEpisodes.GroupBy(e => e.Id).Select(g => g.First()).ToList();
                var duplicateCount = allEpisodes.Count - uniqueEpisodes.Count;
                if (duplicateCount > 0)
                {
                    allEpisodes = uniqueEpisodes;
                    if (Plugin.Instance != null)
                        Plugin.Progress.TotalItems = Math.Max(0, Plugin.Progress.TotalItems - duplicateCount);
                    LogInfo($"Removed {duplicateCount} duplicate queued episode(s)");
                }

                if (_singleEpisodeTargets.IsEmpty)
                {
                    _activeRunTargets = null;
                }
                else
                {
                    var runTargets = new HashSet<string>();
                    foreach (var ep in allEpisodes)
                    {
                        var id = ep.Id.ToString();
                        if (_singleEpisodeTargets.TryRemove(id, out _))
                            runTargets.Add(id);
                    }
                    _activeRunTargets = runTargets.Count > 0 ? runTargets : null;
                }

                var cycleDryIds = new HashSet<string>();
                foreach (var ep in allEpisodes)
                {
                    var id = ep.Id.ToString();
                    if (_dryRunEpisodeIds.TryRemove(id, out _))
                        cycleDryIds.Add(id);
                }
                _activeRunDryIds = cycleDryIds.Count > 0 ? cycleDryIds : null;

                var cycleTargetIds = _activeRunTargets ?? new HashSet<string>(allEpisodes.Select(e => e.Id.ToString()));
                cycleIsDryRun = cycleTargetIds.Count > 0 && cycleTargetIds.All(cycleDryIds.Contains);

                var episodesBySeason = allEpisodes
                    .Where(e => e.Series != null && e.ParentIndexNumber.HasValue)
                    .GroupBy(e => new { SeriesId = e.Series!.Id.ToString(), SeasonNumber = e.ParentIndexNumber!.Value })
                    .ToList();

                LogInfo($"Grouped {allEpisodes.Count} episodes into {episodesBySeason.Count} seasons for batch processing");

                RuleMatchingService? queueRuleService = (_configuration != null && _logger != null && _configuration.DetectionRules?.Count > 0)
                    ? new RuleMatchingService(_logger, _configuration)
                    : null;

                foreach (var seasonGroup in episodesBySeason)
                {
                    if (!_isRunning || _cancellationRequested)
                    {
                        LogInfo("Processing cancelled");
                        break;
                    }

                    await WaitForOtherPluginsIfNeeded().ConfigureAwait(false);

                    if (!_isRunning || _cancellationRequested)
                    {
                        LogInfo("Processing cancelled during coordination wait");
                        break;
                    }

                    var seasonEpisodes = seasonGroup.OrderBy(e => e.IndexNumber).ToList();
                    var firstEpisode = seasonEpisodes.First();
                    var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";

                    if (queueRuleService != null)
                    {
                        var preCheckConfig = queueRuleService.GetEffectiveConfiguration(firstEpisode);
                        if (preCheckConfig.DisableDetection)
                        {
                            var preCheckRuleName = queueRuleService.GetMatchingRuleName(firstEpisode);
                            LogInfo($"[RuleMatching] Detection disabled by rule for '{seriesName}' Season {seasonGroup.Key.SeasonNumber} - skipping {seasonEpisodes.Count} episode(s)");
                            if (Plugin.Instance != null)
                            {
                                foreach (var ep in seasonEpisodes)
                                {
                                    var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                                    Plugin.Progress.SkipReasons[epKey] = "Detection disabled by rule";
                                    if (!string.IsNullOrEmpty(preCheckRuleName))
                                        Plugin.Progress.AppliedRules[epKey] = preCheckRuleName!;
                                    Plugin.Progress.IncrementSkipped();
                                    MarkPendingResolved(ep);
                                }
                            }
                            processedCount += seasonEpisodes.Count;
                            continue;
                        }
                    }

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CurrentItem = $"Preparing: {seriesName} Season {seasonGroup.Key.SeasonNumber} ({seasonEpisodes.Count} episodes)";
                    }

                    LogInfo($"Processing season batch: {seriesName} Season {seasonGroup.Key.SeasonNumber} ({seasonEpisodes.Count} episodes)");

                    _isBatchMode = true;
                    try
                    {
                        await ProcessSeasonBatch(seasonEpisodes, _cancellationTokenSource?.Token ?? CancellationToken.None, isManualRun);
                    }
                    finally
                    {
                        _isBatchMode = false;
                    }

                    processedCount += seasonEpisodes.Count;

                    if (processedCount - lastCleanupProcessedCount >= 20)
                    {
                        lastCleanupProcessedCount = processedCount;
                        CleanupBatchDetectionCache();
                        CleanupOldProcessedEpisodes();
                        
                        var killedCount = Utilities.FFmpegHelper.KillHungProcesses(450);
                        if (killedCount > 0)
                        {
                            LogWarn($"Killed {killedCount} hung ffmpeg processes (older than 450 seconds)");
                        }
                    }

                    await Task.Delay(1000);
                }

                if (Plugin.Instance != null)
                {
                    if (_cancellationRequested)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Cancelled";

                        if (IsDebugMode)
                        {
                            ScheduleDebugLogCleanup();
                        }
                    }
                    else if (_processingQueue.Count == 0)
                    {
                        var savedEpisodes = allEpisodes.Where(e => !IsDryRun(e.Id.ToString())).ToList();
                        await RunAutoBackupForEpisodes(savedEpisodes, isDryRun: false).ConfigureAwait(false);

                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = cycleIsDryRun ? "Dry Run Complete" : "Complete";
                        Plugin.Progress.CurrentItemProgress = 100;
                        LogInfo($"Processing complete: {Plugin.Progress.SuccessfulItems} succeeded, {Plugin.Progress.FailedItems} failed");
                        
                        EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

                        if (_configuration != null && _configuration.EnableAutoDetection && !cycleIsDryRun && Plugin.NotificationManager != null)
                        {
                            try
                            {
                                var duration = Plugin.Progress.EndTime.HasValue && Plugin.Progress.StartTime.HasValue
                                    ? Plugin.Progress.EndTime.Value - Plugin.Progress.StartTime.Value
                                    : TimeSpan.Zero;

                                var failedEpisodes = Plugin.Progress.FailureReasons?.Select(kvp => $"{kvp.Key}: {kvp.Value}").ToList() ?? new List<string>();
                                var successfulSeries = Plugin.Progress.SuccessDetails.Keys
                                    .Select(k => k.Split(new[] { " S" }, StringSplitOptions.None)[0])
                                    .Distinct()
                                    .ToList();

                                if (_logger != null)
                                {
                                    var notificationService = new NotificationService(_logger, Plugin.NotificationManager, _configuration);
                                    notificationService.SendAutoDetectionNotification(
                                        Plugin.Progress.SuccessfulItems,
                                        Plugin.Progress.FailedItems,
                                        Plugin.Progress.ProcessedItems,
                                        failedEpisodes,
                                        successfulSeries,
                                        duration
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                LogError("Failed to send auto-detection notification", ex);
                            }
                        }

                        if (IsDebugMode)
                        {
                            ScheduleDebugLogCleanup();
                        }
                    }
                }
            }
            finally
            {
                _activeRunTargets = null;
                _activeRunDryIds = null;

                var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
                cts?.Dispose();

                lock (_queueStateLock)
                {
                    _isProcessing = false;
                    ReleaseSemaphore(semaphore);

                    if (_processingQueue.Count > 0 && _isRunning && !_cancellationRequested)
                    {
                        _startPending = true;
                        startFollowUp = true;
                    }
                }

                if (startFollowUp)
                {
                    LogInfo($"Queue received {_processingQueue.Count} episode(s) during processing — starting follow-up cycle");
                    StartProcessQueueTask("ProcessQueue follow-up task failed");
                }
                else
                {
                    DisposeRetiredServicesIfIdle();
                }
            }
        }

        public static async Task RunAutoBackupForEpisodes(IEnumerable<Episode> episodes, bool isDryRun)
        {
            if (_configuration == null ||
                !_configuration.EnableAutoBackupAfterDetection ||
                string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) ||
                isDryRun ||
                Plugin.CreditsBackupService == null)
            {
                return;
            }

            try
            {
                var distinctSeries = episodes
                    .Where(e => e.Series != null)
                    .GroupBy(e => e.Series!.Id)
                    .Select(g => g.First().Series!)
                    .ToList();

                foreach (var series in distinctSeries)
                {
                    var seriesEpisodes = _libraryManager?.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true,
                        AncestorIds = new[] { series.InternalId }
                    }).OfType<Episode>().ToList() ?? new List<Episode>();

                    await Plugin.CreditsBackupService.SaveSeriesBackupToFile(
                        series,
                        seriesEpisodes,
                        _configuration.BackupFolderPath,
                        _configuration.MaxScheduledBackups > 0 ? _configuration.MaxScheduledBackups : 10).ConfigureAwait(false);
                }
            }
            catch (Exception backupEx)
            {
                LogError("Auto-backup after detection failed", backupEx);
            }
        }

        public static async Task ProcessSeasonBatch(List<Episode> seasonEpisodes, CancellationToken cancellationToken, bool isManualRun = false, bool skipMarkerPreCheck = false)
        {
            if (_episodeProcessor == null || _configuration == null || seasonEpisodes.Count == 0)
                return;

            var firstEpisode = seasonEpisodes.First();
            var seriesId = firstEpisode.Series?.Id.ToString() ?? string.Empty;
            var seasonNumber = firstEpisode.ParentIndexNumber ?? 0;
            var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";

            var referenceEpisodes = new List<Episode>();
            var runTargets = _activeRunTargets;
            if (runTargets != null)
            {
                referenceEpisodes = seasonEpisodes.Where(ep => !runTargets.Contains(ep.Id.ToString())).ToList();
                if (referenceEpisodes.Count > 0)
                {
                    seasonEpisodes = seasonEpisodes.Where(ep => runTargets.Contains(ep.Id.ToString())).ToList();
                    LogDebug($"[SingleEpisodeTarget] {referenceEpisodes.Count} episode(s) in {seriesName} Season {seasonNumber} are only used for audio comparison");

                    if (Plugin.Instance != null)
                    {
                        foreach (var ep in referenceEpisodes)
                        {
                            var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                            Plugin.Progress.SkipReasons[epKey] = "Only needed for audio comparison";
                            Plugin.Progress.IncrementSkipped();
                        }
                    }

                    if (seasonEpisodes.Count == 0)
                        return;
                }
            }

            if (!_configuration.EnableStrmFileProcessing)
            {
                referenceEpisodes = referenceEpisodes.Where(ep => !IsStrmFile(ep.Path)).ToList();

                var strmEpisodes = seasonEpisodes.Where(ep => IsStrmFile(ep.Path)).ToList();
                if (strmEpisodes.Count > 0)
                {
                    LogInfo($"Skipping {strmEpisodes.Count} .strm episode(s) in {seriesName} Season {seasonNumber} - .strm processing disabled");
                    foreach (var ep in strmEpisodes)
                    {
                        var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[epKey] = "Strm file (processing disabled)";
                            Plugin.Progress.IncrementSkipped();
                        }
                        MarkPendingResolved(ep);
                    }

                    seasonEpisodes = seasonEpisodes.Except(strmEpisodes).ToList();
                    if (seasonEpisodes.Count == 0)
                    {
                        return;
                    }
                }
            }

            LogInfo($"Starting batch processing for {seriesName} Season {seasonNumber} ({seasonEpisodes.Count} episodes)");

            var ruleMatchingService = _logger != null ? new RuleMatchingService(_logger, _configuration) : null;
            var effectiveConfig = ruleMatchingService != null
                ? ruleMatchingService.GetEffectiveConfiguration(firstEpisode)
                : _configuration;

            var matchedRuleName = (effectiveConfig != _configuration && ruleMatchingService != null)
                ? ruleMatchingService.GetMatchingRuleName(firstEpisode)
                : null;

            if (effectiveConfig != _configuration)
            {
                LogInfo($"Using rule-based configuration for series '{seriesName}'");
            }

            if (effectiveConfig.DisableDetection)
            {
                LogInfo($"Detection disabled by rule for series '{seriesName}', skipping {seasonEpisodes.Count} episode(s)");
                if (Plugin.Instance != null)
                {
                    foreach (var ep in seasonEpisodes)
                    {
                        var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                        Plugin.Progress.SkipReasons[epKey] = "Detection disabled by rule";
                        if (!string.IsNullOrEmpty(matchedRuleName))
                            Plugin.Progress.AppliedRules[epKey] = matchedRuleName!;
                        Plugin.Progress.IncrementSkipped();
                    }
                }
                foreach (var ep in seasonEpisodes)
                    MarkPendingResolved(ep);
                return;
            }

            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
            {
                var beforeCount = seasonEpisodes.Count;
                seasonEpisodes = seasonEpisodes.Where(ep =>
                {
                    var hasFailed = ep.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                    if (hasFailed)
                    {
                        var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[epKey] = "Previously failed detection";
                            Plugin.Progress.IncrementSkipped();
                        }
                        MarkPendingResolved(ep);
                    }
                    return !hasFailed;
                }).ToList();

                var skippedCount = beforeCount - seasonEpisodes.Count;
                if (skippedCount > 0)
                    LogInfo($"Skipped {skippedCount} previously failed episode(s) in {seriesName} Season {seasonNumber}");

                if (seasonEpisodes.Count == 0)
                {
                    LogInfo($"All episodes in {seriesName} Season {seasonNumber} were previously failed — nothing to process");
                    return;
                }
            }

            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = $"Preparing: {seriesName} Season {seasonNumber} ({seasonEpisodes.Count} episodes)";
            }

            if (_configuration != null && _configuration.EnableTheIntroDB && _theIntroDbService != null && Plugin.ChapterMarkerService != null)
            {
                var detectionNeeded = new List<Episode>();
                var introDbHits = 0;

                if (Plugin.Instance != null)
                    Plugin.Progress.CurrentItem = $"Querying TheIntroDB: {seriesName} Season {seasonNumber}";

                foreach (var ep in seasonEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested)
                        break;

                    var epId = ep.Id.ToString();
                    var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";

                    if (!skipMarkerPreCheck && !isManualRun && _configuration.ScheduledTaskOnlyProcessMissing && _itemRepository != null)
                    {
                        var existingChapters = _itemRepository.GetChapters(ep);
                        var alreadyHasMarker = existingChapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;
                        if (alreadyHasMarker)
                        {
                            LogInfo($"[TheIntroDB pre-filter] Skipping {ep.Name} - already has credits marker");
                            if (Plugin.Instance != null)
                            {
                                Plugin.Progress.SkipReasons[epKey] = "Already has credits marker";
                                Plugin.Progress.IncrementSkipped();
                            }
                            MarkPendingResolved(ep);
                            continue;
                        }
                    }

                    var ts = await _theIntroDbService.GetCreditsTimestamp(ep, cancellationToken).ConfigureAwait(false);
                    if (!ts.HasValue || ts.Value <= 0)
                    {
                        detectionNeeded.Add(ep);
                        continue;
                    }

                    double creditsStart = ts.Value;
                    double epDuration = ep.RunTimeTicks.HasValue && ep.RunTimeTicks.Value > 0
                        ? ep.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                        : 0;
                    double finalTimestamp = creditsStart + effectiveConfig.TimestampOffsetSeconds;

                    if (finalTimestamp < 0 || (epDuration > 0 && finalTimestamp >= epDuration))
                    {
                        LogWarn($"[TheIntroDB pre-filter] Timestamp out of range for {ep.Name} ({FormatTime(finalTimestamp)}), falling back to detection");
                        detectionNeeded.Add(ep);
                        continue;
                    }

                    bool effectiveDryRun = IsDryRun(epId);

                    var postCreditsNote = string.Empty;
                    PostCreditsCheckResult? postCredits = null;
                    if (_episodeProcessor != null)
                    {
                        try
                        {
                            postCredits = await _episodeProcessor.CheckPostCreditsSceneAsync(ep, Utilities.FFmpegHelper.NormalizeFilePath(ep.Path), creditsStart, finalTimestamp, epDuration, effectiveDryRun).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        postCreditsNote = postCredits.Note;
                        finalTimestamp = postCredits.MarkerSeconds;
                    }

                    if (!effectiveDryRun)
                    {
                        var savedMarkers = _episodeProcessor != null
                            ? _episodeProcessor.SaveDetectionMarkers(ep, finalTimestamp, postCredits, out var introDbSaveError)
                            : Plugin.ChapterMarkerService.TrySaveCreditsMarker(ep, finalTimestamp, out introDbSaveError);
                        if (!savedMarkers)
                        {
                            LogWarn($"[TheIntroDB pre-filter] Could not save marker for {ep.Name} ({introDbSaveError}), falling back to detection");
                            detectionNeeded.Add(ep);
                            continue;
                        }

                        if (_episodesWithoutCreditsMarker.TryRemove(epId, out _))
                            await RecordNoCreditsMarkerInBackup(ep).ConfigureAwait(false);

                        if (ep.ProviderIds != null && ep.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                        {
                            ep.ProviderIds.Remove("EmbyCredits.Fail");
                            _libraryManager?.UpdateItem(ep, ep.Parent, ItemUpdateType.MetadataEdit, null!);
                        }
                    }

                    LogInfo($"✓ [{(effectiveDryRun ? "DRY RUN" : "SAVED")}] [TheIntroDB] Credits at {FormatTime(creditsStart)}, saved at {FormatTime(finalTimestamp)} for {ep.Name}");
                    introDbHits++;

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.StartProcessingItem($"{seriesName} - S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2} - {ep.Name}", "TheIntroDB");
                        Plugin.Progress.CompleteProcessingItem(true);

                        var offsetSeconds = effectiveConfig.TimestampOffsetSeconds;
                        var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(epDuration)} [TheIntroDB] - Community database timestamp";
                        if (!string.IsNullOrEmpty(postCreditsNote))
                            successDetail += $" | {postCreditsNote}";
                        if (offsetSeconds != 0)
                            successDetail += $" (offset: {offsetSeconds:+0;-0}s, final: {FormatTime(finalTimestamp)})";

                        Plugin.Progress.SuccessDetails[epKey] = successDetail;
                        Plugin.Progress.ConfidenceScores[epKey] = 1.0;
                        Plugin.Progress.EpisodeIds[epKey] = epId;
                        if (!string.IsNullOrEmpty(matchedRuleName))
                            Plugin.Progress.AppliedRules[epKey] = matchedRuleName!;

                        if (isManualRun && Plugin.Instance.Configuration.EnableThumbnailGeneration)
                        {
                            try
                            {
                                var thumbnailPath = await GenerateThumbnail(ep, creditsStart, epKey);
                                if (!string.IsNullOrEmpty(thumbnailPath))
                                    Plugin.Progress.ThumbnailPaths[epKey] = thumbnailPath;
                            }
                            catch (Exception thumbEx)
                            {
                                LogDebug($"Failed to generate thumbnail for {epKey}: {thumbEx.Message}");
                            }
                        }
                    }

                    if (!effectiveDryRun)
                    {
                        _processedEpisodes.TryAdd(epId, DateTime.UtcNow);
                        Plugin.TracerService?.MarkDetected(epId);
                        Plugin.PendingEpisodesService?.MarkProcessed(epId);
                    }
                }

                if (introDbHits > 0)
                    LogInfo($"[TheIntroDB] Found timestamps for {introDbHits} episode(s) in {seriesName} Season {seasonNumber}, {detectionNeeded.Count} episode(s) need detection");

                seasonEpisodes = detectionNeeded;

                if (seasonEpisodes.Count == 0)
                {
                    LogInfo($"[TheIntroDB] All episodes in {seriesName} Season {seasonNumber} resolved from database — skipping detection");
                    return;
                }
            }

            if (_configuration != null && _itemRepository != null)
            {
                var preDetectionEpisodes = new List<Episode>();

                foreach (var ep in seasonEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested)
                        break;

                    var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";

                    if (!skipMarkerPreCheck && !isManualRun && _configuration.ScheduledTaskOnlyProcessMissing)
                    {
                        var chapters = _itemRepository.GetChapters(ep);
                        var hasCreditsMarker = chapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;
                        if (hasCreditsMarker)
                        {
                            LogInfo($"Skipping {ep.Name} - already has credits marker (ScheduledTaskOnlyProcessMissing is enabled)");
                            if (Plugin.Instance != null)
                            {
                                Plugin.Progress.SkipReasons[epKey] = "Already has credits marker";
                                Plugin.Progress.IncrementSkipped();
                            }
                            MarkPendingResolved(ep);
                            continue;
                        }
                    }

                    var useEmbedded = isManualRun
                        ? _configuration.UseEmbeddedChapterMarkersManual
                        : _configuration.UseEmbeddedChapterMarkersScheduled;
                    if (useEmbedded && _chapterMarkerService != null && _chapterMarkerService.TryImportEmbeddedCreditChapter(ep))
                    {
                        LogInfo($"Credits marker imported from embedded chapter for {ep.Name}");
                        if (Plugin.Instance != null)
                        {
                            var epDuration = ep.RunTimeTicks.HasValue ? ep.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                            var importedChapters = _itemRepository.GetChapters(ep);
                            var creditsMarker = importedChapters?.FirstOrDefault(c => GetMarkerType(c) == "CreditsStart");
                            var creditsStart = creditsMarker != null ? creditsMarker.StartPositionTicks / (double)TimeSpan.TicksPerSecond : 0;

                            Plugin.Progress.StartProcessingItem($"{seriesName} - S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2} - {ep.Name}", "Embedded Chapter");
                            Plugin.Progress.CompleteProcessingItem(true);
                            Plugin.Progress.SuccessDetails[epKey] = $"{FormatTime(creditsStart)} / {FormatTime(epDuration)} [Embedded Chapter] - Imported from embedded chapter marker";
                            Plugin.Progress.ConfidenceScores[epKey] = 1.0;
                            Plugin.Progress.EpisodeIds[epKey] = ep.Id.ToString();
                            if (!string.IsNullOrEmpty(matchedRuleName))
                                Plugin.Progress.AppliedRules[epKey] = matchedRuleName!;
                        }

                        if (!isManualRun)
                        {
                            var epId = ep.Id.ToString();
                            _processedEpisodes.TryAdd(epId, DateTime.UtcNow);
                            Plugin.TracerService?.MarkDetected(epId);
                        }
                        MarkPendingResolved(ep);
                        continue;
                    }

                    preDetectionEpisodes.Add(ep);
                }

                seasonEpisodes = preDetectionEpisodes;

                if (seasonEpisodes.Count == 0)
                {
                    LogInfo($"All episodes in {seriesName} Season {seasonNumber} resolved via existing/embedded markers - skipping detection");
                    return;
                }
            }

            bool isAnime = false;
            if (effectiveConfig.EnableAnimeDetection && !string.IsNullOrEmpty(seriesId))
            {
                LogDebug($"Checking if {seriesName} is anime (seriesId: {seriesId})");
                isAnime = CheckIfAnime(seriesId);
                if (isAnime)
                {
                    LogInfo($"Series '{seriesName}' identified as ANIME - batch processing will use anime-specific methods");
                }
                else
                {
                    LogDebug($"Series '{seriesName}' is not anime");
                }
            }

            DetectionMethods.ChromaprintDetection? chromaprintMethod = null;
            DetectionCoordinator? tempCoordinator = null;
            try
            {
                if (effectiveConfig != _configuration && _logger != null)
                {
                    LogDebug($"Creating temporary DetectionCoordinator with rule-based DetectionMode: {effectiveConfig.DetectionMode}");
                    tempCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                    chromaprintMethod = tempCoordinator.GetAllDetectionMethods()
                        .OfType<DetectionMethods.ChromaprintDetection>()
                        .FirstOrDefault();
                    LogDebug($"Chromaprint method from rule-based config - IsEnabled: {chromaprintMethod?.IsEnabled ?? false}");
                }
                else if (effectiveConfig == _configuration)
                {
                    chromaprintMethod = _episodeProcessor?.GetDetectionMethods()
                        .OfType<DetectionMethods.ChromaprintDetection>()
                        .FirstOrDefault();
                    LogDebug($"Chromaprint method from default config - IsEnabled: {chromaprintMethod?.IsEnabled ?? false}");
                }

                Dictionary<string, double>? batchResults = null;

                if (!isAnime && 
                    effectiveConfig != null &&
                    chromaprintMethod != null && 
                    chromaprintMethod.IsEnabled && 
                    !string.IsNullOrEmpty(seriesId) &&
                    (effectiveConfig.DetectionMode == DetectionMode.HashOnly || 
                     effectiveConfig.DetectionMode == DetectionMode.HashWithOcrFallback))
                {
                    var episodeData = seasonEpisodes.Concat(referenceEpisodes).Select(ep => (
                        EpisodeId: ep.Id.ToString(),
                        VideoPath: ep.Path,
                        Duration: ep.RunTimeTicks.HasValue ? ep.RunTimeTicks.Value / 10000000.0 : 0
                    )).ToList();

                    LogInfo($"Using batch chromaprint detection for {episodeData.Count} episodes (DetectionMode: {effectiveConfig.DetectionMode})");
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CurrentItem = $"Audio fingerprinting: {seriesName} Season {seasonNumber} ({episodeData.Count} episodes)";
                    }
                    batchResults = await chromaprintMethod.DetectCreditsForSeason(episodeData, seriesId, seasonNumber, cancellationToken);
                    LogInfo($"Batch detection found credits for {batchResults.Count} episodes");
                }
                else if (isAnime)
                {
                    LogInfo($"Anime detected - skipping chromaprint batch processing, will use individual episode detection with BlackFrameDetection");
                }
                else if (effectiveConfig != null && effectiveConfig.DetectionMode == DetectionMode.BlackFrameOnly)
                {
                    LogInfo($"BlackFrameOnly mode - detecting credits using black frame detection");
                }
                else if (effectiveConfig != null && effectiveConfig.DetectionMode == DetectionMode.OcrOnly)
                {
                    LogInfo($"OCR as primary method (DetectionMode: {effectiveConfig.DetectionMode}) - processing episodes individually to try OCR first");
                }
                else if (effectiveConfig != null && effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback)
                {
                    LogInfo("OCR as primary method with Hash fallback - trying OCR first for every episode; Chromaprint batch fingerprinting only runs afterward for episodes where OCR failed");
                }

                var blackFrameParallelSessions = _configuration?.BlackFrameParallelSessions ?? 1;
                var shouldRunParallel = blackFrameParallelSessions > 1 && seasonEpisodes.Count > 2 &&
                    (isAnime || effectiveConfig?.DetectionMode == DetectionMode.BlackFrameOnly);

                var ocrConcurrentFiles = _configuration?.OcrConcurrentFiles ?? 2;
                var shouldRunOcrFilesParallel = !isAnime &&
                    (_configuration?.OcrEnableConcurrentFiles ?? false) &&
                    ocrConcurrentFiles > 1 &&
                    seasonEpisodes.Count > 1 &&
                    (effectiveConfig?.DetectionMode == DetectionMode.OcrOnly ||
                     effectiveConfig?.DetectionMode == DetectionMode.HashWithOcrFallback);

                var shouldDeferChromaFallback = !isAnime &&
                    effectiveConfig != null &&
                    effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback &&
                    chromaprintMethod != null &&
                    chromaprintMethod.IsEnabled &&
                    !string.IsNullOrEmpty(seriesId);

                if (shouldDeferChromaFallback)
                {
                    await ProcessSeasonOcrFirstThenChromaFallback(seasonEpisodes, referenceEpisodes, seriesName, matchedRuleName, seriesId!, seasonNumber, chromaprintMethod!, tempCoordinator, ocrConcurrentFiles, isManualRun, cancellationToken).ConfigureAwait(false);
                }
                else if (shouldRunOcrFilesParallel)
                {
                    const int seedCount = 1;
                    var seedEpisodes = seasonEpisodes.Take(seedCount).ToList();
                    var remainingEpisodes = seasonEpisodes.Skip(seedCount).ToList();

                    LogInfo($"[OCR:{effectiveConfig?.OcrEngine}] Concurrent file mode: processing {seedCount} seed episode sequentially, then {remainingEpisodes.Count} in parallel (max {ocrConcurrentFiles} concurrent)");

                    foreach (var episode in seedEpisodes)
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                        await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                    }

                    if (!cancellationToken.IsCancellationRequested && !_cancellationRequested)
                    {
                        using var ocrFilesSemaphore = new SemaphoreSlim(ocrConcurrentFiles, ocrConcurrentFiles);
                        var parallelTasks = remainingEpisodes.Select(async episode =>
                        {
                            if (cancellationToken.IsCancellationRequested || _cancellationRequested) return;
                            try
                            {
                                await ocrFilesSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { return; }
                            try
                            {
                                await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                            }
                            finally
                            {
                                ocrFilesSemaphore.Release();
                            }
                        }).ToList();
                        await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                    }
                }
                else if (shouldRunParallel)
                {
                    const int seedCount = 2;
                    var seedEpisodes = seasonEpisodes.Take(seedCount).ToList();
                    var remainingEpisodes = seasonEpisodes.Skip(seedCount).ToList();

                    LogInfo($"[BlackFrame] Parallel mode: processing {seedCount} seed episodes sequentially, then {remainingEpisodes.Count} in parallel (max {blackFrameParallelSessions} concurrent)");

                    foreach (var episode in seedEpisodes)
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                        await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                    }

                    if (!cancellationToken.IsCancellationRequested && !_cancellationRequested)
                    {
                        using var bfSemaphore = new SemaphoreSlim(blackFrameParallelSessions, blackFrameParallelSessions);
                        var parallelTasks = remainingEpisodes.Select(async episode =>
                        {
                            if (cancellationToken.IsCancellationRequested || _cancellationRequested) return;
                            try
                            {
                                await bfSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { return; }
                            try
                            {
                                await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                            }
                            finally
                            {
                                bfSemaphore.Release();
                            }
                        }).ToList();
                        await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                    }
                }
                else
                {
                    foreach (var episode in seasonEpisodes)
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                        await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                    }
                }
            }
            finally
            {
                tempCoordinator?.Dispose();
            }
            
            LogInfo($"Batch processing complete for {seriesName} Season {seasonNumber}");
        }



        private static async Task ProcessSingleEpisodeInBatch(
            Episode episode,
            string seriesName,
            string? matchedRuleName,
            Dictionary<string, double>? batchResults,
            DetectionMethods.ChromaprintDetection? chromaprintMethod,
            DetectionCoordinator? tempCoordinator,
            bool isManualRun,
            CancellationToken cancellationToken)
        {
            var episodeId = episode.Id.ToString();

            await WaitWhilePlaybackActiveAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || _cancellationRequested)
                return;

            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
            {
                var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                if (hasFailed)
                {
                    LogInfo($"Skipping {episode.Name} - previously failed detection (SkipPreviouslyFailedEpisodes is enabled)");
                    if (Plugin.Instance != null)
                    {
                        var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                        Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                        Plugin.Progress.IncrementSkipped();
                    }
                    return;
                }
            }

            try
            {
                double? batchCreditsStart = null;
                if (batchResults != null && batchResults.ContainsKey(episodeId))
                {
                    batchCreditsStart = batchResults[episodeId];
                }

                if (Plugin.Instance != null)
                {
                    var episodeDisplayName = $"{seriesName} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.StartProcessingItem(episodeDisplayName, "Detecting");
                }

                LogDebug($"Processing episode {episode.Name}");

                if (_episodeProcessor == null) return;

                var (success, creditsStart, failureReason, confidence, methodName, detectionReason) = await _episodeProcessor.ProcessEpisodeWithBatchResult(
                    episode, IsDryRun(episodeId), batchCreditsStart, chromaprintMethod, tempCoordinator);

                await FinalizeEpisodeOutcome(episode, seriesName, matchedRuleName, success, creditsStart, failureReason, confidence, methodName, detectionReason, isManualRun);
            }
            catch (OperationCanceledException)
            {
                LogInfo($"Processing cancelled for episode {episode.Name}");
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(false);
                }
                return;
            }
            catch (Exception ex)
            {
                LogError($"Error processing episode {episode.Name}", ex);
                if (!IsDryRun(episodeId))
                {
                    Plugin.TracerService?.MarkFailed(episodeId, ex.Message, episode);
                    Plugin.PendingEpisodesService?.MarkProcessed(episodeId);
                }
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(false);
                    var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                    Plugin.Progress.FailureReasons[episodeKey] = ex.Message;
                    if (!string.IsNullOrEmpty(matchedRuleName))
                        Plugin.Progress.AppliedRules[episodeKey] = matchedRuleName!;
                }
            }

            CheckSeasonProgressCompletion();
        }

        private static async Task FinalizeEpisodeOutcome(
            Episode episode,
            string seriesName,
            string? matchedRuleName,
            bool success,
            double creditsStart,
            string failureReason,
            double confidence,
            string methodName,
            string detectionReason,
            bool isManualRun)
        {
            var episodeId = episode.Id.ToString();
            var isDryRun = IsDryRun(episodeId);

            if (success && creditsStart > 0)
            {
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(true);

                    var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                    var statusMessages = GetAndClearEpisodeStatusMessages(episodeId);
                    var duration = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                    var offsetSeconds = GetTimestampOffset(episode);
                    var finalTimestamp = creditsStart + offsetSeconds;
                    var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(duration)}";

                    if (!string.IsNullOrEmpty(methodName))
                        successDetail += $" [{methodName}]";

                    if (!string.IsNullOrEmpty(detectionReason))
                        successDetail += $" - {detectionReason}";

                    if (offsetSeconds != 0)
                        successDetail += $" (offset: {offsetSeconds:+0;-0}s, final: {FormatTime(finalTimestamp)})";

                    if (statusMessages.Count > 0)
                        successDetail += " (" + string.Join(", ", statusMessages) + ")";

                    Plugin.Progress.SuccessDetails[episodeKey] = successDetail;
                    Plugin.Progress.ConfidenceScores[episodeKey] = confidence;
                    Plugin.Progress.EpisodeIds[episodeKey] = episodeId;
                    if (!string.IsNullOrEmpty(matchedRuleName))
                        Plugin.Progress.AppliedRules[episodeKey] = matchedRuleName!;

                    if (isManualRun && Plugin.Instance.Configuration.EnableThumbnailGeneration)
                    {
                        try
                        {
                            var thumbnailPath = await GenerateThumbnail(episode, creditsStart, episodeKey);
                            if (!string.IsNullOrEmpty(thumbnailPath))
                                Plugin.Progress.ThumbnailPaths[episodeKey] = thumbnailPath;
                        }
                        catch (Exception thumbEx)
                        {
                            LogDebug($"Failed to generate thumbnail for {episodeKey}: {thumbEx.Message}");
                        }
                    }
                }

                if (!isDryRun)
                {
                    _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    Plugin.TracerService?.MarkDetected(episodeId);
                    Plugin.PendingEpisodesService?.MarkProcessed(episodeId);

                    if (_episodesWithoutCreditsMarker.TryRemove(episodeId, out _))
                    {
                        await RecordNoCreditsMarkerInBackup(episode).ConfigureAwait(false);
                    }
                    else if (_configuration != null &&
                        _configuration.SkipDetectionIfFileUnchanged &&
                        !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                        Plugin.CreditsBackupService != null)
                    {
                        try
                        {
                            var fpTicks = (long)(creditsStart * TimeSpan.TicksPerSecond);
                            await Plugin.CreditsBackupService.UpsertEpisodeInSeriesBackup(
                                episode, fpTicks, _configuration.BackupFolderPath).ConfigureAwait(false);
                        }
                        catch (Exception fpEx)
                        {
                            LogDebug($"Failed to record file fingerprint for {episode.Name}: {fpEx.Message}");
                        }
                    }
                }

                LogDebug($"Successfully detected credits at {FormatTime(creditsStart)} for {episode.Name}");
            }
            else
            {
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(false);
                    var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                    Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                    if (!string.IsNullOrEmpty(matchedRuleName))
                        Plugin.Progress.AppliedRules[episodeKey] = matchedRuleName!;
                }

                GetAndClearEpisodeStatusMessages(episodeId);

                if (!isDryRun &&
                    _configuration != null &&
                    _configuration.SkipDetectionIfFileUnchanged &&
                    !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                    Plugin.CreditsBackupService != null)
                {
                    try
                    {
                        await Plugin.CreditsBackupService.UpsertEpisodeInSeriesBackup(
                            episode, 0L, _configuration.BackupFolderPath).ConfigureAwait(false);
                    }
                    catch (Exception fpEx)
                    {
                        LogDebug($"Failed to record file fingerprint for {episode.Name}: {fpEx.Message}");
                    }
                }

                if (!isDryRun)
                {
                    Plugin.TracerService?.MarkFailed(episodeId, failureReason, episode);
                    Plugin.PendingEpisodesService?.MarkProcessed(episodeId);
                }
            }
        }

        private static double GetTimestampOffset(Episode episode)
        {
            var ruleMatchingService = _cachedRuleMatchingService;
            if (ruleMatchingService != null)
                return ruleMatchingService.GetEffectiveConfiguration(episode).TimestampOffsetSeconds;
            return _configuration?.TimestampOffsetSeconds ?? 0;
        }

        private static async Task RecordNoCreditsMarkerInBackup(Episode episode)
        {
            if (_configuration == null ||
                string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) ||
                Plugin.CreditsBackupService == null)
            {
                return;
            }

            try
            {
                await Plugin.CreditsBackupService.UpsertEpisodeInSeriesBackup(
                    episode, 0L, _configuration.BackupFolderPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to record missing credits marker for {episode.Name} in backup: {ex.Message}");
            }
        }

        private static void CheckSeasonProgressCompletion()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Progress.CheckAndLimitDictionarySize();
                if (!_isProcessing && Plugin.Progress.ProcessedItems >= Plugin.Progress.TotalItems)
                {
                    Plugin.Progress.IsRunning = false;
                    Plugin.Progress.EndTime = DateTime.Now;
                    Plugin.Progress.CurrentItem = "Complete";
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
                    EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();
                }
            }
        }

        private static async Task ProcessSeasonOcrFirstThenChromaFallback(
            List<Episode> seasonEpisodes,
            List<Episode> referenceEpisodes,
            string seriesName,
            string? matchedRuleName,
            string seriesId,
            int seasonNumber,
            DetectionMethods.ChromaprintDetection chromaprintMethod,
            DetectionCoordinator? tempCoordinator,
            int ocrConcurrentFiles,
            bool isManualRun,
            CancellationToken cancellationToken)
        {
            var pendingRetry = new ConcurrentBag<Episode>();
            var pendingFailureReasons = new ConcurrentDictionary<string, string>();

            async Task TryOcrForEpisode(Episode episode)
            {
                var episodeId = episode.Id.ToString();

                await WaitWhilePlaybackActiveAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || _cancellationRequested)
                    return;

                if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
                {
                    var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                    if (hasFailed)
                    {
                        LogInfo($"Skipping {episode.Name} - previously failed detection (SkipPreviouslyFailedEpisodes is enabled)");
                        if (Plugin.Instance != null)
                        {
                            var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                            Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                            Plugin.Progress.IncrementSkipped();
                        }
                        return;
                    }
                }

                if (Plugin.Instance != null)
                {
                    var episodeDisplayName = $"{seriesName} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.StartProcessingItem(episodeDisplayName, "Detecting");
                }

                LogDebug($"Processing episode {episode.Name} (OCR pass)");

                if (_episodeProcessor == null) return;

                try
                {
                    var (success, creditsStart, failureReason, confidence, methodName, detectionReason) =
                        await _episodeProcessor.ProcessEpisodeWithBatchResult(episode, IsDryRun(episodeId), null, chromaprintMethod, tempCoordinator);

                    if (success && creditsStart > 0)
                    {
                        await FinalizeEpisodeOutcome(episode, seriesName, matchedRuleName, true, creditsStart, string.Empty, confidence, methodName, detectionReason, isManualRun);
                        CheckSeasonProgressCompletion();
                        return;
                    }

                    LogDebug($"OCR failed for {episode.Name}, deferring to Chromaprint batch fallback: {failureReason}");
                    pendingFailureReasons[episodeId] = failureReason;
                    pendingRetry.Add(episode);
                }
                catch (Exception ex)
                {
                    LogError($"Error processing episode {episode.Name}", ex);
                    pendingFailureReasons[episodeId] = $"Error: {ex.Message}";
                    pendingRetry.Add(episode);
                }
            }

            var shouldRunOcrFilesParallel = (_configuration?.OcrEnableConcurrentFiles ?? false) &&
                ocrConcurrentFiles > 1 &&
                seasonEpisodes.Count > 1;

            if (shouldRunOcrFilesParallel)
            {
                const int seedCount = 1;
                var seedEpisodes = seasonEpisodes.Take(seedCount).ToList();
                var remainingEpisodes = seasonEpisodes.Skip(seedCount).ToList();

                foreach (var episode in seedEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                    await TryOcrForEpisode(episode);
                }

                if (!cancellationToken.IsCancellationRequested && !_cancellationRequested)
                {
                    using var ocrFilesSemaphore = new SemaphoreSlim(ocrConcurrentFiles, ocrConcurrentFiles);
                    var parallelTasks = remainingEpisodes.Select(async episode =>
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationRequested) return;
                        try
                        {
                            await ocrFilesSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { return; }
                        try
                        {
                            await TryOcrForEpisode(episode);
                        }
                        finally
                        {
                            ocrFilesSemaphore.Release();
                        }
                    }).ToList();
                    await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var episode in seasonEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                    await TryOcrForEpisode(episode);
                }
            }

            var episodesNeedingFallback = pendingRetry.ToList();
            if (episodesNeedingFallback.Count == 0 || cancellationToken.IsCancellationRequested || _cancellationRequested)
                return;

            LogInfo($"OCR failed for {episodesNeedingFallback.Count} episode(s) in {seriesName} Season {seasonNumber} - running Chromaprint batch fallback now");
            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = $"Audio fingerprinting fallback: {seriesName} Season {seasonNumber} ({episodesNeedingFallback.Count} episode(s) need it)";
            }

            var episodeData = seasonEpisodes.Concat(referenceEpisodes).Select(ep => (
                EpisodeId: ep.Id.ToString(),
                VideoPath: ep.Path,
                Duration: ep.RunTimeTicks.HasValue ? ep.RunTimeTicks.Value / 10000000.0 : 0
            )).ToList();

            Dictionary<string, double> chromaResults;
            try
            {
                chromaResults = await chromaprintMethod.DetectCreditsForSeason(episodeData, seriesId, seasonNumber, cancellationToken);
            }
            catch (Exception ex)
            {
                LogError($"Error running Chromaprint batch fallback for {seriesName} Season {seasonNumber}", ex);
                chromaResults = new Dictionary<string, double>();
            }

            foreach (var episode in episodesNeedingFallback)
            {
                if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;

                var episodeId = episode.Id.ToString();
                var originalFailureReason = pendingFailureReasons.TryGetValue(episodeId, out var reason) ? reason : "OCR failed";

                if (_episodeProcessor != null && chromaResults.TryGetValue(episodeId, out var chromaTimestamp) && chromaTimestamp > 0)
                {
                    AddEpisodeStatusMessage(episodeId, "OCR failed, using Chromaprint batch fallback");
                    var (success, creditsStart, failureReason, confidence, methodName, detectionReason) =
                        await _episodeProcessor.SaveBatchChromaprintResult(episode, IsDryRun(episodeId), chromaTimestamp, chromaprintMethod);

                    await FinalizeEpisodeOutcome(episode, seriesName, matchedRuleName, success, creditsStart,
                        success ? string.Empty : failureReason, confidence, methodName, detectionReason, isManualRun);
                }
                else
                {
                    await FinalizeEpisodeOutcome(episode, seriesName, matchedRuleName, false, 0, originalFailureReason, 0, string.Empty, string.Empty, isManualRun);
                }

                CheckSeasonProgressCompletion();
            }
        }

        public static async Task ProcessEpisode(Episode episode)
        {
            if (_episodeProcessor == null || _configuration == null)
                return;

            var episodeId = episode.Id.ToString();

            try
            {
                if (Plugin.Instance != null)
                {
                    var episodeDisplayName = $"{episode.Series?.Name} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.StartProcessingItem(episodeDisplayName, "Detecting");
                }

                var isDryRun = IsDryRun(episodeId);
                var (success, creditsStart, failureReason, confidence, methodName, detectionReason) = await _episodeProcessor.ProcessEpisode(
                    episode, isDryRun, _isBatchMode, _batchDetectionCache);

                if (success && creditsStart > 0)
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CompleteProcessingItem(true);

                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        
                        var statusMessages = GetAndClearEpisodeStatusMessages(episodeId);
                        var duration = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                        var offsetSeconds = GetTimestampOffset(episode);
                        var finalTimestamp = creditsStart + offsetSeconds;
                        var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(duration)}";
                        
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            successDetail += $" [{methodName}]";
                        }
                        
                        if (!string.IsNullOrEmpty(detectionReason))
                        {
                            successDetail += $" - {detectionReason}";
                        }
                        
                        if (offsetSeconds != 0)
                        {
                            successDetail += $" (offset: {offsetSeconds:+0;-0}s, final: {FormatTime(finalTimestamp)})";
                        }
                        
                        if (statusMessages.Count > 0)
                        {
                            successDetail += " (" + string.Join(", ", statusMessages) + ")";
                        }
                        
                        Plugin.Progress.SuccessDetails[episodeKey] = successDetail;
                        Plugin.Progress.ConfidenceScores[episodeKey] = confidence;
                        Plugin.Progress.EpisodeIds[episodeKey] = episode.Id.ToString();

                        if (Plugin.Instance.Configuration.EnableThumbnailGeneration)
                        {
                            try
                            {
                                var thumbnailPath = await GenerateThumbnail(episode, creditsStart, episodeKey);
                                if (!string.IsNullOrEmpty(thumbnailPath))
                                {
                                    Plugin.Progress.ThumbnailPaths[episodeKey] = thumbnailPath;
                                }
                            }
                            catch (Exception thumbEx)
                            {
                                LogDebug($"Failed to generate thumbnail for {episodeKey}: {thumbEx.Message}");
                            }
                        }
                    }

                    if (!isDryRun)
                    {
                        _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    }
                }
                else
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CompleteProcessingItem(false);

                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                    }
                    
                    GetAndClearEpisodeStatusMessages(episodeId);

                    if (!isDryRun)
                    {
                        Plugin.TracerService?.MarkFailed(episodeId, failureReason, episode);
                        _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    }
                }

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CheckAndLimitDictionarySize();

                    if (Plugin.Progress.ProcessedItems >= Plugin.Progress.TotalItems)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Complete";
                        
                        EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();
                    }
                }
                
                CleanupBatchDetectionCache();
                CleanupOldProcessedEpisodes();
            }
            catch (Exception ex)
            {
                LogError($"Error processing episode {episode.Name}", ex);

                if (!IsDryRun(episodeId))
                    Plugin.TracerService?.MarkFailed(episodeId, ex.Message, episode);

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(false);
                }
            }
        }

        private static string FormatTime(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        private static bool IsStrmFile(string? path)
        {
            return !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> GenerateThumbnail(Episode episode, double timestamp, string episodeKey)
        {
            try
            {
                LogDebug($"Starting thumbnail generation for {episodeKey} at {timestamp}s");
                
                var videoPath = episode.Path;
                if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                {
                    LogDebug($"Video path not found for {episodeKey}");
                    return string.Empty;
                }

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    LogDebug("Plugin configuration not available");
                    return string.Empty;
                }

                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                {
                    LogDebug("Plugin data path not available");
                    return string.Empty;
                }

                var thumbnailDir = Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                Directory.CreateDirectory(thumbnailDir);
                LogDebug($"Thumbnail directory: {thumbnailDir}");

                var safeFileName = string.Join("_", episodeKey.Split(Path.GetInvalidFileNameChars()));
                var thumbnailFileName = $"{safeFileName}_{timestamp:F2}.jpg";
                var thumbnailPath = Path.Combine(thumbnailDir, thumbnailFileName);

                var ffmpegPath = Utilities.FFmpegHelper.GetFfmpegPath();
                var width = config.ThumbnailWidth > 0 ? config.ThumbnailWidth : 320;
                var quality = config.ThumbnailQuality > 0 ? config.ThumbnailQuality : 85;
                
                var ffmpegQuality = Math.Max(2, Math.Min(31, 31 - ((quality - 50) * 29 / 50)));
                
                var thumbnailStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                thumbnailStartInfo.ArgumentList().Add("-ss");
                thumbnailStartInfo.ArgumentList().Add(timestamp.ToString(CultureInfo.InvariantCulture));
                thumbnailStartInfo.ArgumentList().Add("-i");
                thumbnailStartInfo.ArgumentList().Add(Utilities.FFmpegHelper.ResolveInputPath(videoPath));
                thumbnailStartInfo.ArgumentList().Add("-vframes");
                thumbnailStartInfo.ArgumentList().Add("1");
                thumbnailStartInfo.ArgumentList().Add("-vf");
                thumbnailStartInfo.ArgumentList().Add($"scale={width}:-1");
                thumbnailStartInfo.ArgumentList().Add("-q:v");
                thumbnailStartInfo.ArgumentList().Add(ffmpegQuality.ToString(CultureInfo.InvariantCulture));
                thumbnailStartInfo.ArgumentList().Add(thumbnailPath);
                thumbnailStartInfo.ArgumentList().Add("-y");

                LogDebug($"FFmpeg command: {ffmpegPath} -ss {timestamp} -i <path> -vframes 1 -vf scale={width}:-1 -q:v {ffmpegQuality}");

                using (var process = new Process { StartInfo = thumbnailStartInfo })
                {
                    var errorOutput = new System.Text.StringBuilder();
                    DataReceivedEventHandler errorHandler = (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorOutput.AppendLine(e.Data);
                        }
                    };
                    
                    try
                    {
                        process.ErrorDataReceived += errorHandler;

                        thumbnailStartInfo.ApplyArgumentList();
                        process.Start();
                        process.BeginErrorReadLine();
                        
                        await Task.Run(() => process.WaitForExit());
                    }
                    finally
                    {
                        try
                        {
                            process.ErrorDataReceived -= errorHandler;
                            process.CancelErrorRead();
                        }
                        catch { }
                    }

                    if (process.ExitCode == 0 && File.Exists(thumbnailPath))
                    {
                        var fileInfo = new FileInfo(thumbnailPath);
                        LogDebug($"✓ Generated thumbnail for {episodeKey}: {thumbnailFileName} ({fileInfo.Length} bytes)");
                        return thumbnailFileName;
                    }
                    else
                    {
                        LogDebug($"✗ Thumbnail generation failed for {episodeKey}. Exit code: {process.ExitCode}");
                        if (errorOutput.Length > 0)
                        {
                            LogDebug($"FFmpeg error: {errorOutput.ToString().Substring(0, Math.Min(500, errorOutput.Length))}");
                        }
                    }
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                LogDebug($"✗ Error generating thumbnail for {episodeKey}: {ex.Message}");
                LogError($"Thumbnail generation error for {episodeKey}", ex);
                return string.Empty;
            }
        }

        private static string? GetMarkerType(ChapterInfo chapter)
        {
            try
            {
                if (chapter == null) return null;
                if (_markerTypeProp != null && _markerTypeProp.CanRead)
                {
                    var value = _markerTypeProp.GetValue(chapter);
                    return value?.ToString();
                }
            }
            catch { }
            return null;
        }

        private static bool CheckIfAnime(string seriesId)
        {
            try
            {
                LogDebug($"CheckIfAnime called for seriesId: {seriesId}");
                
                if (_libraryManager == null)
                {
                    LogWarn($"CheckIfAnime: _libraryManager is null");
                    return false;
                }

                if (!Guid.TryParse(seriesId, out var seriesGuid))
                {
                    LogWarn($"CheckIfAnime: Failed to parse seriesId as Guid: {seriesId}");
                    return false;
                }

                var item = _libraryManager.GetItemById(seriesGuid);
                if (item == null)
                {
                    LogWarn($"CheckIfAnime: GetItemById returned null for Guid: {seriesGuid}");
                    return false;
                }

                LogDebug($"CheckIfAnime: Found item type: {item.GetType().Name}, Name: {item.Name}");

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    LogDebug($"CheckIfAnime: Series found - Name: {series.Name}");

                    if (series.Tags != null && series.Tags.Length > 0)
                    {
                        if (_configuration?.EnableDetailedLogging == true)
                            LogDebug($"CheckIfAnime: Tags count: {series.Tags.Length}");
                        
                        for (int i = 0; i < series.Tags.Length; i++)
                        {
                            if (series.Tags[i].Equals("anime", StringComparison.OrdinalIgnoreCase))
                            {
                                LogInfo($"CheckIfAnime: Series '{series.Name}' identified as ANIME via Tags");
                                return true;
                            }
                        }
                    }

                    if (series.Genres != null && series.Genres.Length > 0)
                    {
                        if (_configuration?.EnableDetailedLogging == true)
                            LogDebug($"CheckIfAnime: Genres count: {series.Genres.Length}");
                        
                        for (int i = 0; i < series.Genres.Length; i++)
                        {
                            if (series.Genres[i].Equals("anime", StringComparison.OrdinalIgnoreCase))
                            {
                                LogInfo($"CheckIfAnime: Series '{series.Name}' identified as ANIME via Genres");
                                return true;
                            }
                        }
                    }

                    LogDebug($"CheckIfAnime: Series '{series.Name}' is NOT anime");
                }
                else
                {
                    LogWarn($"CheckIfAnime: Item is not a Series, it's: {item.GetType().Name}");
                }

                return false;
            }
            catch (Exception ex)
            {
                LogError($"CheckIfAnime exception for seriesId: {seriesId}", ex);
                return false;
            }
        }

        private sealed class SeasonDispatchState : IDisposable
        {
            public readonly ConcurrentDictionary<Guid, byte> EpisodeIds = new ConcurrentDictionary<Guid, byte>();
            public int RetryCount;
            private Timer? _timer;
            private readonly object _lock = new object();
            private bool _disposed;

            public void ResetTimer(int delayMs, Action callback)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_timer == null)
                        _timer = new Timer(_ =>
                        {
                            try
                            {
                                callback();
                            }
                            catch (Exception ex)
                            {
                                LogError("Season dispatch failed", ex);
                            }
                        }, null, delayMs, Timeout.Infinite);
                    else
                        _timer.Change(delayMs, Timeout.Infinite);
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _disposed = true;
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }
    }
}

