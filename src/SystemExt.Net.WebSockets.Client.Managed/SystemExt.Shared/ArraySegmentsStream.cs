using System.Collections.Generic;

namespace System.IO
{
    public class ArraySegmentsStream : Stream
    {
        private readonly IEnumerable<ArraySegment<byte>> _arraySegments;
        private int _position;
        private int _segmentPosition;
        private IEnumerator<ArraySegment<byte>> _enumerator;
        private bool? _moveNext;

        public ArraySegmentsStream(IEnumerable<ArraySegment<byte>> arraySegments)
        {
            if (arraySegments == null) throw new ArgumentNullException(nameof(arraySegments));
            _arraySegments = arraySegments;
            Init();
        }


        private void Init()
        {
            _enumerator = _arraySegments.GetEnumerator();
            _position = 0;
            _segmentPosition = 0;
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

        private int SegmentCopy(ArraySegment<byte> segment, byte[] buffer, int offset, int count)
        {
            var sourceIndex = segment.Offset + _segmentPosition;
            var segmentCount = segment.Count - _segmentPosition;

            var copyCount = Math.Min(count, segmentCount);
            Array.Copy(segment.Array, sourceIndex, buffer, offset, copyCount);

            _segmentPosition += copyCount;
            return copyCount;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;
            var moveNext = _moveNext ?? _enumerator.MoveNext();
            while (moveNext)
            {
                var segment = _enumerator.Current;
                var r = SegmentCopy(segment, buffer, offset, count);
                read += r;
                if (read == count)
                {
                    _position += read;
                    return read;
                }

                offset += r;
                count -= r;

                moveNext = _enumerator.MoveNext();
                _segmentPosition = 0;
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