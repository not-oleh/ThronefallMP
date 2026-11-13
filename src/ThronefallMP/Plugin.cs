using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Steamworks;
using UnityEngine.SceneManagement;

namespace ThronefallMP;

/// Two instances talk over UDP or Steam P2P (Net/). Each shows the other player as an avatar that enemies
/// can target (Sync/PlayerSync), and the client follows the host's scene, perks, day/night and
/// treasury (Sync/MatchSync), buildings (Sync/BuildSync) and the host's enemies (Sync/EnemySync). Everything else is still simulated per instance.
///
/// Command line:
///   --mp-host [port]         listen (default port 27015)
///   --mp-join [ip[:port]]    connect (default 127.0.0.1:27015)
///   --mp-steam-host          host over Steam P2P: opens a friends-only lobby (id in the log)
///   --mp-steam-join [lobby]  join over Steam; without an id, wait for a Steam invite. Steam's
///                            own +connect_lobby <id> (launch from an invite) does the same
///   --mp-profile name        keep saves in a separate sub-folder
///   --mp-autostart [scene]   skip the title screen; with a scene name, load that level
///   --mp-equip a,b           equip these by name before entering a level (dev/CI)
///   --mp-test-night          host: build + upgrade the castle, run 3 nights parking the
///                            avatar on the nearest enemy, then resign (match-end sync);
///                            client: build one slot, drop to 20% hp at night, attack (dev/CI)
///   --mp-test-coins          both: change the treasury once the avatar appears (dev/CI)
///   --mp-test-rejoin         client: resign mid-match, go to level select, rejoin (dev/CI)
///   --mp-shot file.png       save a screenshot once the avatar appears (dev/CI)
/// F8 (or clicking the overlay) opens the Co-op panel: host a Steam lobby, invite, copy/paste the
/// lobby id, rejoin, disconnect. Steam invites are accepted at any time while the game runs.
[BepInPlugin("dev.thronefallmp", "Thronefall MP", "0.9.0")]
public class Plugin : BaseUnityPlugin
{
    const int DefaultPort = 27015;
    const float SendInterval = 1f / 20f;
    const float PeerTimeout = 3f;

    internal static Plugin Instance;
    internal static string Profile;

    internal string role = "off";
    internal Link link;
    internal PlayerSync Players;
    internal MatchSync Match;
    internal BuildSync Builds;
    internal EnemySync Enemies;
    float nextSend, lastHeard = -999f;
    bool autostart, testNight, testChase, testCoins, shotTaken, testWeakened;
    int testNights;
    string autostartScene, shotPath;
    string[] equipFlag;
    PlayerMovement lastMe;
    bool lastFocused = true;
    GUIStyle overlayStyle, buttonStyle, fieldStyle;
    int overlayFontSize;
    bool panelOpen, etLogged, testRejoin;
    string lobbyText = "";
    Callback<GameLobbyJoinRequested_t> inviteCallback;
    /// Client: the host scene we left (resign); not followed back into until the host moves on
    /// or the player presses Rejoin.
    internal string followPaused;

    internal bool PeerAlive => Time.unscaledTime - lastHeard < PeerTimeout;

    static bool SceneReady =>
        SceneTransitionManager.instance is { SceneTransitionIsRunning: false } stm
        && PerkManager.instance != null
        && UIFrameManager.instance != null
        && Time.frameCount > 10
        && SceneManager.GetSceneByName(stm.uiScene).isLoaded;

