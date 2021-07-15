using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WoodStar.Tds.Streams;

public class Login7Stream
{
    // Represent support for SqlServer 2012 & higher (Same as TDS 7.4)
    private static readonly byte[] _clientTdsVersion = new byte[4] { 0x04, 0x00, 0x00, 0x74 };
    private readonly int _length;
    private readonly uint _packetSize;
    private readonly byte[] _clientProgramVersion;
    private readonly uint _clientProcessId;
    private readonly uint _connectionId;
    private readonly byte _optionFlags1;
    private readonly byte _optionFlags2;
    private readonly byte _typeFlags;
    private readonly byte _optionFlags3;
    private readonly int _clientTimeZone = 0;
    // ClientLCID
    // LCID - 20 bit, ColFlags - 8 bit, Version 4 bit
    // Unsure of values
    private readonly byte[] _clientLcid = new byte[4] { 0, 0, 4, 9 }; // en-us

    private readonly string _hostName;
    private readonly string _userName;
    private readonly string _password;
    private readonly string _applicationName = "Woodstar";
    private readonly string _serverName = ".";
    private readonly string? _extension;
    private readonly string _clientInterfaceName = "Woodstar";
    private readonly string? _language;
    private readonly string _database;
    private readonly byte[] _clientId; // 6 byte // MAC address
    private readonly string _sspi = "";
    private readonly string? _attachDbFile;
    private readonly string? _changePassword;

    public Login7Stream(string userName, string password, string database, byte[] macAdress)
    {
        _hostName = Environment.MachineName;
        _userName = userName;
        _password = password;
        _database = database;
        _clientId = macAdress;

        _packetSize = 4096;

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

        _length = 36 /* Pre-offset part */ + 58 /* Offsets part */
            + 2 * (_hostName.Length + _userName.Length + _password.Length + _applicationName.Length + _serverName.Length
                    + (_extension?.Length ?? 0) + _clientInterfaceName.Length + (_language?.Length ?? 0) + _database.Length + _sspi.Length
                    + (_attachDbFile?.Length ?? 0) + (_changePassword?.Length ?? 0));
    }

    public async Task SendPacket(Stream stream)
    {
        var length = _length;
        var packetId = 1;
        var memoryOwner = MemoryPool<byte>.Shared.Rent(Math.Min(length, 4096));
        while (length != 0)
        {
            if (length < 4088)
            {
                var packetLength = length + TdsHeader.HeaderSize;
                var header = new TdsHeader(PacketType.TDS7Login, PacketStatus.EOM, packetLength, spid: 0, packetId);
                var buffer = memoryOwner.Memory;
                header.Write(buffer);
                Write(buffer[8..]);
                var a = HelperMethods.PrintBuffer(buffer.ToArray(), header.Length);
                await stream.WriteAsync(buffer[..packetLength]);

                length -= length;
            }
            else
            {
                // TODO: when the stream is longer than 4088 bytes
                throw new NotImplementedException();
            }
        }

        memoryOwner.Dispose();
    }

    private void Write(Memory<byte> buffer)
    {
        buffer.WriteUnsignedIntLittleEndian((ushort)_length);
        buffer[4..].WriteBytes(_clientTdsVersion);
        buffer[8..].WriteUnsignedIntLittleEndian(_packetSize);
        buffer[12..].WriteBytes(_clientProgramVersion);
        buffer[16..].WriteUnsignedIntLittleEndian(_clientProcessId);
        buffer[20..].WriteUnsignedIntLittleEndian(_connectionId);
        buffer[24..].Span[0] = _optionFlags1;
        buffer[25..].Span[0] = _optionFlags2;
        buffer[26..].Span[0] = _typeFlags;
        buffer[27..].Span[0] = _optionFlags3;
        buffer[28..].WriteIntBigEndian(_clientTimeZone);
        buffer[32..].WriteBytes(_clientLcid);

        buffer = buffer[36..];
        // OffsetLength
        ushort dataOffset = 94;

        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _hostName);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _userName);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _password);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _applicationName);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _serverName);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _extension);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _clientInterfaceName);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _language);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _database);
        buffer.WriteBytes(_clientId);
        buffer = buffer[6..];
        if (_sspi.Length > ushort.MaxValue)
        {
            // TODO: Not sure how would offset work-out if the length is more than ushort size since offset value would overflow then
            throw new NotImplementedException();
        }
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _sspi);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _attachDbFile);
        (buffer, dataOffset) = WriteOffset(buffer, dataOffset, _changePassword);
        buffer.WriteIntLittleEndian(0);
        buffer = buffer[4..];

        buffer = WriteString(buffer, _hostName);
        buffer = WriteString(buffer, _userName);
        buffer = WriteEncryptedPassword(buffer, _password);
        buffer = WriteString(buffer, _applicationName);
        buffer = WriteString(buffer, _serverName);
        buffer = WriteString(buffer, _extension);
        buffer = WriteString(buffer, _clientInterfaceName);
        buffer = WriteString(buffer, _language);
        buffer = WriteString(buffer, _database);
        buffer = WriteString(buffer, _sspi);
        buffer = WriteString(buffer, _attachDbFile);
        buffer = WriteEncryptedPassword(buffer, _changePassword);

        static (Memory<byte>, ushort) WriteOffset(Memory<byte> buffer, ushort dataOffset, string? value)
        {
            var length = (ushort)(value?.Length ?? 0);
            buffer.WriteUnsignedShortLittleEndian(dataOffset);
            buffer[2..].WriteUnsignedShortLittleEndian(length);

            return (buffer[4..], (ushort)(dataOffset + 2 * length));

        }

        static Memory<byte> WriteString(Memory<byte> buffer, string? value)
        {
            if (value == null)
            {
                return buffer;
            }

            for (var i = 0; i < value.Length; i++)
            {
                buffer.Span[2 * i] = (byte)value[i];
                buffer.Span[2 * i + 1] = 0;
            }

            return buffer[(2 * value.Length)..];
        }

        static Memory<byte> WriteEncryptedPassword(Memory<byte> buffer, string? value)
        {
            if (value == null)
            {
                return buffer;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var byteValue = (byte)value[i];
                var firstFourBits = byteValue & 0b11110000;
                var lastFourBits = byteValue & 0b00001111;
                var swappedValue = (firstFourBits >> 4) + (lastFourBits << 4);
                swappedValue ^= 0xA5;
                buffer.Span[2 * i] = (byte)swappedValue;
                buffer.Span[2 * i + 1] = 0xA5;
            }

            return buffer[(2 * value.Length)..];
        }
    }
}
