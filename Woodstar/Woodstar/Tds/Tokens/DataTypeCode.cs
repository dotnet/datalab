namespace Woodstar.Tds.Tokens;

class DataType
{
    public DataType(
        DataTypeCode code, bool nullable, DataTypeLengthKind lengthKind, int length, int precision = 0, int scale = 0)
    {
        Code = code;
        Nullable = nullable;
        LengthKind = lengthKind;

        Length = length;
        Precision = precision;
        Scale = scale;
    }

    public DataTypeCode Code { get; }
    public bool Nullable { get; }
    public DataTypeLengthKind LengthKind { get; }

    public int Length { get; }
    public int Precision { get; }
    public int Scale { get; }
}

enum DataTypeLengthKind
{
    Zero,
    Fixed,
    VariableByte,
    VariableUShort,
    VariableInt,
    PartiallyLengthPrefixed
}

enum DataTypeCode : byte
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