    void Awake()
    {
        Instance = this;
        Players = new PlayerSync(this);
        if (!PlayerSync.SelfCheck() || !Interp.SelfCheck() || !Reliable.SelfCheck() || !EnemySync.SelfCheck() || !MatchSync.SelfCheck() || !BuildSync.SelfCheck())
            Logger.LogError("Self-check failed (packet round-trip, interpolation, reliable layer, enemy chunks, match or build sync)");

        var args = Environment.GetCommandLineArgs();
        string After(string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[i + 1] : null;
        }

        Profile = After("--mp-profile");
        autostart = args.Contains("--mp-autostart");
        autostartScene = After("--mp-autostart");
        equipFlag = After("--mp-equip")?.Split(',');
        testNight = args.Contains("--mp-test-night");
        testCoins = args.Contains("--mp-test-coins");
        shotPath = After("--mp-shot");
        if (args.Contains("--mp-panel")) panelOpen = true;

        Players.AvatarShown += OnAvatarShown;
        testRejoin = args.Contains("--mp-test-rejoin");
        if (args.Contains("--mp-host"))
            StartLink("host", new UdpTransport(new UdpClient(int.TryParse(After("--mp-host"), out var p) ? p : DefaultPort), null, isHost: true));
        else if (args.Contains("--mp-join"))
        {
            var target = (After("--mp-join") ?? "127.0.0.1").Split(':');
            var peer = new IPEndPoint(IPAddress.Parse(target[0]), target.Length > 1 ? int.Parse(target[1]) : DefaultPort);
            StartLink("client", new UdpTransport(new UdpClient(0), peer, isHost: false));
        }
        else if (args.Contains("--mp-steam-host")) StartLink("host", new SteamTransport(true, 0, Log));
        else if (args.Contains("--mp-steam-join") || args.Contains("+connect_lobby"))
        {
            ulong.TryParse(After("--mp-steam-join") ?? After("+connect_lobby"), out var lobby);
            StartLink("client", new SteamTransport(false, lobby, Log));
        }
        else Disconnect();
        // Delayed: the game re-applies its saved video settings during startup.
        if (link != null) Invoke(nameof(Windowed), 4f);

        new Harmony("dev.thronefallmp").PatchAll();
        Logger.LogInfo($"Thronefall MP loaded: role={role} profile={Profile ?? "(default)"}");
    }

    /// Replaces any current connection (flags at launch, Co-op panel or a Steam invite later).
    /// Sync modules start fresh: a new peer gets everything through the snapshot request.
    internal void StartLink(string newRole, ITransport transport)
    {
        link?.Close();
        role = newRole;
        link = new Link(transport, () => Time.unscaledTime);
        lastHeard = -999f;
        followPaused = null;
        Match = new MatchSync(this, link);
        Builds = new BuildSync(this, link);
        Enemies = new EnemySync(this, link);
        // Two instances on one Mac: the unfocused one must keep simulating.
        Application.runInBackground = true;
        Logger.LogInfo($"Connection: {role} over {transport.GetType().Name}");
    }

    /// Back to single player. The current level (if any) keeps running locally.
    internal void Disconnect()
    {
        if (link != null) Logger.LogInfo($"Connection: closed ({role})");
        link?.Close();
        link = null;
        role = "off";
        lastHeard = -999f;
        var none = new Link(null, () => 0f);
        Match = new MatchSync(this, none);
        Builds = new BuildSync(this, none);
        Enemies = new EnemySync(this, none);
    }

    /// A Steam invite or +connect_lobby arrived while the game was running.
    void OnSteamInvite(GameLobbyJoinRequested_t r)
    {
        Logger.LogInfo($"Steam invite to lobby {r.m_steamIDLobby.m_SteamID}");
        StartLink("client", new SteamTransport(false, r.m_steamIDLobby.m_SteamID, Log));
    }

