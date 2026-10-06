using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{
    public class ChromaprintDetection : BaseDetectionMethod
    {
        public override string MethodName => "Chromaprint Audio Fingerprint Detection";
        
        private static readonly AsyncLocal<double?> _calculatedConfidenceState = new AsyncLocal<double?>();
        private double _calculatedConfidence
        {
            get => _calculatedConfidenceState.Value ?? 0.90;
            set => _calculatedConfidenceState.Value = value;
        }
        public override double Confidence => _calculatedConfidence;
        
        private ConcurrentDictionary<string, double> _batchConfidenceScores = new ConcurrentDictionary<string, double>();
        
        public override int Priority => Configuration.ChromaprintDetectionPriority;
        public override bool IsEnabled => Configuration.DetectionMode == DetectionMode.HashOnly || 
                                          Configuration.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                          Configuration.DetectionMode == DetectionMode.OcrWithHashFallback;

        private static readonly DetectionTimestampCache _cache = new DetectionTimestampCache();

        public ChromaprintDetection(ILogger logger, PluginConfiguration configuration)
            : base(logger, configuration)
        {
        }

        public static void ClearSeriesCache(string seriesId, int seasonNumber) =>
            _cache.ClearSeries(seriesId, seasonNumber);

        public static void ClearAllCache() =>
            _cache.ClearAll();

        public override Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0.0);
        }

        public Task<double> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber, CancellationToken cancellationToken = default)
        {
            LastError = "Chromaprint requires batch comparison across episodes; no batch result was available for this episode";
            return Task.FromResult(0.0);
        }

        public async Task<Dictionary<string, double>> DetectCreditsForSeason(List<(string EpisodeId, string VideoPath, double Duration)> episodes, string seriesId, int seasonNumber, CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, double>();
            _batchConfidenceScores.Clear();
            
            try
            {
                LogInfo($"Processing {episodes.Count} episodes in batch");
                
                var analysisPercent = Configuration.ChromaprintAnalysisPercent / 100.0;
                var minIntroDuration = Configuration.ChromaprintMinDuration;
                var maxIntroDuration = Configuration.ChromaprintMaxDuration;
                
                LogInfo("Attempting chromaprint audio fingerprinting...");
                var fingerprintResults = await DetectCreditsUsingChromaprint(episodes, analysisPercent, minIntroDuration, maxIntroDuration, cancellationToken);
                
                var analyzedEpisodes = new HashSet<string>(fingerprintResults.Keys);
                
                foreach (var kvp in fingerprintResults)
                {
                    results[kvp.Key] = kvp.Value;
                    LogInfo($"Episode {kvp.Key}: Credits detected at {FormatTime(kvp.Value)} using chromaprint");
                }
                
                LogInfo($"Chromaprint successfully analyzed {analyzedEpisodes.Count}/{episodes.Count} episodes");
                
                if (analyzedEpisodes.Count < episodes.Count)
                {
                    LogInfo($"Chromaprint completed. {episodes.Count - analyzedEpisodes.Count} episodes will use fallback detection methods.");
                }
                
                LogInfo($"Batch processing complete: {results.Count}/{episodes.Count} episodes analyzed successfully");
            }
            catch (Exception ex)
            {
                LogError("Error in batch processing", ex);
            }
            
            return results;
        }
        
        public double GetBatchConfidence(string episodeId)
        {
            return _batchConfidenceScores.TryGetValue(episodeId, out var confidence) ? confidence : 0.90;
        }
        
        private async Task<Dictionary<string, double>> DetectCreditsUsingChromaprint(
            List<(string EpisodeId, string VideoPath, double Duration)> episodes,
            double analysisPercent,
            double minIntroDuration,
            double maxIntroDuration,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, double>();
            
            try
            {
                var episodeFingerprints = new ConcurrentDictionary<string, (uint[] fingerprint, double startTime, double secondsPerPoint)>();
                var episodeIntroFingerprints = new ConcurrentDictionary<string, uint[]>();
                var checkRepeatingSegments = Configuration.ChromaprintRejectRepeatingSegments;

                var maxConcurrentFingerprints = Math.Max(1, Configuration.ChromaprintParallelSessions);
                using var fpSemaphore = new SemaphoreSlim(maxConcurrentFingerprints, maxConcurrentFingerprints);
                var fingerprintsCompleted = 0;

                var fingerprintTasks = episodes.Select(async episode =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    var stopSecondsFromEnd = Configuration.ChromaprintStopSecondsFromEnd;
                    var analysisEndTime = Math.Max(0, episode.Duration - stopSecondsFromEnd);
                    var analysisStartTime = Math.Max(0, episode.Duration * (1.0 - analysisPercent));

                    try
                    {
                        await fpSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        await CreditsDetectionService.WaitWhilePlaybackActiveAsync(cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        LogDebug($"Generating chromaprint for episode {episode.EpisodeId} from{FormatTime(analysisStartTime)} to {FormatTime(analysisEndTime)} (duration: {analysisEndTime - analysisStartTime:F1}s, excluding last {stopSecondsFromEnd}s)");
                        var fingerprint = await Task.Run(() => FFmpegHelper.GenerateChromaprint(episode.VideoPath, analysisStartTime, analysisEndTime, Logger, Configuration.ChromaprintFfmpegThreads, Configuration, cancellationToken), cancellationToken).ConfigureAwait(false);

                        if (fingerprint.Length > 0)
                        {
                            var secondsPerPoint = FFmpegHelper.ChromaprintSecondsPerPoint;
                            episodeFingerprints[episode.EpisodeId] = (fingerprint, analysisStartTime, secondsPerPoint);
                            LogDebug($"Episode {episode.EpisodeId}: Generated {fingerprint.Length} fingerprint points ({secondsPerPoint:F4}s/point)");
                        }
                        else
                        {
                            LogDebug($"Episode {episode.EpisodeId}: Failed to generate fingerprint");
                        }

                        if (checkRepeatingSegments)
                        {
                            var introEndTime = Math.Min(episode.Duration, Configuration.ChromaprintRepeatingSegmentCheckSeconds);
                            if (introEndTime > 1.0 && introEndTime < analysisStartTime)
                            {
                                var introFingerprint = await Task.Run(() => FFmpegHelper.GenerateChromaprint(episode.VideoPath, 0, introEndTime, Logger, Configuration.ChromaprintFfmpegThreads, Configuration, cancellationToken), cancellationToken).ConfigureAwait(false);
                                if (introFingerprint.Length > 0)
                                {
                                    episodeIntroFingerprints[episode.EpisodeId] = introFingerprint;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Failed to generate fingerprint for {episode.EpisodeId}: {ex.Message}");
                    }
                    finally
                    {
                        var completed = Interlocked.Increment(ref fingerprintsCompleted);
                        UpdateProgress((completed * 100.0) / episodes.Count, $"Chroma: {completed}/{episodes.Count} analyzed");
                        fpSemaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(fingerprintTasks).ConfigureAwait(false);
                
                if (episodeFingerprints.Count >= 2)
                {
                    LogInfo($"Comparing {episodeFingerprints.Count} episode fingerprints using subsequence matching...");
                    
                    var episodeOffsets = new Dictionary<string, List<int>>(episodeFingerprints.Count);
                    var episodeMatchScores = new Dictionary<string, List<double>>(episodeFingerprints.Count);
                    var episodeMatchLengths = new Dictionary<string, List<int>>(episodeFingerprints.Count);
                    var episodeTopPeers = new Dictionary<string, List<(string peerId, int peerOffset, double score)>>();
                    var allMatchScores = new List<double>();
                    var allMatchLengths = new List<int>();
                    
                    var episodeList = episodes
                        .Select(e => e.EpisodeId)
                        .Where(id => episodeFingerprints.ContainsKey(id))
                        .Distinct()
                        .ToList();

                    var pairs = new List<(int i, int j)>();
                    for (int i = 0; i < episodeList.Count; i++)
                    {
                        for (int j = i + 1; j < episodeList.Count; j++)
                        {
                            pairs.Add((i, j));
                        }
                    }

                    var pairMatches = new (double score, int offsetFp1, int offsetFp2, int matchLength)[pairs.Count];
                    var comparisonsCompleted = 0;
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
                    };

                    Parallel.For(0, pairs.Count, parallelOptions, (pairIndex, loopState) =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            loopState.Stop();
                            return;
                        }

                        var (i, j) = pairs[pairIndex];
                        pairMatches[pairIndex] = FFmpegHelper.FindBestMatchingSubsequence(
                            episodeFingerprints[episodeList[i]].fingerprint,
                            episodeFingerprints[episodeList[j]].fingerprint,
                            minWindowSize: 100);

                        var completed = Interlocked.Increment(ref comparisonsCompleted);
                        if (completed % 10 == 0 || completed == pairs.Count)
                        {
                            UpdateProgress((completed * 100.0) / pairs.Count, $"Chroma: compared {completed}/{pairs.Count}");
                        }
                    });

                    if (cancellationToken.IsCancellationRequested)
                        return results;

                    for (int i = 0; i < episodeList.Count; i++)
                    {
                        episodeOffsets[episodeList[i]] = new List<int>();
                        episodeMatchScores[episodeList[i]] = new List<double>();
                        episodeMatchLengths[episodeList[i]] = new List<int>();
                    }

                    for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                    {
                        var ep1 = episodeList[pairs[pairIndex].i];
                        var ep2 = episodeList[pairs[pairIndex].j];
                        var match = pairMatches[pairIndex];
                        
                        LogDebug($"Comparison {ep1} <-> {ep2}: score={match.score:F3}, offset1={match.offsetFp1}, offset2={match.offsetFp2}, matchedLength={match.matchLength} points ({match.matchLength * episodeFingerprints[ep1].secondsPerPoint:F1}s)");
                        allMatchScores.Add(match.score);
                        allMatchLengths.Add(match.matchLength);
                        
                        episodeOffsets[ep1].Add(match.offsetFp1);
                        episodeOffsets[ep2].Add(match.offsetFp2);
                        episodeMatchScores[ep1].Add(match.score);
                        episodeMatchScores[ep2].Add(match.score);
                        episodeMatchLengths[ep1].Add(match.matchLength);
                        episodeMatchLengths[ep2].Add(match.matchLength);

                        if (!episodeTopPeers.TryGetValue(ep1, out var peers1))
                            episodeTopPeers[ep1] = peers1 = new List<(string peerId, int peerOffset, double score)>();
                        peers1.Add((ep2, match.offsetFp2, match.score));

                        if (!episodeTopPeers.TryGetValue(ep2, out var peers2))
                            episodeTopPeers[ep2] = peers2 = new List<(string peerId, int peerOffset, double score)>();
                        peers2.Add((ep1, match.offsetFp1, match.score));
                    }
                    
                    foreach (var key in episodeTopPeers.Keys.ToList())
                    {
                        episodeTopPeers[key] = episodeTopPeers[key].OrderByDescending(p => p.score).ToList();
                    }
                    
                    var dynamicThreshold = CalculateDynamicThreshold(allMatchScores);
                    LogInfo($"Dynamic match threshold calculated: {dynamicThreshold:F3} (from {allMatchScores.Count} comparisons)");
                    
                    const double unreliableAverageLength = 300;
                    const double unreliableScoreThreshold = 0.65;
                    var averageMatchLength = allMatchLengths.Count > 0 ? allMatchLengths.Average() : 0;
                    if (averageMatchLength < unreliableAverageLength && dynamicThreshold < unreliableScoreThreshold)
                    {
                        LogInfo($"Chromaprint audio matching does not look reliable for this batch (avg matched length={averageMatchLength:F0} points, threshold={dynamicThreshold:F3}) - episodes likely don't share consistent audio. Skipping Chromaprint results for this batch so other detection methods can be used.");
                        return results;
                    }
                    
                    foreach (var epId in episodeList)
                    {
                        if (episodeMatchScores.ContainsKey(epId))
                        {
                            var scores = episodeMatchScores[epId];
                            var offsets = episodeOffsets[epId];
                            var lengths = episodeMatchLengths[epId];
                            var filtered = new List<int>();
                            var filteredScores = new List<double>();
                            var filteredLengths = new List<int>();
                            
                            for (int i = 0; i < scores.Count; i++)
                            {
                                if (scores[i] >= dynamicThreshold)
                                {
                                    filtered.Add(offsets[i]);
                                    filteredScores.Add(scores[i]);
                                    filteredLengths.Add(lengths[i]);
                                }
                            }
                            
                            episodeOffsets[epId] = filtered;
                            episodeMatchScores[epId] = filteredScores;
                            episodeMatchLengths[epId] = filteredLengths;
                            
                            if (filtered.Count != offsets.Count)
                            {
                                LogDebug($"Episode {epId}: Filtered {offsets.Count - filtered.Count} low-confidence matches");
                            }
                        }
                    }
                    
                    var refinementIndex = 0;
                    foreach (var episode in episodes)
                    {
                        refinementIndex++;
                        UpdateProgress((refinementIndex * 100.0) / episodes.Count, $"Chroma: refining {refinementIndex}/{episodes.Count}");

                        if (!episodeFingerprints.ContainsKey(episode.EpisodeId))
                            continue;
                        
                        var epId = episode.EpisodeId;
                        var startTime = episodeFingerprints[epId].startTime;
                        var secondsPerPoint = episodeFingerprints[epId].secondsPerPoint;
                        
                        if (!episodeOffsets.ContainsKey(epId) || episodeOffsets[epId].Count == 0)
                        {
                            LogDebug($"Episode {epId}: No high-confidence matches found");
                            continue;
                        }
                        
                        var rawOffsets = episodeOffsets[epId];
                        var scores = episodeMatchScores[epId];
                        var lengths = episodeMatchLengths[epId];
                        
                        var (filteredOffsets, filteredScores, filteredLengths) = FilterOutliersIQR(rawOffsets, scores, lengths);
                        
                        if (filteredOffsets.Count == 0)
                        {
                            LogDebug($"Episode {epId}: All offsets filtered as outliers");
                            continue;
                        }
                        
                        if (filteredOffsets.Count < rawOffsets.Count)
                        {
                            LogDebug($"Episode {epId}: Removed {rawOffsets.Count - filteredOffsets.Count} outlier offset(s) using IQR method");
                        }
                        
                        var combinedWeights = filteredScores.Zip(filteredLengths, (score, length) => score * length).ToList();
                        var weightedMedianOffset = CalculateWeightedMedian(filteredOffsets, combinedWeights);
                        
                        var coarseOffsetSeconds = weightedMedianOffset * secondsPerPoint;
                        var coarseCreditsStartTime = startTime + coarseOffsetSeconds;
                        
                        LogDebug($"Episode {epId}: Coarse detection - {filteredOffsets.Count} matches, weighted median offset={weightedMedianOffset} points ({coarseOffsetSeconds:F1}s)");
                        
                        if (Configuration.ChromaprintRejectRepeatingSegments &&
                            episodeIntroFingerprints.TryGetValue(epId, out var introFingerprint) &&
                            IsRepeatingSegment(episodeFingerprints[epId].fingerprint, (int)weightedMedianOffset, introFingerprint))
                        {
                            LogInfo($"Episode {epId}: Rejected - matched segment resembles a repeating intro/bumper rather than unique credits");
                            continue;
                        }
                        
                        double refinedCreditsStartTime = 0;
                        if (episodeTopPeers.TryGetValue(epId, out var topPeers) && topPeers.Count > 0)
                        {
                            await CreditsDetectionService.WaitWhilePlaybackActiveAsync(cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();

                            var (fineFingerprint, fineStartTime, coarseOffsetInFine, fineSecondsPerPoint) = await GenerateFineFingerprint(episode.VideoPath, coarseCreditsStartTime, cancellationToken);

                            if (fineFingerprint.Length > 0)
                            {
                                var peersToUse = Configuration.ChromaprintUseMultiPeerConsensus
                                    ? topPeers.Take(Math.Max(1, Configuration.ChromaprintConsensusPeerCount)).ToList()
                                    : topPeers.Take(1).ToList();

                                var candidateRefinements = await Task.Run(() =>
                                {
                                    var results = new List<(double timestamp, double weight)>();
                                    foreach (var peer in peersToUse)
                                    {
                                        if (!episodeFingerprints.TryGetValue(peer.peerId, out var peerFpData))
                                            continue;

                                        var refined = FindRefinedTimestampAgainstReference(
                                            fineFingerprint, fineStartTime, coarseOffsetInFine, fineSecondsPerPoint,
                                            peerFpData.fingerprint, peer.peerOffset, cancellationToken);

                                        if (refined > 0)
                                            results.Add((refined, peer.score));
                                    }
                                    return results;
                                }, cancellationToken);

                                if (candidateRefinements.Count > 0)
                                {
                                    refinedCreditsStartTime = candidateRefinements.Count > 1
                                        ? CalculateWeightedAverage(candidateRefinements)
                                        : candidateRefinements[0].timestamp;

                                    if (candidateRefinements.Count > 1)
                                    {
                                        LogDebug($"Episode {epId}: Multi-peer consensus refinement using {candidateRefinements.Count} peer(s)");
                                    }
                                }
                            }
                        }
                        
                        var finalTimestamp = refinedCreditsStartTime > 0 ? refinedCreditsStartTime : coarseCreditsStartTime;
                        var creditsDuration = episode.Duration - finalTimestamp;
                        
                        if (refinedCreditsStartTime > 0 && Math.Abs(refinedCreditsStartTime - coarseCreditsStartTime) > 0.5)
                        {
                            LogInfo($"Episode {epId}: Fine-grained refinement adjusted timestamp: {FormatTime(coarseCreditsStartTime)} -> {FormatTime(refinedCreditsStartTime)} (delta: {Math.Abs(refinedCreditsStartTime - coarseCreditsStartTime):F1}s)");
                        }
                        
                        LogDebug($"Episode {epId}: Final credits at {FormatTime(finalTimestamp)}, duration={creditsDuration:F1}s");
                        
                        if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                        {
                            results[epId] = finalTimestamp;
                            
                            double avgScore = filteredScores.Count > 0 ? filteredScores.Average() : 0.90;
                            _batchConfidenceScores[epId] = avgScore;
                            
                            LogInfo($"Episode {epId}: Credits at {FormatTime(finalTimestamp)} (matched with {filteredOffsets.Count} episodes, confidence: {avgScore:F3})");
                        }
                        else
                        {
                            LogDebug($"Episode {epId}: Rejected - credits duration {creditsDuration:F1}s outside range {minIntroDuration}-{maxIntroDuration}");
                        }
                    }
                }
                else
                {
                    LogDebug("Not enough episodes with valid fingerprints for comparison");
                }
            }
            catch (OperationCanceledException)
            {
                LogInfo("Chromaprint detection cancelled");
            }
            catch (Exception ex)
            {
                LogError("Error in chromaprint detection", ex);
            }
            
            return results;
        }
        
        private void CleanupCache()
        {
            _cache.EnsureCleanedIfNeeded();
        }

        private async Task<double> DetectBlackFrameTransition(string videoPath, double startTime, double duration, CancellationToken cancellationToken)
        {
            try
            {
                var blackThreshold = Configuration.ChromaprintBlackFrameThreshold;
                var minDuration = Configuration.ChromaprintBlackFrameMinDuration;
                
                LogDebug($"Detecting black frames (threshold: {blackThreshold}, min duration: {minDuration}s)");
                
                var tempFolder = GetTempFolder();
                var ffmpegPath = FFmpegHelper.GetFfmpegPath();
                
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    LogWarn("FFmpeg not found, skipping black frame detection");
                    return 0;
                }
                
                var endTime = duration - Configuration.ChromaprintStopSecondsFromEnd;
                var analysisDuration = endTime - startTime;
                
                if (analysisDuration <= 0)
                {
                    LogDebug("Analysis duration too short, skipping black frame detection");
                    return 0;
                }
                
                var normalizedVideoPath = FFmpegHelper.ResolveInputPath(videoPath);

                var chromaBlackStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                if (Configuration.ChromaprintFfmpegThreads > 0)
                {
                    chromaBlackStartInfo.ArgumentList().Add("-threads");
                    chromaBlackStartInfo.ArgumentList().Add(Configuration.ChromaprintFfmpegThreads.ToString(CultureInfo.InvariantCulture));
                }
                chromaBlackStartInfo.ArgumentList().Add("-ss");
                chromaBlackStartInfo.ArgumentList().Add(startTime.ToString(CultureInfo.InvariantCulture));
                chromaBlackStartInfo.ArgumentList().Add("-t");
                chromaBlackStartInfo.ArgumentList().Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
                FFmpegHelper.AddStrmNetworkResilienceArgs(chromaBlackStartInfo, videoPath, normalizedVideoPath, Configuration);
                chromaBlackStartInfo.ArgumentList().Add("-i");
                chromaBlackStartInfo.ArgumentList().Add(normalizedVideoPath);
                chromaBlackStartInfo.ArgumentList().Add("-vf");
                chromaBlackStartInfo.ArgumentList().Add($"blackdetect=d={minDuration.ToString(CultureInfo.InvariantCulture)}:pix_th={blackThreshold.ToString(CultureInfo.InvariantCulture)}");
                chromaBlackStartInfo.ArgumentList().Add("-an");
                chromaBlackStartInfo.ArgumentList().Add("-f");
                chromaBlackStartInfo.ArgumentList().Add("null");
                chromaBlackStartInfo.ArgumentList().Add("-");
                
                LogInfo($"Executing FFmpeg black detection: {ffmpegPath} -ss {startTime} -t {analysisDuration} -i <path> -vf blackdetect...");
                
                using (var process = new Process { StartInfo = chromaBlackStartInfo })
                {
                    if (Configuration.ChromaprintLowerProcessPriority)
                    {
                        CpuThrottler.SetProcessPriority(process, Configuration);
                    }
                    
                    var output = new List<string>();
                    DataReceivedEventHandler errorHandler = (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                        }
                    };
                    
                    try
                    {
                        process.ErrorDataReceived += errorHandler;
                        
                        chromaBlackStartInfo.ApplyArgumentList();
                        process.Start();
                        
                        if (Configuration.ChromaprintLowerProcessPriority)
                        {
                            CpuThrottler.SetProcessPriority(process, Configuration);
                        }
                        
                        process.BeginErrorReadLine();

                        var resolvedTimeout = FFmpegHelper.ResolveOperationTimeout(Configuration, defaultMinutes: null);
                        using var timeoutCts = resolvedTimeout.HasValue ? new CancellationTokenSource(resolvedTimeout.Value) : null;
                        using var linkedCts = timeoutCts != null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token) : null;
                        var effectiveToken = linkedCts?.Token ?? cancellationToken;

                        await process.WaitForExitAsync(effectiveToken);
                    
                        if (Configuration.ChromaprintDelayBetweenOperationsMs > 0)
                        {
                            await Task.Delay(Configuration.ChromaprintDelayBetweenOperationsMs, cancellationToken);
                        }
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
                    
                    var blackFrames = new List<double>();
                    foreach (var line in output)
                    {
                        if (line.Contains("blackdetect") && line.Contains("black_start:"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"black_start:(\d+\.?\d*)");
                            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var blackStart))
                            {
                                var adjustedTime = startTime + blackStart;
                                blackFrames.Add(adjustedTime);
                                LogDebug($"Found black frame at {FormatTime(adjustedTime)}");
                            }
                        }
                    }
                    
                    output.Clear();
                    output.TrimExcess();
                    output = null;
                    
                    if (blackFrames.Count > 0)
                    {
                        var result = blackFrames.First();
                        blackFrames.Clear();
                        blackFrames.TrimExcess();
                        return result;
                    }
                    
                    blackFrames.Clear();
                    blackFrames.TrimExcess();
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Error detecting black frames: {ex.Message}");
                return 0;
            }
        }

        private string GetTempFolder()
        {
            if (!string.IsNullOrWhiteSpace(Configuration.TempFolderPath) && Directory.Exists(Configuration.TempFolderPath))
            {
                return Configuration.TempFolderPath;
            }
            return Path.GetTempPath();
        }

        private double CalculateDynamicThreshold(List<double> scores)
        {
            if (scores.Count == 0)
                return 0.75;
            
            if (scores.Count < 3)
                return scores.Min() * 0.95;
            
            var sortedScores = scores.OrderBy(s => s).ToList();
            var mean = scores.Average();
            var variance = scores.Select(s => Math.Pow(s - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            
            var threshold = Math.Max(mean - stdDev, sortedScores[sortedScores.Count / 4]);

            var minimumFloor = Math.Max(mean - 2 * stdDev, Configuration.ChromaprintMinimumScoreFloor);
            threshold = Math.Max(minimumFloor, Math.Min(0.85, threshold));
            
            LogDebug($"Threshold calculation: mean={mean:F3}, stdDev={stdDev:F3}, Q1={sortedScores[sortedScores.Count / 4]:F3}, minimumFloor={minimumFloor:F3}, final threshold={threshold:F3}");
            
            return threshold;
        }
        
        private (List<int> filteredOffsets, List<double> filteredScores, List<int> filteredLengths) FilterOutliersIQR(List<int> offsets, List<double> scores, List<int> lengths)
        {
            if (offsets.Count <= 3)
                return (offsets, scores, lengths);
            
            var sortedOffsets = offsets.OrderBy(o => o).ToList();
            var n = sortedOffsets.Count;
            
            var q1Index = n / 4;
            var q3Index = (3 * n) / 4;
            var q1 = sortedOffsets[q1Index];
            var q3 = sortedOffsets[q3Index];
            var iqr = q3 - q1;
            
            var lowerBound = q1 - 1.5 * iqr;
            var upperBound = q3 + 1.5 * iqr;
            
            LogDebug($"IQR outlier detection: Q1={q1}, Q3={q3}, IQR={iqr}, bounds=[{lowerBound}, {upperBound}]");
            
            var filtered = new List<int>();
            var filteredScores = new List<double>();
            var filteredLengths = new List<int>();
            
            for (int i = 0; i < offsets.Count; i++)
            {
                if (offsets[i] >= lowerBound && offsets[i] <= upperBound)
                {
                    filtered.Add(offsets[i]);
                    filteredScores.Add(scores[i]);
                    filteredLengths.Add(lengths[i]);
                }
                else
                {
                    LogDebug($"Filtered outlier: offset={offsets[i]} (outside bounds)");
                }
            }
            
            return (filtered, filteredScores, filteredLengths);
        }
        
        private double CalculateWeightedMedian(List<int> offsets, List<double> weights)
        {
            if (offsets.Count == 0)
                return 0;
            
            if (offsets.Count == 1)
                return offsets[0];
            
            var combined = offsets.Zip(weights, (o, w) => (offset: o, weight: w))
                .OrderBy(x => x.offset)
                .ToList();
            
            var totalWeight = weights.Sum();
            var cumulativeWeight = 0.0;
            var halfWeight = totalWeight / 2.0;
            
            for (int i = 0; i < combined.Count; i++)
            {
                cumulativeWeight += combined[i].weight;
                if (cumulativeWeight >= halfWeight)
                {
                    LogDebug($"Weighted median: offset={combined[i].offset} (weight={combined[i].weight:F3}, cumulative={cumulativeWeight:F3}/{totalWeight:F3})");
                    return combined[i].offset;
                }
            }
            
            return combined[combined.Count / 2].offset;
        }
        
        private async Task<(uint[] fineFingerprint, double fineStartTime, int coarseOffsetInFine, double secondsPerPoint)> GenerateFineFingerprint(
            string videoPath,
            double coarseTimestamp,
            CancellationToken cancellationToken)
        {
            var searchWindowBefore = 5.0;
            var searchWindowAfter = 5.0;
            var fineGrainedDuration = 15.0;

            var fineStartTime = Math.Max(0, coarseTimestamp - searchWindowBefore);
            var fineEndTime = coarseTimestamp + searchWindowAfter;

            LogDebug($"Fine-grained pass: searching {FormatTime(fineStartTime)} to {FormatTime(fineEndTime)} around coarse detection at {FormatTime(coarseTimestamp)}");

            var fineFingerprint = await Task.Run(() => FFmpegHelper.GenerateChromaprint(videoPath, fineStartTime, fineStartTime + fineGrainedDuration, Logger, Configuration.ChromaprintFfmpegThreads, Configuration, cancellationToken), cancellationToken).ConfigureAwait(false);

            if (fineFingerprint.Length == 0)
            {
                LogDebug("Fine-grained pass: Failed to generate fingerprint");
                return (fineFingerprint, fineStartTime, 0, 0);
            }

            var secondsPerPoint = FFmpegHelper.ChromaprintSecondsPerPoint;
            var coarseOffsetInFine = (int)((coarseTimestamp - fineStartTime) / secondsPerPoint);
            return (fineFingerprint, fineStartTime, coarseOffsetInFine, secondsPerPoint);
        }

        private double ScoreOffsetAgainstReference(uint[] fineFingerprint, int offset, uint[] referenceFingerprint, int refBase)
        {
            if (offset < 0 || offset >= fineFingerprint.Length)
                return 0;

            var windowSize = Math.Min(50, fineFingerprint.Length - offset);
            if (windowSize < 20)
                return 0;

            var localBest = 0.0;
            for (int refDelta = -10; refDelta <= 10; refDelta++)
            {
                var refPos = refBase + refDelta;
                if (refPos < 0 || refPos + windowSize > referenceFingerprint.Length)
                    continue;

                int matchCount = 0;
                for (int i = 0; i < windowSize; i++)
                {
                    var xorResult = fineFingerprint[offset + i] ^ referenceFingerprint[refPos + i];
                    matchCount += 32 - NumericsCompat.PopCount(xorResult);
                }
                var sc = (double)matchCount / (windowSize * 32);
                if (sc > localBest)
                    localBest = sc;
            }

            return localBest;
        }

        private (int offset, double score) FindTransitionBoundary(List<(int offset, double score)> scoredCandidates, double peakScore)
        {
            var lowCount = Math.Max(1, scoredCandidates.Count / 4);
            var lowScore = scoredCandidates.Take(lowCount).Select(c => c.score).Average();
            var midThreshold = lowScore + (peakScore - lowScore) * 0.5;

            var low = 0;
            var high = scoredCandidates.Count - 1;

            if (scoredCandidates[low].score >= midThreshold)
                return scoredCandidates[low];

            if (scoredCandidates[high].score < midThreshold)
                return (-1, 0.0);

            while (high - low > 1)
            {
                var mid = (low + high) / 2;
                if (scoredCandidates[mid].score >= midThreshold)
                    high = mid;
                else
                    low = mid;
            }

            return scoredCandidates[high];
        }

        private double EstimateSubFrameOffset(uint[] fineFingerprint, int onsetOffset, uint[] referenceFingerprint, int refBase)
        {
            var scorePrev = ScoreOffsetAgainstReference(fineFingerprint, onsetOffset - 1, referenceFingerprint, refBase - 1);
            var scoreCurrent = ScoreOffsetAgainstReference(fineFingerprint, onsetOffset, referenceFingerprint, refBase);
            var scoreNext = ScoreOffsetAgainstReference(fineFingerprint, onsetOffset + 1, referenceFingerprint, refBase + 1);

            var denominator = scorePrev - 2 * scoreCurrent + scoreNext;
            if (Math.Abs(denominator) < 0.0001)
                return onsetOffset;

            var fractionalDelta = 0.5 * (scorePrev - scoreNext) / denominator;
            fractionalDelta = Math.Max(-1.0, Math.Min(1.0, fractionalDelta));
            return onsetOffset + fractionalDelta;
        }

        private double CalculateWeightedAverage(List<(double timestamp, double weight)> values)
        {
            var totalWeight = values.Sum(v => v.weight);
            if (totalWeight <= 0)
                return values.Average(v => v.timestamp);
            return values.Sum(v => v.timestamp * v.weight) / totalWeight;
        }

        private bool IsRepeatingSegment(uint[] fingerprint, int matchedOffset, uint[] introFingerprint)
        {
            if (matchedOffset < 0)
                return false;

            var windowSize = Math.Min(100, fingerprint.Length - matchedOffset);
            if (windowSize < 20)
                return false;

            var segment = new uint[windowSize];
            Array.Copy(fingerprint, matchedOffset, segment, 0, windowSize);

            var selfMatch = FFmpegHelper.FindBestMatchingSubsequence(segment, introFingerprint, minWindowSize: Math.Min(50, windowSize));
            return selfMatch.score >= Configuration.ChromaprintRepeatingSegmentThreshold;
        }

        private double FindRefinedTimestampAgainstReference(
            uint[] fineFingerprint,
            double fineStartTime,
            int coarseOffsetInFine,
            double secondsPerPoint,
            uint[] referenceFingerprint,
            int referenceMatchOffset,
            CancellationToken cancellationToken)
        {
            try
            {
                if (fineFingerprint.Length == 0)
                    return 0;

                var searchRange = 40;
                var startSearch = Math.Max(0, coarseOffsetInFine - searchRange);
                var endSearch = Math.Min(fineFingerprint.Length - 50, coarseOffsetInFine + searchRange);

                var scoredCandidates = new List<(int offset, double score)>();

                for (int offset = startSearch; offset <= endSearch; offset += 2)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return 0;

                    var refBase = referenceMatchOffset + (offset - coarseOffsetInFine);
                    var localBest = ScoreOffsetAgainstReference(fineFingerprint, offset, referenceFingerprint, refBase);
                    if (localBest > 0)
                        scoredCandidates.Add((offset, localBest));
                }

                if (scoredCandidates.Count == 0)
                    return 0;

                var peakScore = scoredCandidates.Max(s => s.score);

                if (peakScore <= 0.80)
                {
                    LogDebug($"Fine-grained pass: Peak score {peakScore:F3} below threshold, keeping coarse result");
                    return 0;
                }

                int onsetOffset;
                double onsetScore;

                if (Configuration.ChromaprintUseBoundarySearch)
                {
                    (onsetOffset, onsetScore) = FindTransitionBoundary(scoredCandidates, peakScore);
                    LogDebug($"Fine-grained pass: Boundary search located transition at offset {onsetOffset} (score={onsetScore:F3})");
                }
                else
                {
                    var onsetThreshold = peakScore * 0.97;
                    onsetOffset = -1;
                    onsetScore = 0.0;
                    foreach (var (off, score) in scoredCandidates)
                    {
                        if (score >= onsetThreshold)
                        {
                            onsetOffset = off;
                            onsetScore = score;
                            break;
                        }
                    }
                }

                if (onsetOffset < 0)
                    return 0;

                var fractionalOffset = (double)onsetOffset;
                if (Configuration.ChromaprintUseSubFrameInterpolation)
                {
                    var refBase = referenceMatchOffset + (onsetOffset - coarseOffsetInFine);
                    fractionalOffset = EstimateSubFrameOffset(fineFingerprint, onsetOffset, referenceFingerprint, refBase);
                    LogDebug($"Fine-grained pass: Sub-frame interpolation adjusted offset {onsetOffset} -> {fractionalOffset:F2}");
                }

                var refinedTimestamp = fineStartTime + fractionalOffset * secondsPerPoint;
                LogDebug($"Fine-grained pass: Onset at offset {fractionalOffset:F2} (score={onsetScore:F3}, peak={peakScore:F3}), refined timestamp={FormatTime(refinedTimestamp)}");
                return refinedTimestamp;
            }
            catch (Exception ex)
            {
                LogWarn($"Fine-grained refinement failed: {ex.Message}");
                return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
            }
            base.Dispose(disposing);
        }
    }
}
