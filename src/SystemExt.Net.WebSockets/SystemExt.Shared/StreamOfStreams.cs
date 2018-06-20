using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO
{
    public class StreamOfStreams : Stream
    {
        private readonly IList<Stream> _segments;
        private int _position;
        private IEnumerator<Stream> _enumerator;
        private bool? _moveNext;

        public StreamOfStreams(params Stream[] segments) : this((IList<Stream>)segments)
        {

        }

        public StreamOfStreams(IList<Stream> segments)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            _segments = segments;
            Init();
        }


        private void Init()
        {
            _enumerator = ((IEnumerable<Stream>)_segments).GetEnumerator();
            _position = 0;
            _moveNext = null;
        }

        public void Reset() => Init();

        public override bool CanRead { get; } = true;

        public override bool CanSeek { get; } = false;

        public override bool CanWrite { get; } = false;

        public override long Length
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get
            {
                return _position;
            }
            set
            {
                throw new NotSupportedException();

            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;
            var moveNext = _moveNext ?? _enumerator.MoveNext();
            while (moveNext)
            {
                var stream = _enumerator.Current;
                var r = stream.Read(buffer, offset, count);
                read += r;
                if (read == count)
                {
                    _position += read;
                    return read;
                }

                offset += r;
                count -= r;

                moveNext = _enumerator.MoveNext();
            }

            _moveNext = false;
            _position += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = 0;
            var moveNext = _moveNext ?? _enumerator.MoveNext();
            while (moveNext)
            {
                var stream = _enumerator.Current;
                var r = await stream.ReadAsync(buffer, offset, count, cancellationToken);
                read += r;
                if (read == count)
                {
                    _position += read;
                    return read;
                }

                offset += r;
                count -= r;

                moveNext = _enumerator.MoveNext();
            }

            _moveNext = false;
            _position += read;
            return read;
        }

        public override void Flush()
        {
            //nop
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _enumerator.Dispose();
        }
    }
}