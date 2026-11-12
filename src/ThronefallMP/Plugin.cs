using System;
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
/// avatar that enemies can target, and the client follows the host's scene and loadout.
/// Each instance still simulates its own world; shared match state comes next.
///
/// Command line:
///   --mp-host [port]         listen (default port 27015)
///   --mp-join [ip[:port]]    connect (default 127.0.0.1:27015)
///   --mp-profile name        keep saves in a separate sub-folder
///   --mp-autostart [scene]   skip the title screen; with a scene name, load that level
///   --mp-shot file.png       save a screenshot once the ghost appears (dev/CI check)
[BepInPlugin("dev.thronefallmp", "Thronefall MP", "0.2.0")]
public class Plugin : BaseUnityPlugin
{
    const int DefaultPort = 27015;
    const float SendInterval = 1f / 20f;
    const float PeerTimeout = 3f;
    const byte Protocol = 2;

    internal static string Profile;

    string role = "off";
    UdpClient udp;
    IPEndPoint peer;
    float nextSend, lastHeard = -999f;
    State remote;
    GameObject ghost;
    Transform ghostMesh;
    Animator ghostAnimator;
    Hp ghostHp;
    TaggedObject ghostTag;
    bool autostart;
    string autostartScene, shotPath;

    struct State
    {
        public string Scene;
        public Vector3 Pos;
        public float Yaw;
        public bool Moving, Sprinting;
        public string[] Loadout;
    }

    void Awake()
    {
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
            if (autostartScene == null) SceneTransitionManager.instance.TransitionFromNullToLevelSelect();
            else EnterLevel(autostartScene);
        }

