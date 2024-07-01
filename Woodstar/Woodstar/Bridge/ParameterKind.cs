namespace Woodstar;

// Shared exchange type to bridge ADO.NET and protocol layers.
// Mirrors the System.Data enum
enum ParameterKind: byte
{
    Input = 1,
    Output = 2,
    InputOutput = 3,
    ReturnValue = 6
}
