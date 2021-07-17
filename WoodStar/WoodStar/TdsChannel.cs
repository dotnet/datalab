using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WoodStar
{
    public class TdsChannel
    {
        private readonly PipeReader _pipeReader;
        private readonly Stream _stream;

        private SslStream _sslStream;
        private PipeReader _sslPipeReader;

        public TdsChannel(Stream stream)
        {
            _pipeReader = PipeReader.Create(stream);
            _stream = stream;
            _sslStream = null!;
            _sslPipeReader = null!;
        }

        public void SetSslStream(SslStream sslStream)
        {
            _sslStream = sslStream;
            _sslPipeReader = PipeReader.Create(sslStream);
        }

        public async Task Prelogin()
        {
            var preloginStream = new PreloginStream(0, 0, 1, 0, EncryptionOption.Off, null, 0, false, null, null, null);
            var header = new TdsHeader(PacketType.PreLogin, PacketStatus.EOM, preloginStream.Length + TdsHeader.HeaderSize, 0, 1);
            var preloginBuffer = ArrayPool<byte>.Shared.Rent(header.Length);
            header.WriteToBuffer(preloginBuffer);
            preloginStream.WriteToBuffer(preloginBuffer);

            await _stream.WriteAsync(preloginBuffer, 0, header.Length);
            await _stream.FlushAsync();

            ArrayPool<byte>.Shared.Return(preloginBuffer);

            var preloginResponse = await ReadNextPacketAsync(ResponseType.Prelogin);
            if (preloginResponse is PreloginPacket preloginPacket)
            {
                // Assign version etc info to connection
            }
        }

        public async Task Login(string username, string password, string database)
        {
            var login7Stream = new Login7Stream
            {
                UserName = username,
                Password = password,
                Database = database,
            };
            var header = new TdsHeader(PacketType.Login, PacketStatus.EOM, login7Stream.Length + TdsHeader.HeaderSize, 0, 1);
            var loginBuffer = ArrayPool<byte>.Shared.Rent(header.Length);
            header.WriteToBuffer(loginBuffer);
            login7Stream.WriteToBuffer(loginBuffer);

            await _sslStream.WriteAsync(loginBuffer, 0, header.Length);
            await _sslStream.FlushAsync();

            ArrayPool<byte>.Shared.Return(loginBuffer);

            var loginResponse = await ReadNextPacketAsync(ResponseType.Login);
        }

        private async ValueTask<TdsPacket?> ReadNextPacketAsync(ResponseType expectedResponse)
        {
            while (true)
            {
                var reader = expectedResponse == ResponseType.Prelogin || expectedResponse == ResponseType.Login
                    ? _pipeReader
                    : _sslPipeReader;
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.Length >= TdsHeader.HeaderSize)
                {
                    var header = TdsHeader.Parse(buffer.Slice(0, TdsHeader.HeaderSize));
                    if (buffer.Length >= header.Length)
                    {
                        TdsPacket? tdsPacket;
                        var packetBuffer = buffer.Slice(TdsHeader.HeaderSize, header.Length - TdsHeader.HeaderSize);
                        switch (expectedResponse)
                        {
                            case ResponseType.Prelogin:
                                var preloginStream = PreloginStream.ParseResponse(packetBuffer);
                                tdsPacket = new PreloginPacket(header, preloginStream);
                                break;

                            case ResponseType.Login:
                                var loginAckStream = LoginAckStream.ParseResponse(packetBuffer);
                                tdsPacket = new LoginAckPacket(header, loginAckStream);
                                break;

                            default:
                                throw new InvalidOperationException("Unrecognised packet type.");
                        }

                        _pipeReader.AdvanceTo(buffer.GetPosition(header.Length));
                        return tdsPacket;
                    }
                }

                // Tell the PipeReader how much of the buffer has been consumed.
                _pipeReader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }

            return null;
        }

        private enum ResponseType
        {
            Prelogin,
            Login
        }
    }
}