        if (udp == null) return;

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
                if (!PeerAlive) Logger.LogInfo($"Peer connected: {from}");
                lastHeard = Time.unscaledTime;
            }
            catch (Exception e) when (e is SocketException || e is IOException || e is FormatException)
            {
                // Malformed or ICMP-rejected datagram: ignore, the next one is 50 ms away.
            }
        }

        var me = PlayerMovement.instance;
        if (peer != null && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + SendInterval;
            // Sent even without a player (Scene "") so the host learns our address from the menu.
            var state = new State { Scene = "" };
            if (me != null)
                state = new State
                {
                    Scene = me.gameObject.scene.name,
                    Pos = me.transform.position,
                    Yaw = me.meshParent.eulerAngles.y,
                    Moving = me.Moving,
                    Sprinting = me.Sprinting,
                    // shortcut: loadout resent 20x/s instead of once reliably; fine on LAN,
                    // move to a reliable message with the real transport.
                    Loadout = PerkManager.instance.CurrentlyEquipped.Select(e => e.name).ToArray(),
                };
            var bytes = Encode(state);
            try { udp.Send(bytes, bytes.Length, peer); } catch (SocketException) { }
        }

        FollowHost();
        UpdateGhost(me);
    }

    /// Client only: whenever the host's player is in a different scene, go there
    /// with the host's loadout.
    void FollowHost()
    {
        var stm = SceneTransitionManager.instance;
        if (role != "client" || !PeerAlive || string.IsNullOrEmpty(remote.Scene)) return;
        if (stm == null || stm.SceneTransitionIsRunning || PerkManager.instance == null) return;
        if (SceneManager.GetActiveScene().name == remote.Scene) return;

        var equipped = PerkManager.instance.CurrentlyEquipped;
        equipped.Clear();
        equipped.AddRange(PerkManager.instance.allEquippables.Where(e => remote.Loadout.Contains(e.name)));
        Logger.LogInfo($"Following host to {remote.Scene} with {equipped.Count} equippables");
        EnterLevel(remote.Scene);
    }

    static void EnterLevel(string scene)
    {
        LocalGamestate.SelectedGameMode = LocalGamestate.GameMode.Classic;
        SceneTransitionManager.instance.TransitionFromNullToLevel(scene);
    }

    bool PeerAlive => Time.unscaledTime - lastHeard < PeerTimeout;

    void UpdateGhost(PlayerMovement me)
    {
        bool show = me != null && PeerAlive && remote.Scene == me.gameObject.scene.name;
        if (!show)
        {
            if (ghost != null) Destroy(ghost);
            return;
        }

        if (ghost == null)
        {
            ghost = MakeGhost(me);
        }

        var t = ghost.transform;
        t.position = Vector3.Lerp(t.position, remote.Pos, 15f * Time.unscaledDeltaTime);
        ghostMesh.rotation = Quaternion.Euler(0f, remote.Yaw, 0f);
        ghostAnimator.SetBool("Moving", remote.Moving);
        ghostAnimator.SetBool("Sprinting", remote.Sprinting);

        // shortcut: the avatar attracts enemies but takes no damage (the game clears this flag
        // every dusk, hence per frame). Real damage needs the client's health synced: M3.
        ghostHp.invulnerable = true;

        // Game code reads Players[0] as "the" player; keep the local one first.
        if (TagManager.instance.Players[0] == ghostTag)
        {
            ghostTag.enabled = false;
            ghostTag.enabled = true;
        }
    }

    /// Clone the local player, keeping only visuals, colliders, tags and health: enough for
    /// enemies to see and attack it. Cloned under an inactive parent so no game script on
    /// the copy runs before we strip it (several of them overwrite static singletons).
    GameObject MakeGhost(PlayerMovement me)
    {
        var holder = new GameObject("MP Ghost holder");
        holder.SetActive(false);
        var clone = Instantiate(me.gameObject, holder.transform);
        clone.name = "MP Ghost";

        var movement = clone.GetComponent<PlayerMovement>();
        ghostMesh = movement.meshParent;
        ghostAnimator = movement.meshAnimator;
        ghostHp = clone.GetComponent<Hp>();
        ghostTag = clone.GetComponent<TaggedObject>();

        // The game deletes unequipped weapon models in Start; our copy never runs that.
        foreach (var w in clone.GetComponentsInChildren<WeaponEquipper>(true))
            if (!PerkManager.IsEquipped(w.requiredWeapon))
                DestroyImmediate(w.gameObject);

        // Game scripts first: several [RequireComponent] the pathfinding/controller ones.
        bool Strip(Component c) => c != null && c != ghostHp && c != ghostTag;
        var all = clone.GetComponentsInChildren<Component>(true);
        foreach (var c in all)
            if (Strip(c) && c is MonoBehaviour && c.GetType().Assembly == typeof(PlayerMovement).Assembly)
                DestroyImmediate(c);
        foreach (var c in all)
            if (Strip(c) && (c is MonoBehaviour || c is CharacterController))
                DestroyImmediate(c);

        clone.transform.SetParent(null, true);
        Destroy(holder);
        clone.transform.position = remote.Pos;

        bool hitLogged = false;
        ghostHp.OnHit.AddListener(from =>
        {
            if (hitLogged) return;
            hitLogged = true;
            Logger.LogInfo($"Avatar was attacked by {(from != null ? from.name : "unknown")}");
        });

        Logger.LogInfo($"Avatar spawned in {remote.Scene} at {remote.Pos}");
        if (shotPath != null) Invoke(nameof(Shot), 2f);
        Invoke(nameof(LogTargetable), 1f);
        return clone;
    }

    /// Enemies pick targets from TagManager by tag, so this is what "targetable" means.
    void LogTargetable()
    {
        if (ghostTag == null) return;
        Logger.LogInfo($"Avatar targetable: players={TagManager.instance.Players.Count}, "
            + $"alive={ghostTag.Tags.Contains(TagManager.ETag.AUTO_Alive)}, tags=[{string.Join(",", ghostTag.Tags)}]");
    }

    void Windowed() => Screen.SetResolution(1280, 720, FullScreenMode.Windowed);

    void Shot() => ScreenCapture.CaptureScreenshot(shotPath);

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
        w.Write((byte)((s.Moving ? 1 : 0) | (s.Sprinting ? 2 : 0)));
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
        s.Loadout = new string[r.ReadByte()];
        for (int i = 0; i < s.Loadout.Length; i++) s.Loadout[i] = r.ReadString();
        return s;
    }

    static bool SelfCheck()
    {
        var a = new State { Scene = "x", Pos = new Vector3(1, 2, 3), Yaw = 90f, Sprinting = true, Loadout = new[] { "Bow" } };
        var b = Decode(Encode(a));
        return b.Scene == a.Scene && b.Pos == a.Pos && b.Yaw == a.Yaw && b.Sprinting && !b.Moving
            && b.Loadout.SequenceEqual(a.Loadout);
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
