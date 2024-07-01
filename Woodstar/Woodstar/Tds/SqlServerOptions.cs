using System.Net;
using System.Text;

namespace Woodstar.Tds.SqlServer;

record SqlServerOptions
{
    public required EndPoint EndPoint { get; init; }
    public required string? Username { get; init; }
    public string? Password { get; init; }
    public string? Database { get; init; }
    // Hardcoded to UTF8 until a use for another encoding comes up.
    internal Encoding Encoding => DefaultEncoding;

    internal static Encoding DefaultEncoding => Encoding.UTF8;
    internal static Encoding PasswordEncoding => Encoding.UTF8;
}
