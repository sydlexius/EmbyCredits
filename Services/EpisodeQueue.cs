using MediaBrowser.Controller.Entities.TV;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services
{
    public class EpisodeQueue : IDisposable
    {
        private readonly ConcurrentQueue<Episode> _queue = new ConcurrentQueue<Episode>();
        private readonly SemaphoreSlim _itemsAvailable = new SemaphoreSlim(0);
        private bool _completed;
        private bool _disposed;

        public int Count => _queue.Count;

        public Task EnqueueAsync(Episode episode, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryEnqueue(episode);
            return Task.CompletedTask;
        }

        public bool TryEnqueue(Episode episode)
        {
            if (_completed)
                return false;

            _queue.Enqueue(episode);
            _itemsAvailable.Release();
            return true;
        }

        public async Task<Episode> DequeueAsync(CancellationToken cancellationToken = default)
        {
            await _itemsAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            _queue.TryDequeue(out var episode);
            return episode!;
        }

        public bool TryDequeue(out Episode? episode)
        {
            if (_queue.TryDequeue(out episode))
            {
                _itemsAvailable.Wait(0);
                return true;
            }

            episode = default;
            return false;
        }

        public void CompleteAdding()
        {
            _completed = true;
        }

        public void Clear()
        {
            while (TryDequeue(out _))
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _completed = true;
            _itemsAvailable.Dispose();
        }
    }
}
