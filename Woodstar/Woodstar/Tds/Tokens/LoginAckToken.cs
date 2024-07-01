using System;

namespace Woodstar.Tds.Tokens;

class LoginAckToken : Token
{
    public byte Interface { get; }
    public byte[] TdsVersion { get; }
    public string ProgramName { get; }
    public Version ProgramVersion { get; }

    public LoginAckToken(byte @interface, byte[] tdsVersion, string programName, Version programVersion)
    {
        Interface = @interface;
        TdsVersion = tdsVersion;
        ProgramName = programName;
        ProgramVersion = programVersion;
    }
}

