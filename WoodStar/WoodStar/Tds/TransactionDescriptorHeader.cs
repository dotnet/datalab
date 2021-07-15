using System;

namespace WoodStar.Tds;

public sealed class TransactionDescriptorHeader : Header
{
    public TransactionDescriptorHeader(ulong transactionDescriptor, uint outstandingRequestCount)
        : base(8 + 4, 2)
    {
        TransactionDescriptor = transactionDescriptor;
        OutstandingRequestCount = outstandingRequestCount;
    }

    public ulong TransactionDescriptor { get; }
    public uint OutstandingRequestCount { get; }
    public override void Write(Memory<byte> buffer)
    {
        buffer.WriteUnsignedIntLittleEndian(Length);
        buffer[4..].WriteUnsignedShortLittleEndian(HeaderType);
        buffer[6..].WriteUnsignedLongLittleEndian(TransactionDescriptor);
        buffer[14..].WriteUnsignedIntLittleEndian(OutstandingRequestCount);
    }
}
