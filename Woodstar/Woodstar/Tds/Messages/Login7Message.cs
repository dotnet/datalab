using System;
using System.Buffers;
using System.Text;
using Woodstar.Buffers;
using Woodstar.Tds.Packets;

namespace Woodstar.Tds.Messages;

class Login7Message : IFrontendMessage
{
    const int Parameters = 12;
    const int ParametersHeaderLength = Parameters * (sizeof(ushort) + sizeof(ushort));
    const int AllParametersLength = ParametersHeaderLength + 6 + sizeof(int); //clientId == 6

    // Represent support for SqlServer 2012 & higher (Same as TDS 7.4)
    static ReadOnlySpan<byte> _clientTdsVersion => new byte[4] { 0x04, 0x00, 0x00, 0x74 };
    readonly uint _packetSize;
    readonly uint _length;
    readonly byte[] _clientProgramVersion;
    readonly uint _clientProcessId;
    readonly uint _connectionId;
    readonly byte _optionFlags1;
    readonly byte _optionFlags2;
    readonly byte _typeFlags;
    readonly byte _optionFlags3;

    readonly int _clientTimeZone = 0;
    // ClientLCID
    // LCID - 20 bit, ColFlags - 8 bit, Version 4 bit
    // Unsure of values
    readonly byte[] _clientLcid = new byte[4] { 0, 0, 4, 9 }; // en-us

    readonly string? _hostName;
    readonly string? _userName;
    readonly string? _password;
    readonly string? _applicationName = "Woodstar";
    readonly string? _serverName = ".";
    readonly string? _extension;
    readonly string? _clientInterfaceName = "Woodstar";
    readonly string? _language;
    readonly string? _database;
    readonly byte[] _clientId; // 6 byte // MAC address
    readonly string? _sspi = "";
    readonly string? _attachDbFile;
    readonly string? _changePassword;

    public Login7Message(string? userName, string? password, string? database, byte[] macAdress)
    {
        _packetSize = 4 * 1024;
        _hostName = Environment.MachineName;
        _userName = userName;
        _password = password;
        _database = database;
        _clientId = macAdress;

        _clientProgramVersion = new byte[4] { 0, 0, 0, 0 };
        _clientProcessId = 0;
        _connectionId = 0;

        // fByteOrder - ORDER_X86/ORDER_6800 - 1
        // fChar - CHARSET_ASCII/CHARSET_EBCDIC - 1
        // fFloat - FLOAT_IEEE_754/FLOAT_VAX/ND5000 - 2
        // fDumpLoad - DUMPLOAD_ON/DUMPLOAD_OFF - 1
        // fUseDB - USE_DB_OFF/USE_DB_ON - 1
        // fDatabase - INIT_DB_WARN/INIT_DB_FATAL - 1
        // fSetLang - SET_LANG_OFF/SET_LANG_ON - 1
        _optionFlags1 = 0b11010000;

        // fLanguage - INIT_LANG_WARN/INIT_LANG_FATAL - 1
        // fODBC - ODBC_OFF/ODBC_ON - 1
        // fTranBoundary (Removed in 7.2) - 1
        // fCacheConnect (Removed in 7.2) - 1
        // fUserType - USER_NORMAL/USER_SERVER/USER_REMUSER/USER_SQLREPL - 3
        // fIntSecurity - INTEGRATED_SECURITY_OFF/INTEGRATED_SECURITY_ON - 1
        _optionFlags2 = 0b01000000;

        // fSQLType - 4
        // fOLEDB - 1
        // fReadOnlyIntent - 1
        // Reserved - 2
        _typeFlags = 0b00010000;

        // fChangePassword - 1
        // fUserInstance - 1
        // fSendYukonBinaryXML - 1
        // fUnknownCollationHandling - 1
        // fExtension - 1
        // Reserved - 3
        _optionFlags3 = 0b00000000;

        _extension = null;
        _language = null;
        _attachDbFile = null;
        _changePassword = null;

        // TODO should use unicode bytecount.
        _length = (uint)(36 /* Pre-offset part */ + AllParametersLength /* Offsets part */
            + 2 * (_hostName.Length + _userName.Length + _password.Length + _applicationName.Length + _serverName.Length
                    + (_extension?.Length ?? 0) + _clientInterfaceName.Length + (_language?.Length ?? 0) + _database.Length + _sspi.Length
                    + (_attachDbFile?.Length ?? 0) + (_changePassword?.Length ?? 0)));
    }

