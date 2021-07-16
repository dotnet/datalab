using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WoodStar
{
    public class TdsChannel
    {
        private readonly PipeReader _pipeReader;
        private readonly Stream _stream;

        public TdsChannel(Stream stream)
        {
            _pipeReader = PipeReader.Create(stream);
            _stream = stream;
        }

        public async Task Prelogin()
        {
            var preloginStream = new PreloginStream(0, 0, 1, 0, EncryptionOption.Off, null, 0, false, null, null, null);
            var header = new TdsHeader(PacketType.PreLogin, PacketStatus.EOM, preloginStream.Length + TdsHeader.HeaderSize, 0, 1);
            var preloginBuffer = ArrayPool<byte>.Shared.Rent(header.Length);
            header.WriteToBuffer(preloginBuffer);
            preloginStream.WriteToBuffer(preloginBuffer);

            await _stream.WriteAsync(preloginBuffer, 0, header.Length);

            ArrayPool<byte>.Shared.Return(preloginBuffer);

            var preloginResponse = await ReadNextPacketAsync(ResponseType.Prelogin);
            if (preloginResponse is PreloginPacket preloginPacket)
            {
                // Assign version etc info to connection
            }
        }

        public async Task Login()
        {
            var login7Stream = new Login7Stream
            {
                UserName = "sa",
                Password = "Password1!"
            };
            var header = new TdsHeader(PacketType.Login, PacketStatus.EOM, login7Stream.Length + TdsHeader.HeaderSize, 0, 1);
            var loginBuffer = ArrayPool<byte>.Shared.Rent(header.Length);
            header.WriteToBuffer(loginBuffer);
            login7Stream.WriteToBuffer(loginBuffer);

            await _stream.WriteAsync(loginBuffer, 0, loginBuffer.Length);

            ArrayPool<byte>.Shared.Return(loginBuffer);

            var loginResponse = await ReadNextPacketAsync(ResponseType.Login);

        }

        private async ValueTask<TdsPacket?> ReadNextPacketAsync(ResponseType expectedResponse)
        {
            while (true)
            {
                var result = await _pipeReader.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.Length >= 8)
                {
                    var header = TdsHeader.Parse(buffer.Slice(0, 8));
                    if (buffer.Length >= header.Length)
                    {
                        TdsPacket? tdsPacket;
                        var packetBuffer = buffer.Slice(8, header.Length - 8);
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
