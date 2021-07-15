using System;

namespace WoodStar.Tds;

public abstract class Header
{
    protected Header(uint dataLength, ushort headerType)
    {
        Length = 4 + 2 + dataLength;
        HeaderType = headerType;
    }

    public uint Length { get; }
    public ushort HeaderType { get; }
    public abstract void Write(Memory<byte> buffer);
}
