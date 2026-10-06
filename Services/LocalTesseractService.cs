using MediaBrowser.Model.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public static class LocalTesseractService
    {
        private static readonly string[] BinaryNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "tesseract.exe" }
            : new[] { "tesseract" };

        private static readonly string[] WellKnownPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { @"C:\Program Files\Tesseract-OCR\tesseract.exe", @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe" }
            : new[] { "/usr/bin/tesseract", "/usr/local/bin/tesseract", "/opt/homebrew/bin/tesseract", "/opt/local/bin/tesseract" };

        private static readonly object _resolvedPathLock = new object();
        private static string? _resolvedPathKey;
        private static string? _resolvedPath;

        public static string ResolveBinaryPath(string configuredPath)
        {
            var key = configuredPath ?? string.Empty;
            lock (_resolvedPathLock)
            {
                if (_resolvedPath != null && string.Equals(_resolvedPathKey, key, StringComparison.Ordinal) && File.Exists(_resolvedPath))
                    return _resolvedPath;
            }

            var resolved = ResolveBinaryPathUncached(configuredPath ?? string.Empty);

            lock (_resolvedPathLock)
            {
                _resolvedPathKey = key;
                _resolvedPath = resolved;
            }

            return resolved;
        }

        private static string ResolveBinaryPathUncached(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                if (!File.Exists(configuredPath))
                    throw new FileNotFoundException($"Tesseract binary not found at configured path: {configuredPath}");
                return configuredPath;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var dirs = pathEnv.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var name in BinaryNames)
            {
                foreach (var dir in dirs)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            foreach (var known in WellKnownPaths)
            {
                if (File.Exists(known))
                    return known;
            }

            throw new FileNotFoundException(
                "tesseract binary not found in PATH. Install Tesseract or set the 'Local Tesseract Binary Path' in plugin settings.");
        }

        public static (bool Available, string Message) TestAvailability(string configuredPath)
        {
            try
            {
                var binary = ResolveBinaryPath(configuredPath);

                var psi = new ProcessStartInfo
                {
                    FileName = binary,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList().Add("--version");

                using var process = new Process { StartInfo = psi };
                psi.ApplyArgumentList();
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                var version = (stdout + stderr).Split('\n')[0].Trim();
                return (true, $"✓ Tesseract found at {binary} — {version}");
            }
            catch (FileNotFoundException ex)
            {
                return (false, ex.Message);
            }
            catch (Exception ex)
            {
                return (false, $"Error testing Tesseract: {ex.Message}");
            }
        }

        public static async Task<(string Text, double Confidence)> RunOcrAsync(
            byte[] imageBytes,
            string languages,
            int psm,
            int oem,
            string configuredPath,
            bool useDirectMemoryPipeline,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            var binary = ResolveBinaryPath(configuredPath);

            string? tempImage = useDirectMemoryPipeline
                ? null
                : Path.Combine(FFmpegHelper.GetTempPath(), $"ltocr_{Guid.NewGuid():N}.jpg");
            try
            {
                if (tempImage != null)
                    await AsyncFile.WriteAllBytesAsync(tempImage, imageBytes, cancellationToken).ConfigureAwait(false);

                var langArg = string.IsNullOrWhiteSpace(languages) ? "eng" : languages.Replace(',', '+');

                var psi = new ProcessStartInfo
                {
                    FileName = binary,
                    UseShellExecute = false,
                    RedirectStandardInput = useDirectMemoryPipeline,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList().Add(tempImage ?? "-");
                psi.ArgumentList().Add("stdout");
                psi.ArgumentList().Add("-l");
                psi.ArgumentList().Add(langArg);
                psi.ArgumentList().Add("--oem");
                psi.ArgumentList().Add(oem.ToString());
                psi.ArgumentList().Add("--psm");
                psi.ArgumentList().Add(psm.ToString());

                using var process = new Process { StartInfo = psi };
                try
                {
                    psi.ApplyArgumentList();
                    process.Start();
                    FFmpegHelper.RegisterProcess(process, "LocalTesseract OCR", visibleInUi: false);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var outputTask = Task.WhenAll(stdoutTask, stderrTask);

                    void KillProcess()
                    {
                        try
                        {
                            if (!process.HasExited)
                                process.Kill();
                        }
                        catch { }
                    }

                    try
                    {
                        if (useDirectMemoryPipeline)
                        {
                            await process.StandardInput.BaseStream.WriteAsync(imageBytes, 0, imageBytes.Length, cts.Token).ConfigureAwait(false);
                            process.StandardInput.Close();
                        }

                        var finished = await Task.WhenAny(outputTask, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
                        if (finished != outputTask)
                            throw new OperationCanceledException(cts.Token);

                        await outputTask.ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException || ex is IOException)
                    {
                        KillProcess();
                        try { await outputTask.ConfigureAwait(false); } catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                        logger?.Warn("LocalTesseract: process timed out");
                        return (string.Empty, 0);
                    }

                    if (!process.WaitForExit(5000))
                    {
                        KillProcess();
                        logger?.Warn("LocalTesseract: process did not exit after output ended");
                        return (string.Empty, 0);
                    }

                    var text = stdoutTask.Result.Trim();
                    if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(text))
                    {
                        logger?.Debug($"LocalTesseract stderr: {stderrTask.Result.Trim()}");
                        return (string.Empty, 0);
                    }

                    var confidence = CalculateSyntheticConfidence(text);
                    return (text, confidence);
                }
                finally
                {
                    FFmpegHelper.UnregisterProcess(process);
                }
            }
            finally
            {
                if (tempImage != null)
                {
                    try { if (File.Exists(tempImage)) File.Delete(tempImage); } catch { }
                }
            }
        }

        private static double CalculateSyntheticConfidence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            double conf = 0.85;
            if (text.Length < 5) conf -= 0.15;
            else if (text.Length < 20) conf -= 0.05;
            else if (text.Length > 200) conf += 0.05;
            var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3) conf += 0.03;
            if (words.Length >= 10) conf += 0.02;
            int letters = text.Count(c => char.IsLetter(c));
            double ratio = text.Length > 0 ? (double)letters / text.Length : 0;
            if (ratio > 0.7) conf += 0.05;
            else if (ratio < 0.3) conf -= 0.10;
            return Math.Max(0, Math.Min(1, conf));
        }
    }
}
