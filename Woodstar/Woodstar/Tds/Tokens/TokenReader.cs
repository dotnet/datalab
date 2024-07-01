using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Woodstar.Buffers;

namespace Woodstar.Tds.Tokens;

class TokenReader
{
    readonly BufferingStreamReader _streamReader;
    private readonly ResultSetReader _resultSetReader;
    private bool _rowReaderRented;

    public TokenReader(BufferingStreamReader streamReader)
    {
        _streamReader = streamReader;
        _resultSetReader = new(this, streamReader);
    }
    
    public async ValueTask<ResultSetReader> GetResultSetReaderAsync(List<ColumnData> columnData, CancellationToken cancellationToken = default)
    {
        Debug.Assert(!_rowReaderRented);
        await ReadAndExpectAsync<RowToken>(cancellationToken);
        _resultSetReader.Initialize(columnData);
        return _resultSetReader;
    }

    public async ValueTask<Token> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_rowReaderRented)
        {
            _resultSetReader.Reset();
            _rowReaderRented = false;
        }

        var streamReader = _streamReader;
        var reader = await streamReader.ReadAtLeastAsync(1, cancellationToken);
        var tokenTypeReadResult = reader.TryRead(out var tokenTypeByte);
        Debug.Assert(tokenTypeReadResult);
        var tokenType = (TokenType)tokenTypeByte;
        if (BackendMessage.DebugEnabled && !Enum.IsDefined(tokenType))
            throw new ArgumentOutOfRangeException();

        Token result; 
        switch (tokenType)
        {
            case TokenType.LOGINACK:
            {
                // TODO ushort can go out of bounds of underlying stream reader buffer.
                if (!reader.TryReadLittleEndian(out ushort length))
                {
                    reader = await streamReader.ReadAtLeastAsync(length, cancellationToken);
                    reader.TryReadLittleEndian(out length);
                }

                if (reader.Remaining < length)
                    reader = await streamReader.ReadAtLeastAsync(length, cancellationToken);

                reader.TryRead(out var @interface);
                var tdsVersion = new byte[4];
                reader.TryReadTo(tdsVersion.AsSpan());
                reader.TryReadBVarchar(out var programName, out _);
                var versionBytes = new byte[4];
                reader.TryReadTo(versionBytes.AsSpan());
                var version = new Version(versionBytes[0], versionBytes[1], (versionBytes[2] << 8) | versionBytes[3]);

                result = new LoginAckToken(@interface, tdsVersion, programName!, version);
                break;
            }
            case TokenType.ERROR:
            case TokenType.INFO:
            {
                if (!reader.TryReadLittleEndian(out ushort length))
                {
                    reader = await streamReader.ReadAtLeastAsync(length, cancellationToken);
                    reader.TryReadLittleEndian(out length);
                }

                if (reader.Remaining < length)
                    reader = await streamReader.ReadAtLeastAsync(length, cancellationToken);

                reader.TryReadLittleEndian(out int number);
                reader.TryRead(out var state);
                reader.TryRead(out var @class);
                reader.TryReadUsVarchar(out var msgText, out _);
                reader.TryReadBVarchar(out var serverName, out _);
                reader.TryReadBVarchar(out var procName, out _);
                reader.TryReadLittleEndian(out int lineNumber);

                result = tokenType is TokenType.INFO
                    ? new InfoToken(number, state, @class, msgText!, serverName!, procName!, lineNumber)
                    : throw new Exception(msgText);

                break;
            }
            case TokenType.ENVCHANGE:
            {
                if (!reader.TryReadLittleEndian(out ushort length))
                {
                    reader = await streamReader.ReadAtLeastAsync(length, cancellationToken);
                    reader.TryReadLittleEndian(out length);
                }

                if (reader.Remaining < length)
                    reader = await streamReader.ReadAtLeastAsync(length, cancellationToken);

                reader.TryRead(out var envTypeByte);
                var envType = (EnvChangeType)envTypeByte;
                if (BackendMessage.DebugEnabled && !Enum.IsDefined(envType))
                    throw new ArgumentOutOfRangeException();

                switch (envType)
                {
                    case EnvChangeType.Language:
                    case EnvChangeType.PacketSize:
                    case EnvChangeType.ResetAck:
                        reader.TryReadBVarchar(out var newValue, out _);
                        reader.TryReadBVarchar(out var oldValue, out _);
                        result = new EnvChangeToken(envType, newValue!, oldValue!);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                break;
            }
            case TokenType.DONE:
            case TokenType.DONEINPROC:
            case TokenType.DONEPROC:
            {
                const int doneLength = sizeof(ushort) + sizeof(ushort) + sizeof(ulong);
                if (reader.Remaining < doneLength)
                    reader = await streamReader.ReadAtLeastAsync(doneLength, cancellationToken);

                reader.TryReadLittleEndian(out ushort statusBytes);
                var status = (DoneStatus)statusBytes;
                if (BackendMessage.DebugEnabled && !Enum.IsDefined(status))
                    throw new ArgumentOutOfRangeException();

                reader.TryReadLittleEndian(out ushort curCmd);
                reader.TryReadLittleEndian(out ulong doneRowCount);
                result = tokenType switch
                {
                    TokenType.DONE => new DoneToken(status, curCmd, doneRowCount),
                    TokenType.DONEINPROC => new DoneInProcToken(status, curCmd, doneRowCount),
                    TokenType.DONEPROC => new DoneProcToken(status, curCmd, doneRowCount),
                    _ => throw new UnreachableException()
                };

                break;
            }
            case TokenType.COLMETADATA:
            {
                if (!reader.TryReadLittleEndian(out ushort count))
                {
                    reader = await streamReader.ReadAtLeastAsync(sizeof(ushort), cancellationToken);
                    reader.TryReadLittleEndian(out count);
                }

                count = (ushort)(count is 0xFF ? 0 : count);

                if (count is 0)
                {
                    reader.Advance(sizeof(ushort) * 2);
                    result = new ColumnMetadataToken(new());
                    break;
                }

                var columns = new List<ColumnData>();
                const int minimumColDataSize = sizeof(uint) + sizeof(ushort) + sizeof(byte) + sizeof(byte);
                for (var i = 0; i < count; i++)
                {
                    if (reader.Remaining < minimumColDataSize)
                        reader = await streamReader.ReadAtLeastAsync(minimumColDataSize, cancellationToken);

                    reader.TryReadLittleEndian(out uint userType);
                    reader.TryReadLittleEndian(out ushort flagsBytes);
                    var flags = (ColumnDataFlags)flagsBytes;
                    if (BackendMessage.DebugEnabled && !Enum.IsDefined(flags))
                        throw new ArgumentOutOfRangeException();

                    // TYPE_INFO
                    reader.TryRead(out var typeByte);
                    var typeCode = (DataTypeCode)typeByte;
                    if (BackendMessage.DebugEnabled && !Enum.IsDefined(typeCode))
                        throw new ArgumentOutOfRangeException();

                    reader.Commit();
                    DataType type;
                    switch (typeCode)
                    {
                        // Fixed-Length
                        case DataTypeCode.INT1TYPE:
                            type = new DataType(typeCode, nullable: false, DataTypeLengthKind.Fixed, length: 1);
                            break;
                        case DataTypeCode.BITTYPE:
                            type = new DataType(typeCode, nullable: false, DataTypeLengthKind.Fixed, length: 1);
                            break;
                        case DataTypeCode.INT2TYPE:
                            type = new DataType(typeCode, nullable: false, DataTypeLengthKind.Fixed, length: 2);
                            break;
                        case DataTypeCode.INT4TYPE:
                            type = new DataType(typeCode, nullable: false, DataTypeLengthKind.Fixed, length: 4);
                            break;
                        case DataTypeCode.FLT8TYPE:
                            type = new DataType(typeCode, nullable: false, DataTypeLengthKind.Fixed, length: 8);
                            break;
                        case DataTypeCode.INT8TYPE:
                            type = new DataType(typeCode, nullable: false, DataTypeLengthKind.Fixed, length: 8);
                            break;

                        // Variable-Length
                        // ByteLen
                        case DataTypeCode.GUIDTYPE:
                        {
                            if (!reader.TryRead(out var length))
                            {
                                reader = await streamReader.ReadAtLeastAsync(sizeof(byte), cancellationToken);
                                reader.TryRead(out length);
                            }

                            type = new DataType(typeCode, nullable: length == 0, DataTypeLengthKind.VariableByte, length);
                            break;
                        }
                        case DataTypeCode.INTNTYPE:
                        {
                            if (!reader.TryRead(out var length))
                            {
                                reader = await streamReader.ReadAtLeastAsync(sizeof(byte), cancellationToken);
                                reader.TryRead(out length);
                            }

                            type = new DataType(typeCode, nullable: length == 0, DataTypeLengthKind.VariableByte, length);
                            break;
                        }
                        case DataTypeCode.BITNTYPE:
                        {
                            if (!reader.TryRead(out var length))
                            {
                                reader = await streamReader.ReadAtLeastAsync(sizeof(byte), cancellationToken);
                                reader.TryRead(out length);
                            }

                            type = new DataType(typeCode, nullable: length == 0, DataTypeLengthKind.VariableByte, length);
                            break;
                        }
                        case DataTypeCode.DECIMALNTYPE:
                        {
                            if (reader.Remaining < sizeof(byte) * 3)
                                reader = await streamReader.ReadAtLeastAsync(sizeof(byte) * 3, cancellationToken);

                            reader.TryRead(out var length);
                            reader.TryRead(out var precision);
                            reader.TryRead(out var scale);
                            type = new DataType(typeCode, nullable: length == 0, DataTypeLengthKind.VariableByte, length, precision, scale);
                            break;
                        }
                        case DataTypeCode.NUMERICNTYPE:
                        case DataTypeCode.FLTNTYPE:
                        case DataTypeCode.MONEYNTYPE:
                        case DataTypeCode.DATETIMNTYPE:
                        case DataTypeCode.DATENTYPE:
                        case DataTypeCode.TIMENTYPE:
                        case DataTypeCode.DATETIME2NTYPE:
                        case DataTypeCode.DATETIMEOFFSETNTYPE:

                        // UShortLen
                        case DataTypeCode.BIGCHARTYPE:
                        case DataTypeCode.BIGVARBINARYTYPE:
                        case DataTypeCode.BIGBINARYTYPE:
                            throw new NotImplementedException();
                        case DataTypeCode.BIGVARCHARTYPE:
                        case DataTypeCode.NVARCHARTYPE:
                        {
                            const int nvarCharSize = (sizeof(ushort) * 3) + sizeof(byte);
                            if (reader.Remaining < nvarCharSize)
                                reader = await streamReader.ReadAtLeastAsync(nvarCharSize, cancellationToken);

                            reader.TryReadLittleEndian(out ushort length);
                            reader.TryReadLittleEndian(out ushort collationCodePage);
                            reader.TryReadLittleEndian(out ushort collationFlagsRaw);
                            var collationFlags = (CollationFlags)collationFlagsRaw;
                            if (BackendMessage.DebugEnabled && !Enum.IsDefined(collationFlags))
                                throw new ArgumentOutOfRangeException();
                            reader.TryRead(out var collationCharsetId);
                            type = new DataType(typeCode, nullable: length == 0, DataTypeLengthKind.VariableUShort, length);
                            break;
                        }
                        case DataTypeCode.NCHARTYPE:

                        // LongLen
                        case DataTypeCode.TEXTTYPE:
                        case DataTypeCode.IMAGETYPE:
                        case DataTypeCode.NTEXTTYPE:
                        case DataTypeCode.SSVARIANTTYPE:
                        case DataTypeCode.XMLTYPE:

                        // PartLen
                        // Also Includes XML/VarChar/VarBinary/NVarChar
                        case DataTypeCode.UDTTYPE:
                        default:
                            throw new ArgumentOutOfRangeException();

                        case DataTypeCode.FLT4TYPE:
                        case DataTypeCode.DATETIM4TYPE:
                        case DataTypeCode.MONEYTYPE:
                        case DataTypeCode.DATETIMETYPE:
                        case DataTypeCode.MONEY4TYPE:
                        case DataTypeCode.DECIMALTYPE:
                        case DataTypeCode.NUMERICTYPE:
                        case DataTypeCode.CHARTYPE:
                        case DataTypeCode.VARCHARTYPE:
                        case DataTypeCode.BINARYTYPE:
                        case DataTypeCode.VARBINARYTYPE:
                            throw new NotSupportedException();
                    }

                    if (!reader.TryReadBVarchar(out var columnName, out var totalByteLength))
                    {
                        reader = await streamReader.ReadAtLeastAsync(totalByteLength.Value, cancellationToken);
                        reader.TryReadBVarchar(out columnName, out _);
                    }

                    columns.Add(new ColumnData(userType, flags, type, columnName!));
                }

                result = new ColumnMetadataToken(columns);
                break;
            }

            case TokenType.ROW:
                result = new RowToken();
                break;

            case TokenType.RETURNSTATUS:
                reader.TryReadLittleEndian(out int returnValue);
                result = new ReturnStatusToken(returnValue);
                break;

            case TokenType.TVP_ROW:
            case TokenType.ALTMETADATA:
            case TokenType.RETURNVALUE:
            case TokenType.DATACLASSIFICATION:
            case TokenType.TABNAME:
            case TokenType.COLINFO:
            case TokenType.ORDER:

            case TokenType.FEATUREEXTACK:
            case TokenType.NBCROW:
            case TokenType.ALTROW:
            case TokenType.SESSIONSTATE:
            case TokenType.SSPI:
            case TokenType.FEDAUTHINFO:
            default:
                throw new ArgumentOutOfRangeException($"Unknown token type: {tokenType}");
        }

        reader.Commit();
        return result;
    }

    public ValueTask<T> ReadAndExpectAsync<T>(CancellationToken cancellationToken = default) where T : Token
    {
        var task = ReadAsync(cancellationToken);
        if (task.IsCompletedSuccessfully)
            return task.Result is T value ? new(value) : throw new ArgumentOutOfRangeException(nameof(T), task.Result, null);

        return Core(task);

        async ValueTask<T> Core(ValueTask<Token> task)
        {
            var value = await task;
            return value as T ?? throw new ArgumentOutOfRangeException(nameof(T), value, null);
        }
    }

    enum TokenType : byte
    {
        TVP_ROW = 0x01,
        RETURNSTATUS = 0x79,
        COLMETADATA = 0x81,
        ALTMETADATA = 0x88,
        DATACLASSIFICATION = 0xA3,
        TABNAME = 0xA4,
        COLINFO = 0xA5,
        ORDER = 0xA9,
        ERROR = 0xAA,
        INFO = 0xAB,
        RETURNVALUE = 0xAC,
        LOGINACK = 0xAD,
        FEATUREEXTACK = 0xAE,
        ROW = 0xD1,
        NBCROW = 0xD2,
        ALTROW = 0xD3,
        ENVCHANGE = 0xE3,
        SESSIONSTATE = 0xE4,
        SSPI = 0xED,
        FEDAUTHINFO = 0xEE,
        DONE = 0xFD,
        DONEPROC = 0xFE,
        DONEINPROC = 0xFF,
        // OFFSET - removed in 7.2
    }
}
