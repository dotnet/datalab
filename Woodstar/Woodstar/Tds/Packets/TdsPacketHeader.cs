using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Woodstar.Tds.Packets;

static class TdsPackets
{
    public static readonly bool DebugEnabled = false;
}

enum TdsPacketType : byte
{
    SqlBatch = 1,
    Rpc = 3,
    TabularResult = 4,
    AttentionSignal = 6,
    BulkLoadData = 7,
    FederatedAuthenticationToken = 8,
    TransactionManagerRequest = 14,
    Tds7Login = 16,
    Sspi = 17,
    PreLogin = 18
}

[Flags]
enum MessageStatus : byte
{
    /// "Normal" message.
    Normal = 0,
    /// End of message (EOM). The packet is the last packet in the whole request.
    EndOfMessage = 1,
    /// (From client to server) Ignore this event (0x01 MUST also be set)
    IgnoreEvent = 2,
    /// (From client to server) Reset this connection before processing event. Only set for event types Batch,
    /// RPC, or Transaction Manager request. If clients want to set this bit, it MUST be part of the first packet of
    /// the message. This signals the server to clean up the environment state of the connection back to the
    /// default environment setting, effectively simulating a logout and a subsequent login, and provides server
    /// support for connection pooling. This bit SHOULD be ignored if it is set in a packet that is not the first
    /// packet of the message.
    /// This messageStatus bit MUST NOT be set in conjunction with the RESETCONNECTIONSKIPTRAN bit. Distributed
    /// transactions and isolation levels will not be reset.
    ResetConnection = 8,
    /// (From client to server) Reset the connection before processing event but do not modify the transaction
    /// state (the state will remain the same before and after the reset). The transaction in the session can be a
    /// local transaction that is started from the session or it can be a distributed transaction in which the
    /// session is enlisted. This messageStatus bit MUST NOT be set in conjunction with the RESETCONNECTION bit.
    /// Otherwise identical to RESETCONNECTION.
    ResetConnectionSkipTransaction = 16,
}

readonly record struct TdsPacketHeader
{
    public const byte ByteCount =
        sizeof(TdsPacketType) + sizeof(MessageStatus) +
        sizeof(ushort) + sizeof(ushort) +
        sizeof(byte) + sizeof(byte);

    readonly TdsPacketType _type;
    readonly MessageStatus _status;
    readonly short _packetSize; // As max is 2^15.
    readonly ushort _spId; // optional two byte big endian.

    // Ignored
    readonly byte _packetId;
    // readonly byte _window;

    TdsPacketHeader(TdsPacketType type, MessageStatus status, short packetSize, byte packetId, ushort spId = default)
    {
        if (TdsPackets.DebugEnabled)
        {
            if (status.HasFlag(MessageStatus.ResetConnection) && status.HasFlag(MessageStatus.ResetConnectionSkipTransaction))
                throw new ArgumentException($"Cannot specify {nameof(MessageStatus.ResetConnection)} and {nameof(MessageStatus.ResetConnectionSkipTransaction)} together.");

            if (status.HasFlag(MessageStatus.IgnoreEvent) && !status.HasFlag(MessageStatus.EndOfMessage))
                throw new ArgumentException($"Cannot specify {nameof(MessageStatus.IgnoreEvent)} without specifying {nameof(MessageStatus.EndOfMessage)} as well.");
        }

        _type = type;
        _status = status;
        _packetSize = packetSize;
        _packetId = packetId;
        _spId = spId;
    }

    public TdsPacketType Type => _type;
    public MessageStatus Status => _status;
    public short PacketSize => _packetSize;
    public byte PacketId => _packetId;
    public void Write(Span<byte> buffer)
    {
        var header = buffer.Slice(0, ByteCount);
        header.Fill(0);
        Debug.Assert(buffer.Length >= ByteCount);
        buffer[0] = (byte)_type;
        buffer[1] = (byte)_status;
        BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(2), _packetSize);
        buffer[6] = _packetId;
    }

    public bool TypeEquals(TdsPacketHeader other) => other._type == _type;

    public static bool TryParse(ReadOnlySpan<byte> span, out TdsPacketHeader header)
    {
        if (span.Length < ByteCount)
        {
            header = default;
            return false;
        }

        ref var head = ref MemoryMarshal.GetReference(span);
        var type = (TdsPacketType)head;

        if (TdsPackets.DebugEnabled && !Enum.IsDefined(type))
            ThrowNotDefined(type);

        var status = (MessageStatus)Unsafe.Add(ref head, 1);
        if (TdsPackets.DebugEnabled && !Enum.IsDefined(type))
            ThrowNotDefinedStatus(status);

        var length = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref head, 2));
        if (BitConverter.IsLittleEndian)
            length = BinaryPrimitives.ReverseEndianness(length);

        var spId = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref head, 4));
        if (BitConverter.IsLittleEndian)
            spId = BinaryPrimitives.ReverseEndianness(spId);

        var id = Unsafe.Add(ref head, 6);

        header = new TdsPacketHeader(type, status, length, id, spId: spId);
        Debug.Assert(header.PacketSize >= ByteCount);
        return true;

        static void ThrowNotDefined(TdsPacketType type) => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown packet type");
        static void ThrowNotDefinedStatus(MessageStatus status) => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown packet status");
    }

    public static TdsPacketHeader CreateType(TdsPacketType type, MessageStatus status)
        => new(type, status, 0, 0);
    public static TdsPacketHeader Create(TdsPacketType type, MessageStatus status, short length, byte id)
        => new(type, status, length, id);
}
