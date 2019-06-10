﻿using Complete;
using RtmpSharp.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using RtmpSharp.Messaging;
using Fleck;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Security.Authentication;
using RtmpSharp.Controller;

namespace RtmpSharp.Net
{
    public class RtmpServer : IDisposable
    {
        public int ReceiveTimeout { get; set; } = 10000;
        public int SendTimeout { get; set; } = 10000;
        public int PingInterval { get; set; } = 10;
        public int PingTimeout { get; set; } = 10;
        public bool Started { get; private set; } = false;

        // TODO: add rtmps support

        Dictionary<string, ushort> _pathToPusherClientId = new Dictionary<string, ushort>();

        struct CrossClientConnection
        {
            public ushort PusherClientId { get; set; }
            public ushort PlayerClientId { get; set; }
            public ChannelType ChannelType { get; set; }
        }

        List<CrossClientConnection> _crossClientConnections = new List<CrossClientConnection>();
        Dictionary<string, Type> registeredApps = new Dictionary<string, Type>();

        internal IReadOnlyDictionary<string, Type> RegisteredApps
        {
            get
            {
                return registeredApps;
            }
        }
        List<ushort> allocated_stream_id = new List<ushort>();
        List<ushort> allocated_client_id = new List<ushort>();

        Random random = new Random();
        Socket listener = null;
        ManualResetEvent allDone = new ManualResetEvent(false);
        private SerializationContext context = null;
        private ObjectEncoding objectEncoding;
        private X509Certificate2 cert = null;
        private readonly int PROTOCOL_MIN_CSID = 3;
        private readonly int PROTOCOL_MAX_CSID = 65599;
        Dictionary<ushort, StreamConnectState> connects = new Dictionary<ushort, StreamConnectState>();
        List<KeyValuePair<ushort, StreamConnectState>> prepareToAdd = new List<KeyValuePair<ushort, StreamConnectState>>();
        List<ushort> prepare_to_remove = new List<ushort>();

        class StreamConnectState { public IStreamSession Connect; public DateTime LastPing; public Task ReaderTask; public Task WriterTask; }

        public RtmpServer(
            SerializationContext context,
            X509Certificate2 cert = null,
            ObjectEncoding object_encoding = ObjectEncoding.Amf0,
            string bindIp = "0.0.0.0",
            int bindRtmpPort = 1935,
            int bindWebsocketPort = -1
            )
        {
            this.context = context;
            objectEncoding = object_encoding;

            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.NoDelay = true;
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Parse(bindIp), bindRtmpPort);
            listener.Bind(localEndPoint);
            listener.Listen(10);
        }

