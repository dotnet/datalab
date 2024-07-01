using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Woodstar.Buffers;
using Woodstar.SqlServer;

namespace Woodstar.Tds;

readonly struct Parameter
{
    public Parameter(object? value, SqlServerTypeId typeId, bool isDbNull, bool isValueDependent, object? writeState = null)
    {
        Value = value;
        IsDbNull = isDbNull;
        IsValueDependent = isValueDependent;
        WriteState = writeState;
    }

    public bool IsDbNull { get; init; }

    /// Value can be an instance of IParameterSession or a direct parameter value.
    public object? Value { get; init; }
    /// Size set to null represents a db null.
    public object? WriteState { get; init; }
    public bool IsValueDependent { get; }

    public bool TryGetParameterSession([NotNullWhen(true)]out IParameterSession? value)
    {
        if (Value is IParameterSession session)
        {
            value = session;
            return true;
        }

        value = null;
        return false;
    }
}

interface IParameterValueReader : IValueReader
{
    void ReadAsObject(object? value);
}

static class ParameterValueReaderExtensions
{
    public static void ReadParameterValue<TReader>(this ref TReader reader, object? value) where TReader : struct, IParameterValueReader
    {
        if (value is IParameterSession session)
        {
            if (session.IsBoxedValue)
                reader.ReadAsObject(session.Value); // Just avoid the GVM call.
            else
                session.ApplyReader(ref reader);
        }
        else
            reader.ReadAsObject(value);
    }
}

static class SqlServerConverterOptionsExtensions
{
    public static Parameter CreateParameter(object? parameterValue, SqlServerTypeId? typeId, bool nullStructValueIsDbNull = true)
    {
        var reader = new ValueReader(typeId, nullStructValueIsDbNull);
        reader.ReadParameterValue(parameterValue);
        var isDbNull = false;
        return new Parameter(parameterValue, reader.TypeId, isDbNull, isValueDependent: false, reader.WriteState);
    }

    struct ValueReader: IParameterValueReader
    {
        readonly SqlServerTypeId? _typeId;
        readonly bool _nullStructValueIsDbNull;
        object? _writeState;
        public SqlServerTypeId TypeId { get; private set; }
        public object? WriteState => _writeState;

        public ValueReader(SqlServerTypeId? typeId, bool nullStructValueIsDbNull)
        {
            _typeId = typeId;
            _nullStructValueIsDbNull = nullStructValueIsDbNull;
        }

        public void Read<T>(T? value)
        {
            throw new NotImplementedException();
        }

        public void ReadAsObject(object? value)
        {
            throw new NotImplementedException();
        }
    }
}

static class ParameterExtensions
{
    public static void Write<TWriter>(this Parameter parameter, StreamingWriter<TWriter> writer) where TWriter : IStreamingWriter<byte>
    {
        if (parameter.IsDbNull)
            return;

        var reader = new ValueWriter<TWriter>(async: false, writer, parameter.WriteState, CancellationToken.None);
        reader.ReadParameterValue(parameter.Value);

    }

    public static ValueTask WriteAsync<TWriter>(this Parameter parameter, StreamingWriter<TWriter> writer, CancellationToken cancellationToken) where TWriter : IStreamingWriter<byte>
    {
        if (parameter.IsDbNull)
            return new ValueTask();

        var reader = new ValueWriter<TWriter>(async: true, writer, parameter.WriteState, cancellationToken);
        reader.ReadParameterValue(parameter.Value);

        return reader.Result;
    }

    struct ValueWriter<TWriter> : IParameterValueReader where TWriter : IStreamingWriter<byte>
    {
        readonly bool _async;
        readonly StreamingWriter<TWriter> _writer;
        readonly object? _writeState;
        readonly CancellationToken _cancellationToken;

        public ValueWriter(bool async, StreamingWriter<TWriter> writer, object? writeState, CancellationToken cancellationToken)
        {
            _async = async;
            _writer = writer;
            _writeState = writeState;
            _cancellationToken = cancellationToken;
        }

        public ValueTask Result { get; private set; }

        public void Read<T>(T? value)
        {
            Debug.Assert(value is not null);
            if (_async)
            {
                try
                {
                    throw new NotImplementedException();
                }
                catch (Exception ex)
                {
                    Result = new ValueTask(Task.FromException(ex));
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public void ReadAsObject(object? value)
        {
            Debug.Assert(value is not null);
            if (_async)
            {
                try
                {
                    throw new NotImplementedException();
                }
                catch (Exception ex)
                {
                    Result = new ValueTask(Task.FromException(ex));
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }
}
