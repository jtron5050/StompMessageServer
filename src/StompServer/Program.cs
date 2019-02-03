using System;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Buffers;
using System.Text;
using System.IO;

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
    }
    public enum StompCommand : byte
    {
        Connect,
        Stomp
    }

    public class StompConnection
    {
        private enum RequestProcessingStatus
        {
            ConnectionPending,
            RequestPending,
            ParsingCommand,
            ParsingHeaders,
        }
        private readonly StompFrameProcessor _processor;
        private readonly Socket _socket;
        private RequestProcessingStatus _status;

        protected PipeReader Input { get; set; }
        protected PipeWriter Output { get; set;}

        private StompCommand _currentCommand;


        public StompConnection(Socket socket)
        {
            _socket = socket;
            _processor = new StompFrameProcessor();
            _status = RequestProcessingStatus.ConnectionPending;
        }

        public async Task ProcessRequestsAsync()
        {
            Console.WriteLine("Connected");
            var inputPipe = new Pipe();
            var outputPipe = new Pipe();

            Input = inputPipe.Reader;
            Output = outputPipe.Writer;

            var fillInputPipeTask = FillInputPipeAsync(_socket, inputPipe.Writer);
            var readInputPipeTask = ReadInputPipeAsync(_socket, inputPipe.Reader);

            var fillOutputPipeTask = FillOutputPipeAsync(_socket, outputPipe.Writer);
            var readOutputPipeTask = ReadOutputPipeAsync(_socket, outputPipe.Reader);

            await Task.WhenAll(fillInputPipeTask, readInputPipeTask);
            Console.WriteLine("Disconnected");
        }

        public void OnCommandLine(StompCommand command)
        {
            _currentCommand = command;
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

                if (endConnection)
                {
                        return;
                }

                //message body               
                
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
                ParseRequest(result.Buffer, out consumed, out examined);
            }
            finally
            {
                Input.AdvanceTo(consumed, examined);
            }

            if (result.IsCompleted)
            {
                switch (_status)
                {
                    case RequestProcessingStatus.ConnectionPending:
                        endConnection = true;
                        return true;
                    case RequestProcessingStatus.ParsingCommand:
                        throw new IOException("Error parsing command line");
                    case RequestProcessingStatus.ParsingHeaders:
                        throw new IOException("Error parsing headers");
                    default:
                        break;
                }
            }

            endConnection = false;

            //if (_status == RequestProcessingStatus.RequestPending)

            return true;
        }

        private void ParseRequest(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            switch (_status)
            {
                case RequestProcessingStatus.ConnectionPending:
                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    _status = RequestProcessingStatus.ParsingCommand;
                    goto case RequestProcessingStatus.ParsingCommand;
                case RequestProcessingStatus.ParsingCommand:
                    if (_processor.ProcessCommandLine(new StompRequestHandler(this), buffer, out consumed, out examined))
                    {
                        buffer = buffer.Slice(consumed, buffer.End);
                        _status = RequestProcessingStatus.ParsingHeaders;
                        goto case RequestProcessingStatus.ParsingHeaders;
                    }

                    break;
                case RequestProcessingStatus.ParsingHeaders:
                    if (_processor.ProcessHeaders(new StompRequestHandler(this), buffer, out consumed, out examined))
                    {
                        _status = RequestProcessingStatus.RequestPending;
                    }
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

        public void OnCommandLine(StompCommand command)
        {
            Console.WriteLine(command.ToString());

        }

        public void OnHeaderLine(ReadOnlySpan<byte> header)
        {
            Console.WriteLine(Encoding.UTF8.GetString(header));
        }
    }

    public class StompFrameProcessor
    {
        private const byte LFChar = (byte)'\n';
        private const byte CRChar = (byte)'\r';

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

            if (TryGetKnownCommand(span, out var command))
            {
                handler.OnCommandLine(command);
            }

            examined = consumed;
            return true;
        }

        public bool ProcessHeaders(StompRequestHandler handler, in ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
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

            handler.OnHeaderLine(span);
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

        private bool TryGetKnownCommand(ReadOnlySpan<byte> span, out StompCommand command)
        {  
            return Enum.TryParse<StompCommand>(Encoding.UTF8.GetString(span.Slice(0, span.Length - 1)), true, out command);
        }
    }
}
