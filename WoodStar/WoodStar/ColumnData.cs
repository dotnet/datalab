using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WoodStar;

public sealed class ColumnMetadata
{
    public ColumnMetadata(uint userType, ushort flags, TypeInfo typeInfo, string columnName)
    {
        UserType = userType;
        Flags = flags;
        TypeInfo = typeInfo;
        ColumnName = columnName;
    }

    public int Length => 4 + 2 + TypeInfo.Length + (2 * ColumnName.Length + 1);

    public uint UserType { get; }
    public ushort Flags { get; }
    public TypeInfo TypeInfo { get; }
    public string ColumnName { get; }

    public static ColumnMetadata Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out int userType)// FIX
            && reader.TryReadLittleEndian(out ushort flags)
            && reader.TryRead(out var type))
        {
            TypeInfo typeInfo;
            var dataType = (DataType)Enum.ToObject(typeof(DataType), type);
            switch (dataType)
            {
                case DataType.INT4TYPE:
                    typeInfo = new IntegerTypeInfo();
                    break;

                case DataType.NVARCHARTYPE:
                    typeInfo = NVarCharTypeInfo.Parse(ref reader);
                    break;

                default:
                    throw new NotImplementedException();
            }

            if (reader.ReadBVarchar(out var columnName))
            {
                return new ColumnMetadata((uint)userType, flags, typeInfo, columnName);
            }
        }

        throw new NotImplementedException();
    }
}

public sealed class ColMetadataToken : IToken
{
    private readonly List<ColumnMetadata> _columns;
    public TokenType TokenType => TokenType.COLMETADATA;

    public ColMetadataToken(ushort count)
    {
        Count = count;
        _columns = new(count);
    }

    public int TokenLength => 1 + 2 + _columns.Sum(e => e.Length);

    public ushort Count { get; }

    public IReadOnlyList<ColumnMetadata> Columns => _columns;

    public static ColMetadataToken Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort count))
        {
            var result = new ColMetadataToken(count);
            for (var i = 0; i < count; i++)
            {
                result._columns.Add(ColumnMetadata.Parse(ref reader));
            }

            return result;
        }

        throw new NotImplementedException();
    }
}

public enum DataType : byte
{
    // Fixed-Length
    INT1TYPE = 0x30,            // TinyInt
    BITTYPE = 0x32,             // Bit
    INT2TYPE = 0x34,            // SmallInt
    INT4TYPE = 0x38,            // Int
    DATETIM4TYPE = 0x3A,        // SmallDateTime
    FLT4TYPE = 0x3B,            // Real
    MONEYTYPE = 0x3C,           // Money
    DATETIMETYPE = 0x3D,        // DateTime
    FLT8TYPE = 0x3E,            // Float
    MONEY4TYPE = 0x7A,          // SmallMoney
    INT8TYPE = 0x7F,            // BigInt
    DECIMALTYPE = 0x37,         // Decimal
    NUMERICTYPE = 0x3F,         // Numeric

    // Variable-Length
    // ByteLen
    GUIDTYPE = 0x24,            // UniqueIdentifier
    INTNTYPE = 0x26,            // Integer
    BITNTYPE = 0x68,            // Bit
    DECIMALNTYPE = 0x6A,        // Decimal
    NUMERICNTYPE = 0x6C,        // Numeric
    FLTNTYPE = 0x6D,            // Float
    MONEYNTYPE = 0x6E,          // Money
    DATETIMNTYPE = 0x6F,        // DateTime
    DATENTYPE = 0x28,           // Date
    TIMENTYPE = 0x29,           // Time
    DATETIME2NTYPE = 0x2A,      // DataTime2
    DATETIMEOFFSETNTYPE = 0x2B, // DateTimeOffset
    CHARTYPE = 0x2F,            // Char
    VARCHARTYPE = 0x27,         // VarChar
    BINARYTYPE = 0x2D,          // Binary
    VARBINARYTYPE = 0x25,       // VarBinary

