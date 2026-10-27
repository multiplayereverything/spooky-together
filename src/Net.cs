using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpookyTogether
{
    // Star topology: clients talk only to the host (peer id 0), which relays poses and status to everyone else.
    public sealed partial class TogetherRuntime
    {
        void Broadcast(byte[] packet) { Broadcast(packet, -1); }

        void Broadcast(byte[] packet, int except)
        {
            if (Network == null) return;
            if (!Hosting) { if (self > 0) Network.Send(0, packet); return; }
            foreach (Peer peer in peers.Values) if (peer.Accepted && peer.Id != except) Network.Send(peer.Id, packet);
        }

        void SendToHost(byte[] packet) { if (IsClient && self > 0) Network.Send(0, packet); }

        // A joiner that connects but never says Hello (a stuck or older client) gives its slot back after 15 s.
        void DropSilentJoiners()
        {
            if (!Hosting) return;
            float now = Time.realtimeSinceStartup;
            scratchPeers.Clear();
            foreach (Peer peer in peers.Values) if (!peer.Accepted && now - peer.LastHeard > 15) scratchPeers.Add(peer.Id);
            foreach (int id in scratchPeers) { Log("Dropping peer " + id + ": no Hello in 15 s"); Network.Drop(id); }
        }
        static readonly List<int> scratchPeers = new List<int>();   // process-scoped: scratch buffer

        void PumpNetwork()
        {
            NetworkEvent e;
            int budget = 256;
            while (Network != null && budget-- > 0 && Network.Poll(out e))
            {
                try { HandleEvent(e); }
                catch (IOException ex) { Log("Malformed packet from " + e.Peer + ": " + ex.Message); if (Hosting && e.Peer > 0) Network.Drop(e.Peer); }
                // A game-side failure while applying one update must not cost anyone their connection.
                catch (Exception ex) { Log("Could not apply a packet from " + e.Peer + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        void HandleEvent(NetworkEvent e)
        {
            if (e.Kind == "log") { Log(e.Message); return; }
            if (e.Kind == "error")
            {
                Tell("Network error: " + e.Message);
                if (IsClient && self <= 0) Shutdown();
                return;
            }
            if (e.Kind == "connected")
            {
                if (Hosting) peers[e.Peer] = new Peer { Id = e.Peer, LastHeard = Time.realtimeSinceStartup };
                else SendHello();
                return;
            }
            if (e.Kind == "disconnected")
            {
                if (e.Message != null) Log("Link " + e.Peer + " ended: " + e.Message);
                if (!Hosting) { Shutdown(); Tell("Lost connection to the host. You keep playing alone."); return; }
                Peer gone;
                if (peers.TryGetValue(e.Peer, out gone))
                {
                    DestroyAvatar(gone); peers.Remove(e.Peer);
                    if (gone.Accepted) { Tell(gone.Name + " left."); SendRoster(); CheckAllDowned(); }
                }
                return;
            }
            using (BinaryReader reader = new BinaryReader(new MemoryStream(e.Data)))
            {
                Op op = (Op)reader.ReadByte();
                if (Hosting) HostPacket(e.Peer, op, reader); else ClientPacket(op, reader);
            }
        }

        public void SendHello()
        {
            SendToHostRaw(Wire.Packet(Op.Hello, w => { w.Write(Wire.Version); Wire.Text(w, ModVersion); Wire.Text(w, gameHash); Wire.Text(w, Name); w.Write(Colour); }));
        }

        void SendToHostRaw(byte[] packet) { if (IsClient) Network.Send(0, packet); }

        void HostPacket(int from, Op op, BinaryReader reader)
        {
            Peer peer;
            if (!peers.TryGetValue(from, out peer)) return;
            peer.LastHeard = Time.realtimeSinceStartup;
            if (op == Op.Hello)
            {
                int version = reader.ReadInt32();
                string mod = Wire.Text(reader, 32), hash = Wire.Text(reader, 80), name = CleanName(Wire.Text(reader, 32));
                byte colour = reader.ReadByte();
                if (version != Wire.Version || mod != ModVersion) { Reject(from, "Version mismatch: host runs Spooky Together " + ModVersion + "."); return; }
                if (hash != gameHash) { Reject(from, "Your game files differ from the host's. Update both copies of the game."); return; }
                bool first = !peer.Accepted;
                peer.Name = name; peer.Colour = (byte)Mathf.Clamp(colour, 0, Colours.Length - 1); peer.Accepted = true;
                if (first)
                {
                    Network.Send(from, Wire.Packet(Op.Welcome, w => w.Write(from)));
                    Tell(name + " joined.");
                    lastJoinAt = Time.realtimeSinceStartup;
                    SendRunTo(from);
                }
                SendRoster();
                return;
            }
            if (!peer.Accepted) return;
            switch (op)
            {
                case Op.Pose:
                    ReadPose(peer, reader);
                    Broadcast(PosePacket(peer.Id, peer.RoomSeq, peer.Position, peer.Yaw, peer.Pitch, peer.Crawling, peer.Downed), from);
                    break;
                case Op.Status:
                    SetDowned(peer, reader.ReadBoolean());
                    break;
                case Op.Door:
                    HostDoorRequest(peer, reader);
                    break;
                case Op.StateKey: ApplyKey(reader, from); break;
                case Op.StateItem: ApplyItem(reader, from); break;
                case Op.ObjectUsed: ApplyObjectUsed(reader, from); break;
                case Op.Hit: ApplyHit(reader, from); break;
                case Op.Weapon: ApplyWeapon(reader, from); break;
                case Op.Swing: ApplySwing(reader, from); break;
                case Op.Ending: HostEndingRequest(reader); break;
                case Op.BossReflect: ApplyBossReflect(from); break;
                case Op.CutoutRequest: HostCutoutRequest(reader); break;
                case Op.StateRequest: if (RunActive) Network.Send(from, FullStatePacket()); break;
                case Op.Ping: Network.Send(from, Wire.Packet(Op.Pong, null)); break;
                case Op.Pong: NotePong(from); break;
                case Op.Leave: Network.Drop(from); break;
            }
        }

        void Reject(int id, string reason)
        {
            Network.SendAndClose(id, Wire.Packet(Op.Reject, w => Wire.Text(w, reason)));
            Log("Rejected peer " + id + ": " + reason);
        }

        void SendRoster()
        {
            if (!Hosting) return;
            List<Peer> list = new List<Peer>();
            foreach (Peer p in peers.Values) if (p.Accepted) list.Add(p);
            Broadcast(Wire.Packet(Op.Roster, w =>
            {
                w.Write((byte)(list.Count + 1));
                w.Write(0); Wire.Text(w, Name); w.Write(Colour); w.Write(LocallyDowned);
                foreach (Peer p in list) { w.Write(p.Id); Wire.Text(w, p.Name); w.Write(p.Colour); w.Write(p.Downed); }
            }));
        }

        void ClientPacket(Op op, BinaryReader reader)
        {
            switch (op)
            {
                case Op.Welcome:
                    self = reader.ReadInt32();
                    Tell("Connected to the host as player " + self + ". Wait for the host to start a run.");
                    break;
                case Op.Reject:
                    string reason = Wire.Text(reader, 400);
                    Shutdown(); Tell("Host refused: " + reason);
                    break;
                case Op.Roster:
                    int count = reader.ReadByte();
                    HashSet<int> seen = new HashSet<int>();
                    for (int i = 0; i < count; i++)
                    {
                        int id = reader.ReadInt32(); string name = Wire.Text(reader, 32); byte colour = reader.ReadByte(); bool downed = reader.ReadBoolean();
                        if (id == self) continue;
                        seen.Add(id);
                        Peer p; if (!peers.TryGetValue(id, out p)) { p = new Peer { Id = id }; peers[id] = p; Tell(name + " is here."); }
                        p.Name = name; p.Colour = (byte)Mathf.Clamp(colour, 0, Colours.Length - 1); p.Accepted = true; p.Downed = downed;
                        if (p.Avatar != null) p.Avatar.SetColour(Colours[p.Colour]);
                    }
                    List<int> stale = new List<int>();
                    foreach (int id in peers.Keys) if (!seen.Contains(id)) stale.Add(id);
                    foreach (int id in stale) { Tell(peers[id].Name + " left."); DestroyAvatar(peers[id]); peers.Remove(id); }
                    break;
                case Op.Pose:
                    int who = reader.ReadInt32();
                    Peer target;
                    if (who != self && peers.TryGetValue(who, out target)) ReadPose(target, reader);
                    break;
                case Op.Status:
                    int sid = reader.ReadInt32(); bool sdown = reader.ReadBoolean();
                    Peer sp; if (peers.TryGetValue(sid, out sp)) { if (sdown && !sp.Downed) Tell(sp.Name + " is down! They respawn in the next room."); sp.Downed = sdown; }
                    break;
                case Op.StartRun: ClientStartRun(reader); break;
                case Op.Room: ClientRoom(reader); break;
                case Op.GameOver: ClientGameOver(); break;
                case Op.HurtPeer: ClientHurt(reader); break;
                case Op.SpecSpawn: ClientSpawn(reader); break;
                case Op.SpecDespawn: ClientDespawn(reader); break;
                case Op.SpecSnapshot: ClientSnapshot(reader); break;
                case Op.SpecBind: ClientBind(reader); break;
                case Op.StateKey: ApplyKey(reader, 0); break;
                case Op.StateItem: ApplyItem(reader, 0); break;
                case Op.StateFull: ClientFullState(reader); break;
                case Op.StateHash: ClientStateHash(reader); break;
                case Op.ObjectUsed: ApplyObjectUsed(reader, 0); break;
                case Op.Hit: ApplyHit(reader, 0); break;
                case Op.Weapon: ApplyWeapon(reader, 0); break;
                case Op.Swing: ApplySwing(reader, 0); break;
                case Op.Ending: ClientEnding(Wire.Text(reader, 200)); break;
                case Op.BossReflect: ApplyBossReflect(0); break;
                case Op.SpecSound: ClientSound(reader); break;
                case Op.CutoutState: ClientCutoutState(reader); break;
                case Op.RoomLook: ClientLook(reader); break;
                case Op.EndRun: ClientEndRun(); break;
                case Op.Notice: Tell(Wire.Text(reader, 400)); break;
                case Op.Ping: SendToHost(Wire.Packet(Op.Pong, null)); break;
                case Op.Pong: NotePong(0); break;
            }
        }

        // --- Poses ------------------------------------------------------------------------------
        static byte[] PosePacket(int id, int seq, Vector3 pos, float yaw, float pitch, bool crawling, bool downed)
        {
            return Wire.Packet(Op.Pose, w =>
            {
                w.Write(id); w.Write(seq);
                w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(yaw); w.Write(pitch);
                w.Write((byte)((crawling ? 1 : 0) | (downed ? 2 : 0)));
            });
        }

        void ReadPose(Peer peer, BinaryReader reader)
        {
            if (Hosting) reader.ReadInt32();
            peer.RoomSeq = reader.ReadInt32();
            Vector3 pos = new Vector3(Wire.Number(reader), Wire.Number(reader), Wire.Number(reader));
            float yaw = Wire.Number(reader), pitch = Wire.Number(reader);
            byte flags = reader.ReadByte();
            if (!peer.HasPose || (pos - peer.Position).sqrMagnitude > 100f) { peer.ShownPosition = pos; peer.ShownYaw = yaw; }
            peer.Position = pos; peer.Yaw = yaw; peer.Pitch = pitch;
            peer.Crawling = (flags & 1) != 0;
            if (!Hosting) peer.Downed = (flags & 2) != 0;
            peer.HasPose = true; peer.PoseAt = Time.realtimeSinceStartup;
        }

        void SendPose()
        {
            SpookyCharacterController player = SpookyCharacterController.GetInstance();
            bool inRoom = player != null && player.ready && RunActive;
            int seq = inRoom ? CurrentRoomSeq : -1;
            Vector3 pos = player != null ? player.transform.position : Vector3.zero;
            if (LocallyDowned) pos = downedBody;
            float yaw = player != null ? player.transform.eulerAngles.y : 0;
            float pitch = player != null && player.myCamera != null ? player.myCamera.transform.localEulerAngles.x : 0;
            bool crawling = player != null && player.isCrawling;
            Broadcast(PosePacket(SelfId, seq, pos, yaw, pitch, crawling, LocallyDowned));
        }

        public void Notice(string text)
        {
            Tell(text);
            if (Hosting) Broadcast(Wire.Packet(Op.Notice, w => Wire.Text(w, text)));
        }
    }
}
