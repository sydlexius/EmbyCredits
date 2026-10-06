using EmbyCredits.Services.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services
{
    public class CreditMarkerData
    {
        public string? Name { get; set; }
        public long StartPositionTicks { get; set; }
        public string StartTime { get; set; } = string.Empty;
        public string? MarkerType { get; set; }
    }

    public class EpisodeMarkerInfo
    {
        public string EpisodeId { get; set; } = string.Empty;
        public string EpisodeName { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string SeasonEpisode { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public bool HasCreditsMarker { get; set; }
        public List<CreditMarkerData> Markers { get; set; } = new List<CreditMarkerData>();
    }

    public class ChapterMarkerService
    {
        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;

        private static readonly System.Reflection.PropertyInfo? _markerTypeProp =
            typeof(ChapterInfo).GetProperty("MarkerType");

        public const string PostCreditsChapterName = "Post Credits";

        public static bool IsPostCreditsChapter(ChapterInfo chapter)
        {
            return chapter?.Name != null &&
                   string.Equals(chapter.Name.Trim(), PostCreditsChapterName, StringComparison.OrdinalIgnoreCase);
        }

        public ChapterMarkerService(ILogger logger, IItemRepository itemRepository)
        {
            _logger = logger;
            _itemRepository = itemRepository;
        }

        public bool SaveCreditsMarker(Episode episode, double creditsStartSeconds)
        {
            return TrySaveCreditsMarker(episode, creditsStartSeconds, out _);
        }

        public bool TrySaveCreditsMarker(Episode episode, double creditsStartSeconds, out string failureReason)
        {
            failureReason = string.Empty;
            try
            {
                if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
                {
                    _logger.Error($"Cannot save credits marker for {episode.Name} - no valid runtime information");
                    failureReason = "Cannot save credits marker: episode has no valid runtime information";
                    return false;
                }

                var durationSeconds = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                if (creditsStartSeconds < 0)
                {
                    _logger.Error($"Cannot save credits marker for {episode.Name} - negative timestamp: {creditsStartSeconds}s");
                    failureReason = $"Cannot save credits marker: negative timestamp {creditsStartSeconds:F1}s";
                    return false;
                }

                if (creditsStartSeconds >= durationSeconds)
                {
                    _logger.Error($"Cannot save credits marker for {episode.Name} - timestamp ({creditsStartSeconds:F1}s) exceeds video duration ({durationSeconds:F1}s)");
                    failureReason = $"Cannot save credits marker: timestamp {creditsStartSeconds:F1}s exceeds video duration {durationSeconds:F1}s";
                    return false;
                }

                var safeMax = durationSeconds - 30.0;
                if (safeMax <= 0) safeMax = durationSeconds - 1.0;
                if (creditsStartSeconds > safeMax)
                {
                    _logger.Info($"Clamping credits marker for {episode.Name} from {creditsStartSeconds:F3}s to {safeMax:F3}s (within 30s of duration)");
                    creditsStartSeconds = safeMax;
                }

                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                var introAfterCredits = chapters
                    .Where(c => { var mt = GetMarkerType(c); return mt == "IntroStart" || mt == "IntroEnd"; })
                    .Where(c => c.StartPositionTicks / (double)TimeSpan.TicksPerSecond > creditsStartSeconds)
                    .OrderBy(c => c.StartPositionTicks)
                    .FirstOrDefault();

                if (introAfterCredits != null)
                {
                    var introSeconds = introAfterCredits.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
                    _logger.Warn($"Skipping credits marker for {episode.Name}: detected credits at {FormatTime(creditsStartSeconds)} precede {GetMarkerType(introAfterCredits)} marker at {FormatTime(introSeconds)}");
                    failureReason = $"Detected credits at {FormatTime(creditsStartSeconds)} precede the {GetMarkerType(introAfterCredits)} marker at {FormatTime(introSeconds)}";
                    return false;
                }

                var existingCreditsMarkers = chapters.Where(c =>
                {
                    var markerType = GetMarkerType(c);
                    if (markerType == "CreditsStart" || markerType == "Credits")
                        return true;

                    if (IsPostCreditsChapter(c))
                        return false;

                    var duration = episode.RunTimeTicks ?? 0;
                    var isInLaterPart = duration > 0 && (double)c.StartPositionTicks / duration >= 0.40;

                    if (c.Name != null && isInLaterPart)
                    {
                        var nameLower = c.Name.ToLowerInvariant();
                        if (nameLower.Contains("credit") || 
                            nameLower.Contains("end title") ||
                            nameLower.Contains("ending") ||
                            nameLower == "credits")
                            return true;
                    }

                    if (duration > 0)
                    {
                        var positionRatio = (double)c.StartPositionTicks / duration;
                        if (positionRatio >= 0.80 && (string.IsNullOrEmpty(c.Name) || c.Name!.Length < 3))
                            return true;
                    }

                    return false;
                }).ToList();

                if (existingCreditsMarkers.Count > 0)
                {
                    foreach (var marker in existingCreditsMarkers)
                    {
                        chapters.Remove(marker);
                    }
                    _logger.Info($"Removed {existingCreditsMarkers.Count} existing credits marker(s)");
                }

                var creditsMarker = new ChapterInfo
                {
                    Name = "Credits",
                    StartPositionTicks = (long)(creditsStartSeconds * TimeSpan.TicksPerSecond)
                };

                var markerTypeSet = SetMarkerType(creditsMarker, MarkerType.CreditsStart);
                _logger.Info($"MarkerType.CreditsStart set: {markerTypeSet}");

                if (markerTypeSet)
                {
                    var verifyType = GetMarkerType(creditsMarker);
                    _logger.Info($"Verified MarkerType value: {verifyType}");
                }

                int insertIndex = chapters.FindIndex(c => c.StartPositionTicks > creditsMarker.StartPositionTicks);
                if (insertIndex == -1)
                {
                    chapters.Add(creditsMarker);
                }
                else
                {
                    chapters.Insert(insertIndex, creditsMarker);
                }
                _logger.Info($"Added new CreditsStart marker at {FormatTime(creditsStartSeconds)} at index {(insertIndex == -1 ? chapters.Count - 1 : insertIndex)}");

                try
                {
                    _itemRepository.SaveChapters(episode.InternalId, chapters);
                    _logger.Info($"Saved chapter markers for {episode.Name}");
                    return true;
                }
                catch (Exception saveEx)
                {
                    _logger.ErrorException($"Failed to save chapters to repository for {episode.Name}", saveEx);
                    failureReason = $"Failed to save credits marker: {saveEx.Message}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error saving credits chapter marker for {episode.Name}", ex);
                failureReason = $"Failed to save credits marker: {ex.Message}";
                return false;
            }
        }

        public bool TryRemoveCreditsMarkers(Episode episode, out string failureReason)
        {
            failureReason = string.Empty;
            try
            {
                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
                var removed = chapters.RemoveAll(c => GetMarkerType(c) == "CreditsStart");
                if (removed == 0)
                    return true;

                _itemRepository.SaveChapters(episode.InternalId, chapters);
                _logger.Info($"Removed {removed} credits marker(s) from {episode.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error removing credits marker from {episode.Name}", ex);
                failureReason = $"Failed to remove credits marker: {ex.Message}";
                return false;
            }
        }

        public bool TrySetPostCreditsChapter(Episode episode, double? startSeconds, out string failureReason)
        {
            failureReason = string.Empty;
            try
            {
                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
                var existing = chapters.Where(IsPostCreditsChapter).ToList();

                if (!startSeconds.HasValue)
                {
                    if (existing.Count == 0)
                        return true;

                    foreach (var chapter in existing)
                        chapters.Remove(chapter);

                    _itemRepository.SaveChapters(episode.InternalId, chapters);
                    _logger.Info($"Removed '{PostCreditsChapterName}' chapter from {episode.Name} - no post-credits scene found");
                    return true;
                }

                var durationSeconds = episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0
                    ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : 0;
                if (startSeconds.Value <= 0 || (durationSeconds > 0 && startSeconds.Value >= durationSeconds))
                {
                    failureReason = $"'{PostCreditsChapterName}' chapter time {FormatTime(startSeconds.Value)} is outside the episode";
                    return false;
                }

                var startTicks = (long)(startSeconds.Value * TimeSpan.TicksPerSecond);
                if (existing.Count == 1 && Math.Abs(existing[0].StartPositionTicks - startTicks) < TimeSpan.TicksPerSecond)
                    return true;

                foreach (var chapter in existing)
                    chapters.Remove(chapter);

                var postCreditsChapter = new ChapterInfo
                {
                    Name = PostCreditsChapterName,
                    StartPositionTicks = startTicks
                };
                SetMarkerType(postCreditsChapter, MarkerType.Chapter);

                var insertIndex = chapters.FindIndex(c => c.StartPositionTicks > startTicks);
                if (insertIndex == -1)
                    chapters.Add(postCreditsChapter);
                else
                    chapters.Insert(insertIndex, postCreditsChapter);

                _itemRepository.SaveChapters(episode.InternalId, chapters);
                _logger.Info($"Added '{PostCreditsChapterName}' chapter at {FormatTime(startSeconds.Value)} for {episode.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error saving '{PostCreditsChapterName}' chapter for {episode.Name}", ex);
                failureReason = $"Failed to save '{PostCreditsChapterName}' chapter: {ex.Message}";
                return false;
            }
        }

        public List<EpisodeMarkerInfo> GetSeriesMarkers(List<Episode> episodes)
        {
            var result = new List<EpisodeMarkerInfo>(episodes.Count);

            foreach (var episode in episodes)
            {
                try
                {
                    var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                    var creditsMarkers = chapters.Where(c =>
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart" || markerType == "Credits";
                    }).Select(c => new CreditMarkerData
                    {
                        Name = c.Name,
                        StartPositionTicks = c.StartPositionTicks,
                        StartTime = FormatTime(c.StartPositionTicks / TimeSpan.TicksPerSecond),
                        MarkerType = GetMarkerType(c)
                    }).ToList();

                    result.Add(new EpisodeMarkerInfo
                    {
                        EpisodeId = episode.Id.ToString(),
                        EpisodeName = episode.Name,
                        Season = episode.ParentIndexNumber,
                        Episode = episode.IndexNumber,
                        SeasonEpisode = $"S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}",
                        Duration = episode.RunTimeTicks.HasValue ? FormatTime(episode.RunTimeTicks.Value / TimeSpan.TicksPerSecond) : "Unknown",
                        DurationSeconds = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0,
                        HasCreditsMarker = creditsMarkers.Count > 0,
                        Markers = creditsMarkers
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Error getting markers for episode {episode.Name}: {ex.Message}");
                }
            }

            return result;
        }

        public List<ChapterInfo> GetChapters(BaseItem item)
        {
            return _itemRepository.GetChapters(item)?.ToList() ?? new List<ChapterInfo>();
        }

        public bool TryImportEmbeddedCreditChapter(Episode episode)
        {
            try
            {
                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
                if (chapters.Count == 0)
                    return false;

                var durationTicks = episode.RunTimeTicks ?? 0;

                var creditsChapter = chapters.FirstOrDefault(c =>
                {
                    if (string.IsNullOrWhiteSpace(c.Name) || IsPostCreditsChapter(c))
                        return false;

                    var name = c.Name.Trim().ToLowerInvariant();
                    if (!name.Contains("credit") &&
                        name != "end title" &&
                        name != "end titles" &&
                        name != "end card" &&
                        name != "closing")
                        return false;

                    if (durationTicks > 0 && (double)c.StartPositionTicks / durationTicks < 0.40)
                        return false;

                    if (durationTicks > 0 && c.StartPositionTicks >= durationTicks)
                    {
                        _logger.Warn($"Ignoring embedded chapter '{c.Name}' at {FormatTime(c.StartPositionTicks / (double)TimeSpan.TicksPerSecond)} for {episode.Name} - exceeds episode runtime {FormatTime(durationTicks / (double)TimeSpan.TicksPerSecond)}");
                        return false;
                    }

                    return true;
                });

                if (creditsChapter == null)
                    return false;

                var existingType = GetMarkerType(creditsChapter);
                if (existingType == "CreditsStart")
                {
                    _logger.Info($"Episode {episode.Name} already has a properly typed embedded credits chapter");
                    return true;
                }

                var creditsStartSeconds = creditsChapter.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
                _logger.Info($"Importing embedded credits chapter '{creditsChapter.Name}' at {FormatTime(creditsStartSeconds)} for {episode.Name}");
                return SaveCreditsMarker(episode, creditsStartSeconds);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error checking embedded credit chapters for {episode.Name}: {ex.Message}");
                return false;
            }
        }

        private string? GetMarkerType(ChapterInfo chapter)
        {
            try
            {
                if (chapter == null)
                    return null;

                if (_markerTypeProp != null && _markerTypeProp.CanRead)
                {
                    var value = _markerTypeProp.GetValue(chapter);
                    return value?.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error reading MarkerType property (Emby version compatibility issue): {ex.Message}");
            }
            return null;
        }

        private bool SetMarkerType(ChapterInfo chapter, MarkerType markerType)
        {
            try
            {
                if (chapter == null)
                    return false;

                if (_markerTypeProp != null && _markerTypeProp.CanWrite)
                {
                    _markerTypeProp.SetValue(chapter, markerType);
                    return true;
                }
                else
                {
                    _logger.Debug("MarkerType property not found or not writable (Emby version may not support this feature)");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error setting MarkerType property (Emby version compatibility issue): {ex.Message}");
            }
            return false;
        }

        private string FormatTime(double seconds) => ItemLookupHelper.FormatTime(seconds);

        public static bool IsAutoSkipExcluded(Episode episode)
        {
            var excludedIds = Plugin.Instance?.Configuration?.AutoSkipExcludedSeriesIds;
            if (excludedIds == null || excludedIds.Length == 0)
                return false;

            var seriesId = episode.Series?.Id.ToString();
            if (string.IsNullOrEmpty(seriesId))
                return false;

            return excludedIds.Any(id => string.Equals(id, seriesId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
