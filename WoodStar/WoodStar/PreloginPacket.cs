namespace WoodStar
{
    public class PreloginPacket : TdsPacket
    {
        public PreloginPacket(TdsHeader header, PreloginStream preloginStream)
            : base(header)
        {
            PreloginStream = preloginStream;
        }

        public PreloginStream PreloginStream { get; }
    }

    public class LoginAckPacket : TdsPacket
    {
        public LoginAckPacket(TdsHeader header, LoginAckStream loginAckStream)
            : base(header)
        {
            LoginAckStream = loginAckStream;
        }

        public LoginAckStream LoginAckStream { get; }
    }
}
