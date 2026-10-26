using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SpookyTogether;

// Transport checks on the direct (LAN) path: joins, a full game, bad frames. Run with scripts/test-transport.ps1.
// The Steam path needs the game and Steam: run-tour.ps1 -Steam covers it.
static class TransportTests
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
            Check(Wait(a, "packet", 3000).Data[0] == 4 && !host.OnSteam, "direct host reply");
        }
        FullAndBadFrameChecks();
        if (args.Length > 0) File.WriteAllText(args[0], "{\"passed\":" + passed + ",\"failed\":0,\"scope\":\"transport-direct\"}");
        Console.WriteLine("Passed " + passed + " checks.");
    }

    static void FullAndBadFrameChecks()
    {
        using (Transport host = new Transport())
        {
            host.Host(0, true);
            Transport[] clients = { new Transport(), new Transport(), new Transport(), new Transport() };
            try
            {
                for (int i = 0; i < 3; i++) { clients[i].Join("127.0.0.1", host.Port); Wait(clients[i], "connected", 3000); Wait(host, "connected", 3000); }
                // A fourth joiner is told why, instead of seeing a dropped connection.
                clients[3].Join("127.0.0.1", host.Port);
                NetworkEvent refusal = Wait(clients[3], "packet", 3000);
                Check(refusal.Data[0] == (byte)Op.Reject && Encoding.UTF8.GetString(refusal.Data, 3, refusal.Data.Length - 3).Contains("full"), "a full game refuses with a reason");
                Wait(clients[3], "disconnected", 3000);
            }
            finally { foreach (Transport t in clients) t.Dispose(); }
            // Hosting survives all of that: a new joiner still gets in once slots are free.
            NetworkEvent gone; int left = 0; DateTime end = DateTime.UtcNow.AddSeconds(3);
            while (left < 3 && DateTime.UtcNow < end) { while (host.Poll(out gone)) if (gone.Kind == "disconnected") left++; Thread.Sleep(5); }
            using (Transport late = new Transport())
            {
                late.Join("127.0.0.1", host.Port);
                Wait(late, "connected", 3000); Wait(host, "connected", 3000);
                late.Send(0, new byte[] { 9 });
                Check(Wait(host, "packet", 3000).Data[0] == 9, "hosting continues after a full game and three leaves");
            }
            // A bad frame ends the link, and the host is told why.
            using (TcpClient raw = new TcpClient("127.0.0.1", host.Port))
            {
                Wait(host, "connected", 3000);
                raw.GetStream().Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
                NetworkEvent bad = Wait(host, "disconnected", 3000);
                Check(bad.Message != null && bad.Message.Contains("Invalid frame length"), "a bad frame's disconnect says why: " + bad.Message);
            }
        }
    }
}
