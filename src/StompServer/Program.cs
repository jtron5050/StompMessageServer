using System;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Buffers;
using System.Text;
using System.IO;
using System.Collections.Generic;

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
        None,
        Connect,
        Stomp
    }

    public class StompConnection
    {
        private enum ConnectionStatus
        {
            ConectionPending,
            Connected
        }

        private enum RequestProcessingStatus
        {
            RequestPending,
            ParsingCommand,
            ParsingHeaders,
            RequestComplete
        }
        private readonly StompFrameProcessor _processor;
        private readonly Socket _socket;
        private RequestProcessingStatus _requestProcessingStatus;


        protected PipeReader Input { get; set; }
        protected PipeWriter Output { get; set; }

        private StompCommand _currentCommand;
        private Dictionary<string, string> _currentHeaders;


        public StompConnection(Socket socket)
        {
            _socket = socket;
            _processor = new StompFrameProcessor();
            _requestProcessingStatus = RequestProcessingStatus.RequestPending;
            _currentHeaders = new Dictionary<string, string>();
        }

        public async Task ProcessRequestsAsync()
        {
            Console.WriteLine("Connected");
            var inputPipe = new Pipe();
            var outputPipe = new Pipe();

            Input = inputPipe.Reader;
            Output = outputPipe.Writer;

            var fillInputPipeTask = FillInputPipeAsync(_socket, inputPipe.Writer);
            var readInputPipeTask = ProcessRequestsAsync(inputPipe.Reader); //ReadInputPipeAsync(_socket, inputPipe.Reader);

            var readOutputPipeTask = ReadOutputPipeAsync(_socket, outputPipe.Reader);

            await Task.WhenAll(fillInputPipeTask, readInputPipeTask);
            Console.WriteLine("Disconnected");
        }

        public void OnCommandLine(StompCommand command)
        {
            _currentCommand = command;
        }

        public void OnHeaderLine(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            _currentHeaders.Add(Encoding.UTF8.GetString(key), Encoding.UTF8.GetString(value));
        }

        private Task ReadOutputPipeAsync(Socket socket, PipeReader reader)
        {
            return Task.CompletedTask;
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

        private async Task ProcessRequestsAsync(PipeReader reader)
        {
            while (true)
            {
                ReadResult readResult = default;
                bool endConnection = false;
                _currentCommand = StompCommand.None;
                _currentHeaders.Clear();

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

                Console.WriteLine("got a complete request");

                DoResponse();

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
                switch (_requestProcessingStatus)
                {
                    case RequestProcessingStatus.RequestPending:
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

            if (_requestProcessingStatus == RequestProcessingStatus.RequestPending)
            {
                return true;
            }
            else
                return false;
        }

        private void ParseRequest(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            switch (_requestProcessingStatus)
            {
                case RequestProcessingStatus.RequestPending:
                    if (buffer.IsEmpty)
                    {
                        break;
                    }

                    _requestProcessingStatus = RequestProcessingStatus.ParsingCommand;
                    goto case RequestProcessingStatus.ParsingCommand;
                case RequestProcessingStatus.ParsingCommand:
                    if (_processor.ProcessCommandLine(new StompRequestHandler(this), buffer, out consumed, out examined))
                    {
                        buffer = buffer.Slice(consumed, buffer.End);
                        _requestProcessingStatus = RequestProcessingStatus.ParsingHeaders;
                        goto case RequestProcessingStatus.ParsingHeaders;
                    }

                    break;
                case RequestProcessingStatus.ParsingHeaders:
                    if (_processor.ProcessHeaders(new StompRequestHandler(this), buffer, out consumed, out examined))
                    {
                        _requestProcessingStatus = RequestProcessingStatus.RequestPending;
                    }

                    break;
            }
        }

        private void DoResponse()
        {
            
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
            _connection.OnCommandLine(command);
        }

        public void OnHeaderLine(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            Console.WriteLine("got header");
            _connection.OnHeaderLine(key, value);
            //_connection.OnHeaderLine()
        }

        internal void OnHeaderLine(ReadOnlySpan<byte> span)
        {

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

                if (span[0] == LFChar || (span[0] == CRChar && span[1] == LFChar))
                    return true;
                else
                {
                    var colon = span.IndexOf((byte)':');
                    handler.OnHeaderLine(span.Slice(0, colon), span.Slice(colon + 1));
                }
            }
            
            return false;  
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

    public class StompFrame
    {
        public StompCommand MyProperty { get; set; }
    }
}
