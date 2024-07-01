using System;
using System.Collections.Generic;
using System.Data;
using Woodstar.Data;

namespace Woodstar;

// Base class for the two parameter types in Woodstar.
public abstract class WoodstarDbParameter : DbDataParameter, IParameterSession
{
    bool? _preferBinaryRepresentation;
    bool _inferredDbType;
    bool _valueDependent;
    short _valueRevision;

    protected WoodstarDbParameter()
    { }
    protected WoodstarDbParameter(string parameterName)
        : base(parameterName)
    { }

    public new WoodstarDbParameter Clone() => (WoodstarDbParameter)CloneCore();

    // TODO should this be DataRepresentation?
    /// Some converters support both a textual and binary representation for the postgres type this parameter maps to.
    /// When this property is set to true a textual representation should be preferred.
    /// When its set to false a non-textual (binary) representation is preferred.
    /// The default value is null which allows the converter to pick the most optimal representation.
    public bool? PreferTextualRepresentation
    {
        get => _preferBinaryRepresentation;
        set
        {
            ThrowIfInUse();
            _preferBinaryRepresentation = value;
        }
    }

    public WoodstarDbType WoodstarDbType
    {
        get => WoodstarDbTypeCore;
        set
        {
            ThrowIfInUse();
            _inferredDbType = false;
            WoodstarDbTypeCore = value;
        }
    }

    protected WoodstarDbType WoodstarDbTypeCore { get; set; }

    internal short ValueRevision => _valueRevision;

    internal Type? ValueType => ValueTypeCore;

    internal virtual bool ValueEquals(WoodstarDbParameter other) => Equals(ValueCore, other.ValueCore);

    internal void SetInferredDbType(WoodstarDbType woodstarDbType, bool valueDependent)
    {
        _inferredDbType = true;
        _valueDependent = valueDependent;
        WoodstarDbTypeCore = woodstarDbType;
    }

    internal WoodstarDbType? GetExplicitDbType()
        => !_inferredDbType && !WoodstarDbTypeCore.IsInfer ? WoodstarDbTypeCore : null;
    internal bool HasInferredDbType => _inferredDbType;

    Facets GetFacets(IFacetsTransformer? facetsTransformer = null)
    {
        if (Direction is ParameterDirection.Input)
            return new()
            {
                // We don't expect output so we leave IsNullable at default
                IsNullable = default,
                Size = SizeCore
            };

        return facetsTransformer is null ? GetUserSuppliedFacets() : GetFacetsCore(facetsTransformer);
    }

    private protected virtual Facets GetFacetsCore(IFacetsTransformer facetsTransformer)
    {
        if (ValueTypeCore is not null && ValueCore is not null)
            return facetsTransformer.Transform(ValueCore, ValueTypeCore, GetUserSuppliedFacets());

        return facetsTransformer.Transform(dbType: DbType, GetUserSuppliedFacets());
    }

    // Internal for now.
    private protected Facets GetUserSuppliedFacets() =>
        new()
        {
            IsNullable = IsNullable,
            Precision = PrecisionCore,
            Scale = ScaleCore,
            Size = SizeCore,
        };

    protected WoodstarDbParameter Clone(WoodstarDbParameter instance)
    {
        Clone((DbDataParameter)instance);
        instance.PreferTextualRepresentation = PreferTextualRepresentation;
        instance._inferredDbType = true;
        instance._valueDependent = _valueDependent;
        instance.WoodstarDbType = WoodstarDbType;
        return instance;
    }

    protected sealed override void ResetInference()
    {
        base.ResetInference();
        if (_inferredDbType)
        {
            _inferredDbType = false;
            WoodstarDbTypeCore = WoodstarDbType.Infer;
            _valueDependent = false;
        }
    }

    protected void ValueUpdated(Type? previousType)
    {
        _valueRevision++;
        if (_valueDependent || (previousType is not null && previousType != ValueTypeCore))
            ResetInference();
    }

    internal abstract IParameterSession StartSession(IFacetsTransformer facetsTransformer);
    protected abstract void EndSession();
    protected abstract void SetSessionValue(object? value);

    internal static WoodstarParameter Create() => new();
    internal static WoodstarParameter Create(object? value) => new() { Value = value };
    internal static WoodstarParameter Create(string parameterName, object? value) => new(parameterName, value);
    internal static WoodstarParameter<T> Create<T>(T? value) => new() { Value = value };
    internal static WoodstarParameter<T> Create<T>(string parameterName, T? value) => new(parameterName, value);

    ParameterKind IParameterSession.Kind => (ParameterKind)Direction;
    Facets IParameterSession.Facets => GetFacets();
    Type? IParameterSession.ValueType => ValueTypeCore;
    bool IParameterSession.IsBoxedValue => true;
    string IParameterSession.Name => ParameterName;

    void IParameterSession.ApplyReader<TReader>(ref TReader reader) => reader.Read(Value);

    object? IParameterSession.Value
    {
        get => ValueCore;
        set => SetSessionValue(value);
    }
    void IParameterSession.Close() => EndSession();
}

