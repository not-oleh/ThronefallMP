using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ThronefallMP;

/// Shared match state, carried by reliable messages. The host owns it:
///  - Day/night: the host's DayNightCycle decides; the client's own cycle is frozen (see
///    TimePatch) and switched to whatever the host says.
///  - Treasury (coins + Craaghelm energy cores): the host's balance is the truth and is pushed
///    on every change. The client forwards its own gains and spends as deltas, so both players'
///    actions land in the one balance.
///  - Match end: the host's victory/defeat is pushed once; the client ends the same match.
/// All are level-based ("latest wins"), so a message that arrives before the client is in the
/// right scene is kept and applied once it is.
class MatchSync
{
    const byte MsgTime = 1, MsgBalance = 2, MsgDelta = 3, MsgEnd = 4;
    static readonly AccessTools.FieldRef<PlayerInteraction, int> BalanceField =
        AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");
    static readonly AccessTools.FieldRef<PlayerInteraction, int> CoresField =
        AccessTools.FieldRefAccess<PlayerInteraction, int>("energyCoreBalance");

    readonly Plugin p;
    readonly Link link;

    // Host: what the peer was last told.
    string sentTimeScene, sentBalanceScene, sentEndScene;
    int sentTime = -1, sentCoins, sentCores;

    // Client: latest host state, applied when scene and match allow.
    string timeScene, balanceScene, endScene;
    int hostEnd;
    int hostTime = -1, hostCoins, hostCores;
    bool balanceFresh;
    PlayerInteraction balanceAppliedTo;

    // Client: local treasury changes not yet reported to the host.
    int pendingCoins, pendingCores;

    float lastLog;

    public MatchSync(Plugin plugin, Link link)
    {
        p = plugin;
        this.link = link;
        link.PeerRestarted += ResendAll;
    }

    internal void ResendAll() { sentTimeScene = sentBalanceScene = sentEndScene = null; }

    /// Client only: while the host is in our scene, our own day/night cycle must not advance.
    internal bool FrozenByHost(string scene) => p.role == "client" && p.PeerAlive && p.Players.Remote.Scene == scene;

    /// Local treasury change on this instance (PlayerInteraction patches).
    internal void LocalChange(int coins, int cores)
    {
        // Before the host's balance is adopted (level start grants), our number is meaningless.
        if (p.role == "client" && PlayerInteraction.instance == balanceAppliedTo) { pendingCoins += coins; pendingCores += cores; }
    }

    internal void Tick(PlayerMovement me)
    {
        if (me == null) return;
        if (p.role == "host") HostSend(me); else ClientApply(me);
    }

    void HostSend(PlayerMovement me)
    {
        if (!p.PeerAlive) return;
        string scene = me.gameObject.scene.name;

        var cycle = DayNightCycle.Instance;
        if (cycle != null && (scene != sentTimeScene || (int)cycle.CurrentTimestate != sentTime))
        {
            sentTimeScene = scene;
            sentTime = (int)cycle.CurrentTimestate;
            link.SendMessage(Write(w => { w.Write(MsgTime); w.Write(scene); w.Write((byte)sentTime); }));
            p.Log($"Match: time {cycle.CurrentTimestate} in {scene} sent to peer");
        }

        var gs = LocalGamestate.Instance;
        if (gs != null && !IsEnd(gs.CurrentState)) sentEndScene = null; // a new match
        if (gs != null && IsEnd(gs.CurrentState) && scene != sentEndScene)
        {
            sentEndScene = scene;
            link.SendMessage(Write(w => { w.Write(MsgEnd); w.Write(scene); w.Write((byte)gs.CurrentState); }));
            p.Log($"Match: end {gs.CurrentState} in {scene} sent to peer");
        }

        var pi = PlayerInteraction.instance;
        if (pi != null && (scene != sentBalanceScene || pi.Balance != sentCoins || pi.EnergyCoreBalance != sentCores))
        {
            sentBalanceScene = scene;
            sentCoins = pi.Balance;
            sentCores = pi.EnergyCoreBalance;
            link.SendMessage(Write(w => { w.Write(MsgBalance); w.Write(scene); w.Write(sentCoins); w.Write(sentCores); }));
            LogThrottled($"Match: treasury coins={sentCoins} cores={sentCores} sent to peer");
        }
    }

