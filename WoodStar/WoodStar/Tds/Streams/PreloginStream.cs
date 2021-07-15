using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WoodStar;

namespace WoodStar.Tds.Streams;

// TODO: Nonce, FedAuthRequired
public sealed class PreloginStream : ITdsStream
{
    private static readonly byte _terminator = 0xFF;
    private readonly List<PreloginOption> _options = new();
    private readonly int _length;

    public PreloginStream(
        Version version,
        EncryptionOptionValue encryptionOptionValue,
        string? instanceName,
        uint threadId,
        bool marsEnabled)
    {
        _options.Add(new VersionOption(version));
        _options.Add(new EncryptionOption(encryptionOptionValue));
        _options.Add(new InstanceOption(instanceName));
        _options.Add(new ThreadIdOption(threadId));
        _options.Add(new MarsOption(marsEnabled));
        //_options.Add(new TraceIdOption(null, null, null));
        //_options.Add(new FedAuthRequiredOption(fedAuthRequired: false));

        _length = _options.Sum(o => 5 + o.Length) + 1;
    }

    private PreloginStream(IReadOnlyList<PreloginOption> options)
    {
        _options.AddRange(options);

        _length = _options.Sum(o => 5 + o.Length) + 1;
    }

    private enum PreloginOptionToken : byte
    {
        Version = 0,
        Encryption = 1,
        InstOpt = 2,
        ThreadId = 3,
        Mars = 4,
        TraceId = 5, // NotImplemented
        FedAuthRequired = 6, // NotImplemented
        NonceOpt = 7 // NotImplemented
    }

    private abstract class PreloginOption
    {
        private readonly PreloginOptionToken _token;

        public PreloginOption(PreloginOptionToken token, ushort length)
        {
            _token = token;
            Length = length;
        }

        public ushort Length { get; }

        public ushort PrintOption(Memory<byte> buffer, ushort offset)
        {
            buffer.Span[0] = (byte)_token;
            buffer[1..].WriteUnsignedShortBigEndian(offset);
            buffer[3..].WriteUnsignedShortBigEndian(Length);

            return Length;
        }

        public abstract int PrintOptionData(Memory<byte> buffer);
    }

    private sealed class VersionOption : PreloginOption
    {
        private readonly Version _version;

        public VersionOption(Version version)
            : base(PreloginOptionToken.Version, 6)
        {
            _version = version;
        }

        public override int PrintOptionData(Memory<byte> buffer)
        {
            // TODO: Printing is different on x86 system
            buffer.Span[0] = (byte)_version.Major;
            buffer.Span[1] = (byte)_version.Minor;
            buffer[2..].WriteUnsignedShortBigEndian((ushort)_version.Build);
            buffer[4..].WriteUnsignedShortLittleEndian((ushort)_version.Revision);

            return Length;
        }

        public static VersionOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (length != 6)
            {
                throw new InvalidOperationException();
            }

            if (reader.TryRead(out var majorVersion)
                && reader.TryRead(out var minorVersion)
                && reader.TryReadBigEndian(out short buildNumber)
                && reader.TryReadLittleEndian(out short revision))
            {
                return new VersionOption(new Version(majorVersion, minorVersion, buildNumber, revision));
            }