public sealed class WoodstarParameter: WoodstarDbParameter
{
    object? _value;

    public WoodstarParameter() {}
    public WoodstarParameter(string parameterName, object? value)
        :base(parameterName)
    {
        // Make sure it goes through value update.
        Value = value;
    }

    public new WoodstarParameter Clone() => (WoodstarParameter)CloneCore();

    internal override IParameterSession StartSession(IFacetsTransformer facetsTransformer)
    {
        // TODO facets transformer should be used at this point or removed.

        if (IncrementInUse() > 1 && Direction is not ParameterDirection.Input)
        {
            DecrementInUse();
            throw new InvalidOperationException("An output or return value direction parameter can't be used by commands executing in parallel.");
        }

        return this;
    }

    protected override void EndSession() => DecrementInUse();

    protected override void SetSessionValue(object? value)
    {
        if (Direction is ParameterDirection.Input)
            throw new InvalidOperationException("Cannot change value of an input parameter.");
        ValueCore = value;
    }

    protected override object? ValueCore
    {
        get => _value;
        set
        {
            var previousType = ValueTypeCore;
            _value = value;
            ValueUpdated(previousType);
        }
    }

    protected override DbType? DbTypeCore { get; set; }
    protected override DbDataParameter CloneCore() => Clone(new WoodstarParameter { ValueCore = ValueCore });
}

public sealed class WoodstarParameter<T> : WoodstarDbParameter, IDbDataParameter<T>, IParameterSession<T>
{
    static readonly EqualityComparer<T> EqualityComparer = EqualityComparer<T>.Default;
    static readonly bool ImplementsIEquatable = typeof(IEquatable<>).IsAssignableFrom(typeof(T));

    public WoodstarParameter() {}
    public WoodstarParameter(T? value)
        :base(string.Empty)
        => Value = value;
    public WoodstarParameter(string parameterName, T? value)
        :base(parameterName)
        => Value = value;

    T? _value;

    public new T? Value
    {
        get => _value;
        set
        {
            ThrowIfInUse();
            SetValue(value);
        }
    }

    void SetValue(T? value)
    {
        _value = value;
        // We explicitly ignore any derived type polymorphism for the generic WoodstarParameter.
        // So an IEnumerable<T> parameter will stay IEnumerable<T> even though it's now backed by an array.
        ValueUpdated(ValueTypeCore);
    }

    public new WoodstarParameter<T> Clone() => (WoodstarParameter<T>)CloneCore();

    internal override bool ValueEquals(WoodstarDbParameter other)
    {
        if (other is WoodstarParameter<T> otherT)
            return EqualityComparer.Equals(_value!, otherT._value!);
        // At this point we could still find a T if a generic WoodstarParameter is instantiated at a derived type of T *or* if it's boxed on WoodstarParameter.
        // For value types the former is impossible while in the latter case its value is already a reference anyway.
        // Accordingly we never cause any per invocation boxing by calling other.Value here.
        if (other.Value is T valueT)
            return EqualityComparer.Equals(_value!, valueT);
        // Given any type its default default EqualityComparer, when a type implements IEquatable<T> its object equality is never consulted.
        // We do this ourselves so we won't have to box our value (JIT optimizes struct receivers calling their object inherited methods).
        // The worse alternative would be calling EqualityComparer.Equals(object?, object?) which boxes both sides.
        if (!ImplementsIEquatable && _value is not null)
            return _value.Equals(other.Value);

        return false;
    }

    internal override IParameterSession StartSession(IFacetsTransformer facetsTransformer)
    {
        // TODO facets transformer should write back the updated info (also when direction isn't input).

        if (IncrementInUse() > 1 && Direction is not ParameterDirection.Input)
        {
            DecrementInUse();
            throw new InvalidOperationException("An output or return value direction parameter can't be used by commands executing in parallel.");
        }

        return this;
    }

    protected override void EndSession() => DecrementInUse();
    protected override void SetSessionValue(object? value) => ((IParameterSession<T>)this).Value = (T?)value;

    private protected override Facets GetFacetsCore(IFacetsTransformer facetsTransformer)
        => Value is not null ? facetsTransformer.Transform<T>(Value, GetUserSuppliedFacets()) : facetsTransformer.Transform(dbType: DbType, GetUserSuppliedFacets());

    protected override Type? ValueTypeCore => typeof(T);
    protected override object? ValueCore { get => Value; set => Value = (T?)value; }
    protected override DbType? DbTypeCore { get; set; }

    protected override DbDataParameter CloneCore() => Clone(new WoodstarParameter<T> { Value = Value });

    bool IParameterSession.IsBoxedValue => false;
    void IParameterSession.ApplyReader<TReader>(ref TReader reader) => reader.Read(Value);
    T? IParameterSession<T>.Value
    {
        get => _value;
        set
        {
            if (Direction is ParameterDirection.Input)
                throw new InvalidOperationException("Cannot change value of an input parameter.");

            SetValue(value);
        }
    }
}