        private void StartIOLoop(CancellationToken ct)
        {
            try
            {
                while (Started)
                {
                    foreach (var current in connects)
                    {
                        ct.ThrowIfCancellationRequested();
                        StreamConnectState state = current.Value;
                        ushort client_id = current.Key;
                        IStreamSession connect = state.Connect;
                        if (connect.IsDisconnected)
                        {
                            CloseClient(client_id);
                            continue;
                        }
                        try
                        {
                            if (state.WriterTask == null || state.WriterTask.IsCompleted)
                            {
                                state.WriterTask = connect.WriteOnceAsync(ct);
                            }
                            if (state.WriterTask.IsCanceled || state.WriterTask.IsFaulted)
                            {
                                throw state.WriterTask.Exception;
                            }
                            if (state.LastPing == null || DateTime.UtcNow - state.LastPing >= TimeSpan.FromSeconds(PingInterval))
                            {
                                connect.PingAsync(PingTimeout);
                                state.LastPing = DateTime.UtcNow;
                            }


                            if (state.ReaderTask == null || state.ReaderTask.IsCompleted)
                            {
                                state.ReaderTask = connect.StartReadAsync(ct);
                            }
                            if (state.ReaderTask.IsCanceled || state.ReaderTask.IsFaulted)
                            {
                                throw state.ReaderTask.Exception;
                            }

                        }
                        catch
                        {
                            CloseClient(client_id);
                            continue;
                        }
                    }
                    var prepare_add_length = prepareToAdd.Count;
                    if (prepare_add_length != 0)
                    {
                        for (int i = 0; i < prepare_add_length; i++)
                        {
                            var current = prepareToAdd[0];
                            connects.Add(current.Key, current.Value);
                            prepareToAdd.RemoveAt(0);
                        }
                    }

                    var prepareRemoveLength = prepare_to_remove.Count;
                    if (prepareRemoveLength != 0)
                    {
                        for (int i = 0; i < prepareRemoveLength; i++)
                        {
                            var current = prepare_to_remove[0];
                            connects.TryGetValue(current, out var connection);
                            connects.Remove(current);
                            prepare_to_remove.RemoveAt(0);

                        }
                    }
                }
            }
            catch
            {

            }
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            if (Started)
            {
                throw new InvalidOperationException("already started");
            }
            Started = true;
            var ioThread = new Thread(() => StartIOLoop(ct))
            {
                IsBackground = true
            };
            var ret = new TaskCompletionSource<int>();
            var t = new Thread(o =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            allDone.Reset();
                            Console.WriteLine("Waiting for a connection...");
                            listener.BeginAccept(new AsyncCallback(ar =>
                            {
                                _acceptCallback(ar, ct);
                            }), listener);
                            while (!allDone.WaitOne(1))
                            {
                                ct.ThrowIfCancellationRequested();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.ToString());
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _clearConnections();
                    ioThread.Join();
                    ret.SetResult(1);
                }
            });


            ioThread.Start();
            t.Start();
            return ret.Task;
        }

        private void _clearConnections()
        {
            Started = false;
            foreach (var current in connects)
            {
                StreamConnectState state = current.Value;
                ushort client_id = current.Key;
                IStreamSession connect = state.Connect;
                if (connect.IsDisconnected)
                {
                    continue;
                }
                try
                {
                    CloseClient(client_id);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.StackTrace);
                }
            }
            connects.Clear();
            allocated_client_id.Clear();
            allocated_stream_id.Clear();
        }

        async void _acceptCallback(IAsyncResult ar, CancellationToken ct)
        {
            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);
            handler.NoDelay = true;
            // Signal the main thread to continue.
            allDone.Set();
            try
            {
                await _handshakeAsync(handler, ct);
            }
            catch (TimeoutException)
            {
                handler.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine("{0} Message: {1}", e.GetType().ToString(), e.Message);
                Console.WriteLine(e.StackTrace);
                handler.Close();
            }
        }

        private ushort _getUniqueIdOfList(IList<ushort> list, int min_value, int max_value)
        {
            ushort id;
            do
            {
                id = (ushort)random.Next(min_value, max_value);
            } while (list.IndexOf(id) != -1);
            return id;
        }

        private ushort _getUniqueIdOfList(IList<ushort> list)
        {
            ushort id;
            do
            {
                id = (ushort)random.Next();
            } while (list.IndexOf(id) != -1);
            return id;
        }

        internal ushort RequestStreamId()
        {
            return _getUniqueIdOfList(allocated_stream_id, PROTOCOL_MIN_CSID, PROTOCOL_MAX_CSID);
        }

        private ushort _getNewClientId()
        {
            return _getUniqueIdOfList(allocated_client_id);
        }

        private async Task<int> _handshakeAsync(Socket clientSocket, CancellationToken ct)
        {
            Stream stream;
            if (cert != null)
            {
                var tempStream = new SslStream(new NetworkStream(clientSocket));
                try
                {
                    var op = new SslServerAuthenticationOptions();
                    op.ServerCertificate = cert;
                    await tempStream.AuthenticateAsServerAsync(op, ct);
                }
                finally
                {
                    tempStream.Close();
                }
                stream = tempStream;
            }
            else
            {
                stream = new NetworkStream(clientSocket);
            }
            var randomBytes = new byte[HandshakeRandomSize];
            random.NextBytes(randomBytes);
            clientSocket.NoDelay = true;
            var s0s1 = new Handshake()
            {
                Version = 3,
                Time = 0,
                Time2 = 0,
                Random = randomBytes
            };
            using (var cts = new CancellationTokenSource())
            {
                using (var newCt = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct))
                {
                    // read c0 + c1
                    var timer = new Timer((s) => { cts.Cancel(); }, null, ReceiveTimeout, Timeout.Infinite);
                    var c1 = await Handshake.ReadAsync(stream, true, newCt.Token);
                    timer.Change(Timeout.Infinite, Timeout.Infinite);

                    // write s0 + s1
                    timer.Change(SendTimeout, Timeout.Infinite);
                    await Handshake.WriteAsync(stream, s0s1, true, newCt.Token);
                    timer.Change(Timeout.Infinite, Timeout.Infinite);

                    // write s2
                    var s2 = c1;
                    timer.Change(SendTimeout, Timeout.Infinite);
                    await Handshake.WriteAsync(stream, s2, false, newCt.Token);
                    timer.Change(Timeout.Infinite, Timeout.Infinite);

                    // read c2
                    timer.Change(ReceiveTimeout, Timeout.Infinite);
                    var c2 = await Handshake.ReadAsync(stream, false, newCt.Token);
                    timer.Change(Timeout.Infinite, Timeout.Infinite);

                    // handshake check
                    if (!c2.Random.SequenceEqual(s0s1.Random))
                        throw new ProtocolViolationException();
                }
            }

            ushort clientId = _getNewClientId();
            var connect = new RtmpSession(clientSocket, stream, this, clientId, context, objectEncoding, true);
            connect.ChannelDataReceived += _sendDataHandler;

            prepareToAdd.Add(new KeyValuePair<ushort, StreamConnectState>(clientId, new StreamConnectState()
            {
                Connect = connect,
                LastPing = DateTime.UtcNow,
                ReaderTask = null,
                WriterTask = null
            }));

            return clientId;
        }

        public void RegisterController<T>() where T : AbstractController
        {
            lock (registeredApps)
            {
                var typeT = typeof(T);
                var controllerName = typeT.Name;
                if (controllerName.EndsWith("Controller"))
                {
                    controllerName = controllerName.Substring(0, controllerName.LastIndexOf("Controller"));
                }

                if (registeredApps.ContainsKey(controllerName)) throw new InvalidOperationException("controller exists");
                registeredApps.Add(controllerName, typeT);
            }
        }

        private void _sendDataHandler(object sender, ChannelDataReceivedEventArgs e)
        {
            var server = (RtmpSession)sender;

            var server_clients = _crossClientConnections.FindAll((t) => t.PusherClientId == server.ClientId);
            foreach (var i in server_clients)
            {
                IStreamSession client;
                StreamConnectState client_state = null;
                if (e.type != i.ChannelType)
                {
                    continue;
                }
                connects.TryGetValue(i.PlayerClientId, out client_state);

                switch (i.ChannelType)
                {
                    case ChannelType.Video:
                    case ChannelType.Audio:
                        if (client_state == null) continue;
                        client = client_state.Connect;
                        client.SendAmf0DataAsync(e.e);
                        break;
                    case ChannelType.Message:
                        throw new NotImplementedException();
                }

            }
        }

        internal void ConnectToClient(string app, string path, ushort playerClientId, ChannelType channelType)
        {
            StreamConnectState state;
            ushort pusherClientId;
            if (!_pathToPusherClientId.TryGetValue(path, out pusherClientId)) throw new KeyNotFoundException("Request Path Not Found");
            if (!connects.TryGetValue(pusherClientId, out state))
            {
                IStreamSession connect = state.Connect;
                _pathToPusherClientId.Remove(path);
                throw new KeyNotFoundException("Request Client Not Exists");
            }

            _crossClientConnections.Add(new CrossClientConnection()
            {
                PlayerClientId = playerClientId,
                PusherClientId = pusherClientId,
                ChannelType = channelType
            });
        }

        internal bool AuthApp(string app)
        {
            return registeredApps.ContainsKey(app);
        }

        public void Dispose()
        {
            try
            {
                if (Started)
                {
                    _clearConnections();
                    listener.Close();
                }
            }
            catch { }
        }

        #region handshake

        const int HandshakeRandomSize = 1528;

        // size for c0, c1, s1, s2 packets. c0 and s0 are 1 byte each.
        const int HandshakeSize = HandshakeRandomSize + 4 + 4;

        public struct Handshake
        {
            // C0/S0 only
            public byte Version;

            // C1/S1/C2/S2
            public uint Time;
            // in C1/S1, MUST be zero. in C2/S2, time at which C1/S1 was read.
            public uint Time2;
            public byte[] Random;
            public static async Task<Handshake> ReadAsync(Stream stream, bool readVersion, CancellationToken cancellationToken)
            {
                var size = HandshakeSize + (readVersion ? 1 : 0);
                var buffer = await StreamHelper.ReadBytesAsync(stream, size, cancellationToken);

                using (var reader = new AmfReader(new MemoryStream(buffer), null))
                {
                    return new Handshake()
                    {
                        Version = readVersion ? reader.ReadByte() : default,
                        Time = reader.ReadUInt32(),
                        Time2 = reader.ReadUInt32(),
                        Random = reader.ReadBytes(HandshakeRandomSize)
                    };
                }
            }

            public static Task WriteAsync(Stream stream, Handshake h, bool writeVersion, CancellationToken ct)
            {
                using (var memoryStream = new MemoryStream())
                using (var writer = new AmfWriter(memoryStream, null))
                {
                    if (writeVersion)
                        writer.WriteByte(h.Version);

                    writer.WriteUInt32(h.Time);
                    writer.WriteUInt32(h.Time2);
                    writer.WriteBytes(h.Random);

                    var buffer = memoryStream.ToArray();
                    return stream.WriteAsync(buffer, 0, buffer.Length, ct);
                }
            }
        }



        #endregion
    }
}