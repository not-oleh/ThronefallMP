using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using HarmonyLib;
using Thronefall.SaveServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThronefallMP;

/// Two instances exchange player state over UDP. Each shows the other player as an
/// avatar that enemies can target, and the client follows the host's scene and perks.
/// Each instance still simulates its own world; shared match state comes next.
///
/// Command line:
///   --mp-host [port]         listen (default port 27015)
///   --mp-join [ip[:port]]    connect (default 127.0.0.1:27015)
///   --mp-profile name        keep saves in a separate sub-folder
///   --mp-autostart [scene]   skip the title screen; with a scene name, load that level
///   --mp-equip a,b           equip these by name before entering a level (dev/CI)
///   --mp-test-night          host: build the castle, start the night and park the
///                            avatar on the nearest enemy (dev/CI)
///   --mp-shot file.png       save a screenshot once the avatar appears (dev/CI)
[BepInPlugin("dev.thronefallmp", "Thronefall MP", "0.3.0")]
public class Plugin : BaseUnityPlugin
{
    const int DefaultPort = 27015;
    const float SendInterval = 1f / 20f;
    const float PeerTimeout = 3f;
    const byte Protocol = 3;

    internal static Plugin Instance;
    internal static string Profile;

    internal string role = "off";
    UdpClient udp;
    IPEndPoint peer;
    float nextSend, lastHeard = -999f;
    State remote;
    bool autostart, testNight, testChase, shotTaken;
    string autostartScene, shotPath;
    string[] equipFlag;

    // Avatar of the remote player; rebuilt for every scene, inactive while the peer is absent.
    GameObject ghost;
    Transform ghostMesh;
    Animator ghostAnimator;
    internal Hp ghostHp;
    readonly List<(string name, GameObject root, GameObject visuals, List<OneShotAnimationBase> anims)> ghostWeapons = new();
    byte seenAttacks;

    // Host: total damage its enemies dealt to the avatar. Client: how much of that it has taken.
    internal float damageToPeer;
    float damageTaken;
    internal bool applyingHostDamage;
    internal byte attacks;
    PlayerMovement lastMe;

    struct State
    {
        public string Scene, Weapon;
        public Vector3 Pos;
        public float Yaw, DamageToPeer;
        public bool Moving, Sprinting, Dead;
        public byte Attacks;
        public string[] Loadout;
    }

