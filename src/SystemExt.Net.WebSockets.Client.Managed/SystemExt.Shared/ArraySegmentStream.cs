using System.Diagnostics;

namespace System.IO
{
    public class ArraySegmentStream : Stream
    {
        private ArraySegment<byte>? _current;
        private int _position;
        private int _segmentPosition;

        public ArraySegmentStream(ArraySegment<byte> arraySegment)
        {
            if (arraySegment == null) throw new ArgumentNullException(nameof(arraySegment));
            _current = arraySegment;
        }

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
            if (segment.Array != null) Array.Copy(segment.Array, sourceIndex, buffer, offset, copyCount);
            else Debug.Assert(copyCount == 0);

            _segmentPosition += copyCount;
            return copyCount;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (_current.HasValue)
            {
                var segment = _current.Value;
                var r = SegmentCopy(segment, buffer, offset, count);
                read += r;
                if (read == count)
                {
                    _position += read;
                    return read;
                }

                offset += r;
                count -= r;

                _current = null;
                _segmentPosition = 0;
            }

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

    }
}
