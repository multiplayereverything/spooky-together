using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SpookyTogether
{
    public sealed class NetworkEvent
    {
        public int Peer;
        public string Kind;
        public byte[] Data;
        public string Message;
    }

    // No Unity calls are permitted on the socket threads.
    public sealed class Transport : IDisposable
    {
        public const int MaxFrame = 65536;
        // Lab only: simulated one-way delay and jitter on every send, to test desync on slow links.
        public static int LagMs, JitterMs;
        readonly ConcurrentQueue<NetworkEvent> events = new ConcurrentQueue<NetworkEvent>();
        readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
        readonly object gate = new object();
        TcpListener listener;
        ModRelay.RelayHost relayRoom;
        volatile bool disposed;
        public bool Hosting { get; private set; }
        public int Port { get; private set; }
        // Online (global relay, mod-relay/relay.py): friends arrive through the relay's tunnel.
        public bool Relayed { get; private set; }
        public string RoomCode { get { ModRelay.RelayHost room = relayRoom; return room == null ? null : room.Code; } }

        void Emit(int peer, string kind, byte[] data, string message)
        {
            if (disposed) return;
            // Only data packets count against the cap; dropping a "disconnected" event would leave a ghost peer.
            if (kind == "packet" && events.Count >= 512) { Drop(peer); return; }
            events.Enqueue(new NetworkEvent { Peer = peer, Kind = kind, Data = data, Message = message });
        }

        public bool Poll(out NetworkEvent e) { return events.TryDequeue(out e); }

        public void Host(int port, bool loopback)
        {
            if (listener != null || disposed) throw new InvalidOperationException("Transport already used.");
            Hosting = true;
            listener = new TcpListener(loopback ? IPAddress.Loopback : IPAddress.Any, port);
            listener.Start(4);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            StartThread(delegate
            {
                while (!disposed)
                {
                    try
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (gate)
                        {
                            int id = 1;
                            while (links.ContainsKey(id)) id++;
                            if (disposed || id > 3) { client.Close(); continue; }
                            Attach(id, client);
                        }
                    }
                    catch (Exception ex) { if (!disposed) Emit(-1, "error", null, ex.Message); break; }
                }
            });
        }

        // Online host: like Host(), but friends are tunnelled connections from the global relay instead of
        // a TcpListener (no port forwarding). Emits "room" (Message = room code) once the room exists;
        // "relay-lost" (Message = reason) if it can't be opened or the relay connection ends.
        public void HostRelay(string relayHost, int relayPort, string game, string version, bool listed, string roomName)
        {
            if (listener != null || Hosting || disposed) throw new InvalidOperationException("Transport already used.");
            Hosting = true; Relayed = true;
            StartThread(delegate
            {
                try
                {
                    ModRelay.RelayHost room = new ModRelay.RelayHost(relayHost, relayPort, game, version, listed, roomName, 8000);
                    lock (gate) { if (disposed) { room.Dispose(); return; } relayRoom = room; }
                    Emit(-1, "room", null, room.Code);
                    room.Start(delegate(TcpClient client)
                    {
                        lock (gate)
                        {
                            int id = 1;
                            while (links.ContainsKey(id)) id++;
                            if (disposed || id > 3) { client.Close(); return; }
                            Attach(id, client);
                        }
                    }, delegate(string reason) { Emit(-1, "relay-lost", null, reason); });
                }
                catch (Exception ex) { Emit(-1, "relay-lost", null, ex.Message); }
            });
        }

        // Online friend: connect through the global relay with the host's room code. Events are the same as Join().
        public void JoinRelay(string relayHost, int relayPort, string game, string version, string code, string name)
        {
            if (disposed) throw new ObjectDisposedException("Transport");
            Relayed = true;
            StartThread(delegate
            {
                try
                {
                    TcpClient client = ModRelay.RelayLink.Join(relayHost, relayPort, game, version, code, name, 8000);
                    lock (gate)
                    {
                        if (disposed) { client.Close(); return; }
                        Attach(0, client);
                    }
                }
                catch (Exception ex) { Emit(0, "error", null, ex.Message); }
            });
        }

        public void Join(string address, int port)
        {
            if (disposed) throw new ObjectDisposedException("Transport");
            StartThread(delegate
            {
                TcpClient client = new TcpClient();
                try
                {
                    IAsyncResult attempt = client.BeginConnect(address, port, null, null);
                    using (attempt.AsyncWaitHandle)
                    {
                        if (!attempt.AsyncWaitHandle.WaitOne(5000)) throw new IOException("Connection timed out.");
                        client.EndConnect(attempt);
                    }
                    lock (gate)
                    {
                        if (disposed) { client.Close(); return; }
                        Attach(0, client);
                    }
                }
                catch (Exception ex) { client.Close(); Emit(0, "error", null, ex.Message); }
            });
        }

        void Attach(int id, TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 5000;
            Link link = new Link(this, id, client);
            links.Add(id, link);
            Emit(id, "connected", null, null);
            link.Start();
        }

        public void Send(int id, byte[] packet)
        {
            if (packet == null || packet.Length == 0 || packet.Length > MaxFrame) throw new ArgumentException("Invalid packet size.");
            lock (gate) { Link link; if (links.TryGetValue(id, out link)) link.Send(packet); }
        }

        public void Drop(int id)
        { Drop(id, null); }

        public void SendAndClose(int id, byte[] packet)
        {
            if (packet == null || packet.Length < 1 || packet.Length > MaxFrame) throw new ArgumentException("Invalid final packet size.");
            lock (gate) { Link link; if (links.TryGetValue(id, out link)) link.SendAndClose(packet); }
        }

        void Drop(int id, Link expected)
        {
            lock (gate)
            {
                Link link;
                if (!links.TryGetValue(id, out link)) return;
                if (expected != null && link != expected) return;
                links.Remove(id);
                link.Close();
                Emit(id, "disconnected", null, null);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (listener != null) listener.Stop();
                if (relayRoom != null) relayRoom.Dispose();
                foreach (Link link in links.Values) link.Close();
                links.Clear();
                NetworkEvent unused; while (events.TryDequeue(out unused)) { }
            }
        }

        static void StartThread(ThreadStart action) { new Thread(action) { IsBackground = true, Name = "SpookyTogether network" }.Start(); }

        sealed class Link
        {
            readonly Transport owner;
            readonly int id;
            readonly TcpClient client;
            readonly NetworkStream stream;
            readonly ConcurrentQueue<byte[]> outgoing = new ConcurrentQueue<byte[]>();
            readonly ConcurrentQueue<long> due = new ConcurrentQueue<long>();
            readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            readonly Random jitter = new Random();
            long lastDue;
            readonly AutoResetEvent wake = new AutoResetEvent(false);
            volatile bool closed;
            volatile bool closeAfterSend;
            public Link(Transport owner, int id, TcpClient client)
            { this.owner = owner; this.id = id; this.client = client; stream = client.GetStream(); }
            public void Start() { StartThread(Read); StartThread(Write); }
            public void Close() { closed = true; client.Close(); wake.Set(); }
            public void Send(byte[] packet)
            {
                if (closed || closeAfterSend) return;
                // A link that cannot keep up for several seconds is dropped rather than buffering without limit.
                if (outgoing.Count >= 512) { owner.Drop(id); return; }
                if (LagMs > 0)
                {
                    lock (due)
                    {
                        long when = clock.ElapsedMilliseconds + LagMs + (JitterMs > 0 ? jitter.Next(JitterMs) : 0);
                        lastDue = Math.Max(lastDue, when);
                        due.Enqueue(lastDue); outgoing.Enqueue(packet);
                    }
                }
                else outgoing.Enqueue(packet);
                wake.Set();
            }
            public void SendAndClose(byte[] packet) { Send(packet); closeAfterSend = true; wake.Set(); }
            void Read()
            {
                try
                {
                    while (!closed)
                    {
                        byte[] header = ReadExactly(stream, 4);
                        int size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                        if (size < 1 || size > MaxFrame) throw new IOException("Invalid frame length.");
                        owner.Emit(id, "packet", ReadExactly(stream, size), null);
                    }
                }
                catch (Exception) { }
                finally { owner.Drop(id, this); }
            }
            void Write()
            {
                try
                {
                    while (!closed)
                    {
                        byte[] packet;
                        if (LagMs > 0)
                        {
                            long when;
                            if (!due.TryPeek(out when)) { if (closeAfterSend) break; wake.WaitOne(50); continue; }
                            long wait = when - clock.ElapsedMilliseconds;
                            if (wait > 0) { wake.WaitOne((int)Math.Min(wait, 50)); continue; }
                            due.TryDequeue(out when);
                        }
                        if (!outgoing.TryDequeue(out packet)) { if (closeAfterSend) break; wake.WaitOne(1000); continue; }
                        int size = packet.Length;
                        byte[] header = { (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24) };
                        stream.Write(header, 0, 4); stream.Write(packet, 0, size);
                    }
                }
                catch (Exception) { }
                finally { owner.Drop(id, this); wake.Dispose(); }
            }
        }

        public static byte[] ReadExactly(Stream stream, int size)
        {
            byte[] data = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int read = stream.Read(data, offset, size - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return data;
        }
    }

    public enum Op : byte { Hello = 1, Welcome, Roster, Reject, Leave, Pose, StartRun, Room, Status, Ping, Pong, Notice, Door, GameOver, EndRun, HurtPeer, SpecSpawn, SpecDespawn, SpecSnapshot, Hit, Swing, Weapon, Ending, SpecBind, StateKey, StateItem, StateFull, StateHash, StateRequest, ObjectUsed, BossReflect, SpecSound, CutoutRequest, CutoutState, RoomLook }

    public static class Wire
    {
        public const int Version = 7;
        public static byte[] Packet(Op op, Action<BinaryWriter> write)
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write((byte)op); if (write != null) write(writer);
                if (memory.Length > Transport.MaxFrame) throw new IOException("Packet too large.");
                return memory.ToArray();
            }
        }
        public static string Text(BinaryReader reader, int max)
        {
            // Explicit byte lengths avoid BinaryReader.ReadString allocating from a peer supplied prefix.
            int count = reader.ReadUInt16();
            if (count > max * 4 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new IOException("Invalid text length.");
            string value = System.Text.Encoding.UTF8.GetString(Transport.ReadExactly(reader.BaseStream, count));
            if (value.Length > max) throw new IOException("Text too long.");
            return value;
        }
        public static void Text(BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > 4096) throw new IOException("Text too long.");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        public static float Number(BinaryReader reader)
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 100000) throw new IOException("Invalid coordinate.");
            return value;
        }
    }
}