    void ClientApply(PlayerMovement me)
    {
        if (pendingCoins != 0 || pendingCores != 0)
        {
            int c = pendingCoins, e = pendingCores;
            pendingCoins = pendingCores = 0;
            link.SendMessage(Write(w => { w.Write(MsgDelta); w.Write(c); w.Write(e); }));
            LogThrottled($"Match: treasury change coins={c:+#;-#;0} cores={e:+#;-#;0} sent to host");
        }

        string scene = me.gameObject.scene.name;
        if (endScene != null && p.Players.Remote.Scene != endScene) endScene = null; // host moved on
        var gs = LocalGamestate.Instance;
        if (gs == null || gs.CurrentState != LocalGamestate.State.InMatch) return;

        if (endScene == scene)
        {
            p.Log($"Match: end follows host -> {(LocalGamestate.State)hostEnd}");
            endScene = null;
            gs.SetState((LocalGamestate.State)hostEnd);
            return;
        }

        var cycle = DayNightCycle.Instance;
        if (cycle != null && hostTime >= 0 && timeScene == scene && (int)cycle.CurrentTimestate != hostTime)
        {
            p.Log($"Match: time follows host -> {(DayNightCycle.Timestate)hostTime}");
            if (hostTime == (int)DayNightCycle.Timestate.Night) cycle.SwitchToNight(); else cycle.SwithToDay();
        }

        var pi = PlayerInteraction.instance;
        if (pi != null && balanceScene == scene && (balanceFresh || balanceAppliedTo != pi))
        {
            bool first = balanceAppliedTo != pi;
            balanceFresh = false;
            balanceAppliedTo = pi;
            BalanceField(pi) = hostCoins;
            // Cores only exist with the DLC; without it the value stays 0 on both sides.
            if (DlcConfiguration.IsDlcAvailable(IDlcProvider.DlcId.CRAAGHELM)) CoresField(pi) = hostCores;
            var msg = $"Match: treasury follows host -> coins={hostCoins} cores={hostCores}";
            if (first) p.Log(msg); else LogThrottled(msg);
        }
    }

    internal void OnMessage(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        switch (r.ReadByte())
        {
            case MsgTime when p.role == "client":
                timeScene = r.ReadString();
                hostTime = r.ReadByte();
                break;
            case MsgBalance when p.role == "client":
                balanceScene = r.ReadString();
                hostCoins = r.ReadInt32();
                hostCores = r.ReadInt32();
                balanceFresh = true;
                break;
            case MsgEnd when p.role == "client":
                endScene = r.ReadString();
                hostEnd = r.ReadByte();
                if (!IsEnd((LocalGamestate.State)hostEnd)) endScene = null;
                break;
            case MsgDelta when p.role == "host":
                ApplyDelta(r.ReadInt32(), r.ReadInt32());
                break;
        }
    }

    static bool IsEnd(LocalGamestate.State s) =>
        s == LocalGamestate.State.AfterMatchVictory || s == LocalGamestate.State.AfterMatchDefeat;

    void ApplyDelta(int coins, int cores)
    {
        var pi = PlayerInteraction.instance;
        if (pi == null) return;
        if (coins > 0) pi.AddCoin(coins); else if (coins < 0) pi.SpendCoins(-coins);
        if (cores > 0) pi.AddEnergyCores(cores); else if (cores < 0) pi.SpendEnergyCores(-cores);
        LogThrottled($"Match: treasury change coins={coins:+#;-#;0} cores={cores:+#;-#;0} from peer -> coins={pi.Balance} cores={pi.EnergyCoreBalance}");
    }

    /// Treasury changes can come several times a second during a fight.
    void LogThrottled(string message)
    {
        if (Time.unscaledTime - lastLog < 2f) return;
        lastLog = Time.unscaledTime;
        p.Log(message);
    }

    static byte[] Write(System.Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        body(w);
        return ms.ToArray();
    }
}
