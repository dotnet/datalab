using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Woodstar.Buffers;
using Woodstar.Tds.Packets;
using Woodstar.Tds.Tokens;

namespace Woodstar.Tds.Messages;

readonly struct RpcRequestMessage: IFrontendMessage
{
    readonly AllHeaders _allHeaders;
    readonly SpecialProcId? _specialProcId;
    readonly string _commandText;

    // TODO: Parameters
    public RpcRequestMessage(AllHeaders allHeaders, SpecialProcId specialProcId, string commandText)
    {
        _allHeaders = allHeaders;
        _specialProcId = specialProcId;
        _commandText = commandText;
    }

    public static TdsPacketHeader MessageType => TdsPacketHeader.CreateType(TdsPacketType.Rpc, MessageStatus.ResetConnection);

    public void Write<TWriter>(StreamingWriter<TWriter> writer) where TWriter : IStreamingWriter<byte>
    {
        _allHeaders.Write(writer);
        writer.WriteString(_commandText, Encoding.Unicode);
    }

    public bool CanWriteSynchronously => false;
    public async ValueTask WriteAsync<T>(StreamingWriter<T> writer, CancellationToken cancellationToken = default) where T : IStreamingWriter<byte>
    {
        _allHeaders.Write(writer);

        if (_specialProcId is null)
        {
            throw new NotImplementedException("RPC request without a special stored procedure");
        }
        else
        {
            writer.WriteUShortLittleEndian(0xFFFF); // NameLenProcId, 0xFFF to indicate "special" PROCID
            writer.WriteUShortLittleEndian((ushort)_specialProcId.Value);
        }

        // TODO: Accept status flags
        writer.WriteUShortLittleEndian((ushort)RpcMessageOptionFlags.NoMetaData);

        // Parameters, hard-coded for now

        // Parameter1, hardcoded to write the command SQL as string for now
        var commandByteLength = (ushort)Encoding.Unicode.GetByteCount(_commandText);
        writer.WriteByte(0); // Parameter 1 length
        writer.WriteByte((byte)default(ParameterStatusFlags));

        // Parameter1 TYPE_INFO
        writer.WriteByte((byte)DataTypeCode.NVARCHARTYPE);
        writer.WriteUShortLittleEndian(commandByteLength);
        writer.WriteUShortLittleEndian(1033);
        writer.WriteUShortLittleEndian((ushort)default(CollationFlags));
        writer.WriteByte(52);

        // Parameter1 value
        writer.WriteUShortLittleEndian(commandByteLength);
        writer.WriteString(_commandText, Encoding.Unicode, commandByteLength);

        // Parameter2, hardcoded to no parameters for now
        var parametersString = "";
        var parametersStringByteLength = (ushort)Encoding.Unicode.GetByteCount(parametersString);
        writer.WriteByte(0); // Parameter 1 length
        writer.WriteByte((byte)default(ParameterStatusFlags));

        // Parameter2 TYPE_INFO, hardcoded to no parameters for now
        writer.WriteByte((byte)DataTypeCode.NVARCHARTYPE);
        writer.WriteUShortLittleEndian(10);
        writer.WriteUShortLittleEndian(1033);
        writer.WriteUShortLittleEndian((ushort)default(CollationFlags));
        writer.WriteByte(52);

        // Parameter2 value
        writer.WriteUShortLittleEndian(parametersStringByteLength);
        writer.WriteString(parametersString, Encoding.Unicode, parametersStringByteLength);
    }
}

public enum SpecialProcId : ushort
{
    Cursor = 1,
    CursorOpen = 2,
    CursorPrepare = 3,
    CursorExecute = 4,
    CursorPrepExec = 5,
    CursorUnprepare = 6,
    CursorFetch = 7,
    CursorOption = 8,
    CursorClose = 9,
    ExecuteSql = 10,
    Prepare = 11,
    Execute = 12,
    PrepExec = 13,
    PrepExecRpc = 14,
    Unprepare = 15
}

[Flags]
public enum RpcMessageOptionFlags : ushort
{
    WithRecompile = 1,
    NoMetaData = 2,
    ReuseMetaData = 4
}

[Flags]
public enum ParameterStatusFlags : byte
{
    ByRefValue = 1,
    DefaultValue = 2,
    Reserved1 = 4,
    Encrypted = 8
}
