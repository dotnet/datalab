using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Woodstar.Buffers;
using Woodstar.Tds.Packets;

namespace Woodstar.Tds.Messages;

/// <summary>
/// Types of the tokens in TDS prelogin packet
/// </summary>
enum PreLoginOptionToken : byte
{
    Version = 0x00,
    Encryption = 0x01,
    Instance = 0x02,
    ThreadID = 0x03,
    Mars = 0x04,
    TraceID = 0x05,
    FederatedAuthenticationRequired = 0x06,
    NonceOption = 0x07
}

class PreloginMessage : IFrontendMessage
{
    readonly uint _version;

    readonly struct Option
    {
        public Option(PreLoginOptionToken token, ushort offset, ushort length)
        {
            Token = token;
            Offset = offset;
            Length = (ushort)(length - offset);
        }

        public PreLoginOptionToken Token { get; init; }
        public ushort Offset { get; init; }
        public ushort Length { get; init; }

        public const int ByteCount = sizeof(PreLoginOptionToken) + sizeof(ushort) + sizeof(ushort);
    }

    public PreloginMessage()
    {
        _version = 1;
        var offset = (ushort)0;
        _options.Add(new(PreLoginOptionToken.Version, offset, offset += sizeof(uint) + sizeof(ushort)));
        _options.Add(new(PreLoginOptionToken.Encryption, offset, offset += sizeof(byte)));
        _options.Add(new(PreLoginOptionToken.Instance, offset, offset += sizeof(byte)));
        _options.Add(new(PreLoginOptionToken.ThreadID, offset, offset += sizeof(uint)));
        _options.Add(new(PreLoginOptionToken.Mars, offset, offset += sizeof(byte)));
    }

    public static TdsPacketHeader MessageType => TdsPacketHeader.CreateType(TdsPacketType.PreLogin, MessageStatus.Normal);

    readonly List<Option> _options = new();

    public bool CanWriteSynchronously => true;
    public void Write<TWriter>(ref BufferWriter<TWriter> writer) where TWriter : IBufferWriter<byte>
    {
        var offsetStart = _options.Count * Option.ByteCount + 1;
        foreach (var option in _options)
        {
            writer.WriteByte((byte)option.Token);
            writer.WriteUShort((ushort)(offsetStart + option.Offset));
            writer.WriteUShort(option.Length);
        }

        writer.WriteByte(0xFF); // terminator

        foreach (var option in _options)
        {
            switch (option.Token)
            {
                case PreLoginOptionToken.Version:
                    writer.WriteUInt(_version);
                    writer.WriteUShort(0);
                    break;
                case PreLoginOptionToken.Encryption:
                    writer.WriteByte(0x02); // Encryption
                    break;
                case PreLoginOptionToken.Instance:
                    writer.WriteCString("", Encoding.ASCII); // InstValidity
                    break;
                case PreLoginOptionToken.ThreadID:
                    writer.WriteUInt((uint)Environment.CurrentManagedThreadId); // Thread id
                    break;
                case PreLoginOptionToken.Mars:
                    writer.WriteByte(0); // Mars
                    break;
                case PreLoginOptionToken.TraceID:
                case PreLoginOptionToken.FederatedAuthenticationRequired:
                case PreLoginOptionToken.NonceOption:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