    public static TdsPacketHeader MessageType => TdsPacketHeader.CreateType(TdsPacketType.Tds7Login, MessageStatus.Normal);

    public bool CanWriteSynchronously => true;
    public void Write<TWriter>(ref BufferWriter<TWriter> writer) where TWriter : IBufferWriter<byte>
    {
        writer.WriteUIntLittleEndian(_length);
        writer.WriteRaw(_clientTdsVersion);
        writer.WriteUIntLittleEndian(_packetSize);
        writer.WriteRaw(_clientProgramVersion);
        writer.WriteUIntLittleEndian(_clientProcessId);
        writer.WriteUIntLittleEndian(_connectionId);

        writer.WriteByte(_optionFlags1);
        writer.WriteByte(_optionFlags2);
        writer.WriteByte(_typeFlags);
        writer.WriteByte(_optionFlags3);

        writer.WriteInt(_clientTimeZone);
        writer.WriteRaw(_clientLcid);

        var dataOffset = (ushort)(writer.BufferedBytes + AllParametersLength);
        WriteParameterHeader(ref writer, ref dataOffset, _hostName);
        WriteParameterHeader(ref writer, ref dataOffset, _userName);
        WriteParameterHeader(ref writer, ref dataOffset, _password);
        WriteParameterHeader(ref writer, ref dataOffset, _applicationName);
        WriteParameterHeader(ref writer, ref dataOffset, _serverName);
        WriteParameterHeader(ref writer, ref dataOffset, _extension); // TODO should be count in bytes not chars.
        WriteParameterHeader(ref writer, ref dataOffset, _clientInterfaceName);
        WriteParameterHeader(ref writer, ref dataOffset, _language);
        WriteParameterHeader(ref writer, ref dataOffset, _database);
        writer.WriteRaw(_clientId);
        WriteParameterHeader(ref writer, ref dataOffset, _sspi);
        WriteParameterHeader(ref writer, ref dataOffset, _attachDbFile);
        WriteParameterHeader(ref writer, ref dataOffset, _changePassword);
        writer.WriteInt(0); // cbSSPILong

        writer.WriteString(_hostName, Encoding.Unicode);
        writer.WriteString(_userName, Encoding.Unicode);
        WriteEncryptedPassword(ref writer, _password);
        writer.WriteString(_applicationName, Encoding.Unicode);
        writer.WriteString(_serverName, Encoding.Unicode);
        writer.WriteString(_extension ?? "", Encoding.Unicode);
        writer.WriteString(_clientInterfaceName, Encoding.Unicode);
        writer.WriteString(_language ?? "", Encoding.Unicode);
        writer.WriteString(_database, Encoding.Unicode);
        writer.WriteString(_sspi, Encoding.Unicode);
        writer.WriteString(_attachDbFile ?? "", Encoding.Unicode);
        WriteEncryptedPassword(ref writer, _changePassword ?? "");

        static void WriteParameterHeader(ref BufferWriter<TWriter> writer, ref ushort dataOffset, string? value)
        {
            var byteCount = value?.Length ?? 0;
            if (byteCount > 128)
                throw new InvalidOperationException("Parameter too large.");

            var shortByteCount = (ushort)byteCount;
            writer.WriteUShortLittleEndian(dataOffset);
            writer.WriteUShortLittleEndian(shortByteCount);
            dataOffset += value is null ? (ushort)0 : (ushort)Encoding.Unicode.GetByteCount(value);
        }

        static void WriteEncryptedPassword(ref BufferWriter<TWriter> writer, string? value)
        {
            var buffer = Encoding.Unicode.GetBytes(value);
            for (var i = 0; i < buffer.Length; i++)
            {
                // Swap 4 high bits with 4 low bits
                buffer[i] = (byte)(((buffer[i] & 0xf0) >> 4) | ((buffer[i] & 0xf) << 4));
                buffer[i] ^= 0xA5;
            }
            writer.WriteRaw(buffer);
        }

    }
}
