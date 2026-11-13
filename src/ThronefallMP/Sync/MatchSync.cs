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
///    A client that ends its match itself (resign) tells the host, which plays on, and does not
///    follow the host back into that match until it asks to (Plugin.followPaused).
///  - Rejoin: whenever the client enters the host's level it asks for a snapshot; the host resends
///    time + wave number, treasury, its build log and live enemies (Plugin.ResendAll).
/// All are level-based ("latest wins"), so a message that arrives before the client is in the
/// right scene is kept and applied once it is.
class MatchSync
{
    const byte MsgTime = 1, MsgBalance = 2, MsgDelta = 3, MsgEnd = 4, MsgSnapshot = 5, MsgLeave = 6;
    static readonly AccessTools.FieldRef<PlayerInteraction, int> BalanceField =
        AccessTools.FieldRefAccess<PlayerInteraction, int>("balance");
    static readonly AccessTools.FieldRef<EnemySpawner, int> WaveField = AccessTools.FieldRefAccess<EnemySpawner, int>("wavenumber");
    // Inputs of ScoreManager's time bonus; the client's own clocks stand still (frozen cycle, no spawning).
    static readonly AccessTools.FieldRef<DayNightCycle, float> NightLengthField = AccessTools.FieldRefAccess<DayNightCycle, float>("currentNightLength");
    static readonly AccessTools.FieldRef<EnemySpawner, float> SpawnPeriodField = AccessTools.FieldRefAccess<EnemySpawner, float>("lastSpawnPeriodDuration");
    float hostNightLength, hostSpawnPeriod;
    static readonly AccessTools.FieldRef<PlayerInteraction, int> CoresField =
        AccessTools.FieldRefAccess<PlayerInteraction, int>("energyCoreBalance");

    readonly Plugin p;
    readonly Link link;

    // Host: what the peer was last told.
    string sentTimeScene, sentBalanceScene, sentEndScene;
    int sentTime = -1, sentWave, sentCoins, sentCores;

    // Client: latest host state, applied when scene and match allow.
    string timeScene, balanceScene, endScene;
    int hostEnd;
    int hostTime = -1, hostWave = -1, hostCoins, hostCores;
    bool balanceFresh, endFromHost, wasEnded;
    PlayerInteraction balanceAppliedTo, snapshotFor;

    // Client: local treasury changes not yet reported to the host.
    int pendingCoins, pendingCores;

    // Client: world coin/core pickups and dusk/dawn perks are host-authoritative; only refund
    // coins from CostDisplay.CancelFill stay local.
    internal static bool inCancelFill;
    internal static Coin currentPickup;
    internal static int suppressGainDepth;
    internal static readonly System.Collections.Generic.HashSet<Coin> refundCoins = new();

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

    /// Client only: dawn building harvests, world coin/core pickups and dusk/dawn perks run on
    /// both instances; only the host credits them to the shared treasury (and pushes MsgBalance),
    /// while refund coins from a cancelled building fill (CostDisplay.CancelFill) stay local.
    internal bool ShouldSuppressClientGain(PlayerInteraction pi)
    {
        if (pi == null || !FrozenByHost(pi.gameObject.scene.name)) return false;
        if (currentPickup != null) return !refundCoins.Remove(currentPickup);
        return suppressGainDepth > 0;
    }

    /// Local treasury change on this instance (PlayerInteraction patches).
    internal void LocalChange(int coins, int cores)
    {
        // Before the host's balance is adopted (level start grants), our number is meaningless.
        if (p.role == "client" && PlayerInteraction.instance == balanceAppliedTo) { pendingCoins += coins; pendingCores += cores; }
    }

    internal void Tick(PlayerMovement me)
    {
        if (me == null) return;
        if (SceneTransitionManager.instance is { SceneTransitionIsRunning: true }) return;
        if (p.role == "host") HostSend(me); else ClientApply(me);
    }