            throw new ParsingException();
        }
    }

    private sealed class EncryptionOption : PreloginOption
    {
        private readonly EncryptionOptionValue _encryptionOptionValue;

        public EncryptionOption(EncryptionOptionValue encryptionOptionValue)
            : base(PreloginOptionToken.Encryption, 1)
        {
            _encryptionOptionValue = encryptionOptionValue;
        }

        public override int PrintOptionData(Memory<byte> buffer)
        {
            buffer.Span[0] = (byte)_encryptionOptionValue;

            return Length;
        }

        public static EncryptionOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (length != 1)
            {
                throw new InvalidOperationException();
            }

            if (reader.TryRead(out var value)
                && Enum.ToObject(typeof(EncryptionOptionValue), value) is EncryptionOptionValue encryptionOptionValue)
            {
                return new EncryptionOption(encryptionOptionValue);
            }

            throw new ParsingException();
        }
    }

    private sealed class InstanceOption : PreloginOption
    {
        private readonly string? _instanceName;

        public InstanceOption(string? instanceName)
            : base(PreloginOptionToken.InstOpt, (ushort)((instanceName?.Length ?? 0) + 1))
        {
            _instanceName = instanceName;
        }

        public override int PrintOptionData(Memory<byte> buffer)
        {
            var i = 0;
            if (!string.IsNullOrEmpty(_instanceName))
            {
                for (; i < _instanceName.Length; i++)
                {
                    buffer.Span[i] = (byte)_instanceName[i];
                }
            }
            buffer.Span[i] = 0;

            return Length;
        }

        public static InstanceOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (length == 1)
            {
                if (reader.TryRead(out var value)
                    && value != 0)
                {
                    throw new ParsingException();
                }

                return new InstanceOption(null);
            }

            var instanceName = new char[length - 1];
            for (var i = 0; i < instanceName.Length; i++)
            {
                if (!reader.TryRead(out var value))
                {
                    throw new ParsingException();
                }

                instanceName[i] = (char)value;
            }

            return new InstanceOption(new string(instanceName));
        }
    }

    private sealed class ThreadIdOption : PreloginOption
    {
        private readonly uint _threadId;

        public ThreadIdOption(uint threadId)
            : base(PreloginOptionToken.ThreadId, 4)
        {
            _threadId = threadId;
        }

        public override int PrintOptionData(Memory<byte> buffer)
        {
            buffer.WriteUnsignedIntBigEndian(_threadId);

            return Length;
        }

        public static ThreadIdOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (length == 0)
            {
                return new ThreadIdOption(0);
            }

            if (length != 4
                || !reader.TryReadBigEndian(out int value))
            {
                throw new ParsingException();
            }

            return new ThreadIdOption((uint)value);
        }
    }

    private sealed class MarsOption : PreloginOption
    {
        private readonly bool _marsEnabled;

        public MarsOption(bool marsEnabled)
            : base(PreloginOptionToken.Mars, 1)
        {
            _marsEnabled = marsEnabled;
        }

        public override int PrintOptionData(Memory<byte> buffer)
        {
            buffer.Span[0] = (byte)(_marsEnabled ? 1 : 0);

            return Length;
        }

        public static MarsOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (length != 1)
            {
                throw new InvalidOperationException();
            }

            if (reader.TryRead(out var value))
            {
                return new MarsOption(value != 0);
            }

            throw new ParsingException();
        }
    }

    private sealed class TraceIdOption : PreloginOption
    {
        private readonly Guid? _connectionId;
        private readonly Guid? _activityId;
        private readonly uint? _activitySequence;

        public TraceIdOption(Guid? connectionId, Guid? activityId, uint? activitySequence)
            : base(PreloginOptionToken.TraceId, (ushort)(connectionId != null ? 36 : 0))
        {
            if (_connectionId != null)
            {
                throw new NotImplementedException();
            }

            _connectionId = connectionId;
            _activityId = activityId;
            _activitySequence = activitySequence;
        }

        public override int PrintOptionData(Memory<byte> buffer)
        {
            if (_connectionId != null)
            {
                throw new NotImplementedException();
            }

            return Length;
        }

        public static TraceIdOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (length == 0)
            {
                return new TraceIdOption(null, null, null);
            }

            throw new ParsingException();
        }
    }

    private sealed class FedAuthRequiredOption : PreloginOption
    {
        private readonly bool _fedAuthRequired;

        public FedAuthRequiredOption(bool fedAuthRequired)
            : base(PreloginOptionToken.FedAuthRequired, 1)
        {
            _fedAuthRequired = fedAuthRequired;
        }
        public override int PrintOptionData(Memory<byte> buffer)
        {
            buffer.Span[0] = (byte)(_fedAuthRequired ? 0 : 1);

            return Length;
        }

        public static FedAuthRequiredOption Parse(ref SequenceReader<byte> reader, short length)
        {
            if (reader.TryRead(out var value))
            {
                return new FedAuthRequiredOption(value == 0);
            }

            throw new ParsingException();
        }
    }

    private void Write(Memory<byte> buffer)
    {
        var offset = (ushort)(_options.Count * 5 + 1);
        for (var i = 0; i < _options.Count; i++)
        {
            offset += _options[i].PrintOption(buffer, offset);
            buffer = buffer[5..];
        }

        buffer.Span[0] = _terminator;
        buffer = buffer[1..];

        for (var i = 0; i < _options.Count; i++)
        {
            var bytesWritten = _options[i].PrintOptionData(buffer);
            buffer = buffer[bytesWritten..];
        }
    }

    public async Task SendPacket(Stream stream)
    {
        var length = _length;
        var packetId = 1;
        var memoryOwner = MemoryPool<byte>.Shared.Rent(Math.Min(length, 4096));
        while (length != 0)
        {
            if (length < 4088)
            {
                var packetLength = length + TdsHeader.HeaderSize;
                var header = new TdsHeader(PacketType.PreLogin, PacketStatus.EOM, packetLength, spid: 0, packetId);
                var buffer = memoryOwner.Memory;
                header.Write(buffer);
                Write(buffer[8..]);
                var a = HelperMethods.PrintBuffer(buffer.ToArray(), header.Length);
                await stream.WriteAsync(buffer[..packetLength]);

                length -= length;
            }
            else
            {
                // TODO: when the stream is longer than 4088 bytes
                throw new NotImplementedException();
            }
        }

        memoryOwner.Dispose();
    }

    public static PreloginStream ParseResponse(ReadOnlySequence<byte> sequence)
    {
        var reader = new SequenceReader<byte>(sequence);

        var optionOffsets = new List<(PreloginOptionToken, short, short)>();
        while (reader.TryRead(out var value)
            && value != _terminator)
        {
            var tokenType = (PreloginOptionToken)Enum.ToObject(typeof(PreloginOptionToken), value);
            if (optionOffsets.Count == 0
                && tokenType != PreloginOptionToken.Version)
            {
                throw new ParsingException();
            }

            if (!(reader.TryReadBigEndian(out short offset)
                    && reader.TryReadBigEndian(out short length)))
            {
                throw new ParsingException();
            }

            optionOffsets.Add((tokenType, offset, length));
        }

        if (!reader.End)
        {
            var options = new List<PreloginOption>();
            foreach (var (token, offset, length) in optionOffsets)
            {
                PreloginOption? preloginOption = null;
                if (offset != reader.Consumed)
                {
                    throw new InvalidOperationException();
                }

                switch (token)
                {
                    case PreloginOptionToken.Version:
                        preloginOption = VersionOption.Parse(ref reader, length);
                        break;

                    case PreloginOptionToken.Encryption:
                        preloginOption = EncryptionOption.Parse(ref reader, length);
                        break;

                    case PreloginOptionToken.InstOpt:
                        preloginOption = InstanceOption.Parse(ref reader, length);
                        break;

                    case PreloginOptionToken.ThreadId:
                        preloginOption = ThreadIdOption.Parse(ref reader, length);
                        break;

                    case PreloginOptionToken.Mars:
                        preloginOption = MarsOption.Parse(ref reader, length);
                        break;

                    case PreloginOptionToken.TraceId:
                        preloginOption = TraceIdOption.Parse(ref reader, length);
                        break;

                    case PreloginOptionToken.FedAuthRequired:
                        preloginOption = FedAuthRequiredOption.Parse(ref reader, length);
                        break;

                    default:
                        throw new InvalidOperationException();
                }

                options.Add(preloginOption);
            }

            if (reader.End)
            {
                return new PreloginStream(options);
            }
        }

        throw new ParsingException();
    }
}
