using System;
using WoodStar.Tds;

namespace WoodStar;

public class AllHeaders
{
    public AllHeaders(
        QueryNotificationsHeader? queryNotificationsHeader = null,
        TransactionDescriptorHeader? transactionDescriptorHeader = null,
        TraceActivityHeader? traceActivityHeader = null)
    {
        QueryNotificationsHeader = queryNotificationsHeader;
        TransactionDescriptorHeader = transactionDescriptorHeader;
        TraceActivityHeader = traceActivityHeader;
        Length = 4 + (QueryNotificationsHeader?.Length ?? 0) + (TransactionDescriptorHeader?.Length ?? 0)
            + (TraceActivityHeader?.Length ?? 0);
    }

    public uint Length { get; }
    public QueryNotificationsHeader? QueryNotificationsHeader { get; }
    public TransactionDescriptorHeader? TransactionDescriptorHeader { get; }
    public TraceActivityHeader? TraceActivityHeader { get; }

    public void Write(Memory<byte> buffer)
    {
        buffer.WriteUnsignedIntLittleEndian(Length);
        buffer = buffer[4..];
        if (QueryNotificationsHeader != null)
        {
            QueryNotificationsHeader.Write(buffer);
            buffer = buffer[(int)QueryNotificationsHeader.Length..];
        }

        if (TransactionDescriptorHeader != null)
        {
            TransactionDescriptorHeader.Write(buffer);
            buffer = buffer[(int)TransactionDescriptorHeader.Length..];
        }

        if (TraceActivityHeader != null)
        {
            TraceActivityHeader.Write(buffer);
            buffer = buffer[(int)TraceActivityHeader.Length..];
        }
    }
}