    void HostSend(PlayerMovement me)
    {
        if (!p.PeerAlive) return;
        string scene = me.gameObject.scene.name;

        var cycle = DayNightCycle.Instance;
        int wave = EnemySpawner.instance != null ? EnemySpawner.instance.Wavenumber : -1;
        if (cycle != null && (scene != sentTimeScene || (int)cycle.CurrentTimestate != sentTime || wave != sentWave))
        {
            sentTimeScene = scene;
            sentTime = (int)cycle.CurrentTimestate;
            sentWave = wave;
            link.SendMessage(Write(w => { w.Write(MsgTime); w.Write(scene); w.Write((byte)sentTime); w.Write(wave);
                w.Write(cycle.CurrentNightLength); w.Write(EnemySpawner.instance != null ? EnemySpawner.instance.LastSpawnPeriodDuration : 0f); }));
            p.Log($"Match: time {cycle.CurrentTimestate} wave {wave} in {scene} sent to peer");
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
        if (pi != null && p.Players.ghost != null && p.Players.ghost.activeSelf && TagManager.instance != null)
        {
            var gpos = p.Players.ghost.transform.position;
            float r2 = pi.coinMagnetRadius * pi.coinMagnetRadius;
            foreach (var c in TagManager.instance.freeCoins)
                if (c != null && c.IsFree && (c.transform.position - gpos).sqrMagnitude <= r2) c.SetTarget(pi);
            if (DlcConfiguration.IsDlcAvailable(IDlcProvider.DlcId.CRAAGHELM))
                foreach (var c in TagManager.instance.freeEnergyCores)
                    if (c != null && c.IsFree && (c.transform.position - gpos).sqrMagnitude <= r2) c.SetTarget(pi);
        }
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
        bool hostHere = p.Players.Remote.Scene == scene;
        bool ended = gs != null && IsEnd(gs.CurrentState);
        if (ended && !wasEnded && !endFromHost && hostHere)
        {
            link.SendMessage(Write(w => { w.Write(MsgLeave); w.Write(scene); }));
            p.followPaused = scene;
            p.Log($"Match: left {scene} ({gs.CurrentState}); host plays on, not following back until Rejoin");
        }
        wasEnded = ended;
        if (!ended) endFromHost = false;
        if (gs == null || gs.CurrentState != LocalGamestate.State.InMatch) return;

        var pi = PlayerInteraction.instance;
        if (hostHere && pi != null && snapshotFor != pi)
        {
            snapshotFor = pi;
            link.SendMessage(new[] { MsgSnapshot });
            p.Log($"Match: entered host's {scene}, snapshot requested");
        }

        if (endScene == scene)
        {
            p.Log($"Match: end follows host -> {(LocalGamestate.State)hostEnd}");
            endScene = null;
            endFromHost = true;
            gs.SetState((LocalGamestate.State)hostEnd);
            return;
        }

        var cycle = DayNightCycle.Instance;
        if (cycle != null && hostTime >= 0 && timeScene == scene && (int)cycle.CurrentTimestate != hostTime)
        {
            p.Log($"Match: time follows host -> {(DayNightCycle.Timestate)hostTime}");
            if (hostTime == (int)DayNightCycle.Timestate.Night) cycle.SwitchToNight();
            else
            {
                // The dawn score (time bonus) is computed inside SwithToDay: use the host's night.
                NightLengthField(cycle) = hostNightLength;
                if (EnemySpawner.instance != null) SpawnPeriodField(EnemySpawner.instance) = hostSpawnPeriod;
                cycle.SwithToDay();
            }
        }
        // After the switch: SwitchToNight starts spawning, which advances the wave by one.
        var spawner = EnemySpawner.instance;
        if (spawner != null && cycle != null && timeScene == scene && hostWave >= 0 && (int)cycle.CurrentTimestate == hostTime && spawner.Wavenumber != hostWave)
        {
            p.Log($"Match: wave follows host {spawner.Wavenumber} -> {hostWave}");
            WaveField(spawner) = hostWave;
        }

        if (pi != null && balanceScene == scene && (balanceFresh || balanceAppliedTo != pi))
        {
            bool first = balanceAppliedTo != pi;
            if (first) refundCoins.Clear();
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
                hostWave = r.ReadInt32();
                hostNightLength = r.ReadSingle();
                hostSpawnPeriod = r.ReadSingle();
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
            case MsgSnapshot when p.role == "host":
                p.Log("Match: peer asked for a snapshot; resending time, treasury, builds and enemies");
                p.ResendAll();
                break;
            case MsgLeave when p.role == "host":
                p.Log($"Match: peer left {r.ReadString()} (resigned); host match continues");
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

    internal static bool SelfCheck()
    {
        var bytes = Write(w => { w.Write(MsgBalance); w.Write("Nordfels"); w.Write(17); w.Write(4); });
        using var r = new BinaryReader(new MemoryStream(bytes));
        return r.ReadByte() == MsgBalance && r.ReadString() == "Nordfels" && r.ReadInt32() == 17 && r.ReadInt32() == 4;
    }
}
