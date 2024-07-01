using System;

namespace Woodstar.Tds.Tokens;

[Flags]
enum DoneStatus : ushort
{
    Final = 0x00,
    More =  0x1,
    Error = 0x2,
    InTransaction = 0x4,
    Count = 0x10,
    Attention = 0x20,
    RpcInBatch = 0x80,
    ServerError = 0x100
}
