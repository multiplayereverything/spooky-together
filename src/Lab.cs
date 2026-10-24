using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Profiling;

namespace SpookyTogether
{
    // Lab-only instrumentation, enabled by -st-lab. It writes a per-instance log with ST-CHECK and ST-METRIC lines,
    // which tests/verify-lab.py reads to judge pass/fail, error growth, memory growth and desync.
    public sealed partial class TogetherRuntime
    {
        public static bool LabMode;   // lab-only
        static StreamWriter labLog;   // lab-only
        static readonly object labLogGate = new object();   // lab-only
        static int errorCount, modErrorCount, unexpectedErrorCount;   // lab-only
        // Game errors that a single-player control run (no clients, no network) produces too, e.g. two Endless monsters
        // spawned outside the rooms they normally appear in, and hidden lab windows refusing a cursor lock.
        static readonly string[] knownGameErrors = { "EMKHMonster4Controller::UnpausedUpdate", "EMKHMonster6Controller::ActivateFromAccessory", "ClipCursor failed" };
        static string lastError = "";   // lab-only
        float nextMetric;
        float labJoinRetryAt;
        string labTour = "";
        readonly System.Diagnostics.Stopwatch labWatch = new System.Diagnostics.Stopwatch();
        long labModTicks; int labFrames; float labWindowStart;

        void InitLab(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-st-lab") LabMode = true;
                if (args[i] == "-st-tour" && i + 1 < args.Length) labTour = args[++i];
                if (args[i] == "-st-lag" && i + 1 < args.Length) { int lag; if (int.TryParse(args[++i], out lag)) { Transport.LagMs = Mathf.Clamp(lag, 0, 1000); Transport.JitterMs = Transport.LagMs / 2; } }
            }
            if (!LabMode) { Transport.LagMs = 0; Transport.JitterMs = 0; return; }
            try
            {
                string path = Path.Combine(Root, "spooky-together-lab.log");
                labLog = new StreamWriter(path, false) { AutoFlush = true };
                Action<string> previous = Log;
                Log = message => { previous(message); WriteLab(message); };
            }
            catch (Exception ex) { Log("Lab log unavailable: " + ex.Message); }
            Application.logMessageReceivedThreaded += CountErrors;
        }

        static void WriteLab(string line)
        {
            lock (labLogGate) { if (labLog != null) labLog.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line); }
        }

        static void CountErrors(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            int count = Interlocked.Increment(ref errorCount);
            bool known = false;
            foreach (string sig in knownGameErrors) if ((condition != null && condition.Contains(sig)) || (stackTrace != null && stackTrace.Contains(sig))) known = true;
            if (!known) Interlocked.Increment(ref unexpectedErrorCount);
            // The first 200 game errors are kept with their source line, so a failed run says what went wrong.
            if (count <= 200)
            {
                string at = "";
                if (stackTrace != null) foreach (string line in stackTrace.Split('\n')) if (line.Trim().Length > 0) { at = line.Trim(); break; }
                WriteLab("ST-ERROR " + (condition ?? "").Replace('\n', ' ') + " | " + at);
            }
            if ((stackTrace != null && stackTrace.Contains("SpookyTogether")) || (condition != null && condition.Contains("SpookyTogether")))
            {
                Interlocked.Increment(ref modErrorCount);
                WriteLab("ST-MODERROR " + condition + " | " + (stackTrace ?? "").Replace('\n', ' '));
            }
            lastError = condition ?? "";
        }

        public static void Check(string name, bool pass, string detail)
        {
            Log("ST-CHECK " + name + " " + (pass ? "PASS" : "FAIL") + " " + detail);
        }

        void LabUpdate()
        {
            if (!LabMode) return;
            float now = Time.realtimeSinceStartup;
            // Lab clients may start before the host listens; keep retrying instead of giving up.
            if (autoRole == "join" && !Active && !labJoinLan && now >= labJoinRetryAt) { labJoinRetryAt = now + 3; StartJoin(); }
            if (now >= nextMetric)
            {
                nextMetric = now + 10;
                long heap = GC.GetTotalMemory(false);
                long unity = Profiler.GetTotalAllocatedMemoryLong();
                GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
                int objects = all.Length, modObjects = 0;
                foreach (GameObject go in all) if (go.name.StartsWith("SpookyTogether", StringComparison.Ordinal)) modObjects++;
                int avatars = 0; foreach (Peer peer in peers.Values) if (peer.Avatar != null) avatars++;
                float window = Mathf.Max(0.001f, now - labWindowStart);
                double modMs = labFrames > 0 ? labModTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / labFrames : 0;
                float fps = labFrames / window;
                labModTicks = 0; labFrames = 0; labWindowStart = now;
                Log(string.Format("ST-METRIC t={0:0} role={1} seq={2} run={3} peers={4} avatars={5} heapMB={6:0.0} unityMB={7:0.0} objects={8} errors={9} moderrors={10} {11} modms={12:0.000} fps={13:0.0} modobjects={14} phase={15}",
                    now, Hosting ? "host" : "client", CurrentRoomSeq, runId, AcceptedCount, avatars, heap / 1048576.0, unity / 1048576.0, objects,
                    errorCount, modErrorCount, EntityMetric(), modMs, fps, modObjects, Closest<HSMKillscreenState>() != null ? "ending" : RunActive && CurrentRoomSeq >= 0 ? "room" : "menu"));
            }
            TourEndingWatch();
            TourUpdate();
        }
    }
}
