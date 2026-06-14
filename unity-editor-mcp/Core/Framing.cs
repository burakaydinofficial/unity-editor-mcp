using System;
using System.Collections.Generic;
using System.Text;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Length-prefixed message framing shared by both halves of the bridge: a
    /// 4-byte big-endian length header followed by a UTF-8 payload. This is the
    /// Unity-independent core of the transport (netstandard2.0, no UnityEngine /
    /// UnityEditor), so it can be unit-tested with plain <c>dotnet test</c>.
    /// See protocol/schemas/envelope.schema.json.
    /// </summary>
    public sealed class MessageFramer
    {
        /// <summary>Maximum accepted message size; matches the Node client cap.</summary>
        public const int MaxMessageBytes = 1024 * 1024;

        private readonly List<byte> _buffer = new List<byte>();

        /// <summary>Appends a slice of received bytes to the internal buffer.</summary>
        public void Append(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            // Bulk copy — ArraySegment is an ICollection<byte>, so AddRange uses CopyTo
            // instead of a per-byte Add (cheaper for large frames).
            _buffer.AddRange(new ArraySegment<byte>(data, offset, count));
        }

        /// <summary>Appends an entire received array to the internal buffer.</summary>
        public void Append(byte[] data) => Append(data, 0, data?.Length ?? 0);

        /// <summary>
        /// Tries to read one complete framed message. Returns <c>false</c> when
        /// more bytes are needed. Throws <see cref="FramingException"/> on a frame
        /// whose declared length is negative or exceeds <see cref="MaxMessageBytes"/>
        /// (a corrupt / oversize prefix) — the editor side previously had no such
        /// guard.
        /// </summary>
        public bool TryReadMessage(out string message)
        {
            message = null;
            if (_buffer.Count < 4) return false;

            int length = ReadInt32BigEndian(_buffer, 0);
            if (length < 0 || length > MaxMessageBytes)
                throw new FramingException($"Invalid framed message length: {length}");

            if (_buffer.Count < 4 + length) return false;

            var bytes = new byte[length];
            _buffer.CopyTo(4, bytes, 0, length);
            _buffer.RemoveRange(0, 4 + length);
            message = Encoding.UTF8.GetString(bytes);
            return true;
        }

        /// <summary>Encodes a string payload as a single framed message.</summary>
        public static byte[] Encode(string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var body = Encoding.UTF8.GetBytes(message);
            var framed = new byte[4 + body.Length];
            WriteInt32BigEndian(framed, 0, body.Length);
            Buffer.BlockCopy(body, 0, framed, 4, body.Length);
            return framed;
        }

        private static int ReadInt32BigEndian(List<byte> buf, int index)
        {
            return (buf[index] << 24) | (buf[index + 1] << 16) | (buf[index + 2] << 8) | buf[index + 3];
        }

        private static void WriteInt32BigEndian(byte[] buf, int index, int value)
        {
            buf[index] = (byte)((value >> 24) & 0xFF);
            buf[index + 1] = (byte)((value >> 16) & 0xFF);
            buf[index + 2] = (byte)((value >> 8) & 0xFF);
            buf[index + 3] = (byte)(value & 0xFF);
        }
    }

    /// <summary>Thrown when a framed message violates the protocol framing rules.</summary>
    public sealed class FramingException : Exception
    {
        public FramingException(string message) : base(message) { }
    }
}