    void Awake()
    {
        Instance = this;
        Debug.Assert(SelfCheck(), "ThronefallMP: packet round-trip failed");

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
        shotPath = After("--mp-shot");

        if (args.Contains("--mp-host"))
        {
            role = "host";
            udp = new UdpClient(int.TryParse(After("--mp-host"), out var p) ? p : DefaultPort);
        }
        else if (args.Contains("--mp-join"))
        {
            role = "client";
            var target = (After("--mp-join") ?? "127.0.0.1").Split(':');
            peer = new IPEndPoint(IPAddress.Parse(target[0]), target.Length > 1 ? int.Parse(target[1]) : DefaultPort);
            udp = new UdpClient(0);
        }

        if (udp != null)
        {
            // Two instances on one Mac: the unfocused one must keep simulating.
            Application.runInBackground = true;
            // Delayed: the game re-applies its saved video settings during startup.
            Invoke(nameof(Windowed), 4f);
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

        if (udp == null) return;

        var me = PlayerMovement.instance;
        bool rebase = me != lastMe;
        lastMe = me;

        while (udp.Available > 0)
        {
            IPEndPoint from = null;
            try
            {
                var data = udp.Receive(ref from);
                remote = Decode(data);
                // shortcut: one peer, last sender wins, no handshake/auth. Replace with a real
                // transport (LiteNetLib / Steam P2P) when gameplay sync starts.
                if (role == "host") peer = from;
                if (!PeerAlive)
                {
                    Logger.LogInfo($"Peer connected: {from}");
                    rebase = true;
                }
                lastHeard = Time.unscaledTime;
            }
            catch (Exception e) when (e is SocketException || e is IOException || e is FormatException)
            {
                // Malformed or ICMP-rejected datagram: ignore, the next one is 50 ms away.
            }
        }

        // Damage dealt before we (re)joined or respawned is history, not ours to take.
        if (rebase) damageTaken = remote.DamageToPeer;
        TakeHostDamage(me);

        if (peer != null && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + SendInterval;
            // Sent even without a player (Scene "") so the host learns our address from the menu.
            var state = new State { Scene = "" };
            if (me != null)
            {
                var equipped = PerkManager.instance.CurrentlyEquipped;
                state = new State
                {
                    Scene = me.gameObject.scene.name,
                    Pos = me.transform.position,
                    Yaw = me.meshParent.eulerAngles.y,
                    Moving = me.Moving,
                    Sprinting = me.Sprinting,
                    Dead = me.Hp != null && me.Hp.KnockedOut,
                    Attacks = attacks,
                    DamageToPeer = damageToPeer,
                    Weapon = equipped.OfType<EquippableWeapon>().FirstOrDefault()?.name,
                    // shortcut: loadout resent 20x/s instead of once reliably; fine on LAN,
                    // move to a reliable message with the real transport.
                    Loadout = equipped.Select(e => e.name).ToArray(),
                };
            }
            var bytes = Encode(state);
            try { udp.Send(bytes, bytes.Length, peer); } catch (SocketException) { }
        }

        // Test only: park the avatar on the nearest enemy so the damage path is exercised
        // without anyone steering the client.
        if (testChase && TagManager.instance != null && ghost != null)
        {
            var enemy = TagManager.instance.FindClosestTaggedObjectWithTags(ghost.transform.position,
                new List<TagManager.ETag> { TagManager.ETag.EnemyOwned }, new List<TagManager.ETag>());
            if (enemy != null) remote.Pos = enemy.transform.position;
        }

        FollowHost();
        UpdateGhost(me);
    }

    bool PeerAlive => Time.unscaledTime - lastHeard < PeerTimeout;

    /// Client only: the host decides what hurts us (see DamagePatch), we apply it to our player.
    void TakeHostDamage(PlayerMovement me)
    {
        float owed = remote.DamageToPeer - damageTaken;
        if (role != "client" || me == null || me.Hp == null || owed <= 0f) return;
        damageTaken = remote.DamageToPeer;
        applyingHostDamage = true;
        try { me.Hp.TakeDamage(owed); }
        finally { applyingHostDamage = false; }
    }

    /// Client only: whenever the host's player is in a different scene, go there with the
    /// host's perks. We keep our own weapon if we have one.
    void FollowHost()
    {
        var stm = SceneTransitionManager.instance;
        if (role != "client" || !PeerAlive || string.IsNullOrEmpty(remote.Scene)) return;
        if (stm == null || stm.SceneTransitionIsRunning || PerkManager.instance == null) return;
        if (SceneManager.GetActiveScene().name == remote.Scene) return;

        ApplyEquipFlag();
        var equipped = PerkManager.instance.CurrentlyEquipped;
        var ownWeapon = equipped.OfType<EquippableWeapon>().FirstOrDefault();
        var fromHost = PerkManager.instance.allEquippables
            .Where(e => remote.Loadout.Contains(e.name) && (ownWeapon == null || !(e is EquippableWeapon)))
            .ToList();
        equipped.Clear();
        equipped.AddRange(fromHost);
        if (ownWeapon != null) equipped.Add(ownWeapon);
        Logger.LogInfo($"Following host to {remote.Scene} with [{string.Join(", ", equipped.Select(e => e.name))}]");
        EnterLevel(remote.Scene);
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

    /// Called from PlayerSpawnPatch before any of the player's scripts have started, which is
    /// the only moment the player still carries every weapon model. We clone it and keep only
    /// visuals, colliders, tags and health: enough for enemies to see and attack it. Cloned
    /// under an inactive parent so no game script on the copy ever runs (several of them
    /// overwrite static singletons).
    internal void BuildGhost(PlayerMovement me)
    {
        if (udp == null) return;

        var holder = new GameObject("MP Ghost holder");
        holder.SetActive(false);
        ghost = Instantiate(me.gameObject, holder.transform);
        ghost.name = "MP Ghost";

        var movement = ghost.GetComponent<PlayerMovement>();
        ghostMesh = movement.meshParent;
        ghostAnimator = movement.meshAnimator;
        ghostHp = ghost.GetComponent<Hp>();
        var tag = ghost.GetComponent<TaggedObject>();

        ghostWeapons.Clear();
        foreach (var w in ghost.GetComponentsInChildren<WeaponEquipper>(true))
            ghostWeapons.Add((w.requiredWeapon != null ? w.requiredWeapon.name : null, w.gameObject, w.visuals,
                w.visuals != null ? w.visuals.GetComponent<PlayerAttackAnimator>()?.animations : null));

        // Game scripts first: several [RequireComponent] the pathfinding/controller ones.
        bool Strip(Component c) => c != null && c != ghostHp && c != tag && !(c is OneShotAnimationBase);
        var all = ghost.GetComponentsInChildren<Component>(true);
        foreach (var c in all)
            if (Strip(c) && c is MonoBehaviour && c.GetType().Assembly == typeof(PlayerMovement).Assembly)
                DestroyImmediate(c);
        foreach (var c in all)
            if (Strip(c) && (c is MonoBehaviour || c is CharacterController))
                DestroyImmediate(c);

        ghost.SetActive(false);
        ghost.transform.SetParent(null, true);
        // During a transition the active scene is still the one being unloaded.
        SceneManager.MoveGameObjectToScene(ghost, me.gameObject.scene);
        Destroy(holder);
        damageToPeer = 0f;
        Logger.LogInfo($"Avatar built for {me.gameObject.scene.name}, weapons=[{string.Join(", ", ghostWeapons.Select(w => w.name))}]");
    }

    void UpdateGhost(PlayerMovement me)
    {
        if (ghost == null) return;

        bool show = me != null && PeerAlive && !remote.Dead && remote.Scene == me.gameObject.scene.name;
        if (show != ghost.activeSelf)
        {
            ghost.SetActive(show);
            if (!show)
            {
                Logger.LogInfo($"Avatar hidden (peer {(PeerAlive ? (remote.Dead ? "is dead" : "left the scene") : "lost")})");
                return;
            }
            ghost.transform.position = remote.Pos;
            seenAttacks = remote.Attacks;
            Logger.LogInfo($"Avatar shown in {remote.Scene} at {remote.Pos}, weapon={remote.Weapon ?? "none"}");
            Invoke(nameof(LogTargetable), 1f);
            if (shotPath != null && !shotTaken) Invoke(nameof(Shot), 2f);
            if (testNight && role == "host") Invoke(nameof(TestNight), 3f);
        }
        if (!show) return;

        var t = ghost.transform;
        t.position = Vector3.Lerp(t.position, remote.Pos, 15f * Time.unscaledDeltaTime);
        ghostMesh.rotation = Quaternion.Euler(0f, remote.Yaw, 0f);
        ghostAnimator.SetBool("Moving", remote.Moving);
        ghostAnimator.SetBool("Sprinting", remote.Sprinting);

        // Same rule as the game's PlayerWeaponVisuals: the weapon is drawn at night only.
        bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
        bool attacked = remote.Attacks != seenAttacks;
        seenAttacks = remote.Attacks;
        foreach (var w in ghostWeapons)
        {
            bool equipped = w.name != null && w.name == remote.Weapon;
            w.root.SetActive(equipped);
            if (!equipped || w.visuals == null) continue;
            w.visuals.SetActive(night);
            if (attacked && night && w.anims != null)
                foreach (var a in w.anims) a.Trigger();
        }

        // Game code reads Players[0] as "the" player; keep the local one first.
        var tag = ghostHp.TaggedObj;
        if (tag != null && TagManager.instance.Players[0] == tag)
        {
            tag.enabled = false;
            tag.enabled = true;
        }
    }

    /// Enemies pick targets from TagManager by tag, so this is what "targetable" means.
    void LogTargetable()
    {
        var tag = ghostHp != null ? ghostHp.TaggedObj : null;
        if (tag == null) return;
        Logger.LogInfo($"Avatar targetable: players={TagManager.instance.Players.Count}, "
            + $"alive={tag.Tags.Contains(TagManager.ETag.AUTO_Alive)}, tags=[{string.Join(",", tag.Tags)}]");
    }

    void TestNight()
    {
        testNight = false;
        var castle = CastleCenter.instance.GetComponentInParent<BuildSlot>();
        castle.ExecuteBuildOrUpgrade(PlayerInteraction.instance, false);
        Logger.LogInfo($"Test night: castle at {castle.transform.position}, me at {PlayerMovement.instance.transform.position}");
        Invoke(nameof(TestNightStart), 2f);
    }

    void TestNightStart()
    {
        DayNightCycle.Instance.SwitchToNight();
        if (shotPath != null) Invoke(nameof(Shot), 8f);
        InvokeRepeating(nameof(TestStatus), 5f, 10f);
        testChase = true;
    }

    void TestStatus() => Logger.LogInfo($"Test status: {DayNightCycle.Instance.CurrentTimestate}, "
        + $"enemies={EnemySpawner.instance.NumberOfEnemiesOnTheMap}, avatar at {ghost.transform.position} "
        + $"active={ghost.activeSelf}, damageToPeer={damageToPeer}, my hp={PlayerMovement.instance.Hp.HpValue}");

    void Windowed() => Screen.SetResolution(1280, 720, FullScreenMode.Windowed);

    void Shot()
    {
        shotTaken = true;
        ScreenCapture.CaptureScreenshot(shotPath);
    }

    internal void Log(string message) => Logger.LogInfo(message);

    void OnGUI()
    {
        string status = role == "off" ? "single player" : $"{role} · peer {(PeerAlive ? "connected" : "waiting…")}";
        GUI.Label(new Rect(10, 10, 600, 24), $"Thronefall MP {Info.Metadata.Version} · {status}");
    }

    void OnDestroy() => udp?.Close();

    static byte[] Encode(State s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Protocol);
        w.Write(s.Scene ?? "");
        w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z);
        w.Write(s.Yaw);
        w.Write((byte)((s.Moving ? 1 : 0) | (s.Sprinting ? 2 : 0) | (s.Dead ? 4 : 0)));
        w.Write(s.Attacks);
        w.Write(s.DamageToPeer);
        w.Write(s.Weapon ?? "");
        var loadout = s.Loadout ?? Array.Empty<string>();
        w.Write((byte)loadout.Length);
        foreach (var name in loadout) w.Write(name);
        return ms.ToArray();
    }

