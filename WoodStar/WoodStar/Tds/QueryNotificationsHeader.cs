using System;

namespace WoodStar.Tds;

public sealed class QueryNotificationsHeader : Header
{
    public QueryNotificationsHeader(string notifyId, string ssbDeployment, uint? notifyTimeout)
        : base((uint)(2 + 2 * notifyId.Length + 2 + 2 * ssbDeployment.Length + notifyTimeout != null ? 4 : 0), 1)
    {
        NotifyId = notifyId;
        SsbDeployment = ssbDeployment;
        NotifyTimeout = notifyTimeout;
    }

    public string NotifyId { get; }
    public string SsbDeployment { get; }
    public uint? NotifyTimeout { get; }
    public override void Write(Memory<byte> buffer)
    {
        throw new NotImplementedException();
    }
}
