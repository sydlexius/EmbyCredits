using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;

namespace EmbyCredits.ScheduledTasks
{
    public class CreditsNewEpisodesScheduledTask : IScheduledTask
    {
        private static readonly TimeSpan MinimumFileAge = TimeSpan.FromMinutes(2);

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly IItemRepository _itemRepository;

        public string Name => "Detect Credits in New Episodes";
        public string Description => "Detects end credits only for episodes added to the selected libraries since this task last completed";
        public string Category => ScheduledDetectionRunner.TaskCategory;
        public string Key => "CreditsDetectionNewEpisodes";

        public CreditsNewEpisodesScheduledTask(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            IItemRepository itemRepository)
        {
            _logger = logManager.GetLogger($"[EmbyCredits] {GetType().Name}");
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _itemRepository = itemRepository;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (Plugin.Instance == null)
            {
                _logger.Error("Plugin instance not available");
                return;
            }

            if (!CreditsDetectionService.TryBeginScheduledRun())
            {
                _logger.Info("Another detection process is already running; skipping this new-episode run");
                return;
            }

            try
            {
                var config = Plugin.Instance.Configuration;
                var runStartedUtc = DateTime.UtcNow;

                CreditsDetectionService.SetLibraryManager(_libraryManager);
                CreditsDetectionService.SetItemRepository(_itemRepository);

                var stateStore = new NewEpisodeDetectionStateStore(_logger, _appPaths.DataPath);
                var previousState = stateStore.Load();

                var libraryEpisodes = ScheduledDetectionRunner.GetLibraryEpisodes(_libraryManager, config, _logger, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return;

                var previousMaxInternalId = previousState?.LastMaxInternalId ?? 0;
                var snapshotMaxInternalId = libraryEpisodes.Count > 0
                    ? Math.Max(previousMaxInternalId, libraryEpisodes.Max(e => e.InternalId))
                    : previousMaxInternalId;

                if (previousState == null)
                {
                    stateStore.Save(new NewEpisodeDetectionState
                    {
                        LastMaxInternalId = snapshotMaxInternalId,
                        LastRunStartedUtc = runStartedUtc,
                        LastCompletedRunUtc = DateTime.UtcNow
                    });
                    _logger.Info("First run of new-episode detection: no earlier run to compare against, so no episodes were processed. Episodes added from now on will be detected on the next run");
                    progress.Report(100);
                    return;
                }

                var deferredIds = new HashSet<string>(previousState.DeferredEpisodeIds, StringComparer.OrdinalIgnoreCase);
                var lastRunUtc = previousState.LastRunStartedUtc ?? previousState.LastCompletedRunUtc;
                var newEpisodes = libraryEpisodes
                    .Where(e => e.InternalId > previousState.LastMaxInternalId ||
                                (lastRunUtc.HasValue && e.DateCreated.ToUniversalTime() > lastRunUtc.Value) ||
                                deferredIds.Contains(e.Id.ToString()))
                    .ToList();

                var since = lastRunUtc.HasValue
                    ? lastRunUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "the previous run";
                _logger.Info($"Found {newEpisodes.Count} episode(s) added since {since} ({deferredIds.Count} carried over from the previous run)");

                var readyEpisodes = new List<Episode>();
                var notReadyIds = new List<string>();
                foreach (var episode in newEpisodes)
                {
                    if (IsFileReady(episode, out var reason))
                    {
                        readyEpisodes.Add(episode);
                    }
                    else
                    {
                        notReadyIds.Add(episode.Id.ToString());
                        _logger.Info($"Deferring '{episode.SeriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}' to the next run: {reason}");
                    }
                }

                var episodesToProcess = readyEpisodes.Count > 0
                    ? ScheduledDetectionRunner.FilterEpisodesNeedingDetection(readyEpisodes, config, _itemRepository, _logger, onlyMissingMarkers: true, cancellationToken)
                    : new List<Episode>();

                var completed = true;
                if (episodesToProcess.Count > 0)
                {
                    completed = await ScheduledDetectionRunner.RunDetectionAsync(episodesToProcess, _logger, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.Info("No new episodes need credits detection");
                    progress.Report(100);
                }

                if (completed && !cancellationToken.IsCancellationRequested)
                {
                    stateStore.Save(new NewEpisodeDetectionState
                    {
                        LastMaxInternalId = snapshotMaxInternalId,
                        LastRunStartedUtc = runStartedUtc,
                        LastCompletedRunUtc = DateTime.UtcNow,
                        DeferredEpisodeIds = notReadyIds
                    });
                }
                else
                {
                    _logger.Info("New-episode detection was cancelled; the same episodes will be checked again on the next run");
                }
            }
            finally
            {
                CreditsDetectionService.EndScheduledRun();
            }
        }

        private static bool IsFileReady(Episode episode, out string reason)
        {
            reason = string.Empty;
            var path = episode.Path;

            if (string.IsNullOrEmpty(path))
            {
                reason = "no file path";
                return false;
            }

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    reason = "file not available";
                    return false;
                }

                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < MinimumFileAge)
                {
                    reason = "file was modified in the last few minutes and may still be copying";
                    return false;
                }

                using (fileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                return true;
            }
            catch (IOException)
            {
                reason = "file is locked and may still be copying";
                return false;
            }
            catch (Exception ex)
            {
                reason = $"file could not be opened: {ex.Message}";
                return false;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