    // UShortLen
    BIGVARBINARYTYPE = 0xA5,    // VarBinary
    BIGVARCHARTYPE = 0xA7,      // VarChar
    BIGBINARYTYPE = 0xAD,       // Binary
    BIGCHARTYPE = 0xAF,         // Char
    NVARCHARTYPE = 0xE7,        // NVarChar
    NCHARTYPE = 0xEF,           // NChar

    // LongLen
    TEXTTYPE = 0x23,            // Text
    IMAGETYPE = 0x22,           // Image
    NTEXTTYPE = 0x63,           // NText
    SSVARIANTTYPE = 0x62,       // sql_variant
    XMLTYPE = 0xF1,             // XML

    // PartLen
    // Also Includes XML/VarChar/VarBinary/NVarChar
    UDTTYPE = 0xF0,             // CLR UDT
}

public class IntegerTypeInfo : FixedLengthTypeInfo<int>
{
    public IntegerTypeInfo()
        : base(DataType.INT4TYPE)
    {
    }

    public override int Length => 1;

    public override object ReadValue(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out int value))
        {
            return value;
        }

        throw new ParsingException();
    }
}

public abstract class TypeInfo
{
    protected TypeInfo(DataType dataType, Type type, bool nullable)
    {
        DataType = dataType;
        Type = type;
        IsNullable = nullable;
    }

    public Type Type { get; }

    public DataType DataType { get; }

    public bool IsNullable { get; }

    public abstract int Length { get; }

    public abstract object ReadValue(ref SequenceReader<byte> reader);
}

public abstract class TypeInfo<T> : TypeInfo
{
    protected TypeInfo(DataType dataType, bool nullable)
        : base(dataType, typeof(T), nullable)
    {
    }
}

public abstract class FixedLengthTypeInfo<T> : TypeInfo<T>
{
    protected FixedLengthTypeInfo(DataType dataType)
        : base(dataType, nullable: false)
    {
        Size = GetSize(dataType);
    }

    public int Size { get; }

    private static int GetSize(DataType dataType)
        => dataType switch
        {
            DataType.INT4TYPE => 4,
            _ => throw new InvalidOperationException()
        };
}

public abstract class VariableLengthTypeInfo<T> : TypeInfo<T>
{
    protected VariableLengthTypeInfo(DataType dataType)
        : base(dataType, nullable: true)
    {
    }
}

public abstract class VariableLengthStringTypeInfo<TSize> : VariableLengthTypeInfo<string>
{
    private static readonly List<DataType> _validTypes = new()
    {
        DataType.BIGCHARTYPE,
        DataType.BIGVARCHARTYPE,
        DataType.TEXTTYPE,
        DataType.NTEXTTYPE,
        DataType.NCHARTYPE,
        DataType.NVARCHARTYPE
    };

    protected VariableLengthStringTypeInfo(DataType dataType, TSize maxLength, byte[] collation)
        : base(dataType)
    {
        if (!_validTypes.Contains(dataType))
        {
            throw new InvalidOperationException();
        }
        MaxLength = maxLength;
        Collation = collation;
    }

    public TSize MaxLength { get; }
    public byte[] Collation { get; }
}

public class NVarCharTypeInfo : VariableLengthStringTypeInfo<ushort>
{
    public NVarCharTypeInfo(ushort maxLength, byte[] collation)
        : base(DataType.NVARCHARTYPE, maxLength, collation)
    {
    }

    public override int Length => 8;

    public static NVarCharTypeInfo Parse(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort maxLength)
            && reader.TryReadByteArray(size: 5, out var collation))
        {
            return new NVarCharTypeInfo(maxLength, collation);
        }

        throw new ParsingException();
    }

    public override object ReadValue(ref SequenceReader<byte> reader)
    {
        if (reader.TryReadLittleEndian(out ushort length))
        {
            var sequence = reader.UnreadSequence.Slice(0, length);
            var value = Encoding.Unicode.GetString(sequence);
            reader.Advance(length);

            return value;
        }

        throw new ParsingException();
    }
}