    static State Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadByte() != Protocol) throw new FormatException("protocol mismatch");
        var s = new State { Scene = r.ReadString() };
        s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.Yaw = r.ReadSingle();
        byte flags = r.ReadByte();
        s.Moving = (flags & 1) != 0;
        s.Sprinting = (flags & 2) != 0;
        s.Dead = (flags & 4) != 0;
        s.Attacks = r.ReadByte();
        s.DamageToPeer = r.ReadSingle();
        s.Weapon = r.ReadString();
        if (s.Weapon == "") s.Weapon = null;
        s.Loadout = new string[r.ReadByte()];
        for (int i = 0; i < s.Loadout.Length; i++) s.Loadout[i] = r.ReadString();
        return s;
    }

    static bool SelfCheck()
    {
        var a = new State
        {
            Scene = "x", Pos = new Vector3(1, 2, 3), Yaw = 90f, Sprinting = true, Dead = true,
            Attacks = 7, DamageToPeer = 12.5f, Weapon = "Bow", Loadout = new[] { "Bow", "Perk" },
        };
        var b = Decode(Encode(a));
        return b.Scene == a.Scene && b.Pos == a.Pos && b.Yaw == a.Yaw && b.Sprinting && !b.Moving && b.Dead
            && b.Attacks == 7 && b.DamageToPeer == 12.5f && b.Weapon == "Bow" && b.Loadout.SequenceEqual(a.Loadout);
    }
}

