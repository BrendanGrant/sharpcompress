using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using SharpCompress.Crypto;
using SharpCompress.IO;

namespace SharpCompress.Compressors.LZMA
{
    // TODO:
    // - Write as well as read
    // - Multi-volume support
    // - Use of the data size / member size values at the end of the stream

    /// <summary>
    /// Stream supporting the LZIP format, as documented at http://www.nongnu.org/lzip/manual/lzip_manual.html
    /// </summary>
    public sealed class LZipStream : Stream
    {
        #nullable disable
        private Stream _stream;
#nullable enable
        private CountingWritableSubStream? _countingWritableSubStream;
        private bool _disposed;
        private bool _finished;

        private long _writeCount;

        private LZipStream()
        {
            
        }

        public static async ValueTask<LZipStream> CreateAsync(Stream stream, CompressionMode mode)
        {
            var lzip = new LZipStream();
            lzip.Mode = mode;

            if (mode == CompressionMode.Decompress)
            {
                int dSize = await ValidateAndReadSize(stream);
                if (dSize == 0)
                {
                    throw new IOException("Not an LZip stream");
                }
                byte[] properties = GetProperties(dSize);
                lzip._stream = await LzmaStream.CreateAsync(properties, stream);
            }
            else
            {
                //default
                int dSize = 104 * 1024;
                WriteHeaderSize(stream);

                lzip._countingWritableSubStream = new CountingWritableSubStream(stream);
                lzip._stream = new Crc32Stream(new LzmaStream(new LzmaEncoderProperties(true, dSize), false, lzip._countingWritableSubStream));
            }
            return lzip;
        }

        public void Finish()
        {
            if (!_finished)
            {
                if (Mode == CompressionMode.Compress)
                {
                    var crc32Stream = (Crc32Stream)_stream;
                    crc32Stream.WrappedStream.Dispose();
                    crc32Stream.Dispose();
                    var compressedCount = _countingWritableSubStream!.Count;

                    byte[] intBuf = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(intBuf, crc32Stream.Crc);
                    _countingWritableSubStream.Write(intBuf, 0, 4);

                    BinaryPrimitives.WriteInt64LittleEndian(intBuf, _writeCount);
                    _countingWritableSubStream.Write(intBuf, 0, 8);

                    //total with headers
                    BinaryPrimitives.WriteUInt64LittleEndian(intBuf, compressedCount + 6 + 20);
                    _countingWritableSubStream.Write(intBuf, 0, 8);
                }
                _finished = true;
            }
        }

        #region Stream methods

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (disposing)
            {
                Finish();
                _stream.Dispose();
            }
        }

        public CompressionMode Mode { get; private set; }

        public override bool CanRead => Mode == CompressionMode.Decompress;

        public override bool CanSeek => false;

        public override bool CanWrite => Mode == CompressionMode.Compress;

        public override void Flush()
        {
            _stream.Flush();
        }

        // TODO: Both Length and Position are sometimes feasible, but would require
        // reading the output length when we initialize.
        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

        public override int ReadByte() => _stream.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotImplementedException();


#if !NET461 && !NETSTANDARD2_0

        public override int Read(Span<byte> buffer)
        {
            return _stream.Read(buffer);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _stream.Write(buffer);

            _writeCount += buffer.Length;
        }

#endif

        public override void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
            _writeCount += count;
        }

        public override void WriteByte(byte value)
        {
            _stream.WriteByte(value);
            ++_writeCount;
        }

        #endregion

        /// <summary>
        /// Determines if the given stream is positioned at the start of a v1 LZip
        /// file, as indicated by the ASCII characters "LZIP" and a version byte
        /// of 1, followed by at least one byte.
        /// </summary>
        /// <param name="stream">The stream to read from. Must not be null.</param>
        /// <returns><c>true</c> if the given stream is an LZip file, <c>false</c> otherwise.</returns>
        public static async ValueTask<bool> IsLZipFileAsync(Stream stream) => await ValidateAndReadSize(stream) != 0;

        /// <summary>
        /// Reads the 6-byte header of the stream, and returns 0 if either the header
        /// couldn't be read or it isn't a validate LZIP header, or the dictionary
        /// size if it *is* a valid LZIP file.
        /// </summary>
        private static async ValueTask<int> ValidateAndReadSize(Stream stream)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            // Read the header
            using var buffer = MemoryPool<byte>.Shared.Rent(6);
            var header = buffer.Memory.Slice(0,6);
            int n = await stream.ReadAsync(header);

            // TODO: Handle reading only part of the header?

            if (n != 6)
            {
                return 0;
            }

            if (header.Span[0] != 'L' || header.Span[1] != 'Z' || header.Span[2] != 'I' || header.Span[3] != 'P' || header.Span[4] != 1 /* version 1 */)
            {
                return 0;
            }
            int basePower = header.Span[5] & 0x1F;
            int subtractionNumerator = (header.Span[5] & 0xE0) >> 5;
            return (1 << basePower) - subtractionNumerator * (1 << (basePower - 4));
        }

        private static readonly byte[] headerBytes = new byte[6] { (byte)'L', (byte)'Z', (byte)'I', (byte)'P', 1, 113 };

        public static void WriteHeaderSize(Stream stream)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            // hard coding the dictionary size encoding
            stream.Write(headerBytes, 0, 6);
        }

        /// <summary>
        /// Creates a byte array to communicate the parameters and dictionary size to LzmaStream.
        /// </summary>
        private static byte[] GetProperties(int dictionarySize) =>
            new byte[]
            {
                // Parameters as per http://www.nongnu.org/lzip/manual/lzip_manual.html#Stream-format
                // but encoded as a single byte in the format LzmaStream expects.
                // literal_context_bits = 3
                // literal_pos_state_bits = 0
                // pos_state_bits = 2
                93,
                // Dictionary size as 4-byte little-endian value
                (byte)(dictionarySize & 0xff),
                (byte)((dictionarySize >> 8) & 0xff),
                (byte)((dictionarySize >> 16) & 0xff),
                (byte)((dictionarySize >> 24) & 0xff)
            };
    }
}
