using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.ScheduledTasks
{
    internal static class ScheduledDetectionRunner
    {
        public const string TaskCategory = "Credits Detector";

        private static readonly System.Reflection.PropertyInfo? _markerTypeProp =
            typeof(MediaBrowser.Model.Entities.ChapterInfo).GetProperty("MarkerType");

        public static List<Episode> GetLibraryEpisodes(
            ILibraryManager libraryManager,
            PluginConfiguration config,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var libraryIds = config.LibraryIds ?? Array.Empty<string>();
            var allEpisodes = new List<Episode>();
            List<Folder> librariesToProcess;

            if (libraryIds.Length == 0)
            {
                logger.Info("No specific libraries configured, processing all TV Show and Mixed libraries");

                var allLibraries = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "CollectionFolder" }
                }).ToList();

                librariesToProcess = allLibraries
                    .Where(lib =>
                    {
                        var collectionType = lib.GetType().GetProperty("CollectionType")?.GetValue(lib) as string;
                        return collectionType == "tvshows" || collectionType == "mixed" || string.IsNullOrEmpty(collectionType);
                    })
                    .OfType<Folder>()
                    .ToList();
            }
            else
            {
                librariesToProcess = new List<Folder>();
                foreach (var libraryId in libraryIds)
                {
                    Folder? library = null;

                    if (Guid.TryParse(libraryId, out var libraryGuid))
                    {
                        library = libraryManager.GetItemById(libraryGuid) as Folder;
                    }
                    else if (long.TryParse(libraryId, out var libraryInternalId))
                    {
                        library = libraryManager.GetItemById(libraryInternalId) as Folder;
                    }
                    else
                    {
                        logger.Warn($"Invalid library ID format: {libraryId}");
                        continue;
                    }

                    if (library != null)
                        librariesToProcess.Add(library);
                    else
                        logger.Warn($"Library not found: {libraryId}");
                }
            }

            logger.Info($"Scanning {librariesToProcess.Count} libraries for episodes");

            foreach (var library in librariesToProcess)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    logger.Info($"Scanning library: {library.Name}");

                    var query = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true,
                        Recursive = true,
                        Parent = library
                    };

                    var allLibraryEpisodes = libraryManager.GetItemList(query).OfType<Episode>().ToList();
                    var episodes = allLibraryEpisodes.Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0).ToList();
                    var specialCount = allLibraryEpisodes.Count - episodes.Count;
                    allEpisodes.AddRange(episodes);

                    logger.Info($"Found {episodes.Count} episodes in {library.Name} (excluded {specialCount} specials)");
                }
                catch (Exception ex)
                {
                    logger.ErrorException($"Error processing library {library.Name}", ex);
                }
            }

            return allEpisodes.GroupBy(e => e.Id).Select(g => g.First()).ToList();
        }

        public static List<Episode> FilterEpisodesNeedingDetection(
            List<Episode> episodes,
            PluginConfiguration config,
            IItemRepository itemRepository,
            ILogger logger,
            bool onlyMissingMarkers,
            CancellationToken cancellationToken)
        {
            List<Episode> episodesToProcess;

            if (onlyMissingMarkers)
            {
                var checkFileChanges = config.SkipDetectionIfFileUnchanged &&
                    !string.IsNullOrWhiteSpace(config.BackupFolderPath) &&
                    Plugin.CreditsBackupService != null;

                var episodesToProcessBag = new ConcurrentBag<Episode>();
                var skipCounter = 0;
                var maxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount));

                try
                {
                    Parallel.ForEach(
                        episodes,
                        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken },
                        episode =>
                        {
                            if (!HasCreditsMarker(itemRepository, logger, episode))
                            {
                                episodesToProcessBag.Add(episode);
                            }
                            else if (checkFileChanges && Plugin.CreditsBackupService!.HasFileChanged(episode, config.BackupFolderPath))
                            {
                                logger.Info($"File changed since last detection for '{episode.Name}' — reprocessing despite existing marker");
                                episodesToProcessBag.Add(episode);
                            }
                            else
                            {
                                Interlocked.Increment(ref skipCounter);
                                Plugin.PendingEpisodesService?.MarkProcessed(episode.Id.ToString());
                            }
                        });
                }
                catch (OperationCanceledException)
                {
                }

                episodesToProcess = episodesToProcessBag.ToList();
                logger.Info($"Processing {episodesToProcess.Count} episodes (skipping {skipCounter} episodes with existing credits)");
            }
            else
            {
                episodesToProcess = episodes.ToList();
                logger.Info($"Processing all {episodesToProcess.Count} episodes (reprocess mode enabled)");
            }

            if (episodesToProcess.Count > 0 && config.SkipPreviouslyFailedEpisodes && !config.IgnoreFailureMarkers)
            {
                var failedEpisodes = episodesToProcess
                    .Where(ep => ep.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var fv) == true && fv == "true")
                    .ToList();
                foreach (var failed in failedEpisodes)
                    Plugin.PendingEpisodesService?.MarkProcessed(failed.Id.ToString());
                episodesToProcess = episodesToProcess.Except(failedEpisodes).ToList();
                if (failedEpisodes.Count > 0)
                    logger.Info($"Skipping {failedEpisodes.Count} previously failed episode(s) (not retrying)");
            }

            return episodesToProcess;
        }

        public static async Task<bool> RunDetectionAsync(
            List<Episode> episodesToProcess,
            ILogger logger,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var stillWriting = episodesToProcess.Where(FileWriteGuard.IsBeingWritten).ToList();
            if (stillWriting.Count > 0)
            {
                logger.Info($"Skipping {stillWriting.Count} episode(s) that are still being recorded or written - they will be picked up on a later run");
                episodesToProcess = episodesToProcess.Except(stillWriting).ToList();
            }

            var targetIds = new HashSet<string>(episodesToProcess.Select(e => e.Id.ToString()));

            var episodesBySeason = new List<(string SeriesId, int SeasonNumber, List<Episode> Episodes)>();
            var referenceCount = 0;
            foreach (var group in episodesToProcess
                .Where(e => e.Series != null && e.ParentIndexNumber.HasValue)
                .GroupBy(e => new { SeriesId = e.Series!.Id.ToString(), SeasonNumber = e.ParentIndexNumber!.Value }))
            {
                var targets = group.ToList();
                var batch = targets;

                if (CreditsDetectionService.SeasonNeedsReferenceEpisodes(targets[0]))
                {
                    var references = CreditsDetectionService.GetSeasonEpisodes(targets[0])
                        .Where(e => !targetIds.Contains(e.Id.ToString()))
                        .ToList();
                    if (references.Count > 0)
                    {
                        batch = targets.Concat(references).ToList();
                        referenceCount += references.Count;
                    }
                }

                episodesBySeason.Add((group.Key.SeriesId, group.Key.SeasonNumber, batch));
            }

            if (referenceCount > 0)
                logger.Info($"Added {referenceCount} already-processed episode(s) as audio references for Chromaprint comparison");

            Plugin.Progress.Reset();
            Plugin.Progress.TotalItems = episodesBySeason.Sum(s => s.Episodes.Count);
            Plugin.Progress.IsRunning = true;
            Plugin.Progress.StartTime = DateTime.Now;

            CreditsDetectionService.ResetForScheduledTask();
            CreditsDetectionService.SetActiveRunTargets(targetIds);
            using var cancellationRegistration = cancellationToken.Register(() => CreditsDetectionService.CancelProcessing());

            var failedEpisodeNames = new List<string>();
            var successfulSeriesNames = new HashSet<string>();

            logger.Info($"Grouped {episodesToProcess.Count} episodes into {episodesBySeason.Count} seasons for batch processing");

            using var progressReporterCts = new CancellationTokenSource();
            var progressReporterTask = Task.Run(async () =>
            {
                while (!progressReporterCts.Token.IsCancellationRequested)
                {
                    var total = Plugin.Progress.TotalItems;
                    if (total > 0)
                    {
                        progress.Report((double)Plugin.Progress.ProcessedItems / total * 100);
                    }
                    try { await Task.Delay(1000, progressReporterCts.Token); } catch (OperationCanceledException) { break; }
                }
            });

            foreach (var seasonGroup in episodesBySeason)
            {
                if (cancellationToken.IsCancellationRequested || CreditsDetectionService.IsCancellationRequested)
                {
                    logger.Info("Cancellation requested, stopping credits detection");
                    break;
                }

                await CreditsDetectionService.WaitForOtherPluginsIfNeeded().ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested || CreditsDetectionService.IsCancellationRequested)
                {
                    logger.Info("Cancellation requested during coordination wait, stopping credits detection");
                    break;
                }

                var seasonEpisodes = seasonGroup.Episodes.OrderBy(e => e.IndexNumber).ToList();
                var firstEpisode = seasonEpisodes.First();
                var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";
                var seasonTargets = seasonEpisodes.Where(e => targetIds.Contains(e.Id.ToString())).ToList();

                logger.Info($"Processing season batch: {seriesName} Season {seasonGroup.SeasonNumber} ({seasonTargets.Count} episodes, {seasonEpisodes.Count - seasonTargets.Count} reference episodes)");

                try
                {
                    await CreditsDetectionService.ProcessSeasonBatch(seasonEpisodes, cancellationToken, isManualRun: false, skipMarkerPreCheck: true);
                    successfulSeriesNames.Add(seriesName);
                }
                catch (Exception ex)
                {
                    logger.ErrorException($"Error processing season batch {seriesName} S{seasonGroup.SeasonNumber}", ex);
                    Plugin.Progress.FailedItems += seasonTargets.Count;

                    foreach (var episode in seasonTargets)
                    {
                        var episodeLabel = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} - {episode.Name}";
                        failedEpisodeNames.Add(episodeLabel);
                    }
                }

                try { await Task.Delay(1000, cancellationToken); } catch (OperationCanceledException) { }
            }

            progressReporterCts.Cancel();
            try { await progressReporterTask; } catch (Exception) { }
            progress.Report(100);

            await CreditsDetectionService.RunAutoBackupForEpisodes(episodesToProcess, isDryRun: false);

            var cancelled = cancellationToken.IsCancellationRequested || CreditsDetectionService.IsCancellationRequested;

            Plugin.Progress.IsRunning = false;
            Plugin.Progress.EndTime = DateTime.Now;
            Plugin.Progress.CurrentItem = cancelled ? "Cancelled" : "Complete";

            EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

            logger.Info($"Credits detection complete. Processed: {Plugin.Progress.SuccessfulItems}, Failed: {Plugin.Progress.FailedItems}");

            var duration = DateTime.Now - Plugin.Progress.StartTime;
            SendCompletionNotification(logger, Plugin.Progress.SuccessfulItems, Plugin.Progress.FailedItems, episodesToProcess.Count, failedEpisodeNames, successfulSeriesNames.ToList(), duration ?? TimeSpan.Zero);

            return !cancelled;
        }

        private static void SendCompletionNotification(ILogger logger, int successCount, int failedCount, int totalProcessed, List<string> failedEpisodes, List<string> successfulSeries, TimeSpan duration)
        {
            try
            {
                if (Plugin.Instance == null)
                {
                    logger.Debug("Plugin instance not available");
                    return;
                }

                var notificationManager = Plugin.Instance.GetNotificationManager();
                if (notificationManager == null)
                {
                    logger.Debug("Notification manager not available");
                    return;
                }

                var config = Plugin.Instance.Configuration;
                var notificationService = new NotificationService(logger, notificationManager, config);
                notificationService.SendScheduledTaskCompletionNotification(successCount, failedCount, totalProcessed, failedEpisodes, successfulSeries, duration);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Failed to send completion notification", ex);
            }
        }

        private static bool HasCreditsMarker(IItemRepository itemRepository, ILogger logger, Episode episode)
        {
            try
            {
                var chapters = itemRepository.GetChapters(episode)?.ToList();
                if (chapters == null || chapters.Count == 0)
                    return false;

                return chapters.Any(c => GetMarkerType(c) == "CreditsStart");
            }
            catch (Exception ex)
            {
                logger.ErrorException($"Error checking credits marker for {episode.Name}", ex);
                return false;
            }
        }

        private static string? GetMarkerType(MediaBrowser.Model.Entities.ChapterInfo chapter)
        {
            try
            {
                if (_markerTypeProp != null && _markerTypeProp.CanRead)
                {
                    var value = _markerTypeProp.GetValue(chapter);
                    if (value != null)
                    {
                        return value.ToString();
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
