using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WoodStar
{
    public class WoodStarConnection
    {
        private readonly string _serverIp;
        private readonly string _username;
        private readonly string _password;
        private readonly string _database;

        private TdsChannel? _tdsChannel;

        public WoodStarConnection(string serverIp, string username, string password, string database)
        {
            _serverIp = serverIp;
            _username = username;
            _password = password;
            _database = database;
        }

        public async Task OpenAsync()
        {
            var host = Dns.GetHostEntry(_serverIp);
            var ipAddress = host.AddressList[1];
            var remoteEP = new IPEndPoint(ipAddress, 1433);
            var socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(remoteEP);
            var networkStream = new NetworkStream(socket);
            _tdsChannel = new TdsChannel(networkStream);
            await _tdsChannel.Prelogin();

            var interceptingStream = new InterceptingStream(networkStream);
            var sslStream = new SslStream(interceptingStream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions()
            {
                TargetHost = _serverIp,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            });
            interceptingStream.StopIntercepting();
            _tdsChannel = new TdsChannel(sslStream);
            await _tdsChannel.Login();
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
                        var header = new byte[8];
                        var headerBytes = 0;
                        do
                        {
                            var headerReadCount = await _stream.ReadAsync(header.AsMemory()[headerBytes..8], cancellationToken);
                            if (headerReadCount == 0)
                            {
                                throw new EndOfStreamException();
                            }
                            headerBytes += headerReadCount;
                        } while (headerBytes < 8);

                        _packetBytes = 256 * header[2] + header[3] - 8;
                    }

                    var readCount = await _stream.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, _packetBytes)), cancellationToken);
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
                    var modifiedLength = buffer.Length + 8;
                    var modifiedBuffer = ArrayPool<byte>.Shared.Rent(modifiedLength);
                    buffer.CopyTo(modifiedBuffer.AsMemory(8));

                    var tdsHeader = new TdsHeader(PacketType.PreLogin, PacketStatus.EOM, modifiedLength, 0, 1);
                    tdsHeader.WriteToBuffer(modifiedBuffer);
                    await _stream.WriteAsync(modifiedBuffer.AsMemory().Slice(0, modifiedLength), cancellationToken);
                    ArrayPool<byte>.Shared.Return(modifiedBuffer);

                    return;
                }

                await _stream.WriteAsync(buffer, cancellationToken);
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
}
