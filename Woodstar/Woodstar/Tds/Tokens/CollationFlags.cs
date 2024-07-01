using System;

namespace Woodstar.Tds.Tokens;

[Flags]
enum CollationFlags : ushort
{
    IgnoreCase = 1,
    IgnoreAccent = 2,
    IgnoreKana = 4,
    IgnoreWidth = 8,
    Binary = 16,
    Binary2 = 32,
    UTF8 = 64
}
