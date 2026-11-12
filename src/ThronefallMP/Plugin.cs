using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using HarmonyLib;
using Thronefall.SaveServices;
using UnityEngine;

namespace ThronefallMP;

/// Prototype: two instances exchange their player's position over UDP and each
/// shows the other as a visual-only "ghost". No gameplay is synced yet.
///
/// Command line:
///   --mp-host [port]         listen (default port 27015)
///   --mp-join [ip[:port]]    connect (default 127.0.0.1:27015)
///   --mp-profile name        keep saves in a separate sub-folder
///   --mp-autostart           skip the title screen (dev convenience)
///   --mp-shot file.png       save a screenshot once the ghost appears (dev/CI check)
[BepInPlugin("dev.thronefallmp", "Thronefall MP", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    const int DefaultPort = 27015;
    const float SendInterval = 1f / 20f;
    const float PeerTimeout = 3f;

    internal static string Profile;

    string role = "off";
    UdpClient udp;
    IPEndPoint peer;
    float nextSend, lastHeard = -999f;
    State remote;
    GameObject ghost;
    Animator ghostAnimator;
    bool autostart;
    string shotPath;

    struct State
    {
        public string Scene;
        public Vector3 Pos;
        public float Yaw;
        public bool Moving, Sprinting;
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
            SceneTransitionManager.instance.TransitionFromNullToLevelSelect();
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
        if (me != null && peer != null && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + SendInterval;
            var bytes = Encode(new State
            {
                Scene = me.gameObject.scene.name,
                Pos = me.transform.position,
                Yaw = me.meshParent.eulerAngles.y,
                Moving = me.Moving,
                Sprinting = me.Sprinting,
            });
            try { udp.Send(bytes, bytes.Length, peer); } catch (SocketException) { }
        }

        UpdateGhost(me);
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
            ghost.transform.position = remote.Pos;
        }

        var t = ghost.transform;
        t.position = Vector3.Lerp(t.position, remote.Pos, 15f * Time.unscaledDeltaTime);
        t.rotation = Quaternion.Euler(0f, remote.Yaw, 0f);
        if (ghostAnimator != null)
        {
            ghostAnimator.SetBool("Moving", remote.Moving);
            ghostAnimator.SetBool("Sprinting", remote.Sprinting);
        }
    }

    /// Clone only the local player's visuals. Cloned under an inactive parent so no
    /// game script on the copy ever runs Awake before we strip it.
    GameObject MakeGhost(PlayerMovement me)
    {
        var holder = new GameObject("MP Ghost holder");
        holder.SetActive(false);
        var clone = Instantiate(me.meshParent.gameObject, holder.transform);
        clone.name = "MP Ghost";

        foreach (var c in Enumerable.Reverse(clone.GetComponentsInChildren<Component>(true)))
            if (c is MonoBehaviour || c is Collider)
                DestroyImmediate(c);

        clone.transform.SetParent(null, false);
        clone.transform.localScale = me.meshParent.lossyScale;
        Destroy(holder);
        ghostAnimator = clone.GetComponentInChildren<Animator>(true);
        Logger.LogInfo($"Ghost spawned in {remote.Scene} at {remote.Pos}");
        if (shotPath != null) Invoke(nameof(Shot), 2f);
        return clone;
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
        w.Write(s.Scene ?? "");
        w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z);
        w.Write(s.Yaw);
        w.Write((byte)((s.Moving ? 1 : 0) | (s.Sprinting ? 2 : 0)));
        return ms.ToArray();
    }

    static State Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var s = new State { Scene = r.ReadString() };
        s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.Yaw = r.ReadSingle();
        byte flags = r.ReadByte();
        s.Moving = (flags & 1) != 0;
        s.Sprinting = (flags & 2) != 0;
        return s;
    }

    static bool SelfCheck()
    {
        var a = new State { Scene = "x", Pos = new Vector3(1, 2, 3), Yaw = 90f, Sprinting = true };
        var b = Decode(Encode(a));
        return b.Scene == a.Scene && b.Pos == a.Pos && b.Yaw == a.Yaw && b.Sprinting && !b.Moving;
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