    void Update()
    {
        // Held so it is not garbage collected; needs the game's SteamManager to be up.
        if (inviteCallback == null && SteamTransport.SteamReady) inviteCallback = Callback<GameLobbyJoinRequested_t>.Create(OnSteamInvite);

        if (autostart && SceneReady && Time.realtimeSinceStartup > 6f)
        {
            ApplyEquipFlag();
            if (autostartScene == null)
            {
                Time.timeScale = PlayerMovement.gameplayTimeScale;
                UIFrameManager.instance.CloseAllFrames();
                SceneTransitionManager.instance.TransitionFromNullToLevelSelect();
            }
            else EnterLevel(autostartScene);
            if (SceneTransitionManager.instance.SceneTransitionIsRunning) autostart = false;
        }

        if (link == null) return;

        bool focused = FocusInput.Active;
        if (focused != lastFocused)
        {
            lastFocused = focused;
            Logger.LogInfo($"Window focus: {focused} (Unity says {Application.isFocused}, input {(focused ? "live" : "blocked")})");
        }

        var me = PlayerMovement.instance;
        bool rebase = me != lastMe;
        lastMe = me;

        bool wasAlive = PeerAlive;
        if (link.Pump(OnState, OnMessage))
        {
            lastHeard = Time.unscaledTime;
            if (!wasAlive)
            {
                Logger.LogInfo($"Peer connected: {link.PeerName}");
                rebase = true;
                ResendAll();
            }
        }

        Players.Tick(me, rebase);

        if (link.HasPeer && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + SendInterval;
            link.SendState(new byte[] { 0 }.Concat(Players.Snapshot(me)).ToArray());
            var enemies = Enemies.HostSnapshot(me);
            if (enemies != null) foreach (var chunk in enemies) link.SendState(chunk);
        }

        Match.Tick(me);
        Builds.Tick(me);
        Enemies.Tick(me);
        if (testChase) Players.ChaseNearestEnemy();
        FollowHost();
        // The game's SettingsManager re-applies the saved vsync choice; a vsynced unfocused
        // macOS window stalls, which stalls our packets. Steady 60 FPS while connected.
        if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != 60)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }
    }

    /// After every Update (ours and the game's), so remote motion is applied once per render frame.
    void LateUpdate()
    {
        Players.UpdateGhost(PlayerMovement.instance);
        if (link != null) Enemies.Smooth();
    }

    /// Host: everything a (re)joining client needs to catch up; each module's sends are idempotent.
    internal void ResendAll()
    {
        Match.ResendAll();
        Builds.ResendAll();
        Enemies.ResendAll();
    }

    void OnMessage(byte[] data)
    {
        if (data.Length == 0) return;
        if (data[0] >= EnemySync.MsgSpawn) Enemies.OnMessage(data);
        else if (data[0] >= BuildSync.MsgBuilt) Builds.OnMessage(data);
        else Match.OnMessage(data);
    }

    /// State bodies start with a kind byte: 0 = player, EnemySync.StateKind = enemies + buildings.
    void OnState(byte[] data)
    {
        if (data.Length == 0) return;
        if (data[0] == EnemySync.StateKind) Enemies.OnState(data); else Players.OnState(data.Skip(1).ToArray());
    }

    /// Runs `action` after `seconds` (scaled game time, like Invoke).
    internal void After(float seconds, Action action) => StartCoroutine(Delayed(seconds, action));

    static IEnumerator Delayed(float seconds, Action action)
    {
        yield return new WaitForSeconds(seconds);
        action();
    }

    void OnAvatarShown()
    {
        if (shotPath != null && !shotTaken) Invoke(nameof(Shot), 2f);
        if (testNight) Invoke(role == "host" ? nameof(TestNight) : nameof(TestClientBuild), role == "host" ? 3f : 8f);
        if (testCoins) Invoke(nameof(TestCoins), 3f);
        if (testRejoin && role == "client") StartCoroutine(TestRejoin());
    }

    /// Client only: whenever the host's player is in a different scene, go there with the
    /// host's perks. We keep our own weapon if we have one.
    void FollowHost()
    {
        if (role != "client" || !PeerAlive || string.IsNullOrEmpty(Players.Remote.Scene)) return;
        if (!SceneReady) return;
        if (Players.Remote.EternalTrial)
        {
            // shortcut: Eternal Trials runs (seeded maps, mutators, run perks) are not shared yet;
            // following as Classic would mismatch buildings and waves, so the client stays put.
            if (!etLogged) Logger.LogInfo("Host is in an Eternal Trials run; co-op does not support it yet, not following");
            etLogged = true;
            return;
        }
        etLogged = false;
        if (followPaused != null)
        {
            if (followPaused == Players.Remote.Scene) return;
            followPaused = null; // the host moved on: follow again
        }
        if (SceneManager.GetActiveScene().name == Players.Remote.Scene) return;

        ApplyEquipFlag();
        var equipped = PerkManager.instance.CurrentlyEquipped;
        var ownWeapon = equipped.OfType<EquippableWeapon>().FirstOrDefault();
        var fromHost = PerkManager.instance.allEquippables
            .Where(e => Players.Remote.Loadout.Contains(e.name) && (ownWeapon == null || !(e is EquippableWeapon)))
            .ToList();
        equipped.Clear();
        equipped.AddRange(fromHost);
        if (ownWeapon != null) equipped.Add(ownWeapon);
        Logger.LogInfo($"Following host to {Players.Remote.Scene} with [{string.Join(", ", equipped.Select(e => e.name))}]");
        EnterLevel(Players.Remote.Scene);
    }

    void ApplyEquipFlag()
    {
        if (equipFlag == null) return;
        var equipped = PerkManager.instance.CurrentlyEquipped;
        equipped.Clear();
        equipped.AddRange(PerkManager.instance.allEquippables.Where(e => equipFlag.Contains(e.name)));
    }

    static void EnterLevel(string scene)
    {
        Time.timeScale = PlayerMovement.gameplayTimeScale;
        UIFrameManager.instance?.CloseAllFrames();
        LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
        SceneTransitionManager.instance.TransitionFromNullToLevel(scene);
    }

    void TestNight()
    {
        testNight = false;
        var castle = CastleCenter.instance.GetComponentInParent<BuildSlot>();
        castle.ExecuteBuildOrUpgrade(PlayerInteraction.instance, false);
        Logger.LogInfo($"Test night: castle at {castle.transform.position}, me at {PlayerMovement.instance.transform.position}, "
            + $"craaghelm={DlcConfiguration.IsDlcAvailable(IDlcProvider.DlcId.CRAAGHELM)}");
        // Second castle level: a random branch of a (possible) choice, to check choice sync.
        After(1f, () => castle.ExecuteBuildOrUpgrade(PlayerInteraction.instance, false));
        Invoke(nameof(TestNightStart), 10f);
    }

    /// Client: build the first free blueprint (castle must already have come from the host).
    void TestClientBuild()
    {
        testNight = false;
        var slot = FindObjectsByType<BuildSlot>(FindObjectsSortMode.None)
            .Where(s => s.Level == 0 && s.CanBeUpgraded && !s.ActivatorUpgradesThis && s.GetComponentInChildren<CastleCenter>(true) == null)
            .OrderBy(s => BuildSync.SlotId(s), StringComparer.Ordinal).FirstOrDefault();
        Logger.LogInfo($"Test build: client builds {(slot != null ? slot.buildingName + " " + BuildSync.SlotId(slot) : "nothing (no free slot)")}");
        slot?.TryToBuildOrUpgradeAndPay(PlayerInteraction.instance, false);
        var castle = CastleCenter.instance != null ? CastleCenter.instance.GetComponentInParent<BuildSlot>() : null;
        if (castle != null && castle.Level >= 2) Builds.TestSimultaneousRace(castle);
        InvokeRepeating(nameof(TestClientAttack), 5f, 0.5f);
    }

    /// Client test: at night, stand next to the nearest puppet and swing the equipped weapon
    /// through the game's own attack (the hit must reach the host as an intent).
    void TestClientAttack()
    {
        var me = PlayerMovement.instance;
        if (me == null || me.Hp == null || me.Hp.KnockedOut || DayNightCycle.Instance?.CurrentTimestate != DayNightCycle.Timestate.Night) return;
        if (!testWeakened)
        {
            // Regeneration outpaces wave-1 damage; start low so forwarded hits reach a knockout.
            testWeakened = true;
            Players.applyingHostDamage = true;
            try { me.Hp.TakeDamage(me.Hp.HpValue - me.Hp.maxHp * 0.2f); } finally { Players.applyingHostDamage = false; }
            Logger.LogInfo($"Test night: client weakened to hp {me.Hp.HpValue:0.#}");
        }
        var target = Enemies.NearestPuppet(me.transform.position);
        if (target == null) return;
        var pos = target.transform.position;
        if ((me.transform.position - pos).sqrMagnitude > 4f) me.TeleportTo(pos + Vector3.right * 1.5f);
        foreach (var a in me.GetComponentsInChildren<ManualAttack>())
            if (a.isActiveAndEnabled) a.TryToAttack();
    }

    void TestNightStart()
    {
        testNights = 1;
        DayNightCycle.Instance.SwitchToNight();
        if (shotPath != null) Invoke(nameof(Shot), 8f);
        InvokeRepeating(nameof(TestStatus), 5f, 10f);
        testChase = true;
    }

    void TestStatus()
    {
        TestStatusLog();
        var gs = LocalGamestate.Instance;
        if (DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Day || gs.CurrentState != LocalGamestate.State.InMatch) return;
        if (testNights < 3) { testNights++; Logger.LogInfo($"Test night: night {testNights}"); DayNightCycle.Instance.SwitchToNight(); }
        else { Logger.LogInfo("Test night: host resigns"); gs.SetState(LocalGamestate.State.AfterMatchDefeat); }
    }

    void TestStatusLog() => Logger.LogInfo($"Test status: {DayNightCycle.Instance.CurrentTimestate}, "
        + $"enemies={EnemySpawner.instance.NumberOfEnemiesOnTheMap}, avatar at {Players.ghost.transform.position} "
        + $"active={Players.ghost.activeSelf}, rtt={link.Rtt * 1000f:0}ms, Players.damageToPeer={Players.damageToPeer}, my hp={PlayerMovement.instance.Hp.HpValue}, {Enemies.Status()}");

    void Windowed() => Screen.SetResolution(1280, 720, FullScreenMode.Windowed);

    void Shot()
    {
        shotTaken = true;
        if (panelOpen)
        {
            var prev = GUIUtility.systemCopyBuffer;
            const ulong sampleLobby = 109775241012345678UL;
            GUIUtility.systemCopyBuffer = sampleLobby.ToString();
            bool clipOk = ulong.TryParse(GUIUtility.systemCopyBuffer?.Trim(), out var parsed) && parsed == sampleLobby;
            lobbyText = "Lobby ID copied";
            Logger.LogInfo($"Panel: F8 Co-op panel open, clipboard copy/paste round-trip ok={clipOk} ({parsed})");
            GUIUtility.systemCopyBuffer = prev;
        }
        ScreenCapture.CaptureScreenshot(shotPath);
    }

    /// Test only: host +10 coins, client +5 coins (forwarded to the host), host +3 cores if it
    /// owns the DLC. Both logs must then show the same totals.
    void TestCoins()
    {
        testCoins = false;
        var pi = PlayerInteraction.instance;
        Logger.LogInfo($"Test coins: start coins={pi.Balance} cores={pi.EnergyCoreBalance}");
        pi.AddCoin(role == "host" ? 10 : 5);
        if (role == "host" && DlcConfiguration.IsDlcAvailable(IDlcProvider.DlcId.CRAAGHELM)) pi.AddEnergyCores(3);
        InvokeRepeating(nameof(TestCoinsStatus), 3f, 3f);
    }

    void TestCoinsStatus() => Logger.LogInfo($"Test coins: coins={PlayerInteraction.instance.Balance} "
        + $"cores={PlayerInteraction.instance.EnergyCoreBalance}, time={DayNightCycle.Instance?.CurrentTimestate}");

    internal void Log(string message) => Logger.LogInfo(message);

    void OnGUI()
    {
        // IMGUI works in backing pixels, so the default 12 px font is tiny on Retina. Size the
        // font itself (not GUI.matrix, which blurs) from the window height.
        int size = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 40f), 14, 48);
        if (overlayStyle == null || size != overlayFontSize)
        {
            overlayFontSize = size;
            overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = false };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = size, padding = new RectOffset(size / 2, size / 2, size / 4, size / 4) };
            fieldStyle = new GUIStyle(GUI.skin.box) { fontSize = size, padding = new RectOffset(size / 2, size / 2, size / 2, size / 2) };
        }
        var e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F8) { panelOpen = !panelOpen; e.Use(); }

        string status = role == "off" ? "single player" : $"{role} · peer {(PeerAlive ? "connected" : "waiting…")}";
        if (PeerAlive && link.Rtt >= 0f) status += $" · {link.Rtt * 1000f:0} ms";
        var text = $"Thronefall MP {Info.Metadata.Version} · {status}  [{(panelOpen ? "▲" : "▼")} F8]";
        var rect = new Rect(size * 0.7f, size * 0.7f, overlayStyle.CalcSize(new GUIContent(text)).x + 2, size * 1.6f);
        var color = overlayStyle.normal.textColor;
        overlayStyle.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, overlayStyle);
        overlayStyle.normal.textColor = color;
        if (GUI.Button(rect, text, overlayStyle)) panelOpen = !panelOpen;
        if (!panelOpen) return;

        GUILayout.BeginArea(new Rect(rect.x, rect.yMax + size * 0.3f, size * 18f, Screen.height), fieldStyle);
        var steam = link?.Transport as SteamTransport;
        if (role == "off")
        {
            if (GUILayout.Button("Host Steam lobby", buttonStyle)) StartLink("host", new SteamTransport(true, 0, Log));
            if (GUILayout.Button("Join lobby ID from clipboard", buttonStyle))
            {
                if (ulong.TryParse(GUIUtility.systemCopyBuffer?.Trim(), out var id) && id != 0) StartLink("client", new SteamTransport(false, id, Log));
                else lobbyText = "Clipboard has no lobby ID";
            }
            GUILayout.Label("Or accept a Steam invite (friend list → Join game).", overlayStyle);
        }
        else
        {
            GUILayout.Label($"Peer: {link.PeerName}", overlayStyle);
            if (steam != null)
            {
                GUILayout.Label(steam.LobbyId != 0 ? $"Lobby {steam.LobbyId}" : "Lobby: opening…", overlayStyle);
                if (steam.IsHost && steam.LobbyId != 0 && GUILayout.Button("Invite friend", buttonStyle)) steam.Invite();
                if (steam.LobbyId != 0 && GUILayout.Button("Copy lobby ID", buttonStyle))
                {
                    GUIUtility.systemCopyBuffer = steam.LobbyId.ToString();
                    lobbyText = "Lobby ID copied";
                }
            }
            if (followPaused != null && GUILayout.Button("Rejoin host's match", buttonStyle)) Rejoin();
            if (GUILayout.Button("Disconnect", buttonStyle)) Disconnect();
        }
        if (lobbyText != "") GUILayout.Label(lobbyText, overlayStyle);
        GUILayout.EndArea();
    }

    /// Client: follow the host back into the match we resigned from (FollowHost does the rest).
    internal void Rejoin()
    {
        Logger.LogInfo($"Rejoining host in {followPaused}");
        followPaused = null;
    }

    /// Test: at the first night the client resigns, goes to level select, then rejoins. The
    /// host must log the leave and the snapshot; the client must rebuild buildings and puppets.
    IEnumerator TestRejoin()
    {
        testRejoin = false;
        while (DayNightCycle.Instance == null || DayNightCycle.Instance.CurrentTimestate != DayNightCycle.Timestate.Night) yield return null;
        yield return new WaitForSecondsRealtime(8f);
        Logger.LogInfo($"Test rejoin: client resigns ({Enemies.Status()}, wave {EnemySpawner.instance?.Wavenumber})");
        LocalGamestate.Instance.SetState(LocalGamestate.State.AfterMatchDefeat, forceTransition: false, immediate: true);
        yield return new WaitForSecondsRealtime(6f);
        Time.timeScale = 1f;
        SceneTransitionManager.instance.TransitionFromLevelToLevelSelect();
        yield return new WaitForSecondsRealtime(10f);
        Logger.LogInfo($"Test rejoin: at {SceneManager.GetActiveScene().name}, followPaused={followPaused}; rejoining");
        Rejoin();
        yield return new WaitForSecondsRealtime(20f);
        Logger.LogInfo($"Test rejoin: back in {SceneManager.GetActiveScene().name}, {Enemies.Status()}, wave {EnemySpawner.instance?.Wavenumber}, "
            + $"time {DayNightCycle.Instance?.CurrentTimestate}, coins {PlayerInteraction.instance?.Balance}");
    }

    void OnDestroy()
    {
        Players.FlushDamageBurst(force: true);
        link?.Close();
    }
}
