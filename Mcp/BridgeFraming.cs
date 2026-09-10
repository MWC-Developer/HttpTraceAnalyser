using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace HttpTraceAnalyser.Mcp
{
    /// <summary>
    /// Length-prefixed message framing shared by <see cref="TraceBridgeServer"/> and
    /// <see cref="TraceBridgeClient"/>: each message is a 4-byte little-endian length prefix
    /// followed by that many bytes of UTF-8 JSON. Operates directly on the pipe's async Stream
    /// APIs rather than via <see cref="StreamReader"/>/<see cref="StreamWriter"/>, which were
    /// found (by direct testing) to hang indefinitely when wrapped around a
    /// <see cref="PipeStream"/> opened with <see cref="PipeOptions.Asynchronous"/>.
    /// </summary>
    internal static class BridgeFraming
    {
        private const int MaxFrameLength = 16 * 1024 * 1024; // guards against a malformed/hostile length prefix

        public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
        {
            var lengthPrefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payload.Length);
            await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one length-prefixed frame, or returns null if the stream was closed before any
        /// bytes of a new frame arrived (a clean end-of-connection).
        /// </summary>
        public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            var lengthPrefix = new byte[4];
            if (!await ReadExactAsync(stream, lengthPrefix, cancellationToken).ConfigureAwait(false))
                return null;

            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
            if (length < 0 || length > MaxFrameLength)
                throw new InvalidDataException($"Invalid bridge frame length: {length}.");

            var payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
                throw new EndOfStreamException("Connection closed mid-frame.");

            return payload;
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return offset == 0 ? false : throw new EndOfStreamException("Connection closed mid-frame.");

                offset += read;
            }

            return true;
        }
    }
}
