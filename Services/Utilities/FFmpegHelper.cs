using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.Utilities
{

    public static class FFmpegHelper
    {
        private static string? _customTempPath;
        private static IFfmpegManager? _ffmpegManager;
        private static IMediaEncoder? _mediaEncoder;
        private static MediaBrowser.Model.Logging.ILogger? _logger;
        private static readonly ConcurrentDictionary<int, (Process process, DateTime startTime, string description, bool visibleInUi)> _activeProcesses = new ConcurrentDictionary<int, (Process, DateTime, string, bool)>();
        private static readonly ConcurrentDictionary<int, DateTime> _lastOutputTime = new ConcurrentDictionary<int, DateTime>();
        private static readonly ConcurrentDictionary<int, int> _processProgress = new ConcurrentDictionary<int, int>();
        private static readonly object _cleanupLock = new object();
        private static Timer? _hungProcessCleanupTimer;

        public static void SetLogger(MediaBrowser.Model.Logging.ILogger logger)
        {
            _logger = logger;
        }

        private static readonly string[] _repetitiveFfmpegNoisePatterns =
        {
            "RPU validation failed",
            "Error parsing DOVI NAL unit",
            "deprecated pixel format used"
        };

        public static string RemoveRepetitiveFfmpegNoise(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
                return stderr;

            var lines = stderr.Split('\n');
            var keptLines = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                var matchedPattern = Array.Find(_repetitiveFfmpegNoisePatterns, p => line.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
                if (matchedPattern != null)
                    continue;
                keptLines.Add(line);
            }

            return string.Join("\n", keptLines);
        }

        public static void UpdateLastOutputTime(int pid)
        {
            _lastOutputTime[pid] = DateTime.UtcNow;
        }

        public static void UpdateProcessProgress(int pid, int percent)
        {
            _processProgress[pid] = Math.Max(0, Math.Min(100, percent));
        }

        public static void RegisterProcess(Process process, string description, bool visibleInUi = true)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    _activeProcesses.TryAdd(process.Id, (process, DateTime.UtcNow, description, visibleInUi));
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Failed to register process '{description}': {ex.Message}");
            }
        }

        public static void UnregisterProcess(Process process)
        {
            try
            {
                if (process != null)
                {
                    _activeProcesses.TryRemove(process.Id, out _);
                    _lastOutputTime.TryRemove(process.Id, out _);
                    _processProgress.TryRemove(process.Id, out _);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Failed to unregister process: {ex.Message}");
            }
        }

        public static int KillHungProcesses(int maxAgeSeconds = 900)
        {
            lock (_cleanupLock)
            {
                var killedCount = 0;
                var now = DateTime.UtcNow;
                var toRemove = new List<int>();

                foreach (var kvp in _activeProcesses)
                {
                    try
                    {
                        var (process, startTime, description, _) = kvp.Value;
                        var age = (now - startTime).TotalSeconds;

                        if (process.HasExited)
                        {
                            toRemove.Add(kvp.Key);
                            continue;
                        }

                        if (age > maxAgeSeconds)
                        {
                            try
                            {
                                process.Kill();
                                killedCount++;
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug($"Failed to kill hung process '{description}': {ex.Message}");
                            }
                            toRemove.Add(kvp.Key);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"Error checking active process during hung-process cleanup: {ex.Message}");
                        toRemove.Add(kvp.Key);
                    }
                }

                foreach (var id in toRemove)
                {
                    _activeProcesses.TryRemove(id, out _);
                }

                return killedCount;
            }
        }

        private const int HungProcessTimeoutSeconds = 900;
        private const double MinDisplayAgeSeconds = 1.5;

        public static IReadOnlyList<object> GetActiveProcesses()
        {
            var result = new List<object>();
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeProcesses)
            {
                try
                {
                    var (process, startTime, description, visibleInUi) = kvp.Value;
                    if (!visibleInUi) continue;
                    if (process.HasExited) continue;
                    var ageSeconds = (now - startTime).TotalSeconds;
                    if (ageSeconds < MinDisplayAgeSeconds) continue;
                    var percentOfTimeout = Math.Min(100.0, ageSeconds / HungProcessTimeoutSeconds * 100.0);
                    int? secondsSinceLastOutput = null;
                    if (_lastOutputTime.TryGetValue(kvp.Key, out var lastOutput))
                    {
                        secondsSinceLastOutput = (int)(now - lastOutput).TotalSeconds;
                    }
                    int? progressPercent = _processProgress.TryGetValue(kvp.Key, out var storedProgress) ? storedProgress : (int?)null;
                    result.Add(new { Description = description, AgeSeconds = (int)ageSeconds, PercentOfTimeout = Math.Round(percentOfTimeout, 1), ProgressPercent = progressPercent, SecondsSinceLastOutput = secondsSinceLastOutput });
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Error reading active process info: {ex.Message}");
                }
            }
            return result;
        }

        public static string NormalizeFilePath(string path)
        {            if (string.IsNullOrEmpty(path))
                return path;

            if (path.StartsWith("smb://"))
            {

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {

                    var uncPath = path.Substring(6);

                    uncPath = uncPath.Replace('/', '\\');

                    uncPath = "\\\\" + uncPath;

                    return uncPath;
                }
                else
                {

                    var smbPath = path.Substring(6);
                    var pathParts = smbPath.Split('/');

                    if (pathParts.Length >= 2)
                    {
                        var server = pathParts[0];
                        var remainingPath = string.Join("/", pathParts.Skip(1));

                        var mountPatterns = new[]
                        {
                            $"/mnt/{server}/{remainingPath}",
                            $"/media/{server}/{remainingPath}",
                            $"/mnt/smb/{remainingPath}",
                            $"/media/smb/{remainingPath}",
                            $"/mnt/nas/{remainingPath}",
                            $"/media/nas/{remainingPath}"
                        };

                        foreach (var mountPath in mountPatterns)
                        {
                            if (File.Exists(mountPath))
                            {
                                return mountPath;
                            }
                        }
                    }

                    return path;
                }
            }

            return path;
        }

        public static void Initialize(IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder)
        {
            _ffmpegManager = ffmpegManager ?? throw new ArgumentNullException(nameof(ffmpegManager));
            _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
            _hungProcessCleanupTimer?.Dispose();
            _hungProcessCleanupTimer = new Timer(_ =>
            {
                try
                {
                    KillHungProcesses();
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Hung process cleanup failed: {ex.Message}");
                }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public static void SetCustomTempPath(string? customPath)
        {
            _customTempPath = customPath;
        }

        public static string GetTempPath()
        {
            if (!string.IsNullOrWhiteSpace(_customTempPath) && Directory.Exists(_customTempPath))
            {
                return _customTempPath!;
            }
            return Path.GetTempPath();
        }

        public static string GetFfmpegPath()
        {
            if (_ffmpegManager == null)
                throw new InvalidOperationException("FFmpegHelper not initialized");

            var config = _ffmpegManager.FfmpegConfiguration;
            if (config == null || string.IsNullOrEmpty(config.EncoderPath) || !File.Exists(config.EncoderPath))
            {
                throw new FileNotFoundException("FFmpeg encoder not found");
            }

            return config.EncoderPath;
        }

        public static string GetFfprobePath()
        {
            if (_ffmpegManager == null)
                throw new InvalidOperationException("FFmpegHelper not initialized");

            var config = _ffmpegManager.FfmpegConfiguration;
            if (config == null || string.IsNullOrEmpty(config.ProbePath) || !File.Exists(config.ProbePath))
            {
                throw new FileNotFoundException("FFprobe not found");
            }

            return config.ProbePath;
        }

        public static string GetInputArgument(string path)
        {
            if (_mediaEncoder == null)
            {
                return NormalizeFilePath(path);
            }

            var normalizedPath = NormalizeFilePath(path);
            
            MediaProtocol protocol;
            if (normalizedPath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.File;
            }
            else if (normalizedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     normalizedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.Http;
            }
            else if (normalizedPath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.Rtsp;
            }
            else
            {
                protocol = MediaProtocol.File;
            }

            return _mediaEncoder.GetInputArgument(normalizedPath.AsSpan(), protocol);
        }

        public static string ResolveInputPath(string path)
        {
            return GetInputArgument(path).Replace("\"", string.Empty);
        }

        private const string StrmNetworkResilienceArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_at_eof 1 -reconnect_delay_max 5 -rw_timeout 15000000";

        public static bool IsStrmFile(string? path)
        {
            return !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldApplyStrmNetworkResilience(string? originalPath, string? resolvedInputArgument, PluginConfiguration? configuration)
        {
            if (configuration == null || !configuration.EnableStrmNetworkResilience)
                return false;
            if (!IsStrmFile(originalPath) || string.IsNullOrEmpty(resolvedInputArgument))
                return false;
            return resolvedInputArgument!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   resolvedInputArgument.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public static void AddStrmNetworkResilienceArgs(ProcessStartInfo startInfo, string? originalPath, string? resolvedInputArgument, PluginConfiguration? configuration)
        {
            if (!ShouldApplyStrmNetworkResilience(originalPath, resolvedInputArgument, configuration))
                return;

            foreach (var arg in StrmNetworkResilienceArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                startInfo.ArgumentList().Add(arg);
            }
        }

        public static string GetStrmNetworkResilienceArgsPrefix(string? originalPath, string? resolvedInputArgument, PluginConfiguration? configuration)
        {
            return ShouldApplyStrmNetworkResilience(originalPath, resolvedInputArgument, configuration)
                ? StrmNetworkResilienceArgs + " "
                : string.Empty;
        }

        public static int CleanupOrphanedTempDirectories()
        {
            var deletedCount = 0;
            try
            {
                var tempPath = GetTempPath();
                var directories = Directory.GetDirectories(tempPath, "ocr_frames_*");

                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);

                        if (dirInfo.Exists && (DateTime.UtcNow - dirInfo.CreationTimeUtc).TotalHours > 1)
                        {
                            Directory.Delete(dir, true);
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"Failed to delete stale temp directory '{dir}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Error cleaning up stale OCR temp directories: {ex.Message}");
            }
            return deletedCount;
        }

        public static TimeSpan? ResolveOperationTimeout(PluginConfiguration? configuration, double? defaultMinutes)
        {
            var configured = configuration?.FfmpegOperationTimeoutMinutes ?? 0;
            if (configured < 0)
                return null;
            if (configured > 0)
                return TimeSpan.FromMinutes(configured);
            return defaultMinutes.HasValue ? TimeSpan.FromMinutes(defaultMinutes.Value) : (TimeSpan?)null;
        }

        public const double ChromaprintSecondsPerPoint = 1365.0 / 11025.0;

        public static int ToMjpegQScale(int quality)
        {
            var clamped = Math.Max(1, Math.Min(100, quality));
            return (int)Math.Round(2 + (100 - clamped) * 29.0 / 99.0);
        }

        public static (string Type, string Device) GetSharedHardwareDecode(PluginConfiguration? configuration)
        {
            if (configuration == null ||
                !configuration.OcrEnableHardwareAcceleration ||
                !IsHardwareDecodeEnabled(configuration.OcrHardwareAccelerationType))
            {
                return ("none", string.Empty);
            }

            return (configuration.OcrHardwareAccelerationType, configuration.OcrHardwareDevice ?? string.Empty);
        }

        public static bool IsHardwareDecodeEnabled(string? hwType)
        {
            return !string.IsNullOrWhiteSpace(hwType) && !hwType!.Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        public static void AddHardwareDecodeArgs(ProcessStartInfo startInfo, string? hwType, string? device, MediaBrowser.Model.Logging.ILogger? logger = null)
        {
            if (!IsHardwareDecodeEnabled(hwType))
                return;

            var args = startInfo.ArgumentList();

            switch (hwType!.ToLowerInvariant())
            {
                case "vaapi":
                    args.Add("-hwaccel");
                    args.Add("vaapi");
                    args.Add("-vaapi_device");
                    args.Add(string.IsNullOrWhiteSpace(device) ? "/dev/dri/renderD128" : device!);
                    break;
                case "qsv":
                    args.Add("-hwaccel");
                    args.Add("qsv");
                    if (!string.IsNullOrWhiteSpace(device))
                    {
                        args.Add("-hwaccel_device");
                        args.Add(device!);
                    }
                    break;
                case "cuda":
                case "nvdec":
                    args.Add("-hwaccel");
                    args.Add(hwType.ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(device))
                    {
                        args.Add("-hwaccel_device");
                        args.Add(device!);
                    }
                    break;
                case "d3d11va":
                    args.Add("-hwaccel");
                    args.Add("d3d11va");
                    if (!string.IsNullOrWhiteSpace(device))
                    {
                        args.Add("-hwaccel_device");
                        args.Add(device!);
                    }
                    break;
                case "dxva2":
                    args.Add("-hwaccel");
                    args.Add("dxva2");
                    break;
                case "videotoolbox":
                    args.Add("-hwaccel");
                    args.Add("videotoolbox");
                    break;
                default:
                    logger?.Warn($"Unknown hardware acceleration type: {hwType}");
                    break;
            }
        }

        public static uint[] GenerateChromaprint(string filePath, double startTime, double endTime, MediaBrowser.Model.Logging.ILogger? logger = null, int threads = 0, PluginConfiguration? configuration = null, CancellationToken cancellationToken = default)
        {
            var inputArgument = GetInputArgument(filePath);
            var ffmpegPath = GetFfmpegPath();
            var duration = endTime - startTime;

            var threadArg = threads > 0 ? $"-threads {threads} " : string.Empty;
            var resilienceArg = GetStrmNetworkResilienceArgsPrefix(filePath, inputArgument, configuration);
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-hide_banner -loglevel warning {3}{4}-ss {0} -i {1} -to {2} -vn -sn -dn -ac 2 -f chromaprint -fp_format raw -",
                startTime,
                inputArgument,
                duration,
                threadArg,
                resilienceArg);

            logger?.Debug($"FFmpeg chromaprint command: {ffmpegPath} {args}");

            var info = new ProcessStartInfo(ffmpegPath, args)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                ErrorDialog = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = info };
            process.Start();
            
            RegisterProcess(process, $"Chromaprint: {Path.GetFileName(filePath)}");

            var chromaprintTimeout = ResolveOperationTimeout(configuration, defaultMinutes: 1.0);
            var chromaprintTimeoutMs = chromaprintTimeout.HasValue ? (int)chromaprintTimeout.Value.TotalMilliseconds : -1;

            if (configuration != null && configuration.ChromaprintLowerProcessPriority)
            {
                CpuThrottler.SetProcessPriority(process, configuration);
            }

            var ms = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(ms, 8192);
            var stderrTask = process.StandardError.ReadToEndAsync();

            void KillAndDrain()
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch (Exception ex)
                {
                    logger?.Error($"Failed to kill chromaprint process: {ex.Message}");
                }

                try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000); } catch { }
            }

            try
            {
                bool completed;
                try
                {
                    completed = stdoutTask.Wait(chromaprintTimeoutMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    KillAndDrain();
                    throw;
                }
                catch (AggregateException ex)
                {
                    KillAndDrain();
                    logger?.Warn($"Failed to read chromaprint output for {filePath}: {ex.GetBaseException().Message}");
                    return Array.Empty<uint>();
                }

                if (!completed)
                {
                    KillAndDrain();
                    logger?.Warn($"Chromaprint process timed out and was killed for {filePath}");
                    return Array.Empty<uint>();
                }

                if (!process.WaitForExit(10000))
                {
                    KillAndDrain();
                    logger?.Warn($"Chromaprint process did not exit after output ended and was killed for {filePath}");
                    return Array.Empty<uint>();
                }

                try { stderrTask.Wait(5000); } catch { }

                var rawPoints = ms.ToArray();
                if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
                {
                    logger?.Warn($"Chromaprint returned {rawPoints.Length} bytes for {filePath}");
                    return Array.Empty<uint>();
                }

                var pointCount = rawPoints.Length / 4;
                var results = new uint[pointCount];
                for (int i = 0, j = 0; i < rawPoints.Length; i += 4, j++)
                {
                    results[j] = BitConverter.ToUInt32(rawPoints, i);
                }

                logger?.Debug($"Generated {results.Length} fingerprint points for {filePath}");
                return results;
            }
            finally
            {
                UnregisterProcess(process);
            }
        }

        public static (double score, int offset) CompareFingerprints(uint[] fp1, uint[] fp2, int maxShift = 120)
        {
            if (fp1.Length == 0 || fp2.Length == 0)
                return (0, 0);

            double bestScore = 0;
            int bestOffset = 0;

            for (int shift = -maxShift; shift <= maxShift; shift++)
            {
                int matchCount = 0;
                int compareCount = 0;

                int start1 = Math.Max(0, -shift);
                int start2 = Math.Max(0, shift);
                int length = Math.Min(fp1.Length - start1, fp2.Length - start2);

                if (length <= 0)
                    continue;

                for (int i = 0; i < length; i++)
                {
                    var xorResult = fp1[start1 + i] ^ fp2[start2 + i];
                    
                    var matchingBits = 32 - NumericsCompat.PopCount(xorResult);
                    matchCount += matchingBits;
                    compareCount += 32;
                }

                double score = (double)matchCount / compareCount;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = shift;
                }
            }

            return (bestScore, bestOffset);
        }
        
        public static (double score, int offsetFp1, int offsetFp2, int matchLength) FindBestMatchingSubsequence(uint[] fp1, uint[] fp2, int minWindowSize = 100)
        {
            if (fp1.Length == 0 || fp2.Length == 0)
                return (0, 0, 0, 0);

            var seedWindowSize = Math.Min(Math.Min(fp1.Length, fp2.Length), minWindowSize);
            if (seedWindowSize < 10)
                return (0, 0, 0, 0);

            const double seedScoreFloor = 0.60;
            var candidates = new List<(int start1, int start2, double score)>();

            const int seedStride = 10;
            var rows = (fp1.Length - seedWindowSize) / seedStride + 1;
            var cols = (fp2.Length - seedWindowSize) / seedStride + 1;
            var gridSize = rows * cols;
            var grid = ArrayPool<int>.Shared.Rent(gridSize);
            var prefix = ArrayPool<int>.Shared.Rent(Math.Min(fp1.Length, fp2.Length) + seedStride + 1);
            try
            {
                for (int diagonal = -(rows - 1); diagonal <= cols - 1; diagonal++)
                {
                    var rowStart = Math.Max(0, -diagonal);
                    var rowEnd = Math.Min(rows - 1, cols - 1 - diagonal);
                    if (rowStart > rowEnd)
                        continue;

                    var shift = diagonal * seedStride;
                    var segmentStart = rowStart * seedStride;
                    var segmentEnd = rowEnd * seedStride + seedWindowSize;

                    prefix[0] = 0;
                    for (int i = segmentStart, k = 0; i < segmentEnd; i++, k++)
                    {
                        prefix[k + 1] = prefix[k] + 32 - NumericsCompat.PopCount(fp1[i] ^ fp2[i + shift]);
                    }

                    for (int row = rowStart; row <= rowEnd; row++)
                    {
                        var offset = row * seedStride - segmentStart;
                        grid[row * cols + row + diagonal] = prefix[offset + seedWindowSize] - prefix[offset];
                    }
                }

                var denominator = (double)(seedWindowSize * 32);
                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < cols; col++)
                    {
                        double score = grid[row * cols + col] / denominator;
                        if (score >= seedScoreFloor)
                        {
                            candidates.Add((row * seedStride, col * seedStride, score));
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(grid);
                ArrayPool<int>.Shared.Return(prefix);
            }

            if (candidates.Count == 0)
                return (0, 0, 0, 0);

            var topCandidates = SelectDistinctTopCandidates(candidates, seedWindowSize, maxCandidates: 8);

            (double score, int offsetFp1, int offsetFp2, int matchLength) best = (0, 0, 0, 0);
            foreach (var candidate in topCandidates)
            {
                var extended = ExtendMatchedSegment(fp1, fp2, candidate.start1, candidate.start2, seedWindowSize);
                if (extended.matchLength > best.matchLength ||
                    (extended.matchLength == best.matchLength && extended.score > best.score))
                {
                    best = extended;
                }
            }

            return best;
        }

        private static List<(int start1, int start2, double score)> SelectDistinctTopCandidates(
            List<(int start1, int start2, double score)> candidates, int seedWindowSize, int maxCandidates)
        {
            var sorted = candidates.OrderByDescending(c => c.score).ToList();
            var selected = new List<(int start1, int start2, double score)>();

            foreach (var candidate in sorted)
            {
                if (selected.Count >= maxCandidates)
                    break;

                var overlapsExisting = selected.Any(s => Math.Abs(s.start1 - candidate.start1) < seedWindowSize);
                if (!overlapsExisting)
                {
                    selected.Add(candidate);
                }
            }

            return selected;
        }

        private static (double score, int offsetFp1, int offsetFp2, int matchLength) ExtendMatchedSegment(uint[] fp1, uint[] fp2, int seedStart1, int seedStart2, int seedWindowSize)
        {
            const double extensionScoreFloor = 0.70;
            const int maxExtensionPerSide = 3000;
            const int trailingWindowSize = 50;

            var left = 0;
            var right = seedWindowSize;

            int matchBits = 0;
            for (int i = 0; i < seedWindowSize; i++)
            {
                var xorResult = fp1[seedStart1 + i] ^ fp2[seedStart2 + i];
                matchBits += 32 - NumericsCompat.PopCount(xorResult);
            }

            var rightTrail = new Queue<int>();
            var rightTrailSum = 0;
            while (right - seedWindowSize < maxExtensionPerSide &&
                   seedStart1 + right < fp1.Length && seedStart2 + right < fp2.Length)
            {
                var xorResult = fp1[seedStart1 + right] ^ fp2[seedStart2 + right];
                var bits = 32 - NumericsCompat.PopCount(xorResult);

                rightTrail.Enqueue(bits);
                rightTrailSum += bits;
                if (rightTrail.Count > trailingWindowSize)
                    rightTrailSum -= rightTrail.Dequeue();

                if (rightTrail.Count >= trailingWindowSize &&
                    (double)rightTrailSum / (rightTrail.Count * 32) < extensionScoreFloor)
                    break;

                matchBits += bits;
                right++;
            }

            var leftTrail = new Queue<int>();
            var leftTrailSum = 0;
            while (-left < maxExtensionPerSide &&
                   seedStart1 + left - 1 >= 0 && seedStart2 + left - 1 >= 0)
            {
                var xorResult = fp1[seedStart1 + left - 1] ^ fp2[seedStart2 + left - 1];
                var bits = 32 - NumericsCompat.PopCount(xorResult);

                leftTrail.Enqueue(bits);
                leftTrailSum += bits;
                if (leftTrail.Count > trailingWindowSize)
                    leftTrailSum -= leftTrail.Dequeue();

                if (leftTrail.Count >= trailingWindowSize &&
                    (double)leftTrailSum / (leftTrail.Count * 32) < extensionScoreFloor)
                    break;

                matchBits += bits;
                left--;
            }

            var finalWindowSize = right - left;
            var finalScore = (double)matchBits / (finalWindowSize * 32);
            return (finalScore, seedStart1 + left, seedStart2 + left, finalWindowSize);
        }
    }
}