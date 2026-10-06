using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EmbyCredits
{
    public class CreditsDetectionProgress
    {
        private const int MaxDictionarySize = 5000;
        
        public bool IsRunning { get; set; }

        private int _totalItems;
        private int _processedItems;
        private int _successfulItems;
        private int _failedItems;
        private int _skippedItems;
        private int _backupRestoredCount;

        public int TotalItems
        {
            get => Volatile.Read(ref _totalItems);
            set => Volatile.Write(ref _totalItems, value);
        }
        public int ProcessedItems
        {
            get => Volatile.Read(ref _processedItems);
            set => Volatile.Write(ref _processedItems, value);
        }
        public int SuccessfulItems
        {
            get => Volatile.Read(ref _successfulItems);
            set => Volatile.Write(ref _successfulItems, value);
        }
        public int FailedItems
        {
            get => Volatile.Read(ref _failedItems);
            set => Volatile.Write(ref _failedItems, value);
        }
        public int SkippedItems
        {
            get => Volatile.Read(ref _skippedItems);
            set => Volatile.Write(ref _skippedItems, value);
        }
        public int BackupRestoredCount
        {
            get => Volatile.Read(ref _backupRestoredCount);
            set => Volatile.Write(ref _backupRestoredCount, value);
        }
        public string CurrentItem { get; set; } = string.Empty;
        public int CurrentItemProgress { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string CurrentMethod { get; set; } = string.Empty;
        public double AverageProcessingTimeSeconds { get; set; }
        
        private readonly Queue<double> _processingTimes = new Queue<double>();
        private double _processingTimesSum = 0.0;
        private readonly object _processingTimesLock = new object();
        private readonly object _dictionariesLock = new object();
        private DateTime? _currentItemStartTime;
        
        private ConcurrentDictionary<string, string> _failureReasons = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, string> _successDetails = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, string> _skipReasons = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, double> _confidenceScores = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, string> _thumbnailPaths = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, string> _episodeIds = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, string> _appliedRules = new ConcurrentDictionary<string, string>();
        
        public ConcurrentDictionary<string, string> FailureReasons 
        { 
            get => _failureReasons;
            set { lock (_dictionariesLock) { _failureReasons = value; } }
        }
        
        public ConcurrentDictionary<string, string> SuccessDetails 
        { 
            get => _successDetails;
            set { lock (_dictionariesLock) { _successDetails = value; } }
        }
        
        public ConcurrentDictionary<string, string> SkipReasons 
        { 
            get => _skipReasons;
            set { lock (_dictionariesLock) { _skipReasons = value; } }
        }
        
        public ConcurrentDictionary<string, double> ConfidenceScores 
        { 
            get => _confidenceScores;
            set { lock (_dictionariesLock) { _confidenceScores = value; } }
        }

        public ConcurrentDictionary<string, string> ThumbnailPaths 
        { 
            get => _thumbnailPaths;
            set { lock (_dictionariesLock) { _thumbnailPaths = value; } }
        }

        public ConcurrentDictionary<string, string> EpisodeIds 
        { 
            get => _episodeIds;
            set { lock (_dictionariesLock) { _episodeIds = value; } }
        }

        public ConcurrentDictionary<string, string> AppliedRules 
        { 
            get => _appliedRules;
            set { lock (_dictionariesLock) { _appliedRules = value; } }
        }

        public void Reset()
        {
            IsRunning = false;
            Volatile.Write(ref _totalItems, 0);
            Volatile.Write(ref _processedItems, 0);
            Volatile.Write(ref _successfulItems, 0);
            Volatile.Write(ref _failedItems, 0);
            Volatile.Write(ref _skippedItems, 0);
            Volatile.Write(ref _backupRestoredCount, 0);
            CurrentItem = string.Empty;
            CurrentItemProgress = 0;
            StartTime = null;
            EndTime = null;
            CurrentMethod = string.Empty;
            AverageProcessingTimeSeconds = 0;
            lock (_processingTimesLock) 
            { 
                _processingTimes.Clear();
                _processingTimesSum = 0.0;
            }
            _currentItemStartTime = null;
            DeleteOldThumbnails();
            CleanupDictionaries();
        }
        
        public void StartProcessingItem(string itemName, string methodName)
        {
            CurrentItem = itemName;
            CurrentMethod = methodName;
            _currentItemStartTime = DateTime.UtcNow;
        }
        
        public void IncrementSkipped()
        {
            Interlocked.Increment(ref _skippedItems);
            Interlocked.Increment(ref _processedItems);
        }

        public void IncrementBackupRestored() => Interlocked.Increment(ref _backupRestoredCount);

        public void CompleteProcessingItem(bool success)
        {
            if (_currentItemStartTime.HasValue)
            {
                var elapsed = (DateTime.UtcNow - _currentItemStartTime.Value).TotalSeconds;
                lock (_processingTimesLock)
                {
                    _processingTimesSum += elapsed;
                    _processingTimes.Enqueue(elapsed);
                    if (_processingTimes.Count > 100)
                        _processingTimesSum -= _processingTimes.Dequeue();
                    AverageProcessingTimeSeconds = _processingTimesSum / _processingTimes.Count;
                }
                _currentItemStartTime = null;
            }
            
            if (success)
                Interlocked.Increment(ref _successfulItems);
            else
                Interlocked.Increment(ref _failedItems);
            Interlocked.Increment(ref _processedItems);
            
            CheckAndLimitDictionarySize();
        }
        
        private void DeleteOldThumbnails()
        {
            try
            {
                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                    return;

                var thumbnailDir = System.IO.Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                if (!System.IO.Directory.Exists(thumbnailDir))
                    return;

                foreach (var file in System.IO.Directory.GetFiles(thumbnailDir, "*.jpg"))
                {
                    try { System.IO.File.Delete(file); } catch { }
                }
            }
            catch
            {
            }
        }
        
        private void CleanupDictionaries()
        {
            lock (_dictionariesLock)
            {
                _failureReasons.Clear();
                _successDetails.Clear();
                _skipReasons.Clear();
                _confidenceScores.Clear();
                _thumbnailPaths.Clear();
                _episodeIds.Clear();
                _appliedRules.Clear();
            }
        }
        
        internal void CheckAndLimitDictionarySize()
        {
            List<string>? thumbnailFilesToDelete = null;

            lock (_dictionariesLock)
            {
                if (_failureReasons.Count > MaxDictionarySize)
                {
                    var toRemove = _failureReasons.Keys.Take(_failureReasons.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _failureReasons.TryRemove(key, out _);
                }

                if (_successDetails.Count > MaxDictionarySize)
                {
                    var toRemove = _successDetails.Keys.Take(_successDetails.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _successDetails.TryRemove(key, out _);
                }

                if (_skipReasons.Count > MaxDictionarySize)
                {
                    var toRemove = _skipReasons.Keys.Take(_skipReasons.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _skipReasons.TryRemove(key, out _);
                }

                if (_confidenceScores.Count > MaxDictionarySize)
                {
                    var toRemove = _confidenceScores.Keys.Take(_confidenceScores.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _confidenceScores.TryRemove(key, out _);
                }

                if (_thumbnailPaths.Count > MaxDictionarySize)
                {
                    var toRemove = _thumbnailPaths.Keys.Take(_thumbnailPaths.Count - MaxDictionarySize).ToList();
                    thumbnailFilesToDelete = new List<string>(toRemove.Count);
                    foreach (var key in toRemove)
                    {
                        if (_thumbnailPaths.TryGetValue(key, out var thumbnailFile))
                            thumbnailFilesToDelete.Add(thumbnailFile);
                        _thumbnailPaths.TryRemove(key, out _);
                    }
                }

                if (_episodeIds.Count > MaxDictionarySize)
                {
                    var toRemove = _episodeIds.Keys.Take(_episodeIds.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _episodeIds.TryRemove(key, out _);
                }

                if (_appliedRules.Count > MaxDictionarySize)
                {
                    var toRemove = _appliedRules.Keys.Take(_appliedRules.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _appliedRules.TryRemove(key, out _);
                }
            }

            if (thumbnailFilesToDelete != null && thumbnailFilesToDelete.Count > 0)
            {
                try
                {
                    var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                    if (!string.IsNullOrEmpty(pluginDataPath))
                    {
                        var thumbnailDir = System.IO.Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                        foreach (var thumbnailFile in thumbnailFilesToDelete)
                        {
                            try
                            {
                                var fullPath = System.IO.Path.Combine(thumbnailDir, thumbnailFile);
                                if (System.IO.File.Exists(fullPath))
                                    System.IO.File.Delete(fullPath);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        public int PercentComplete => TotalItems > 0 ? (int)((ProcessedItems / (double)TotalItems) * 100) : 0;

        public TimeSpan? EstimatedTimeRemaining
        {
            get
            {
                if (!StartTime.HasValue || ProcessedItems == 0 || TotalItems == 0)
                    return null;

                var elapsed = DateTime.Now - StartTime.Value;
                var avgTimePerItem = elapsed.TotalSeconds / ProcessedItems;
                var remainingItems = TotalItems - ProcessedItems;
                return TimeSpan.FromSeconds(avgTimePerItem * remainingItems);
            }
        }
    }
}
