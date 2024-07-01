using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Woodstar.Data;

// TODO Maybe want to use a BitVector32 for _combinedEnums.
// TODO store whether DBNull.Value was set as there is a difference between null and DBNull.

// Size optimized base class.
public abstract partial class DbDataParameter
{
    // Either a parameter name (string) or a reference to additional (less commonly used) properties, see Props below.
    object _nameOrProps = "";

    // Combines 'Uses' (first 3 bytes), the last byte contains 'ParameterDirection' (least significant 3 bits), 'IsNullable' (bit 7) and 'IsFrozenName' (bit 8).
    volatile uint _combinedEnums;

    // Internal for now.
    private protected DbDataParameter() {}
    private protected DbDataParameter(string parameterName)
        :this()
        => _nameOrProps = parameterName ?? string.Empty; // Just to be sure, it's relied upon in the implementation.

    bool IsFrozenName
    {
        get => (_combinedEnums & 0x80) == 0x80; // get the most significant bit.
        set
        {
            uint current;
            uint newValue;
            do
            {
                current = _combinedEnums;
                newValue = (uint)(value ? 1 : 0) << 7 | (current & 0xffffff7f); // 0x7f == 255 - 128
#pragma warning disable CS0420
            } while (Interlocked.CompareExchange(ref Unsafe.As<uint,int>(ref _combinedEnums), (int)newValue, (int)current) != (int)current);
#pragma warning restore CS0420
        }
    }

    Props GetOrCreateProps()
    {
        var nameOrProps = _nameOrProps;
        if (nameOrProps is string name)
            nameOrProps = _nameOrProps = Props.Create(name);

        return (Props)nameOrProps;
    }

    protected abstract DbType? DbTypeCore { get; set; }
    protected abstract object? ValueCore { get; set; }

    protected virtual Type? ValueTypeCore => ValueCore?.GetType();

    protected int InUseCount => (int)_combinedEnums >> 8;
    protected bool IsInUse => InUseCount != 0;

    // private protected for testing.
    private protected int IncrementInUse(int count = 1)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot be negative.");

        uint current;
        uint newValue;
        do
        {
            // Operate on an unsigned int as we don't want the top bit to be interpreted as a sign bit, we have 24 bits.
            current = _combinedEnums;
            if ((current >> 8) + count > (int)Math.Pow(2, 24) - 1)
                throw new InvalidOperationException("Cannot increment past uint24.MaxValue.");

            var incremented = (uint)((current >> 8) + count) << 8;
            newValue = incremented | (current & 0x000000ff);
#pragma warning disable CS0420
        } while (Interlocked.CompareExchange(ref Unsafe.As<uint,int>(ref _combinedEnums), (int)newValue, (int)current) != (int)current);
#pragma warning restore CS0420
        return (int)(newValue >> 8);
    }

    /// <returns>The new value that was stored by this operation.</returns>
    protected int IncrementInUse() => IncrementInUse(count: 1);

    // private protected for testing.
    private protected int DecrementInUse(int count = 1)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot be negative.");

        uint current;
        uint newValue;
        do
        {
            // Operate on an unsigned int as we don't want the top bit to be interpreted as a sign bit, we have 24 bits.
            current = _combinedEnums;
            if ((current >> 8) - count < 0)
                throw new InvalidOperationException("Cannot decrement past 0.");

            var incremented = (uint)((current >> 8) - count) << 8;
            newValue = incremented | (current & 0x000000ff);
#pragma warning disable CS0420
        } while (Interlocked.CompareExchange(ref Unsafe.As<uint,int>(ref _combinedEnums), (int)newValue, (int)current) != (int)current);
