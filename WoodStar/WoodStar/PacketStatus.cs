namespace WoodStar
{
    public enum PacketStatus : byte
    {
        Normal = 0,
        EOM = 1,
        Ignore = 2,
        ResetConnection = 8,
        ResetConnectionSkipTransaction = 10
    }
}
