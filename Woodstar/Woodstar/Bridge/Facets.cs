namespace Woodstar;

// Shared exchange type to bridge ADO.NET and protocol layers.
readonly record struct Facets
{
    // Is DBNull allowed.
    public bool IsNullable { get; init; }

    // Maximum length of the value.
    public int? Size { get; init; }

    // Precision of the value.
    public byte? Precision { get; init; }

    // Scale of the value.
    public byte? Scale { get; init; }
}
