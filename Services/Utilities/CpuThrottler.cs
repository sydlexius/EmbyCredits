using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace EmbyCredits.Services.Utilities
{
    public class CpuThrottler
    {
        private readonly PluginConfiguration _configuration;
        private readonly ILogger? _logger;
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private const int MaxThrottleDelayMs = 10 * 60 * 1000;

        public CpuThrottler(PluginConfiguration configuration, ILogger? logger = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger;
        }

        public long BeginWork()
        {
            return Stopwatch.GetTimestamp();
        }

        public async Task EndWork(long workStartTimestamp, CancellationToken cancellationToken = default)
        {
            if (_configuration.CpuUsageLimit >= 100)
                return;

            var workDuration = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - workStartTimestamp) / (double)Stopwatch.Frequency);
            var throttleDelayMs = CalculateThrottleDelay(workDuration);
            if (throttleDelayMs > 0)
            {
                if (_configuration.EnableDetailedLogging)
                {
                    var achievedRatio = workDuration.TotalMilliseconds / (workDuration.TotalMilliseconds + throttleDelayMs) * 100.0;
                    _logger?.Debug($"[CpuThrottler] Work: {workDuration.TotalMilliseconds:F0}ms, Delay: {throttleDelayMs}ms, Target: {_configuration.CpuUsageLimit}%, Achieved: {achievedRatio:F1}%");
                }

                try { await Task.Delay(throttleDelayMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        private int CalculateThrottleDelay(TimeSpan workDuration)
        {
            if (_configuration.CpuUsageLimit >= 100)
                return 0;

            var cpuLimit = Math.Max(1, Math.Min(100, _configuration.CpuUsageLimit));
            var workMs = workDuration.TotalMilliseconds;
            
            var targetIdleRatio = (100.0 - cpuLimit) / cpuLimit;
            var delayMs = Math.Min((double)MaxThrottleDelayMs, workMs * targetIdleRatio);
            
            var baseThrottle = _configuration.CpuThrottleDelayMs;
            if (baseThrottle > 0)
            {
                delayMs = Math.Max(delayMs, baseThrottle);
            }

            return (int)Math.Max(0, Math.Min(delayMs, MaxThrottleDelayMs));
        }

        public static void SetProcessPriority(Process process, PluginConfiguration configuration)
        {
            if (process == null || configuration == null)
                return;

            if (!configuration.LowerProcessPriority)
                return;

            try
            {
                if (process.HasExited)
                    return;

                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    return;
                }
                catch (Exception)
                {
                    if (IsWindows)
                        return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "renice",
                    Arguments = $"10 -p {process.Id}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var reniceProcess = Process.Start(startInfo))
                {
                    reniceProcess?.WaitForExit(1000);
                }
            }
            catch (Exception)
            {
            }
        }

        public static void SetProcessPriority(int processId, PluginConfiguration configuration)
        {
            if (configuration == null || !configuration.LowerProcessPriority)
                return;

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    SetProcessPriority(process, configuration);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
