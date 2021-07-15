using System;
using System.Buffers;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using WoodStar.Tds.Streams;
using WoodStar.Tds.Tokens;

namespace WoodStar.Tds;

public class TdsParser
{
    private readonly PipeReader _pipeReader;

    public TdsParser(Stream underlyingStream)
    {
        _pipeReader = PipeReader.Create(underlyingStream);
    }

    public async Task<PreloginStream> ParsePreloginResponseAsync()
    {
        var packetBuffer = await GetBufferAsync();
        var preloginStream = PreloginStream.ParseResponse(packetBuffer);

        _pipeReader.AdvanceTo(packetBuffer.GetPosition(packetBuffer.Length));

        return preloginStream;
    }

    public async Task ParseLoginResponseAsync()
    {
        var packetBuffer = await GetBufferAsync();
        var tokens = new List<IToken>();
        IToken token;
        do
        {
            token = GetNextToken(ref packetBuffer);
            tokens.Add(token);
        } while (token.TokenType != TokenType.DONE);

        if (!tokens.Any(e => e.TokenType == TokenType.LOGINACK))
        {
            throw new InvalidOperationException();
        }

        _pipeReader.AdvanceTo(packetBuffer.GetPosition(packetBuffer.Length));
    }

    public async Task<QueryResult> ParseSqlBatchResponseAsync(bool resetConnectionRequested)
    {
        var packetBuffer = await GetBufferAsync();
        var nextToken = GetNextToken(ref packetBuffer);
        if (resetConnectionRequested)
        {
            if (nextToken is not EnvChangeToken { Type: 18 } envChangeToken)
            {
                throw new ParsingException();
            }
            nextToken = GetNextToken(ref packetBuffer);
        }

        if (nextToken is not ColMetadataToken colMetadataToken)
        {
            throw new ParsingException();
        }

        return new QueryResult(this, _pipeReader, packetBuffer, colMetadataToken);
    }

    private async Task<ReadOnlySequence<byte>> GetBufferAsync()
    {
        var result = await _pipeReader!.ReadAtLeastAsync(TdsHeader.HeaderSize);
        var buffer = result.Buffer;
        var header = TdsHeader.Parse(buffer.Slice(0, TdsHeader.HeaderSize));
        if (header.Type != PacketType.TabularResult)
        {
            throw new InvalidOperationException();
        }

        if (buffer.Length < header.Length)
        {
            throw new NotImplementedException();
            //_pipeReader.AdvanceTo(buffer.GetPosition(TdsHeader.HeaderSize));
            //result = await _pipeReader.ReadAtLeastAsync(header.Length - TdsHeader.HeaderSize);
            //buffer = result.Buffer;
        }

        var packetBuffer = buffer.Slice(TdsHeader.HeaderSize, header.Length - TdsHeader.HeaderSize);
        _pipeReader.AdvanceTo(buffer.GetPosition(TdsHeader.HeaderSize));

        return packetBuffer;
    }

    private static IToken GetNextToken(ref ReadOnlySequence<byte> sequence)
    {
        var reader = new SequenceReader<byte>(sequence);
        if (reader.TryRead(out var tokenByte))
        {
            IToken token;
            var tokenType = (TokenType)Enum.ToObject(typeof(TokenType), tokenByte);
            switch (tokenType)
            {
                case TokenType.ENVCHANGE:
                    token = EnvChangeToken.Parse(ref reader);
                    break;

                case TokenType.INFO:
                    token = InfoToken.Parse(ref reader);
                    break;

                case TokenType.LOGINACK:
                    token = LoginAckToken.Parse(ref reader);
                    break;

                case TokenType.DONE:
                    token = DoneToken.Parse(ref reader);
                    break;

                case TokenType.COLMETADATA:
                    token = ColMetadataToken.Parse(ref reader);
                    break;

                //case TokenType.ROW:
                //    token = RowToken.Parse(ref reader);
                //    break;

                default:
                    throw new NotImplementedException();
            }

            sequence = sequence.Slice(token.TokenLength);

            if (token.TokenType == TokenType.DONE
                && !sequence.IsEmpty)
            {
                throw new InvalidOperationException();
            }

            return token;
        }

        throw new ParsingException();
    }
}
