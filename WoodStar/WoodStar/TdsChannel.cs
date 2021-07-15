using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
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

        public TdsChannel(Stream stream)
        {
            _pipeReader = PipeReader.Create(stream);
            _stream = stream;
            _sslStream = null!;
        }

        public void SetSslStream(SslStream sslStream)
        {
            _sslStream = sslStream;
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

            await ReadNextPacketAsync(ResponseType.Login);
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            var sqlBatchStream = new SqlBatchStream(
                new StreamHeader
                {
                    TransactionDescriptor = new TransactionDescriptorHeader(0, 1)
                },
                sql);

            var header = new TdsHeader(PacketType.SQLBatch, PacketStatus.EOM, sqlBatchStream.Length + TdsHeader.HeaderSize, 0, 1);
            var sqlBatchBuffer = ArrayPool<byte>.Shared.Rent(header.Length);
            header.WriteToBuffer(sqlBatchBuffer);
            sqlBatchStream.WriteToBuffer(sqlBatchBuffer);
            await _stream.WriteAsync(sqlBatchBuffer, 0, header.Length);
            await _stream.FlushAsync();

            ArrayPool<byte>.Shared.Return(sqlBatchBuffer);

            await ReadNextPacketAsync(ResponseType.SqlBatchResult);
        }

        private async ValueTask<TdsPacket?> ReadNextPacketAsync(ResponseType expectedResponse)
        {
            var result = await _pipeReader.ReadAtLeastAsync(TdsHeader.HeaderSize);
            var buffer = result.Buffer;
            var header = TdsHeader.Parse(buffer.Slice(0, TdsHeader.HeaderSize));
            if (buffer.Length < header.Length)
            {
                _pipeReader.AdvanceTo(buffer.GetPosition(TdsHeader.HeaderSize));
                result = await _pipeReader.ReadAtLeastAsync(header.Length - TdsHeader.HeaderSize);
                buffer = result.Buffer;
            }
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

                case ResponseType.SqlBatchResult:
                    tdsPacket = null;
                    break;

                default:
                    throw new InvalidOperationException("Unrecognised packet type.");
            }

            _pipeReader.AdvanceTo(buffer.GetPosition(header.Length));

            return tdsPacket;
        }

        private enum ResponseType
        {
            Prelogin,
            Login,
            SqlBatchResult
        }
    }
}
