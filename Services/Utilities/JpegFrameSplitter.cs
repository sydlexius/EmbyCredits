using System;

namespace EmbyCredits.Services.Utilities
{
    internal sealed class JpegFrameSplitter
    {
        private readonly int _maxBufferedBytes;
        private byte[] _buffer;
        private int _start;
        private int _count;
        private bool _inFrame;
        private int _endSearchOffset;

        public JpegFrameSplitter(int maxBufferedBytes, int initialCapacity = 256 * 1024)
        {
            _maxBufferedBytes = maxBufferedBytes;
            _buffer = new byte[Math.Max(1024, initialCapacity)];
        }

        public int BufferedBytes => _count;

        public bool DiscardedOversizedData { get; private set; }

        public void ResetDiscardedFlag()
        {
            DiscardedOversizedData = false;
        }

        public void Append(byte[] data, int count)
        {
            if (count <= 0)
                return;

            if (_start + _count + count > _buffer.Length)
            {
                if (_count + count <= _buffer.Length)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
                }
                else
                {
                    var grown = new byte[Math.Max(_buffer.Length * 2, _count + count)];
                    Buffer.BlockCopy(_buffer, _start, grown, 0, _count);
                    _buffer = grown;
                }
                _start = 0;
            }

            Buffer.BlockCopy(data, 0, _buffer, _start + _count, count);
            _count += count;
        }

        public bool TryReadFrame(out byte[] frame)
        {
            frame = Array.Empty<byte>();

            if (!_inFrame)
            {
                var frameStart = IndexOfStartMarker();
                if (frameStart < 0)
                {
                    if (_count > 3)
                        Consume(_count - 3);
                    return false;
                }

                Consume(frameStart);
                _inFrame = true;
                _endSearchOffset = 3;
            }

            var frameEnd = IndexOfEndMarker(_endSearchOffset);
            if (frameEnd < 0)
            {
                _endSearchOffset = Math.Max(3, _count - 1);
                if (_count > _maxBufferedBytes)
                {
                    Consume(_count);
                    _inFrame = false;
                    DiscardedOversizedData = true;
                }
                return false;
            }

            var length = frameEnd + 2;
            frame = new byte[length];
            Buffer.BlockCopy(_buffer, _start, frame, 0, length);
            Consume(length);
            _inFrame = false;
            return true;
        }

        private int IndexOfStartMarker()
        {
            var i = 0;
            while (i <= _count - 3)
            {
                var absolute = Array.IndexOf(_buffer, (byte)0xFF, _start + i, _count - 3 - i + 1);
                if (absolute < 0)
                    return -1;

                if (_buffer[absolute + 1] == 0xD8 && _buffer[absolute + 2] == 0xFF)
                    return absolute - _start;

                i = absolute - _start + 1;
            }
            return -1;
        }

        private int IndexOfEndMarker(int from)
        {
            var i = from;
            while (i <= _count - 2)
            {
                var absolute = Array.IndexOf(_buffer, (byte)0xFF, _start + i, _count - 2 - i + 1);
                if (absolute < 0)
                    return -1;

                if (_buffer[absolute + 1] == 0xD9)
                    return absolute - _start;

                i = absolute - _start + 1;
            }
            return -1;
        }

        private void Consume(int bytes)
        {
            _start += bytes;
            _count -= bytes;
            if (_count == 0)
                _start = 0;
        }
    }
}
