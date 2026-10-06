using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public class NewEpisodeDetectionState
    {
        public long LastMaxInternalId { get; set; }
        public DateTime? LastRunStartedUtc { get; set; }
        public DateTime? LastCompletedRunUtc { get; set; }
        public List<string> DeferredEpisodeIds { get; set; } = new List<string>();
    }

    public class NewEpisodeDetectionStateStore
    {
        private readonly ILogger _logger;
        private readonly string _stateFilePath;
        private readonly object _lock = new object();

        public NewEpisodeDetectionStateStore(ILogger logger, string dataFolderPath)
        {
            _logger = logger;
            _stateFilePath = Path.Combine(dataFolderPath, "credits_new_episode_detection.json");
        }

        public NewEpisodeDetectionState? Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_stateFilePath))
                        return null;

                    var state = SimpleJson.Deserialize<NewEpisodeDetectionState>(File.ReadAllText(_stateFilePath));
                    if (state != null)
                        state.DeferredEpisodeIds ??= new List<string>();
                    return state;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error loading new-episode detection state", ex);
                    return null;
                }
            }
        }

        public void Save(NewEpisodeDetectionState state)
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_stateFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var tempPath = _stateFilePath + ".tmp";
                    File.WriteAllText(tempPath, SimpleJson.Serialize(state, indented: true));
                    if (File.Exists(_stateFilePath))
                        File.Delete(_stateFilePath);
                    File.Move(tempPath, _stateFilePath);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error saving new-episode detection state", ex);
                }
            }
        }
    }
}
