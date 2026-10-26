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
    public sealed partial class Transport : IDisposable
    {
        public const int MaxFrame = 65536;
        // Lab only: simulated one-way delay and jitter on every send, to test desync on slow links.
        public static int LagMs, JitterMs;   // lab-only
        readonly ConcurrentQueue<NetworkEvent> events = new ConcurrentQueue<NetworkEvent>();
        readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
        readonly object gate = new object();
        // Packets waiting for the main thread, per peer id (0 = the host on a client, 1-3 = clients on the host).
        readonly int[] pending = new int[4];
        // About 30 s of a client's busiest stream (poses, snapshots, pings): a long load must not cost the link.
        const int MaxPending = 2048;
        TcpListener listener;
        volatile bool disposed;
        public bool Hosting { get; private set; }
        public int Port { get; private set; }

        void Emit(int peer, string kind, byte[] data, string message)
        {
            if (disposed) return;
            // Only data packets count against the cap, and only the sender's own; dropping a "disconnected" event
            // would leave a ghost peer.
            if (kind == "packet" && peer >= 0 && peer < pending.Length && Interlocked.Increment(ref pending[peer]) > MaxPending)
            {
                Interlocked.Decrement(ref pending[peer]);
                Drop(peer, null, MaxPending + " packets were waiting for the game (it stalled)");
                return;
            }
            events.Enqueue(new NetworkEvent { Peer = peer, Kind = kind, Data = data, Message = message });
        }

        public bool Poll(out NetworkEvent e)
        {
            if (OnSteam && !disposed && events.IsEmpty) PumpSteam();
            if (!events.TryDequeue(out e)) return false;
            if (e.Kind == "packet" && e.Peer >= 0 && e.Peer < pending.Length) Interlocked.Decrement(ref pending[e.Peer]);
            return true;
        }

        // A full game tells the joiner why, instead of closing on it.
        static void RefuseFull(TcpClient client)
        {
            try
            {
                client.SendTimeout = 2000;
                byte[] packet = Wire.Packet(Op.Reject, w => Wire.Text(w, "The game is full (4 players)."));
                byte[] header = { (byte)packet.Length, (byte)(packet.Length >> 8), (byte)(packet.Length >> 16), (byte)(packet.Length >> 24) };
                NetworkStream stream = client.GetStream();
                stream.Write(header, 0, 4); stream.Write(packet, 0, packet.Length);
            }
            catch (Exception) { }   // the joiner is refused either way; the close below ends it
            finally { client.Close(); }
        }

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
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch (Exception ex)
                    {
                        if (disposed) break;
                        // One failed accept (a joiner that reset at once) must not end hosting for everyone else.
                        Emit(-1, "log", null, "Accept failed: " + ex.Message);
                        Thread.Sleep(200);
                        continue;
                    }
                    AcceptClient(client);
                }
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

        void AcceptClient(TcpClient client)
        {
            bool full = false;
            lock (gate)
            {
                int id = 1;
                while (links.ContainsKey(id)) id++;
                if (disposed) { client.Close(); return; }
                if (id > 3) full = true;
                else
                {
                    try { Attach(id, client); }
                    catch (Exception ex) { links.Remove(id); client.Close(); Emit(-1, "log", null, "A joiner's connection failed: " + ex.Message); }
                }
            }
            if (full) RefuseFull(client);
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
            if (OnSteam) { if (!disposed) SendSteam(id, packet, false); return; }
            lock (gate) { Link link; if (links.TryGetValue(id, out link)) link.Send(packet); }
        }

        public void Drop(int id)
        { Drop(id, null, null); }

        public void SendAndClose(int id, byte[] packet)
        {
            if (packet == null || packet.Length < 1 || packet.Length > MaxFrame) throw new ArgumentException("Invalid final packet size.");
            if (OnSteam) { if (!disposed) SendSteam(id, packet, true); return; }
            lock (gate) { Link link; if (links.TryGetValue(id, out link)) link.SendAndClose(packet); }
        }

        // reason: why the link ended, for the log (null for a normal close).
        void Drop(int id, Link expected, string reason)
        {
            if (OnSteam) { if (!disposed) DropSteam(id, reason, false); return; }
            lock (gate)
            {
                Link link;
                if (!links.TryGetValue(id, out link)) return;
                if (expected != null && link != expected) return;
                links.Remove(id);
                link.Close();
                Emit(id, "disconnected", null, reason);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (listener != null) listener.Stop();
                DisposeSteam();
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
                if (outgoing.Count >= 512) { owner.Drop(id, this, "512 packets could not be sent (the connection stalled)"); return; }
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
                string reason = null;
                try
                {
                    while (!closed)
                    {
                        byte[] header = ReadExactly(stream, 4);
                        int size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                        if (size < 1 || size > MaxFrame) throw new IOException("Invalid frame length.");
                        byte[] data = ReadExactly(stream, size);
                        // A packet read just before this link closed must not reach a new link given the same id.
                        if (!closed) owner.Emit(id, "packet", data, null);
                    }
                }
                catch (EndOfStreamException) { if (!closed) reason = "closed by the other side"; }
                catch (Exception ex) { if (!closed) reason = "receive failed: " + ex.GetType().Name + ": " + ex.Message; }
                finally { owner.Drop(id, this, reason); }
            }
            void Write()
            {
                string reason = null;
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
                catch (Exception ex) { if (!closed) reason = "send failed: " + ex.GetType().Name + ": " + ex.Message; }
                finally { owner.Drop(id, this, reason); wake.Dispose(); }
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
        public const int Version = 8;
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
