using System;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Buffers;
using System.Text;

namespace StompServer
{
    class Program
    {
        static void Main(string[] args)
        {
            var program = new Program();

            program.RunAsync(args).GetAwaiter().GetResult();
        }


        public async Task RunAsync(string[] args)
        {
            var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 41493));

            listener.Listen(120);
            Console.WriteLine("Listening...");

            while (true)
            {
                var socket = await listener.AcceptAsync();
                var connection = new StompConnection(socket);

                _ = HandleConnectionAsync(connection);
            }
        }

        private async Task HandleConnectionAsync(StompConnection connection)
        {
            var connectionTask = connection.ProcessRequestsAsync();

            await connectionTask;

            //dispose
        }



        private void ProcessLine(ReadOnlySequence<byte> lineBuffer)
        {
            if (lineBuffer.IsSingleSegment)
            {
                Console.WriteLine(Encoding.UTF8.GetString(lineBuffer.First.Span));
                return;
            }

            foreach (var segment in lineBuffer)
            {
                Console.Write(Encoding.UTF8.GetString(segment.Span));
            }

            Console.WriteLine();
        }
    }

    public class StompConnection
    {
        private enum RequestProcessingStatus
        {
            RequestPending,
            ParsingCommandLine,
            ParsingHeaders,
            Connected
        }
        private readonly StompFrameProcessor _processor;
        private readonly Socket _socket;
        private RequestProcessingStatus _status;

        protected PipeReader Input { get; set; }



        public StompConnection(Socket socket)
        {
            _socket = socket;
            _processor = new StompFrameProcessor();
            _status = RequestProcessingStatus.RequestPending;
        }

        public async Task ProcessRequestsAsync()
        {
            Console.WriteLine("Connected");
            var inputPipe = new Pipe();
            var outputPipe = new Pipe();

            Input = inputPipe.Reader;

            var fillInputPipeTask = FillInputPipeAsync(_socket, inputPipe.Writer);
            var readInputPipeTask = ReadInputPipeAsync(_socket, inputPipe.Reader);

            var fillOutputPipeTask = FillOutputPipeAsync(_socket, outputPipe.Writer);
            var readOutputPipeTask = ReadOutputPipeAsync(_socket, outputPipe.Reader);

            await Task.WhenAll(fillInputPipeTask, readInputPipeTask);
            Console.WriteLine("Disconnected");
        }

        private Task ReadOutputPipeAsync(Socket socket, PipeReader reader)
        {
            throw new NotImplementedException();
        }

        private Task FillOutputPipeAsync(Socket socket, PipeWriter writer)
        {
            throw new NotImplementedException();
        }

        private async Task FillInputPipeAsync(Socket connection, PipeWriter writer)
        {
            const int minimumBufferSize = 512;

            while (true)
            {
                try
                {
                    var memory = writer.GetMemory(minimumBufferSize);
                    var bytesRead = await connection.ReceiveAsync(memory, SocketFlags.None);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    writer.Advance(bytesRead);
                }
                catch
                {
                    break;
                }

                var result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }

            writer.Complete();
        }

        private async Task ReadInputPipeAsync(Socket connection, PipeReader reader)
        {

            while (true)
            {
                ReadResult readResult = default;
                bool endConnection = false;

                do
                {
                    readResult = await reader.ReadAsync();
                }
                while (!TryParseRequest(readResult, out endConnection));

                if (readResult.IsCompleted)
                {
                    break;
                }
            }

            reader.Complete();
        }

        private bool TryParseRequest(ReadResult result, out bool endConnection)
        {
            var consumed = result.Buffer.Start;
            var examined = result.Buffer.End;
            endConnection = false;
            try
            {
                //processor.ProcessCommandLine(handler, result.Buffer, out consumed, out examined);
                ParseRequest(result.Buffer, out consumed, out examined);
            }
            finally
            {
                Input.AdvanceTo(consumed, examined);
            }

            // advance reader first char after \n.  mark all of buffer examined.
            return true;
        }

        private void ParseRequest(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            switch (_status)
            {
                case RequestProcessingStatus.RequestPending:
                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    _status = RequestProcessingStatus.ParsingCommandLine;
                    goto case RequestProcessingStatus.ParsingCommandLine;
                case RequestProcessingStatus.ParsingCommandLine:
                    if (_processor.ProcessCommandLine(new StompRequestHandler(this), buffer, out consumed, out examined))
                        buffer = buffer.Slice(consumed, buffer.End);

                    break;
            }
        }
    }

    public class StompRequestHandler
    {
        private readonly StompConnection _connection;

        public StompRequestHandler(StompConnection connection)
        {
            _connection = connection;
        }

        public void OnCommandLine(ReadOnlySpan<byte> command)
        {
            Console.WriteLine(Encoding.UTF8.GetString(command));
        }

        public void OnHeaderLine(ReadOnlySpan<byte> header)
        {

        }
    }

    public class StompFrameProcessor
    {
        private const byte LFChar = (byte)'\n';

        public bool ProcessCommandLine(StompRequestHandler handler, in ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;
            ReadOnlySpan<byte> span = null;

            if (TryGetNewLine(buffer, out var position))
            {
                span = buffer.Slice(consumed, position).ToArray();
                consumed = position;
            }
            else
            {
                return false;
            }

            handler.OnCommandLine(span);

            examined = consumed;
            return true;
        }

        private static bool TryGetNewLine(ReadOnlySequence<byte> buffer, out SequencePosition position)
        {
            var lfPosition = buffer.PositionOf(LFChar);

            if (lfPosition != null)
            {
                position = buffer.GetPosition(1, lfPosition.Value);
                return true;
            }

            position = default;
            return false;
        }
    }

}
