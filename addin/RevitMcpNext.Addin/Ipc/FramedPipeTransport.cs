using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpNext.Addin.Ipc
{
    internal static class FramedPipeTransport
    {
        public const int MaxFrameBytes = 4 * 1024 * 1024;

        public static async Task<string> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
            }

            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxFrameBytes)
            {
                throw new InvalidDataException("Invalid frame length: " + length);
            }

            byte[] body = await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(body);
        }

        public static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken cancellationToken)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload);
            if (body.Length > MaxFrameBytes)
            {
                throw new InvalidDataException("Frame too large: " + body.Length);
            }

            byte[] header = BitConverter.GetBytes(body.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
            }

            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("Pipe closed while reading frame.");
                }

                offset += read;
            }

            return buffer;
        }
    }
}

