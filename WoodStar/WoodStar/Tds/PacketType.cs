namespace WoodStar.Tds;

public enum PacketType : byte
{
    SQLBatch = 1,
    PreTDS7Login = 2,
    RPC = 3,
    TabularResult = 4,
    AttentionSignal = 6,
    BulkLoadData = 7,
    FederatedAuthenticationToken = 8,
    TransactionManagerRequest = 14,
    TDS7Login = 16,
    SSPIMessage = 17,
    PreLogin = 18
}
