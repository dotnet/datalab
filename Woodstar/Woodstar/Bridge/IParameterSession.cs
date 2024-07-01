using System;

namespace Woodstar;

/// Support type for reading a value stored on an instance of IParameterSession({T}), allows values to stay unboxed if they are.
interface IValueReader
{
    void Read<T>(T? value);
}

// Shared exchange type to bridge ADO.NET and protocol layers.
interface IParameterSession
{
    ParameterKind Kind { get; }
    Facets Facets { get; }
    string Name { get; }
    bool IsBoxedValue { get; }
    /// The canonical type of the Value.
    /// This could be an assignable type for the value when IParameterSession is generic or the result of Value.GetType() otherwise.
    Type? ValueType { get; }
    /// Can be set to an output value, throws if its an input only parameter.
    object? Value { get; set; }

    /// Apply a reader to a value stored on an instance of IParameterSession({T}), allows values to stay unboxed if they are.
    void ApplyReader<TReader>(ref TReader reader) where TReader: IValueReader;

    /// Close the session once writes or reads from the session are finished, for instance once the protocol write is done.
    void Close();
}

interface IParameterSession<T> : IParameterSession
{
    new T? Value { get; set; }
}
