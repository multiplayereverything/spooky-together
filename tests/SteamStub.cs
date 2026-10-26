namespace SpookyTogether
{
    // Stands in for src/SteamNet.cs in the offline transport test: Steamworks.NET needs the game's runtime, and these
    // tests cover the direct path only.
    public sealed partial class Transport
    {
        public bool OnSteam { get { return false; } }
        void PumpSteam() { }
        void SendSteam(int id, byte[] packet, bool close) { }
        void DropSteam(int id, string reason, bool linger) { }
        void DisposeSteam() { }
    }
}
