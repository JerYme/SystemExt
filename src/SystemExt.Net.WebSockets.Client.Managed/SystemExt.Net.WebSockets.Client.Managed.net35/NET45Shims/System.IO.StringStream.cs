using System.Buffers;
using System.IO;
using System.Text;

namespace System.IO
{
    public class StringStream : Stream
    {
        private readonly string _string;
        private readonly Encoding _encoding;
        private readonly long _byteLength;
        private int _bytePosition;
        private readonly int _toCharCount;

        public StringStream(string value, Encoding encoding = null)
        {
            _string = value ?? string.Empty;
            _encoding = encoding ?? Encoding.UTF8;

            _byteLength = _encoding.GetByteCount(_string);
            _toCharCount = _encoding.GetMaxCharCount(1);
        }

        public override bool CanRead { get; } = true;

        public override bool CanSeek { get; } = true;

        public override bool CanWrite { get; } = false;

        public override long Length => _byteLength;

        public override long Position
        {
            get
            {
                return _bytePosition;
            }

            set
            {
                if (value < 0 || value > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _bytePosition = (int)value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.End:
                    Position = _byteLength + offset;
                    break;
                case SeekOrigin.Current:
                    Position += offset;
                    break;
            }

            return Position;
        }

        public override int Read(byte[] buffer, int offset, int byteCount)
        {
            if (_bytePosition < 0) throw new InvalidOperationException();

            var charLength = _byteLength * _toCharCount;
            if (_bytePosition >= charLength) return 0;

            var charPosition = _bytePosition * _toCharCount;
            var bufferMaxByCharReads = byteCount * _toCharCount;
            var charMaxByStringLengthLeft = charLength - charPosition;
            var charCount = (int)Math.Min(bufferMaxByCharReads, charMaxByStringLengthLeft);
            unsafe
            {
                fixed (byte* bytePtr = buffer)
                fixed (char* charPtr = _string)
                {
                    var read = _encoding.GetEncoder().GetBytes(charPtr + charPosition, charCount, bytePtr + offset, byteCount, true);
                    _bytePosition += read;
                    return read;
                }
            }
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