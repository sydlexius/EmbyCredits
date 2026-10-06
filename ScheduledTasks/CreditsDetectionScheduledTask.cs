using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;

namespace EmbyCredits.ScheduledTasks
{
    public class CreditsDetectionScheduledTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly IItemRepository _itemRepository;

        public string Name => "Detect Credits in TV Shows";
        public string Description => "Scans TV shows in selected libraries and detects end credits timestamps";
        public string Category => ScheduledDetectionRunner.TaskCategory;
        public string Key => "CreditsDetection";

        public CreditsDetectionScheduledTask(
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
                _logger.Info("Another detection process is already running; skipping this scheduled run");
                return;
            }

            try
            {
                var config = Plugin.Instance.Configuration;

                CreditsDetectionService.SetLibraryManager(_libraryManager);
                CreditsDetectionService.SetItemRepository(_itemRepository);

                var allEpisodes = new List<Episode>();

                if (config.OnlyProcessNewEpisodes && Plugin.PendingEpisodesService != null)
                {
                    var pendingIds = Plugin.PendingEpisodesService.GetPendingEpisodeIds();
                    _logger.Info($"OnlyProcessNewEpisodes: {pendingIds.Count} pending episode(s) in queue");

                    if (pendingIds.Count == 0)
                    {
                        _logger.Info("No pending episodes to process");
                        return;
                    }

                    foreach (var id in pendingIds)
                    {
                        Episode? ep = null;
                        if (Guid.TryParse(id, out var guid))
                            ep = _libraryManager.GetItemById(guid) as Episode;

                        if (ep == null || ep.ParentIndexNumber == null || ep.ParentIndexNumber == 0 || ep.Series == null)
                        {
                            Plugin.PendingEpisodesService.MarkProcessed(id);
                            continue;
                        }

                        allEpisodes.Add(ep);
                    }

                    if (allEpisodes.Count == 0)
                    {
                        _logger.Info("No resolvable pending episodes found (items may have been removed from library)");
                        return;
                    }

                    allEpisodes = allEpisodes.GroupBy(e => e.Id).Select(g => g.First()).ToList();
                    _logger.Info($"Pending queue: {allEpisodes.Count} episode(s) to process");
                }
                else
                {
                    allEpisodes = ScheduledDetectionRunner.GetLibraryEpisodes(_libraryManager, config, _logger, cancellationToken);
                }

                if (allEpisodes.Count == 0)
                {
                    _logger.Info("No episodes found to process");
                    return;
                }

                _logger.Info($"Found {allEpisodes.Count} total episodes");

                var episodesToProcess = ScheduledDetectionRunner.FilterEpisodesNeedingDetection(
                    allEpisodes, config, _itemRepository, _logger, config.ScheduledTaskOnlyProcessMissing, cancellationToken);

                if (episodesToProcess.Count == 0)
                {
                    _logger.Info("All episodes already have credits or were previously processed, nothing to process");
                    return;
                }

                await ScheduledDetectionRunner.RunDetectionAsync(episodesToProcess, _logger, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CreditsDetectionService.EndScheduledRun();
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
