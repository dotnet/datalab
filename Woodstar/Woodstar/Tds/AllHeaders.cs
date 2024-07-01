using System;
using System.Text;
using Woodstar.Buffers;

namespace Woodstar.Tds;

readonly struct QueryNotificationsHeader
{
    public QueryNotificationsHeader(string notifyId, string ssbDeployment, uint? notifyTimeout)
    {
        NotifyId = notifyId;
        SsbDeployment = ssbDeployment;
        NotifyTimeout = notifyTimeout;
    }
    public string NotifyId { get; }
    public string SsbDeployment { get; }
    public uint? NotifyTimeout { get; }
}

struct TraceActivityHeader
{
    public TraceActivityHeader(Guid activityId, uint activitySequence)
    {
        ActivityId = activityId;
        ActivitySequence = activitySequence;
    }

    public Guid ActivityId { get; }
    public uint ActivitySequence { get; }
}

readonly struct TransactionDescriptorHeader
{
    public TransactionDescriptorHeader(ulong transactionDescriptor, uint outstandingRequestCount)
    {
        TransactionDescriptor = transactionDescriptor;
        OutstandingRequestCount = outstandingRequestCount;
    }
    public ulong TransactionDescriptor { get; }
    public uint OutstandingRequestCount { get; }
}

readonly struct AllHeaders
{
    enum HeaderType: ushort
    {
        QueryNotifications = 1,
        TransactionDescriptor = 2,
        TraceActivity = 3
    }

    readonly uint _length;
    const int headerByteCount = 4 + 2;

    public AllHeaders(
        QueryNotificationsHeader? queryNotificationsHeader,
        TransactionDescriptorHeader? transactionDescriptorHeader,
        TraceActivityHeader? traceActivityHeader
    )
    {
        QueryNotificationsHeader = queryNotificationsHeader;
        TransactionDescriptorHeader = transactionDescriptorHeader;
        TraceActivityHeader = traceActivityHeader;
        _length = GetLength();
    }

    uint GetLength()
    {
        var length = 4;
        if (QueryNotificationsHeader is { } qheader)
        {
            length += headerByteCount;

            length += 2;
            length += Encoding.Unicode.GetByteCount(qheader.NotifyId);

            length += 2;
            length += Encoding.Unicode.GetByteCount(qheader.SsbDeployment);

            length += qheader.NotifyTimeout is null ? 0 : 4;
        }

        if (TransactionDescriptorHeader is { } theader)
        {
            length += headerByteCount + 8 + 4;
        }

        if (TraceActivityHeader is { } trheader)
        {
            length += headerByteCount + 16 + 4;
        }

        return (uint)length;
    }

    public QueryNotificationsHeader? QueryNotificationsHeader { get; }
    public TransactionDescriptorHeader? TransactionDescriptorHeader { get; }
    public TraceActivityHeader? TraceActivityHeader { get; }

    public void Write<TWriter>(StreamingWriter<TWriter> writer) where TWriter : IStreamingWriter<byte>
    {
        writer.WriteUIntLittleEndian(_length);
        if (QueryNotificationsHeader is not null)
        {
            throw new NotImplementedException();
        }

        if (TransactionDescriptorHeader is { } tHeader)
        {
            writer.WriteUIntLittleEndian(headerByteCount + 8 + 4);
            writer.WriteUShortLittleEndian((ushort)HeaderType.TransactionDescriptor);
            writer.WriteULongLittleEndian(tHeader.TransactionDescriptor);
            writer.WriteUIntLittleEndian(tHeader.OutstandingRequestCount);
        }

        if (TraceActivityHeader is { } traHeader)
        {
            throw new NotImplementedException();
        }
    }
}
