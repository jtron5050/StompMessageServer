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
            var connectionTask = connection.StartAsync();

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
        private readonly StompFrameProcessor _processor;
        private readonly Socket _socket;

        public StompConnection(Socket socket)
        {
            _socket = socket;
            _processor = new StompFrameProcessor();
        }

        public Task StartAsync()
        {
            return ProcessLinesAsync();
        }

        private async Task ProcessLinesAsync()
        {
            Console.WriteLine("Connected");
            var inputPipe = new Pipe();
            var outputPipe = new Pipe();

            var fillInputPipeTask = FillInputPipeAsync(_socket, inputPipe.Writer);
            var readInputPipeTask = ReadInputPipeAsync(_socket, inputPipe.Reader);

            var fillOutputPipeTask = FillOutputPipeAsync(_socket, outputPipe.Writer);
            var readOutputPipeTask = FeadOutputPipeAsync(_socket, outputPipe.Reader);
            
            await Task.WhenAll(fillInputPipeTask, readInputPipeTask);
            Console.WriteLine("Disconnected");
        }

        private Task FeadOutputPipeAsync(Socket socket, PipeReader reader)
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
            var processor = new StompFrameProcessor();
            var handler = new StompRequestHandler();

            while (true)
            {
                var readResult = await reader.ReadAsync();
                var buffer = readResult.Buffer;
                SequencePosition consumed, examined;

                processor.ProcessCommandLine(handler, buffer, out consumed, out examined);

                // advance reader first char after \n.  mark all of buffer examined.
                reader.AdvanceTo(consumed, examined);

                if (readResult.IsCompleted)
                {
                    break;
                }
            }

            reader.Complete();
        }


    }

    public class StompRequestHandler
    {
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
