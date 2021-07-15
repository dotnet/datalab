using System;

namespace WoodStar
{
    internal static class HelperMethods
    {
        public static void WriteUnsignedShortBigEndian(this byte[] buffer, int offset, ushort value)
        {
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)value;
        }

        public static void WriteUnsignedShortLittleEndian(this byte[] buffer, int offset, ushort value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
        }

        public static void WriteUnsignedIntBigEndian(this byte[] buffer, int offset, uint value)
        {
            buffer[offset++] = (byte)(value >> 24);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)value;
        }

        public static void WriteUnsignedIntLittleEndian(this byte[] buffer, int offset, uint value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }

        public static void WriteUnsignedLongLittleEndian(this byte[] buffer, int offset, ulong value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
            buffer[offset++] = (byte)(value >> 32);
            buffer[offset++] = (byte)(value >> 40);
            buffer[offset++] = (byte)(value >> 48);
            buffer[offset++] = (byte)(value >> 56);
        }

        public static void WriteIntBigEndian(this byte[] buffer, int offset, int value)
        {
            buffer[offset++] = (byte)(value >> 24);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)value;
        }

        public static void WriteIntLittleEndian(this byte[] buffer, int offset, int value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }

        public static void WriteByte(this byte[] buffer, int offset, byte value)
        {
            buffer[offset] = value;
        }

        public static void WriteBytes(this byte[] buffer, int offset, params byte[] value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                buffer[offset++] = value[i];
            }
        }

        public static ushort ReadUshortBigEndian(this ReadOnlySpan<byte> span, int offset)
        {
            return (ushort)(256 * span[offset] + span[offset + 1]);
        }

        public static ushort ReadUshortLittleEndian(this ReadOnlySpan<byte> span, int offset)
        {
            return (ushort)(256 * span[offset + 1] + span[offset]);
        }
    }
}
