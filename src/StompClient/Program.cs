using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers;

namespace StompClient
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var c = new TcpClient())
            {
                c.Connect(new IPEndPoint(IPAddress.Loopback, 41493));

                var stream = c.GetStream();

                while (true)
                {
                    var command = Console.ReadLine() + "\r\n";

                    if (string.IsNullOrWhiteSpace(command))
                        break;
                    
                    stream.Write(Encoding.UTF8.GetBytes(command).AsSpan());
                    stream.Flush();
                }
            }
        }
    }
}
