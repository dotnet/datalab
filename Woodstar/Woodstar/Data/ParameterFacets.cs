using System;

namespace Woodstar.Data;

// TODO use Type.GetTypeCode
// TODO add {U}Int128 DateOnly and TimeOnly.
// See https://blog.demofox.org/2017/11/21/floating-point-precision/ for an intro.
static class WellKnownClrFacets
{
    public static bool TryGetPrecision(Type type, out byte max, out byte effectiveMax)
    {
        (byte? Max, byte Effective) result = type switch
        {
            _ when type == typeof(DateTime) => (7, 7),
            _ when type == typeof(DateTimeOffset) => (7, 7),
            _ when type == typeof(decimal) => (29, 28),
            _ when type == typeof(float) => (7, 6),
            _ when type == typeof(double) => (16, 15),
#if !NETSTANDARD2_0
            _ when type == typeof(Half) => (4, 3),
#endif

            _ when type == typeof(int) || type == typeof(uint) => (10, 10),
            _ when type == typeof(long) => (19, 19),
            _ when type == typeof(ulong) => (20, 20),

            _ when type == typeof(short) || type == typeof(ushort) || type == typeof(char) => (5, 5),
            _ when type == typeof(byte) || type == typeof(sbyte) => (3, 3),

            _ => (null, default)
        };

        max = result.Max.GetValueOrDefault();
        effectiveMax = result.Effective;
        return result.Max.HasValue;
    }

    public static bool TryGetMaxScale(Type type, out byte max, out byte effectiveMax)
    {
        (byte? Max, byte Effective) result = type switch
        {
#if !NETSTANDARD2_0
            _ when type == typeof(Half) => (4, 3),
#endif
            _ when type == typeof(float) => (7, 6),
            _ when type == typeof(double) => (16, 15),

            // All remaining primitives can't have any numbers after the decimal point.
            // See https://learn.microsoft.com/en-us/dotnet/api/System.Type.IsPrimitive?view=netstandard-2.0 for the list.
            _ when type == typeof(bool) || type == typeof(char) => (null, default),
            _ when type.IsPrimitive => (0, 0),

            _ => (null, default)
        };

        max = result.Max.GetValueOrDefault();
        effectiveMax = result.Effective;
        return result.Max.HasValue;
    }

    public static bool GetNullability(Type type)
    {
        if (type.IsValueType)
            return !type.IsGenericTypeDefinition || (type.IsGenericTypeDefinition && type.GetGenericTypeDefinition() != typeof(Nullable<>));

        // We want to prevent null DBNull's (could happen for WoodstarParameter<T>).
        if (type == typeof(DBNull))
            return false;

        return true;
    }

    public static bool TryGetSize(Type type, out int value)
    {
        int? result = type switch
        {
            _ when type == typeof(decimal) => sizeof(decimal),
            _ when type == typeof(float) => sizeof(float),
            _ when type == typeof(double) => sizeof(double),
            _ when type == typeof(int) || type == typeof(uint) => sizeof(int),
            _ when type == typeof(long) || type == typeof(ulong) => sizeof(long),
            _ when type == typeof(char) => sizeof(char),
            _ when type == typeof(short) || type == typeof(ushort) => sizeof(short),
            _ when type == typeof(bool) => sizeof(bool),
            _ when type == typeof(byte) || type == typeof(sbyte) => sizeof(byte),
            _ => null
        };

        unsafe
        {
            if (!result.HasValue)
            {
                if (type == typeof(Guid))
                    result = sizeof(Guid);
#if !NETSTANDARD2_0
                else if (type == typeof(Half))
                    result = sizeof(Half);
#endif
            }
        }

        value = result.GetValueOrDefault();
        return result.HasValue;
    }

    public static byte GetDecimalScale(decimal value)
    {
#if !NETSTANDARD2_0
        Span<int> destination = stackalloc int[4];
        decimal.GetBits(value, destination);
#else
        Span<int> destination = decimal.GetBits(value).AsSpan();
#endif
        return (byte)((destination[3] & 0x00ff0000) >> 0x10);
    }
}
