using System;

namespace WoodStar.Tds;

public sealed class TraceActivityHeader : Header
{
    public TraceActivityHeader(Guid activityId, uint activitySequence)
        : base(16 + 4, 3)
    {
        ActivityId = activityId;
        ActivitySequence = activitySequence;
    }

    public Guid ActivityId { get; }
    public uint ActivitySequence { get; }
    public override void Write(Memory<byte> buffer)
    {
        throw new NotImplementedException();
    }
}
