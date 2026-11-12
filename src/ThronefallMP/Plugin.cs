using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThronefallMP;

/// Two instances talk over UDP (Net/). Each shows the other player as an avatar that enemies
/// can target (Sync/PlayerSync), and the client follows the host's scene, perks, day/night and
/// treasury (Sync/MatchSync) and buildings (Sync/BuildSync). Everything else is still simulated per instance.
///
/// Command line:
///   --mp-host [port]         listen (default port 27015)
///   --mp-join [ip[:port]]    connect (default 127.0.0.1:27015)
///   --mp-profile name        keep saves in a separate sub-folder
///   --mp-autostart [scene]   skip the title screen; with a scene name, load that level
///   --mp-equip a,b           equip these by name before entering a level (dev/CI)
///   --mp-test-night          host: build + upgrade the castle, start the night and park
///                            the avatar on the nearest enemy; client: build one slot (dev/CI)
///   --mp-test-coins          both: change the treasury once the avatar appears (dev/CI)
///   --mp-shot file.png       save a screenshot once the avatar appears (dev/CI)
[BepInPlugin("dev.thronefallmp", "Thronefall MP", "0.5.0")]
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
    float nextSend, lastHeard = -999f;
    bool autostart, testNight, testChase, testCoins, shotTaken;
    string autostartScene, shotPath;
    string[] equipFlag;
    PlayerMovement lastMe;
    bool lastFocused = true;
    GUIStyle overlayStyle;
    int overlayFontSize;

    internal bool PeerAlive => Time.unscaledTime - lastHeard < PeerTimeout;

    void Awake()
    {
        Instance = this;
        Players = new PlayerSync(this);
        if (!PlayerSync.SelfCheck() || !Reliable.SelfCheck()) Logger.LogError("Self-check failed (packet round-trip or reliable layer)");

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

        if (args.Contains("--mp-host"))
        {
            role = "host";
            link = new Link(new UdpClient(int.TryParse(After("--mp-host"), out var p) ? p : DefaultPort), null, () => Time.unscaledTime);
        }
        else if (args.Contains("--mp-join"))
        {
            role = "client";
            var target = (After("--mp-join") ?? "127.0.0.1").Split(':');
            var peer = new IPEndPoint(IPAddress.Parse(target[0]), target.Length > 1 ? int.Parse(target[1]) : DefaultPort);
            link = new Link(new UdpClient(0), peer, () => Time.unscaledTime);
        }

        if (link != null)
        {
            Match = new MatchSync(this, link);
            Builds = new BuildSync(this, link);
            Players.AvatarShown += OnAvatarShown;
            // Two instances on one Mac: the unfocused one must keep simulating.
            Application.runInBackground = true;
            // Delayed: the game re-applies its saved video settings during startup.
            Invoke(nameof(Windowed), 4f);
        }
        else
        {
            var none = new Link(null, null, () => 0f);
            Match = new MatchSync(this, none);
            Builds = new BuildSync(this, none);
        }

        new Harmony("dev.thronefallmp").PatchAll();
        Logger.LogInfo($"Thronefall MP loaded: role={role} profile={Profile ?? "(default)"}");
    }

    void Update()
    {
        if (autostart && SceneTransitionManager.instance != null && Time.realtimeSinceStartup > 6f)
        {
            autostart = false;
            ApplyEquipFlag();
            if (autostartScene == null) SceneTransitionManager.instance.TransitionFromNullToLevelSelect();
            else EnterLevel(autostartScene);
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
        if (link.Pump(role == "host", Players.OnState, OnMessage))
        {
            lastHeard = Time.unscaledTime;
            if (!wasAlive)
            {
                Logger.LogInfo($"Peer connected: {link.Peer}");
                rebase = true;
                Match.ResendAll();
                Builds.ResendAll();
            }
        }

        Players.Tick(me, rebase);

        if (link.Peer != null && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + SendInterval;
            link.SendState(Players.Snapshot(me));
        }

        Match.Tick(me);
        Builds.Tick(me);
        if (testChase) Players.ChaseNearestEnemy();
        FollowHost();
        Players.UpdateGhost(me);
    }

    void OnMessage(byte[] data)
    {
        if (data.Length == 0) return;
        if (data[0] >= BuildSync.MsgBuilt) Builds.OnMessage(data); else Match.OnMessage(data);
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
    }

    /// Client only: whenever the host's player is in a different scene, go there with the
    /// host's perks. We keep our own weapon if we have one.
    void FollowHost()
    {
        var stm = SceneTransitionManager.instance;
        if (role != "client" || !PeerAlive || string.IsNullOrEmpty(Players.Remote.Scene)) return;
        if (stm == null || stm.SceneTransitionIsRunning || PerkManager.instance == null) return;
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
    }

    void TestNightStart()
    {
        DayNightCycle.Instance.SwitchToNight();
        if (shotPath != null) Invoke(nameof(Shot), 8f);
        InvokeRepeating(nameof(TestStatus), 5f, 10f);
        testChase = true;
    }

    void TestStatus() => Logger.LogInfo($"Test status: {DayNightCycle.Instance.CurrentTimestate}, "
        + $"enemies={EnemySpawner.instance.NumberOfEnemiesOnTheMap}, avatar at {Players.ghost.transform.position} "
        + $"active={Players.ghost.activeSelf}, Players.damageToPeer={Players.damageToPeer}, my hp={PlayerMovement.instance.Hp.HpValue}");

    void Windowed() => Screen.SetResolution(1280, 720, FullScreenMode.Windowed);

    void Shot()
    {
        shotTaken = true;
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
        }
        string status = role == "off" ? "single player" : $"{role} · peer {(PeerAlive ? "connected" : "waiting…")}";
        var text = $"Thronefall MP {Info.Metadata.Version} · {status}";
        var rect = new Rect(size * 0.7f, size * 0.7f, Screen.width, size * 1.6f);
        var color = overlayStyle.normal.textColor;
        overlayStyle.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, overlayStyle);
        overlayStyle.normal.textColor = color;
        GUI.Label(rect, text, overlayStyle);
    }

    void OnDestroy()
    {
        Players.FlushDamageBurst(force: true);
        link?.Close();
    }
}
