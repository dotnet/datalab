using System;
using System.Data;

namespace Woodstar;

public static class WoodstarDbTypes
{
}

// A potentially invalid or unknown type identifier, used in frontend operations like configuring DbParameter types.
// The DbDataSource this is passed to decides on the validity of the contents.
public readonly record struct WoodstarDbType
{
    readonly string? _dataTypeName;

    internal WoodstarDbType(string dataTypeName)
    {
        _dataTypeName = dataTypeName;
    }

    internal bool IsInfer => _dataTypeName is null;
    internal string DataTypeName => _dataTypeName ?? throw new InvalidOperationException("DbType does not carry a name.");

    public override string ToString() => IsInfer
        ? @"Case = ""Inference"""
        : $@"Case = ""DataTypeName"", Value = ""{DataTypeName}""";

    /// Infer a database type from the parameter value instead of specifying one.
    public static WoodstarDbType Infer => default;
    public static WoodstarDbType Create(string dataTypeName) => new(dataTypeName.Trim());

    // public DbType? ToDbType() => WoodstarDbTypes.ToDbType(this);
    public static explicit operator WoodstarDbType(DbType dbType) => throw new NotImplementedException();
}
