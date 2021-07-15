using System;

namespace WoodStar;

public sealed class ParsingException : Exception
{
    public ParsingException()
        : base("Invalid data encountered while parsing")
    {
    }
}
