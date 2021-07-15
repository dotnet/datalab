using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace WoodStar
{
    internal static class HelperMethods
    {
        public static void WriteUnsignedShortBigEndian(this Memory<byte> buffer, ushort value)
        {
            buffer.Span[0] = (byte)(value >> 8);
            buffer.Span[1] = (byte)value;
        }

        public static void WriteUnsignedShortLittleEndian(this Memory<byte> buffer, ushort value)
        {
            buffer.Span[0] = (byte)value;
            buffer.Span[1] = (byte)(value >> 8);
        }

        public static void WriteUnsignedIntBigEndian(this Memory<byte> buffer, uint value)
        {
            buffer.Span[0] = (byte)(value >> 24);
            buffer.Span[1] = (byte)(value >> 16);
            buffer.Span[2] = (byte)(value >> 8);
            buffer.Span[3] = (byte)value;
        }

        public static void WriteUnsignedIntLittleEndian(this Memory<byte> buffer, uint value)
        {
            buffer.Span[0] = (byte)value;
            buffer.Span[1] = (byte)(value >> 8);
            buffer.Span[2] = (byte)(value >> 16);
            buffer.Span[3] = (byte)(value >> 24);
        }

        public static void WriteIntBigEndian(this Memory<byte> buffer, int value)
        {
            buffer.Span[0] = (byte)(value >> 24);
            buffer.Span[1] = (byte)(value >> 16);
            buffer.Span[2] = (byte)(value >> 8);
            buffer.Span[3] = (byte)value;
        }

        public static void WriteIntLittleEndian(this Memory<byte> buffer, int value)
        {
            buffer.Span[0] = (byte)value;
            buffer.Span[1] = (byte)(value >> 8);
            buffer.Span[2] = (byte)(value >> 16);
            buffer.Span[3] = (byte)(value >> 24);
        }

        public static void WriteUnsignedLongLittleEndian(this Memory<byte> buffer, ulong value)
        {
            buffer.Span[0] = (byte)value;
            buffer.Span[1] = (byte)(value >> 8);
            buffer.Span[2] = (byte)(value >> 16);
            buffer.Span[3] = (byte)(value >> 24);
            buffer.Span[4] = (byte)(value >> 32);
            buffer.Span[5] = (byte)(value >> 40);
            buffer.Span[6] = (byte)(value >> 48);
            buffer.Span[7] = (byte)(value >> 56);
        }

        public static void WriteBytes(this Memory<byte> buffer, byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                buffer.Span[i] = bytes[i];
            }
        }

        public static bool ReadBVarchar(ref this SequenceReader<byte> reader, [NotNullWhen(true)] out string value)
        {
            value = "";
            if (reader.TryRead(out var len))
            {
                var sequence = reader.UnreadSequence.Slice(0, 2 * len);
                value = Encoding.Unicode.GetString(sequence);
                reader.Advance(2 * len);

                return true;
            }

            return false;
        }

        public static bool ReadUsVarchar(ref this SequenceReader<byte> reader, [NotNullWhen(true)] out string value)
        {
            value = "";
            if (reader.TryReadLittleEndian(out ushort len))
            {
                var sequence = reader.UnreadSequence.Slice(0, 2 * len);
                value = Encoding.Unicode.GetString(sequence);
                reader.Advance(2 * len);

                return true;
            }

            return false;
        }

        public static bool TryReadLittleEndian(ref this SequenceReader<byte> reader, [NotNullWhen(true)] out ushort value)
        {
            if (reader.TryReadByteArray(size: 2, out var bytes))
            {
                value = 0;
                for (var i = bytes.Length - 1; i >= 0; i--)
                {
                    value = (ushort)((value << 8) | bytes[i]);
                }

                return true;
            }

            value = default;
            return false;
        }

        public static bool TryReadLittleEndian(ref this SequenceReader<byte> reader, [NotNullWhen(true)] out ulong value)
        {
            if (reader.TryReadByteArray(size: 8, out var bytes))
            {
                value = 0;
                for (var i = bytes.Length - 1; i >= 0; i--)
                {
                    value = (value << 8) | bytes[i];
                }

                return true;
            }

            value = default;
            return false;
        }

        public static bool TryReadByteArray(ref this SequenceReader<byte> reader, int size, [NotNullWhen(true)] out byte[]? value)
        {
            value = new byte[size];
            for (var i = 0; i < size; i++)
            {
                if (reader.TryRead(out var b))
                {
                    value[i] = b;
                }
                else
                {
                    value = default;
                    return false;
                }
            }

            return true;
        }

        public static string PrintBuffer(byte[] array, int length)
        {
            var result = "";
            for (var i = 0; i < length; i++)
            {
                result += array[i] + "\t";
            }

            return result;
        }
    }
}
