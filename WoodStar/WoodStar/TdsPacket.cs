namespace WoodStar
{
    public abstract class TdsPacket
    {
        protected TdsPacket(TdsHeader header)
        {
            Header = header;
        }

        public TdsHeader Header { get; set; }
    }
}
