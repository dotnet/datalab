using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WoodStar.Tds;
using WoodStar.Tds.Streams;

namespace WoodStar;

internal class WoodStarConnectionInternal
{
    private readonly string _serverIp;
    private readonly string _username;
    private readonly string _password;
    private readonly string _database;

    private Stream? _stream;
    private SslStream? _sslStream;
    private TdsParser? _tdsParser;
    private bool _resetConnection;

    public WoodStarConnectionInternal(string serverIp, string username, string password, string database)
    {
        _serverIp = serverIp;
        _username = username;
        _password = password;
        _database = database;
        State = ConnectionState.Closed;
    }

    public async Task OpenAsync()
    {
        var host = Dns.GetHostEntry(_serverIp);
        var ipAddress = host.AddressList.Single(e => e.AddressFamily == AddressFamily.InterNetwork);
        var remoteEP = new IPEndPoint(ipAddress, 1433);
        var socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(remoteEP);
        _stream = new NetworkStream(socket);
        _tdsParser = new TdsParser(_stream);

        var preloginStream = new PreloginStream(new Version("0.0.1.0"), EncryptionOptionValue.Off, null, 0, false);
        await preloginStream.SendPacket(_stream);

        var preloginResponse = await _tdsParser.ParsePreloginResponseAsync();
        if (preloginResponse is PreloginStream preloginStreamResponse)
        {
            // Assign version etc info to connection
        }

        var interceptingStream = new InterceptingStream(_stream);
        _sslStream = new SslStream(interceptingStream, leaveInnerStreamOpen: false);
        await _sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions()
        {
            TargetHost = _serverIp,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });
        interceptingStream.StopIntercepting();
        var localAddress = ((IPEndPoint)socket.LocalEndPoint!).Address;
        //var macAddress = NetworkInterface.GetAllNetworkInterfaces()
        //    .Single(ni => ni.OperationalStatus == OperationalStatus.Up
        //        && ni.GetIPProperties().UnicastAddresses.Any(e => e.Address.AddressFamily == AddressFamily.InterNetwork
        //            && e.Address.Equals(localAddress)))
        //    .GetPhysicalAddress()
        //    .GetAddressBytes();
        var login7Stream = new Login7Stream(_username, _password, _database, new byte[6]);
        await login7Stream.SendPacket(_sslStream);

        await _tdsParser.ParseLoginResponseAsync();

        State = ConnectionState.Open;
    }

    public void ResetConnection()
    {
        _resetConnection = true;
    }

    public async Task<QueryResult> ExecuteSqlAsync(string sql)
    {
        var sqlBatchStream = new SqlBatchStream(new AllHeaders(transactionDescriptorHeader: new TransactionDescriptorHeader(0, 1)), sql);

        await sqlBatchStream.SendPacket(_stream!, _resetConnection);

        return await _tdsParser!.ParseSqlBatchResponseAsync(_resetConnection);
    }

    public ConnectionState State { get; private set; }

    public void ReturnToPool()
    {
        State = ConnectionState.Returned;
    }

    public void Close()
    {
        State = ConnectionState.Closed;
        _stream = null;
        _sslStream = null;
        _tdsParser = null;
    }

    private sealed class InterceptingStream : Stream
    {
        private readonly Stream _stream;

        private bool _intercept;
        private int _packetBytes;

        public InterceptingStream(Stream stream)
        {
            _stream = stream;
            _intercept = true;
        }

        public void StopIntercepting() => _intercept = false;

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => _stream.Length;

        public override long Position { get => _stream.Position; set => _stream.Position = value; }

        public override void Flush() => _stream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

        public override void SetLength(long value) => _stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_intercept)
            {
                if (_packetBytes == 0)
                {
                    // Process header
                    var headerMemory = new Memory<byte>(new byte[TdsHeader.HeaderSize]);
                    var headerBytes = 0;
                    do
                    {
                        var headerReadCount = await _stream.ReadAsync(headerMemory[headerBytes..TdsHeader.HeaderSize], cancellationToken);
                        if (headerReadCount == 0)
                        {
                            throw new EndOfStreamException();
                        }
                        headerBytes += headerReadCount;
                    } while (headerBytes < TdsHeader.HeaderSize);
                    var header = TdsHeader.Parse(new ReadOnlySequence<byte>(headerMemory));
                    _packetBytes = header.Length - TdsHeader.HeaderSize;
                }

                var readCount = await _stream.ReadAsync(buffer[..Math.Min(buffer.Length, _packetBytes)], cancellationToken);
                if (readCount == 0)
                {
                    throw new EndOfStreamException();
                }

                _packetBytes -= readCount;
                return readCount;
            }

            return await _stream.ReadAsync(buffer, cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_intercept)
            {
                var modifiedLength = buffer.Length + TdsHeader.HeaderSize;
                if (modifiedLength > 4096)
                {
                    throw new NotImplementedException();
                }

                var memoryOwner = MemoryPool<byte>.Shared.Rent(modifiedLength);

                var memoryBuffer = memoryOwner.Memory;
                var tdsHeader = new TdsHeader(PacketType.PreLogin, PacketStatus.EOM, modifiedLength, 0, 1);
                tdsHeader.Write(memoryBuffer);
                buffer.CopyTo(memoryBuffer[TdsHeader.HeaderSize..]);

                await _stream.WriteAsync(memoryBuffer[..modifiedLength], cancellationToken);

                memoryOwner.Dispose();
            }
            else
            {
                await _stream.WriteAsync(buffer, cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }
        }
    }
}
