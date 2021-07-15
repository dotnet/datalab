using System;

namespace WoodStar
{
    public class Login7Stream
    {
        private static readonly byte[] _clientTdsVersion = new byte[] { 0x04, 0x00, 0x00, 0x74 }; // This is what client is supposed to send

        private const string LibraryName = "Woodstar";

        public int Length =>
            4 * 6
            + 4
            + 8
            + 4 * 12
            + 4
            + (UserName.Length + Password.Length + LibraryName.Length + ApplicationName.Length + 10 + Environment.MachineName.Length) * 2;

        //public Version ClientProgramVersion { get; set; }
        public uint ClientPid { get; set; }
        public uint ConnectionId { get; set; }

        public string UserName { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;
        public string ApplicationName { get; init; } = LibraryName;

        public void WriteToBuffer(byte[] buffer)
        {
            var bufferOffset = TdsHeader.HeaderSize;
            buffer.WriteUnsignedIntLittleEndian(bufferOffset, (uint)Length);
            bufferOffset += 4;
            buffer.WriteBytes(bufferOffset, _clientTdsVersion);
            bufferOffset += 4;
            buffer.WriteUnsignedIntLittleEndian(bufferOffset, 4096); // PacketSize defaulted to 4096
            bufferOffset += 4;
            buffer.WriteBytes(bufferOffset, new byte[] { 6, 0, 0, 0 });
            bufferOffset += 4;
            buffer.WriteUnsignedIntLittleEndian(bufferOffset, 0);
            bufferOffset += 4;
            buffer.WriteUnsignedIntLittleEndian(bufferOffset, 0);
            bufferOffset += 4;

            // fByteOrder - ORDER_X86 - 1 BIT
            // fChar - CHARSET_ASCII - 1 BIT
            // fFloat - FLOAT_IEEE_754 - 2 BIT
            // fDumpLoad - DUMPLOAD_ON - 1 BIT
            // fUseDB - USE_DB_ON - 1 BIT
            // fDatabase - INIT_DB_FATAL - 1 BIT
            // fSetLang - SET_LANG_ON - 1 BIT
            var optionFlags1 = (byte)0;
            buffer.WriteByte(bufferOffset, optionFlags1);
            bufferOffset += 1;

            // fLanguage - INIT_LANG_FATAL - 1 BIT
            // fODBC - ODBC_ON - 1 BIT
            // fTranBoundary - Deprecated - 1 BIT
            // fCacheConnect - Deprecated - 1 BIT
            // fUserType - USER_NORMAL - 3 BIT
            // fIntSecurity - INTEGRATED_SECURITY_OFF - 1 BIT
            var optionFlags2 = (byte)0;
            buffer.WriteByte(bufferOffset, optionFlags2);
            bufferOffset += 1;

            // fSQLType - SQL_TSQL - 4 BIT
            // fOLEDB - OLEDB_OFF - 1 BIT
            // fReadOnlyIntent - 1 BIT
            // FRESERVEDBIT - 2 BIT
            var typeFlags = (byte)0;
            buffer.WriteByte(bufferOffset, typeFlags);
            bufferOffset += 1;

            // OptionFlags3
            var optionFlags3 = (byte)0;
            buffer.WriteByte(bufferOffset, optionFlags3);
            bufferOffset += 1;

            // ClientTimeZone
            buffer.WriteIntBigEndian(bufferOffset, 0);
            bufferOffset += 4;

            // ClientLCID
            buffer.WriteUnsignedIntBigEndian(bufferOffset, 0); // en-us specific locale
            bufferOffset += 4;

            // OffsetLength
            ushort dataOffset = 94;
            var hostName = Environment.MachineName;
            var serverName = ".";
            var extension = "";
            var language = "";
            var attachDbFileName = "";
            var changePassword = "";

            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, hostName);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, UserName);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, Password);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, ApplicationName);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, serverName);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, extension);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, LibraryName);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, language);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, Database);
            buffer.WriteBytes(bufferOffset, new byte[6]); // Client Mac address
            bufferOffset += 6;
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, ""); // SSPI
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, attachDbFileName);
            WriteOffsetLength(buffer, ref bufferOffset, ref dataOffset, changePassword);
            buffer.WriteIntLittleEndian(bufferOffset, 0); // SSPI Long
            bufferOffset += 4;

            WriteString(buffer, ref bufferOffset, hostName);
            WriteString(buffer, ref bufferOffset, UserName);
            WriteEncryptedPassword(buffer, ref bufferOffset, Password);
            WriteString(buffer, ref bufferOffset, ApplicationName);
            WriteString(buffer, ref bufferOffset, serverName);
            WriteString(buffer, ref bufferOffset, LibraryName);
            WriteString(buffer, ref bufferOffset, language);
            WriteString(buffer, ref bufferOffset, Database);
            WriteString(buffer, ref bufferOffset, attachDbFileName);
            WriteString(buffer, ref bufferOffset, changePassword);

            static void WriteOffsetLength(byte[] buffer, ref int bufferOffset, ref ushort dataOffset, string value)
            {
                buffer.WriteUnsignedShortLittleEndian(bufferOffset, dataOffset);
                buffer.WriteUnsignedShortLittleEndian(bufferOffset + 2, (ushort)value.Length);
                dataOffset += (ushort)(value.Length * 2);
                bufferOffset += 4;
            }

            static void WriteString(byte[] buffer, ref int bufferOffset, string value)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    buffer.WriteByte(bufferOffset++, (byte)value[i]);
                    buffer.WriteByte(bufferOffset++, 0);
                }
            }

            static void WriteEncryptedPassword(byte[] buffer, ref int bufferOffset, string value)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var byteValue = (byte)value[i];
                    var firstFourBits = byteValue & 0b11110000;
                    var lastFourBits = byteValue & 0b00001111;
                    var swappedValue = (firstFourBits >> 4) + (lastFourBits << 4);
                    swappedValue ^= 0xA5;
                    buffer.WriteByte(bufferOffset++, (byte)swappedValue);
                    buffer.WriteByte(bufferOffset++, 0xA5);
                }
            }
        }
    }
}