/// With --mp-profile, saves go to a sub-folder so a second local instance
/// cannot overwrite the real save file.
[HarmonyPatch(typeof(SteamSaveServices), nameof(SteamSaveServices.GetFullSavePath))]
static class SaveProfilePatch
{
    static void Postfix(string filename, ref string __result)
    {
        if (string.IsNullOrEmpty(Plugin.Profile)) return;
        var dir = Path.Combine(Application.persistentDataPath, "mp-" + Plugin.Profile);
        Directory.CreateDirectory(dir);
        __result = Path.Combine(dir, filename);
    }
}

[HarmonyPatch(typeof(PlayerMovement), "Awake")]
static class PlayerSpawnPatch
{
    static void Postfix(PlayerMovement __instance) => Plugin.Instance.BuildGhost(__instance);
}

[HarmonyPatch(typeof(PlayerAttackAnimator), nameof(PlayerAttackAnimator.TriggerAnimations))]
static class AttackPatch
{
    // The avatar has no PlayerAttackAnimator, so every call is the local player attacking.
    static void Postfix() => Plugin.Instance.attacks++;
}

/// The host owns damage. Hits on the avatar are not applied to it but counted and sent to
/// the player it stands for; the client's own enemies cannot hurt the client's player.
[HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
static class DamagePatch
{
    static bool Prefix(Hp __instance, float _amount, TaggedObject _damageComingFrom)
    {
        var p = Plugin.Instance;
        if (_amount <= 0f) return true; // healing

        if (p.ghostHp != null && __instance == p.ghostHp)
        {
            if (p.role == "host")
            {
                if (p.damageToPeer == 0f)
                    p.Log($"Avatar was attacked by {(_damageComingFrom != null ? _damageComingFrom.name : "unknown")} for {_amount}");
                p.damageToPeer += _amount;
            }
            return false;
        }

        var me = PlayerMovement.instance;
        if (p.role == "client" && me != null && __instance == me.Hp)
        {
            if (!p.applyingHostDamage) return false;
            p.Log($"Took {_amount} damage from the host's world");
        }
        return true;
    }
}
