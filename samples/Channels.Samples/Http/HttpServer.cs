﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Channels.Samples.Http
{
    public delegate Task RequestDelegate(HttpContext context);

    public class HttpContext
    {
        private static Vector<byte> _vectorCRs = new Vector<byte>((byte)'\r');
        private static Vector<byte> _vectorLFs = new Vector<byte>((byte)'\n');
        private static Vector<byte> _vectorColons = new Vector<byte>((byte)':');
        private static Vector<byte> _vectorSpaces = new Vector<byte>((byte)' ');
        private static Vector<byte> _vectorTabs = new Vector<byte>((byte)'\t');
        private static Vector<byte> _vectorQuestionMarks = new Vector<byte>((byte)'?');
        private static Vector<byte> _vectorPercentages = new Vector<byte>((byte)'%');

        private static readonly byte[] _bytesTransferEncodingChunked = Encoding.ASCII.GetBytes("\r\nTransfer-Encoding: chunked");

        public string Method { get; set; }
        public string Path { get; set; }
        public string HttpVersion { get; set; }

        public IDictionary<string, string> RequestHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IDictionary<string, string> ResponseHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int StatusCode { get; set; }

        public IReadableChannel Input { get; set; }

        public IWritableChannel Output { get; set; }

        // TODO: Check the http version
        internal bool KeepAlive => RequestHeaders.ContainsKey("Connection") && string.Equals(RequestHeaders["Connection"], "keep-alive");
        internal bool HasContentLength => ResponseHeaders.ContainsKey("Content-Length");

        internal RequestDelegate ProcessRequestAsync;

        internal List<byte[]> Body = new List<byte[]>();

        internal IWritableChannel Outgoing;

        internal async Task ProcessResponse(IReadableChannel incoming, IWritableChannel outgoing)
        {
            Outgoing = outgoing;

            while (true)
            {
                await incoming;

                var begin = incoming.BeginRead();

                if (begin.IsEnd && incoming.Completion.IsCompleted)
                {
                    break;
                }

                try
                {
                    BufferSpan span;
                    while (begin.TryGetBuffer(out span))
                    {
                        var data = new byte[span.Length];
                        Buffer.BlockCopy(span.Buffer.Array, span.Buffer.Offset, data, 0, span.Length);
                        Body.Add(data);
                    }
                }
                finally
                {
                    incoming.EndRead(begin);
                }
            }

            incoming.CompleteReading();
        }

        internal async Task FinishResponse()
        {
            ResponseHeaders["Server"] = "Channels HTTP Sample Server";
            ResponseHeaders["Content-Length"] = Body.Sum(b => b.Length).ToString();

            var buffer = Outgoing.BeginWrite();

            var httpVersion = Encoding.UTF8.GetBytes("HTTP/1.1 ");
            buffer.Write(httpVersion, 0, httpVersion.Length);
            var status = Encoding.UTF8.GetBytes("200 OK");
            buffer.Write(status, 0, status.Length);

            foreach (var header in ResponseHeaders)
            {
                var headerRaw = "\r\n" + header.Key + ':' + header.Value;
                var headerBytes = Encoding.UTF8.GetBytes(headerRaw);
                buffer.Write(headerBytes, 0, headerBytes.Length);
            }

            var crlf = Encoding.UTF8.GetBytes("\r\n\r\n");
            buffer.Write(crlf, 0, crlf.Length);

            foreach (var data in Body)
            {
                buffer.Write(data, 0, data.Length);
            }

            await Outgoing.EndWriteAsync(buffer);
        }

        public void Reset()
        {
            RequestHeaders.Clear();
            ResponseHeaders.Clear();
            Body.Clear();
        }

        internal async Task ProcessRequest()
        {
            Reset();

            while (true)
            {
                await Input;

                var begin = Input.BeginRead();
                var end = begin;
                bool needMoreData = true;

                try
                {

                    if (end.Seek(ref _vectorSpaces) == -1)
                    {
                        continue;
                    }

                    // Assume it'll be in a single block
                    BufferSpan span;
                    if (begin.TryGetBuffer(end, out span))
                    {
                        Method = Encoding.UTF8.GetString(span.Buffer.Array, span.Buffer.Offset, span.Length);
                    }

                    // Skip ' '
                    begin.Take();
                    end.Take();

                    if (end.Seek(ref _vectorSpaces) == -1)
                    {
                        continue;
                    }

                    if (begin.TryGetBuffer(end, out span))
                    {
                        Path = Encoding.UTF8.GetString(span.Buffer.Array, span.Buffer.Offset, span.Length);
                    }

                    begin.Take();
                    end.Take();

                    if (end.Seek(ref _vectorLFs) == -1)
                    {
                        continue;
                    }

                    if (begin.TryGetBuffer(end, out span))
                    {
                        HttpVersion = Encoding.UTF8.GetString(span.Buffer.Array, span.Buffer.Offset, span.Length).Trim();
                    }
                    // Skip '\n'
                    begin.Take();
                    end.Take();

                    // Parse headers
                    // key: value\r\n

                    while (!end.IsEnd)
                    {
                        var ch = end.Peek();

                        if (ch == -1)
                        {
                            break;
                        }

                        if (ch == '\r')
                        {
                            // Check for final CRLF.
                            end.Take();
                            ch = end.Take();

                            if (ch == -1)
                            {
                                break;
                            }
                            else if (ch == '\n')
                            {
                                needMoreData = false;
                                break;
                            }

                            // Headers don't end in CRLF line.
                        }

                        string headerName = null;
                        string headerValue = null;

                        // :
                        if (end.Seek(ref _vectorColons) == -1)
                        {
                            continue;
                        }

                        if (begin.TryGetBuffer(end, out span))
                        {
                            headerName = Encoding.UTF8.GetString(span.Buffer.Array, span.Buffer.Offset, span.Length);
                        }

                        begin.Take();
                        end.Take();

                        // \n
                        if (end.Seek(ref _vectorLFs) == -1)
                        {
                            continue;
                        }

                        if (begin.TryGetBuffer(end, out span))
                        {
                            headerValue = Encoding.UTF8.GetString(span.Buffer.Array, span.Buffer.Offset, span.Length).Trim();
                        }

                        RequestHeaders[headerName] = headerValue;
                        begin.Take();
                        end.Take();
                    }
                }
                finally
                {
                    Input.EndRead(end);
                }


                if (!needMoreData)
                {
                    await ProcessRequestAsync(this);
                    break;
                }
            }

            await FinishResponse();
        }
    }

    public class HttpServer
    {
        public static async void Listen(int port, Action<AppBuilder> configure)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Listen(10);

            using (var pool = new MemoryPool())
            {
                var app = new AppBuilder();
                configure(app);
                var callback = app.Build();

                while (true)
                {
                    var clientSocket = await socket.AcceptAsync();
                    var t = ProcessClient(pool, clientSocket, callback);
                }
            }
        }

        private static async Task ProcessClient(MemoryPool pool, Socket socket, RequestDelegate callback)
        {
            using (var ns = new NetworkStream(socket))
            {
                var channelFactory = new ChannelFactory(pool);
                var input = channelFactory.MakeReadableChannel(ns);
                var output = channelFactory.MakeWriteableChannel(ns);
                var context = new HttpContext();
                output = channelFactory.MakeWriteableChannel(output, DumpData);
                output = channelFactory.MakeWriteableChannel(output, context.ProcessResponse);
                input = channelFactory.MakeReadableChannel(input, DumpData);

                context.Input = input;
                context.Output = output;
                context.ProcessRequestAsync = callback;

                while (true)
                {
                    await context.ProcessRequest();

                    if (input.Completion.IsCompleted)
                    {
                        break;
                    }

                    if (!context.KeepAlive)
                    {
                        break;
                    }
                }
            }
        }

        private static async Task DumpData(IReadableChannel input, IWritableChannel output)
        {
            await input.CopyToAsync(output, span =>
            {
                Console.Write(Encoding.UTF8.GetString(span.Buffer.Array, span.Buffer.Offset, span.Length));
            });
        }
    }
}
