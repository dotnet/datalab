using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using WoodStar.Tds;

namespace WoodStar;

public class QueryResult
{
    private readonly TdsParser _tdsParser;
    private readonly ColMetadataToken _colMetadataToken;
    private readonly object[] _currentValues;
    private ReadOnlySequence<byte> _currentPacketBuffer;
    private bool _finished;
    private readonly PipeReader _pipeReader;

    public QueryResult(TdsParser tdsParser, PipeReader pipeReader, ReadOnlySequence<byte> currentPacketBuffer, ColMetadataToken colMetadataToken)
    {
        _tdsParser = tdsParser;
        _pipeReader = pipeReader;
        _currentPacketBuffer = currentPacketBuffer;
        _colMetadataToken = colMetadataToken;
        _finished = false;
        _currentValues = new object[_colMetadataToken.Count];
    }

    public ValueTask<bool> ReadAsync()
    {
        var reader = new SequenceReader<byte>(_currentPacketBuffer);
        if (reader.TryRead(out var tokenByte))
        {
            var tokenType = (TokenType)Enum.ToObject(typeof(TokenType), tokenByte);

            if (tokenType == TokenType.ROW)
            {
                for (var i = 0; i < _colMetadataToken.Count; i++)
                {
                    _currentValues[i] = _colMetadataToken.Columns[i].TypeInfo.ReadValue(ref reader);
                }

                _currentPacketBuffer = _currentPacketBuffer.Slice(reader.Position);
                _pipeReader.AdvanceTo(reader.Position);

                return new ValueTask<bool>(true);
            }

            if (tokenType == TokenType.DONE)
            {
                _finished = true;
                _pipeReader.AdvanceTo(_currentPacketBuffer.End);
                return new ValueTask<bool>(false);
            }
        }

        throw new ParsingException();
    }

    public T GetValue<T>(int ordinal)
    {
        if (_finished)
        {
            throw new InvalidOperationException();
        }

        return (T)_currentValues[ordinal];
    }
}
