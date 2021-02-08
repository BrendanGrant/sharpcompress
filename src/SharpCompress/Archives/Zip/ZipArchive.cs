﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Zip;
using SharpCompress.Common.Zip.Headers;
using SharpCompress.Compressors.Deflate;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Readers.Zip;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace SharpCompress.Archives.Zip
{
    public class ZipArchive : AbstractWritableArchive<ZipArchiveEntry, ZipVolume>
    {
#nullable disable
        private readonly SeekableZipHeaderFactory headerFactory;
#nullable enable

        /// <summary>
        /// Gets or sets the compression level applied to files added to the archive,
        /// if the compression method is set to deflate
        /// </summary>
        public CompressionLevel DeflateCompressionLevel { get; set; }

        /// <summary>
        /// Constructor expects a filepath to an existing file.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="readerOptions"></param>
        public static ZipArchive Open(string filePath, ReaderOptions? readerOptions = null)
        {
            filePath.CheckNotNullOrEmpty(nameof(filePath));
            return Open(new FileInfo(filePath), readerOptions ?? new ReaderOptions());
        }

        /// <summary>
        /// Constructor with a FileInfo object to an existing file.
        /// </summary>
        /// <param name="fileInfo"></param>
        /// <param name="readerOptions"></param>
        public static ZipArchive Open(FileInfo fileInfo, ReaderOptions? readerOptions = null,
                                      CancellationToken cancellationToken = default)
        {
            fileInfo.CheckNotNull(nameof(fileInfo));
            return new ZipArchive(fileInfo, readerOptions ?? new ReaderOptions(), cancellationToken);
        }

        /// <summary>
        /// Takes a seekable Stream as a source
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="readerOptions"></param>
        public static ZipArchive Open(Stream stream, ReaderOptions? readerOptions = null,
                                      CancellationToken cancellationToken = default)
        {
            stream.CheckNotNull(nameof(stream));
            return new ZipArchive(stream, readerOptions ?? new ReaderOptions(), cancellationToken);
        }

        public static ValueTask<bool> IsZipFile(string filePath, string? password = null)
        {
            return IsZipFileAsync(new FileInfo(filePath), password);
        }

        public static async ValueTask<bool> IsZipFileAsync(FileInfo fileInfo, string? password = null)
        {
            if (!fileInfo.Exists)
            {
                return false;
            }

            await using Stream stream = fileInfo.OpenRead();
            return await IsZipFileAsync(stream, password);
        }

        public static async ValueTask<bool> IsZipFileAsync(Stream stream, string? password = null, CancellationToken cancellationToken = default)
        {
            StreamingZipHeaderFactory headerFactory = new(password, new ArchiveEncoding());
            try
            {
                RewindableStream rewindableStream;
                if (stream is RewindableStream rs)
                {
                    rewindableStream = rs;
                }
                else
                {
                    rewindableStream = new RewindableStream(stream);
                }
                ZipHeader? header = await headerFactory.ReadStreamHeader(rewindableStream, cancellationToken)
                                                       .FirstOrDefaultAsync(x => x.ZipHeaderType != ZipHeaderType.Split, cancellationToken: cancellationToken);
                if (header is null)
                {
                    return false;
                }
                return Enum.IsDefined(typeof(ZipHeaderType), header.ZipHeaderType);
            }
            catch (CryptographicException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Constructor with a FileInfo object to an existing file.
        /// </summary>
        /// <param name="fileInfo"></param>
        /// <param name="readerOptions"></param>
        internal ZipArchive(FileInfo fileInfo, ReaderOptions readerOptions,
                            CancellationToken cancellationToken)
            : base(ArchiveType.Zip, fileInfo, readerOptions, cancellationToken)
        {
            headerFactory = new SeekableZipHeaderFactory(readerOptions.Password, readerOptions.ArchiveEncoding);
        }

        protected override IAsyncEnumerable<ZipVolume> LoadVolumes(FileInfo file,
                                                                   CancellationToken cancellationToken)
        {
            return new ZipVolume(file.OpenRead(), ReaderOptions).AsAsyncEnumerable();
        }

        internal ZipArchive()
            : base(ArchiveType.Zip)
        {
        }

        /// <summary>
        /// Takes multiple seekable Streams for a multi-part archive
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="readerOptions"></param>
        internal ZipArchive(Stream stream, ReaderOptions readerOptions,
                            CancellationToken cancellationToken)
            : base(ArchiveType.Zip, stream, readerOptions, cancellationToken)
        {
            headerFactory = new SeekableZipHeaderFactory(readerOptions.Password, readerOptions.ArchiveEncoding);
        }

        protected override async IAsyncEnumerable<ZipVolume> LoadVolumes(IAsyncEnumerable<Stream> streams,
                                                                         [EnumeratorCancellation]CancellationToken cancellationToken)
        {
            yield return new ZipVolume(await streams.FirstAsync(cancellationToken: cancellationToken), ReaderOptions);
        }

        protected override async IAsyncEnumerable<ZipArchiveEntry> LoadEntries(IAsyncEnumerable<ZipVolume> volumes,
                                                                               [EnumeratorCancellation]CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            var volume = await volumes.SingleAsync(cancellationToken: cancellationToken);
            Stream stream = volume.Stream;
            await foreach (ZipHeader h in headerFactory.ReadSeekableHeader(stream, cancellationToken))
            {
                if (h != null)
                {
                    switch (h.ZipHeaderType)
                    {
                        case ZipHeaderType.DirectoryEntry:
                        {
                            yield return new ZipArchiveEntry(this,
                                                             new SeekableZipFilePart(headerFactory,
                                                                                     (DirectoryEntryHeader)h,
                                                                                     stream));
                        }
                            break;
                        case ZipHeaderType.DirectoryEnd:
                        {
                            byte[] bytes = ((DirectoryEndHeader)h).Comment ?? Array.Empty<byte>();
                            volume.Comment = ReaderOptions.ArchiveEncoding.Decode(bytes);
                            yield break;
                        }
                    }
                }
            }
        }

        public ValueTask SaveToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            return SaveToAsync(stream, new WriterOptions(CompressionType.Deflate), cancellationToken);
        }

        protected override async ValueTask SaveToAsync(Stream stream, WriterOptions options, 
                                                       IAsyncEnumerable<ZipArchiveEntry> oldEntries, 
                                                       IAsyncEnumerable<ZipArchiveEntry> newEntries, 
                                                       CancellationToken cancellationToken = default)
        {
            await using var writer = new ZipWriter(stream, new ZipWriterOptions(options));
            await foreach (var entry in oldEntries.Concat(newEntries)
                                                  .Where(x => !x.IsDirectory)
                                                  .WithCancellation(cancellationToken))
            {
                await using (var entryStream = await entry.OpenEntryStreamAsync(cancellationToken))
                {
                    await writer.WriteAsync(entry.Key, entryStream, entry.LastModifiedTime, cancellationToken);
                }
            }
        }

        protected override ValueTask<ZipArchiveEntry> CreateEntryInternal(string filePath, Stream source, long size, DateTime? modified,
                                                               bool closeStream, CancellationToken cancellationToken = default)
        {
            return new(new ZipWritableArchiveEntry(this, source, filePath, size, modified, closeStream));
        }

        public static ZipArchive Create()
        {
            return new();
        }

        protected override async ValueTask<IReader> CreateReaderForSolidExtraction()
        {
            var stream = (await Volumes.SingleAsync()).Stream;
            stream.Position = 0;
            return ZipReader.Open(stream, ReaderOptions);
        }
    }
}
