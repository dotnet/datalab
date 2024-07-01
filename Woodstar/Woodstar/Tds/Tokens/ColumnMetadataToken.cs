using System;
using System.Collections.Generic;

namespace Woodstar.Tds.Tokens;

class ColumnMetadataToken : Token
{
    public List<ColumnData> ColumnData { get; }

    public ColumnMetadataToken(List<ColumnData> columnData)
    {
        ColumnData = columnData;
    }
}

struct ColumnData
{
    public uint UserType { get; }
    public ColumnDataFlags Flags { get; }
    public DataType Type { get; }
    public string Name { get; }

    public ColumnData(uint userType, ColumnDataFlags flags, DataType type, string name)
    {
        UserType = userType;
        Flags = flags;
        Type = type;
        Name = name;
    }
}

[Flags]
enum ColumnDataFlags : ushort
{
    Nullable = 1,
    CaseSensitive = 2,
    Updatable = 4,
    Identity = 16,
    Computed = 32,
    FixedLengthClrType = 128,
    SparseColumnSet = 512,
    Encrypted = 1024,
    Hidden = 4096,
    Key = 8192,
    NullableUnknown = 16384
}