#pragma warning restore CS0420
        return (int)(newValue >> 8);
    }

    /// <returns>The new value that was stored by this operation.</returns>
    protected int DecrementInUse() => DecrementInUse(count: 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void ThrowIfInUse()
    {
        if (IsInUse)
            ThrowInUse();

        static void ThrowInUse() => throw new InvalidOperationException("This parameter is currently in use for a command execution, clone the parameter to change its values or wait for execution to end."); 
    }

    protected virtual ParameterDirection DirectionCore
    {
        get => (ParameterDirection)(_combinedEnums & 0x07); // take the first 3 bits.
        set => _combinedEnums = (byte)value | (_combinedEnums & 0xfffffff8); // 0xf8 == 255 - 7
    }

    protected virtual bool IsNullableCore
    {
        get => (_combinedEnums & 0x40) == 0x40; // get the second most significant bit of the first byte.
        set => _combinedEnums = (uint)(value ? 1 : 0) << 6 | (_combinedEnums & 0xffffffbf); // 0xbf == 255 - 64
    }

    protected virtual byte? PrecisionCore
    {
        get
        {
            // If we have props and it's set, return it, otherwise try to infer from the type.
            if (_nameOrProps is Props { Precision: { } } p)
                return p.Precision;

            if (ValueTypeCore is not null && WellKnownClrFacets.TryGetPrecision(ValueTypeCore, out _, out var effectiveMax))
                return effectiveMax;

            return default;
        }
        set
        {
            if (!value.HasValue && _nameOrProps is not Props)
                return;

            GetOrCreateProps().Precision = value;
        }
    }

    protected virtual byte? ScaleCore
    {
        get
        {
            // If we have props and it's set, return it, otherwise try to infer from the type.
            if (_nameOrProps is Props { Scale: { } } p)
                return p.Scale;

            return default;
        }
        set
        {
            if (!value.HasValue && _nameOrProps is not Props)
                return;

            GetOrCreateProps().Scale = value;
        }
    }

    // See https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbparameter.size#remarks
    protected virtual int? SizeCore
    {
        get
        {
            // If we have props and it's set, return it, otherwise try to infer from the type.
            if (_nameOrProps is Props { Size: { } } p)
                return p.Size;

            // "For fixed length data types, the value of Size is ignored. It can be retrieved for informational purposes"
            if (ValueTypeCore is not null && WellKnownClrFacets.TryGetSize(ValueTypeCore, out var size))
                return size;

            return default;
        }
        set
        {
            if (value < -1)
                throw new ArgumentOutOfRangeException($"Invalid parameter value '{value}'. The value must be greater than or equal to 0.");

            if (!value.HasValue && _nameOrProps is not Props)
                return;

            // "For fixed length data types, the value of Size is ignored. It can be retrieved for informational purposes"
            if (ValueTypeCore is not null && WellKnownClrFacets.TryGetSize(ValueTypeCore, out _))
                return;

            GetOrCreateProps().Size = value;
        }
    }

    protected virtual void ResetInference()
    {
        DbTypeCore = null;
        if (_nameOrProps is Props p)
            p.ResetFacets();
    }

    protected abstract DbDataParameter CloneCore();
    protected DbDataParameter Clone(DbDataParameter instance)
    {
        if (_nameOrProps is Props p)
            instance._nameOrProps = p.Clone();
        else
            instance._nameOrProps = _nameOrProps;
        instance._combinedEnums = _combinedEnums;
        return instance;
    }

    protected internal void NotifyCollectionAdd() => IsFrozenName = true;

    sealed class Props
    {
        public string ParameterName { get; set; } = string.Empty;
        public byte? Precision { get; set; }
        public byte? Scale { get; set; }
        public int? Size { get; set; }
        public string SourceColumn { get; set; } = string.Empty;
        public bool SourceColumnNullMapping { get; set; }
        public DataRowVersion SourceVersion { get; set; }

        public Props Clone() => (Props)MemberwiseClone();

        public void ResetFacets()
        {
            Precision = default;
            Scale = default;
            Size = default;
        }

        public static Props Create(string parameterName)
            => new() { ParameterName = parameterName };
    }
}

// Public surface & ADO.NET
public abstract partial class DbDataParameter: DbParameter
{
    [AllowNull]
    public sealed override string ParameterName
    {
        get
        {
            if (_nameOrProps is string name)
                return name;

            return ((Props)_nameOrProps).ParameterName;
        }
        set
        {
            if (IsFrozenName)
                throw new InvalidOperationException("Parameter has been added to a collection at least once, clone the parameter to change the name.");

            ThrowIfInUse();
            value ??= string.Empty;
            if (_nameOrProps is Props p)
            {
                p.ParameterName = value;
                return;
            }
            _nameOrProps = value;
        }
    }
    public sealed override object? Value
    {
        get => ValueCore;
        set
        {
            ThrowIfInUse();
            ValueCore = value;
        }
    }
    public sealed override DbType DbType
    {
        get => DbTypeCore ?? DbType.String;
        set
        {
            ThrowIfInUse();
            if ((int)value is 24 or > 27 && !Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), $"Invalid {nameof(System.Data.DbType)} value.");
            DbTypeCore = value;
        }
    }
    public sealed override ParameterDirection Direction
    {
        get
        {
            var value = DirectionCore;
            return value == default ? ParameterDirection.Input : value;
        }
        set
        {
            ThrowIfInUse();
            switch (value)
            {
                case ParameterDirection.Input or ParameterDirection.Output or ParameterDirection.InputOutput or ParameterDirection.ReturnValue:
                case var _ when Enum.IsDefined(value):
                    DirectionCore = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(value), $"Invalid {nameof(ParameterDirection)} value.");
            }
        }
    }

    public sealed override bool IsNullable
    {
        get => IsNullableCore;
        set
        {
            ThrowIfInUse();
            IsNullableCore = value;
        }
    }

    public sealed override byte Precision
    {
        get => PrecisionCore.GetValueOrDefault();
        set
        {
            ThrowIfInUse();
            PrecisionCore = value;
        }
    }

    public sealed override byte Scale
    {
        get => ScaleCore.GetValueOrDefault();
        set
        {
            ThrowIfInUse();
            ScaleCore = value;
        }
    }

    public sealed override int Size
    {
        get => SizeCore.GetValueOrDefault();
        set
        {
            ThrowIfInUse();
            SizeCore = value;
        }
    }

    public sealed override void ResetDbType()
    {
        ThrowIfInUse();
        ResetInference();
    }

    [AllowNull]
    public override string SourceColumn
    {
        get
        {
            if (_nameOrProps is Props p)
                return p.SourceColumn;

            return string.Empty;
        }
        set
        {
            ThrowIfInUse();
            value ??= string.Empty;
            if (value is "")
                return;

            GetOrCreateProps().SourceColumn = value;
        }
    }
    public override bool SourceColumnNullMapping
    {
        get
        {
            if (_nameOrProps is Props p)
                return p.SourceColumnNullMapping;

            return false;
        }
        set
        {
            ThrowIfInUse();
            if (value == default)
                return;

            GetOrCreateProps().SourceColumnNullMapping = value;
        }
    }
    public override DataRowVersion SourceVersion
    {
        get
        {
            if (_nameOrProps is Props p)
                return p.SourceVersion;

            return default;
        }
        set
        {
            ThrowIfInUse();
            if (value == default)
                return;

            GetOrCreateProps().SourceVersion = value;
        }
    }

    public DbDataParameter Clone() => CloneCore();
}
