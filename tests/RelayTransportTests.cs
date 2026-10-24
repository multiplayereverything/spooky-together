using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SpookyTogether;

// Transport checks: the direct LAN path, then the global relay (mod-relay/relay.py, tunnel mode).
// Run with scripts/test-relay.ps1; the relay part needs MOD_RELAY (path to relay.py) and Python 3.
static class RelayTransportTests
{
    static int passed;
    static void Check(bool result, string name) { if (!result) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static NetworkEvent Wait(Transport transport, string kind, int timeout)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeout);
        NetworkEvent e;
        while (DateTime.UtcNow < end)
        { while (transport.Poll(out e)) if (e.Kind == kind) return e; Thread.Sleep(5); }
        throw new Exception("Timed out waiting for " + kind);
    }

    static void Main(string[] args)
    {
        using (Transport host = new Transport())
        using (Transport a = new Transport())
        {
            host.Host(0, true);
            a.Join("127.0.0.1", host.Port);
            Wait(a, "connected", 3000); int id = Wait(host, "connected", 3000).Peer;
            a.Send(0, new byte[] { 1, 2, 3 });
            Check(Wait(host, "packet", 3000).Data.SequenceEqual(new byte[] { 1, 2, 3 }), "direct LAN join still works");
            host.Send(id, new byte[] { 4 });
            Check(Wait(a, "packet", 3000).Data[0] == 4 && !host.Relayed, "direct host reply");
        }
        string relay = Environment.GetEnvironmentVariable("MOD_RELAY");
        if (!string.IsNullOrEmpty(relay) && File.Exists(relay)) RelayChecks(relay);
        else Console.WriteLine("SKIP global relay checks (set MOD_RELAY to relay.py)");
        if (args.Length > 0) File.WriteAllText(args[0], "{\"passed\":" + passed + ",\"failed\":0,\"scope\":\"transport-direct-and-relay\"}");
        Console.WriteLine("Passed " + passed + " checks.");
    }

    static void RelayChecks(string relayPath)
    {
        System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Process relay = Process.Start(new ProcessStartInfo("python", "-u \"" + relayPath + "\" --port " + port) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        StringBuilder relayLog = new StringBuilder();
        relay.OutputDataReceived += (sender, line) => { lock (relayLog) relayLog.AppendLine(line.Data); };
        relay.ErrorDataReceived += (sender, line) => { lock (relayLog) relayLog.AppendLine(line.Data); };
        relay.BeginOutputReadLine(); relay.BeginErrorReadLine();
        try
        {
            for (int i = 0; i < 100; i++) { try { using (new TcpClient("127.0.0.1", port)) break; } catch (SocketException) { Thread.Sleep(50); } }
            using (Transport host = new Transport())
            using (Transport a = new Transport())
            using (Transport b = new Transport())
            using (Transport c = new Transport())
            {
                host.HostRelay("127.0.0.1", port, "spooky", "test-1", true, "Lab room");
                string code = Wait(host, "room", 5000).Message;
                Check(code != null && code.Length == 5 && host.RoomCode == code && host.Hosting && host.Relayed, "relay host gets a room code");
                string[][] rooms = ModRelay.RelayLink.List("127.0.0.1", port, "spooky", 3000);
                Check(rooms.Any(r => r[0] == code && r[3] == "Lab room"), "public room listed for the game");
                Check(ModRelay.RelayLink.List("127.0.0.1", port, "baldi", 3000).Length == 0, "another game's list doesn't show it");
                a.JoinRelay("127.0.0.1", port, "spooky", "test-1", code.ToLowerInvariant(), "A");
                b.JoinRelay("127.0.0.1", port, "spooky", "test-1", code, "B");
                c.JoinRelay("127.0.0.1", port, "spooky", "test-1", code, "C");
                Wait(a, "connected", 5000); Wait(b, "connected", 5000); Wait(c, "connected", 5000);
                int[] ids = { Wait(host, "connected", 5000).Peer, Wait(host, "connected", 5000).Peer, Wait(host, "connected", 5000).Peer };
                Check(ids.Distinct().Count() == 3 && ids.All(i => i >= 1 && i <= 3), "three relay friends get peer IDs 1-3");
                byte[] payload = Enumerable.Range(0, Transport.MaxFrame).Select(i => (byte)(i % 253)).ToArray();
                a.Send(0, payload);
                NetworkEvent got = Wait(host, "packet", 5000);
                Check(payload.SequenceEqual(got.Data), "maximum-size frame through the relay");
                host.Send(got.Peer, new byte[] { 7 });
                Check(Wait(a, "packet", 5000).Data[0] == 7, "host reply reaches the right relay friend");
                using (Transport d = new Transport())
                {
                    d.JoinRelay("127.0.0.1", port, "spooky", "test-1", code, "D");
                    Check(Wait(d, "error", 5000).Message.Contains("full"), "relay room caps at host plus three friends");
                }
                using (Transport wrong = new Transport())
                {
                    wrong.JoinRelay("127.0.0.1", port, "baldi", "test-1", code, "W");
                    Check(Wait(wrong, "error", 5000).Message.Contains("another game"), "a Baldi client can't enter a Spooky room");
                }
                using (Transport old = new Transport())
                {
                    old.JoinRelay("127.0.0.1", port, "spooky", "test-0", code, "Old");
                    Check(Wait(old, "error", 5000).Message.Contains("Version mismatch"), "different mod version refused by the relay");
                }
                a.Dispose(); Wait(host, "disconnected", 5000);
                b.Send(0, new byte[] { 9 });
                Check(Wait(host, "packet", 5000).Data[0] == 9, "one relay friend leaving leaves the others working");
                host.Dispose(); Wait(b, "disconnected", 5000); Wait(c, "disconnected", 5000);
                Check(true, "relay host leaving disconnects its friends");
            }
            using (Transport host = new Transport())
            {
                host.HostRelay("127.0.0.1", 1, "spooky", "test-1", false, "x");
                Check(Wait(host, "relay-lost", 12000).Message.Length > 0, "unreachable relay reported to the host");
            }
        }
        catch (Exception) { lock (relayLog) Console.WriteLine("relay log:\n" + relayLog); throw; }
        finally { try { relay.Kill(); } catch (Exception) { } }
    }
}
