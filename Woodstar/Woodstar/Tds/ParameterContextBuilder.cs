using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Woodstar.SqlServer;

namespace Woodstar.Tds;

struct ParameterContextBuilder
{
    readonly int _length;
    Parameter[]? Parameters { get; set; }
    int _index;
    ParameterContextFlags _flags;

    public ParameterContextBuilder(int length, int revision)
    {
        _length = length;
        _flags = ParameterContextFlags.None;
        NullStructValueIsDbNull = true;
        Revision = revision;
    }

    public int Length => _length;
    public int Count => _index;

    /// This property controls what should happen when a null value is passed for a non nullable struct type.
    /// This can happen if the boxed value is accompanied by a type id that resolves to a data type for a
    /// struct type (e.g. 'int').
    /// Parameter construction can be lenient and coerce these values to a db null when this property is true.
    /// When the property is false an exception is thrown during parameter construction.
    /// Default is true.
    public bool NullStructValueIsDbNull { get; set; }

    [MemberNotNull(nameof(Parameters))]
    void EnsureCapacity()
    {
        // Under failure we just let any rented builder arrays be collected by the GC.
        Parameters ??= ArrayPool<Parameter>.Shared.Rent(_length);
        if (_length <= _index)
            throw new IndexOutOfRangeException("Cannot add more parameters, builder was initialized with a length that has been reached.");
    }

    public ReadOnlySpan<Parameter> Items => new(Parameters, 0, _index);
    public int Revision { get; }

    public Parameter AddParameter(Parameter parameter)
    {
        EnsureCapacity();
        return Parameters[_index++] = parameter;
    }

    public Parameter AddParameter(IParameterSession value, SqlServerTypeId? typeId = null)
        => AddParameterCore(value, typeId);

    public Parameter AddParameter(object? value, SqlServerTypeId? typeId = null)
        => AddParameterCore(value, typeId);

    Parameter AddParameterCore(object? value, SqlServerTypeId? typeId)
    {
        EnsureCapacity();
        var parameterKind = ParameterKind.Input;
        var isSession = false;
        Parameter parameter;
        // if (value is IParameterSession session)
        // {
        //     parameter = ConverterOptions.CreateParameter(value, typeId, NullStructValueIsDbNull);
        //     parameterKind = session.Kind;
        //     isSession = true;
        // }
        // else
        // {
        //     parameter = ConverterOptions.CreateParameter(value, typeId, NullStructValueIsDbNull);
        // }
        //
        // if (parameter.Size is { Value: null })
        //     _flags |= ParameterContextFlags.AnyUnknownByteCount;
        //
        // if (isSession)
        //     _flags |= parameterKind is not ParameterKind.Input ? ParameterContextFlags.AnySessions : ParameterContextFlags.AnySessions | ParameterContextFlags.AnyWritableParamSessions;
        //
        // return Parameters[_index++] = parameter;

        throw new NotImplementedException();
    }

    public ParameterContext Build()
    {
        var parameters = Parameters!;
        var length = _index;
        Parameters = null;
        _index = 0;
        return new ParameterContext
        {
            Parameters = new(parameters, length, pooledArray: true),
            Flags = _flags,
        };
    }

}
