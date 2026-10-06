using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.Utilities
{
    internal static class ProcessStartInfoCompat
    {
        private static readonly ConditionalWeakTable<ProcessStartInfo, List<string>> _argumentLists = new ConditionalWeakTable<ProcessStartInfo, List<string>>();

        public static List<string> ArgumentList(this ProcessStartInfo startInfo)
        {
            return _argumentLists.GetValue(startInfo, _ => new List<string>());
        }

        public static void ApplyArgumentList(this ProcessStartInfo startInfo)
        {
            if (_argumentLists.TryGetValue(startInfo, out var args) && args.Count > 0)
            {
                startInfo.Arguments = BuildArguments(args);
            }
        }

        private static string BuildArguments(List<string> args)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < args.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');

                AppendArgument(sb, args[i]);
            }

            return sb.ToString();
        }

        private static void AppendArgument(StringBuilder sb, string argument)
        {
            if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                sb.Append(argument);
                return;
            }

            sb.Append('"');

            for (var i = 0; i < argument.Length;)
            {
                var c = argument[i++];
                if (c == '\\')
                {
                    var backslashCount = 1;
                    while (i < argument.Length && argument[i] == '\\')
                    {
                        backslashCount++;
                        i++;
                    }

                    if (i == argument.Length)
                    {
                        sb.Append('\\', backslashCount * 2);
                    }
                    else if (argument[i] == '"')
                    {
                        sb.Append('\\', backslashCount * 2 + 1);
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        sb.Append('\\', backslashCount);
                    }
                }
                else if (c == '"')
                {
                    sb.Append('\\').Append('"');
                }
                else
                {
                    sb.Append(c);
                }
            }

            sb.Append('"');
        }
    }

    internal static class ProcessWaitForExitCompat
    {
        public static Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
        {
            if (process.HasExited)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;

            EventHandler? handler = null;
            handler = (_, _) =>
            {
                process.Exited -= handler;
                tcs.TrySetResult(true);
            };
            process.Exited += handler;

            if (process.HasExited)
            {
                process.Exited -= handler;
                tcs.TrySetResult(true);
                return tcs.Task;
            }

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    process.Exited -= handler;
                    tcs.TrySetCanceled(cancellationToken);
                });

                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }

            return tcs.Task;
        }
    }

    internal static class AsyncFile
    {
        public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => File.ReadAllText(path), cancellationToken);
        }

        public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => File.WriteAllText(path, contents), cancellationToken);
        }

        public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => File.WriteAllBytes(path, bytes), cancellationToken);
        }
    }

    internal static class NumericsCompat
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static int PopCount(uint value)
        {
            value -= (value >> 1) & 0x55555555u;
            value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
            value = (value + (value >> 4)) & 0x0F0F0F0Fu;
            return (int)((value * 0x01010101u) >> 24);
        }
    }

    internal static class EnumerableCompat
    {
        public static List<T> TakeLast<T>(this List<T> source, int count)
        {
            if (count <= 0)
                return new List<T>();

            var skip = source.Count - count;
            return skip <= 0 ? new List<T>(source) : source.GetRange(skip, count);
        }
    }
}
