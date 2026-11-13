using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ThronefallMP;

/// The other player: its 20 Hz state packet, the avatar that represents it in our world, and
/// the damage the host's enemies deal to that avatar (forwarded to the client's real player).
class PlayerSync
{
    internal struct State
    {
        public string Scene, Weapon;
        public Vector3 Pos;
        public float Yaw, DamageToPeer;
        public bool Moving, Sprinting, Dead, EternalTrial;
        public byte Attacks;
        public string[] Loadout;
    }

    readonly Plugin p;
    internal State Remote;
    internal event Action AvatarShown;

    // Avatar of the remote player; rebuilt for every scene, inactive while the peer is absent.
    internal GameObject ghost;
    Transform ghostMesh;
    Animator ghostAnimator;
    internal Hp ghostHp;
    readonly List<(string name, GameObject root, GameObject visuals, List<OneShotAnimationBase> anims)> ghostWeapons = new();
    byte seenAttacks;
    bool localDead;

    // Host: total damage its enemies dealt to the avatar. Client: how much of that it has taken.
    internal float damageToPeer;
    float damageTaken;
    internal bool applyingHostDamage;
    internal byte attacks;

    // Client: host damage is logged once per burst (hits less than BurstGap apart), and at
    // least every BurstMax seconds while a burst keeps going.
    const float BurstGap = 3f, BurstMax = 10f;
    float burstDamage, lastHitTime, burstStart;
    int burstHits;

    public PlayerSync(Plugin plugin) => p = plugin;

    internal void OnState(byte[] data) => Remote = Decode(data);

    /// `rebase`: we just (re)joined or respawned; damage dealt before is history, not ours to take.
    internal void Tick(PlayerMovement me, bool rebase)
    {
        if (rebase) damageTaken = Remote.DamageToPeer;
        // Each player is knocked out and revived (AutoRevive, dawn) only in its own world; the
        // peer sees it through State.Dead, which hides the avatar. Logged to verify both sides.
        bool dead = me != null && me.Hp != null && me.Hp.KnockedOut;
        if (me != null && dead != localDead) p.Log(dead ? "Local player knocked out" : $"Local player revived (hp {me.Hp.HpValue:0.#})");
        localDead = dead;
        TakeHostDamage(me);
        FlushDamageBurst();
    }

    internal byte[] Snapshot(PlayerMovement me)
    {
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
                EternalTrial = LocalGamestate.SelectedGameMode == LocalGamestate.GameMode.EternalTrial,
                Attacks = attacks,
                DamageToPeer = damageToPeer,
                Weapon = equipped.OfType<EquippableWeapon>().FirstOrDefault()?.name,
                // shortcut: loadout resent 20x/s instead of once reliably; fine on LAN, move to
                // a reliable message when the Steam transport arrives.
                Loadout = equipped.Select(e => e.name).ToArray(),
            };
        }
        return Encode(state);
    }

    /// Test only: park the avatar on the nearest enemy so the damage path is exercised
    /// without anyone steering the client.
    internal void ChaseNearestEnemy()
    {
        if (TagManager.instance == null || ghost == null) return;
        var enemy = TagManager.instance.FindClosestTaggedObjectWithTags(ghost.transform.position,
            new List<TagManager.ETag> { TagManager.ETag.EnemyOwned }, new List<TagManager.ETag>());
        if (enemy != null) Remote.Pos = enemy.transform.position;
    }

    /// Client only: the host decides what hurts us (see DamagePatch), we apply it to our player.
    void TakeHostDamage(PlayerMovement me)
    {
        float owed = Remote.DamageToPeer - damageTaken;
        if (p.role != "client" || me == null || me.Hp == null || owed <= 0f) return;
        damageTaken = Remote.DamageToPeer;
        applyingHostDamage = true;
        try { me.Hp.TakeDamage(owed); }
        finally { applyingHostDamage = false; }
    }

    internal void NoteHostDamage(float amount)
    {
        if (burstHits == 0) burstStart = Time.unscaledTime;
        burstDamage += amount;
        burstHits++;
        lastHitTime = Time.unscaledTime;
    }

    internal void FlushDamageBurst(bool force = false)
    {
        if (burstHits == 0) return;
        float now = Time.unscaledTime;
        if (!force && now - lastHitTime < BurstGap && now - burstStart < BurstMax) return;
        var hp = PlayerMovement.instance != null ? PlayerMovement.instance.Hp : null;
        p.Log($"Took {burstDamage} damage from the host's world ({burstHits} hit{(burstHits == 1 ? "" : "s")}"
            + (hp != null ? $", hp now {hp.HpValue}{(hp.KnockedOut ? ", knocked out" : "")})" : ")"));
        burstDamage = 0f;
        burstHits = 0;
    }

    /// Called from PlayerSpawnPatch before any of the player's scripts have started, which is
    /// the only moment the player still carries every weapon model. We clone it and keep only
    /// visuals, colliders, tags and health: enough for enemies to see and attack it. Cloned
    /// under an inactive parent so no game script on the copy ever runs (several of them
    /// overwrite static singletons).
    internal void BuildGhost(PlayerMovement me)
    {
        if (p.role == "off") return;

        var holder = new GameObject("MP Ghost holder");
        holder.SetActive(false);
        ghost = Object.Instantiate(me.gameObject, holder.transform);
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
                Object.DestroyImmediate(c);
        foreach (var c in all)
            if (Strip(c) && (c is MonoBehaviour || c is CharacterController))
                Object.DestroyImmediate(c);

        ghost.SetActive(false);
        ghost.transform.SetParent(null, true);
        // During a transition the active scene is still the one being unloaded.
        SceneManager.MoveGameObjectToScene(ghost, me.gameObject.scene);
        Object.Destroy(holder);
        damageToPeer = 0f;
        p.Log($"Avatar built for {me.gameObject.scene.name}, weapons=[{string.Join(", ", ghostWeapons.Select(w => w.name))}]");
    }

    internal void UpdateGhost(PlayerMovement me)
    {
        if (ghost == null) return;

        bool show = me != null && p.PeerAlive && !Remote.Dead && Remote.Scene == me.gameObject.scene.name;
        if (show != ghost.activeSelf)
        {
            ghost.SetActive(show);
            if (!show)
            {
                KeepPlayersList(false);
                p.Log($"Avatar hidden (peer {(p.PeerAlive ? (Remote.Dead ? "is dead" : "left the scene") : "lost")})");
                return;
            }
            ghost.transform.position = Remote.Pos;
            seenAttacks = Remote.Attacks;
            p.Log($"Avatar shown in {Remote.Scene} at {Remote.Pos}, weapon={Remote.Weapon ?? "none"}");
            p.After(1f, LogTargetable);
            AvatarShown?.Invoke();
        }
        if (!show) return;

        var t = ghost.transform;
        t.position = Vector3.Lerp(t.position, Remote.Pos, 15f * Time.unscaledDeltaTime);
        ghostMesh.rotation = Quaternion.Euler(0f, Remote.Yaw, 0f);
        ghostAnimator.SetBool("Moving", Remote.Moving);
        ghostAnimator.SetBool("Sprinting", Remote.Sprinting);

        // Same rule as the game's PlayerWeaponVisuals: the weapon is drawn at night only.
        bool night = DayNightCycle.Instance != null && DayNightCycle.Instance.CurrentTimestate == DayNightCycle.Timestate.Night;
        bool attacked = Remote.Attacks != seenAttacks;
        seenAttacks = Remote.Attacks;
        foreach (var w in ghostWeapons)
        {
            bool equipped = w.name != null && w.name == Remote.Weapon;
            w.root.SetActive(equipped);
            if (!equipped || w.visuals == null) continue;
            w.visuals.SetActive(night);
            if (attacked && night && w.anims != null)
                foreach (var a in w.anims) a.Trigger();
        }

        // Game code reads Players[0] as "the" player; keep the local one first.
        KeepPlayersList(true);
    }

    static readonly AccessTools.FieldRef<TagManager, List<TaggedObject>> PlayersField =
        AccessTools.FieldRefAccess<TagManager, List<TaggedObject>>("bufferedPlayers");

    /// TagManager adds to its player list on every OnEnable and never removes (game bug that
    /// single player never hits). Keep the avatar listed exactly once, never first, and only
    /// while it is shown.
    void KeepPlayersList(bool listed)
    {
        var tag = ghostHp != null ? ghostHp.TaggedObj : null;
        if (tag == null || TagManager.instance == null) return;
        var players = PlayersField(TagManager.instance);
        int count = players.Count(x => x == tag);
        bool first = players.Count > 0 && players[0] == tag;
        if (count == (listed ? 1 : 0) && !first) return;
        players.RemoveAll(x => x == tag);
        if (listed) players.Add(tag);
    }

    /// Enemies pick targets from TagManager by tag, so this is what "targetable" means.
    void LogTargetable()
    {
        var tag = ghostHp != null ? ghostHp.TaggedObj : null;
        if (tag == null) return;
        p.Log($"Avatar targetable: players={TagManager.instance.Players.Count}, "
            + $"alive={tag.Tags.Contains(TagManager.ETag.AUTO_Alive)}, tags=[{string.Join(",", tag.Tags)}]");
    }

    internal static byte[] Encode(State s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(s.Scene ?? "");
        w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z);
        w.Write(s.Yaw);
        w.Write((byte)((s.Moving ? 1 : 0) | (s.Sprinting ? 2 : 0) | (s.Dead ? 4 : 0) | (s.EternalTrial ? 8 : 0)));
        w.Write(s.Attacks);
        w.Write(s.DamageToPeer);
        w.Write(s.Weapon ?? "");
        var loadout = s.Loadout ?? Array.Empty<string>();
        w.Write((byte)loadout.Length);
        foreach (var name in loadout) w.Write(name);
        return ms.ToArray();
    }

    internal static State Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var s = new State { Scene = r.ReadString() };
        s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        s.Yaw = r.ReadSingle();
        byte flags = r.ReadByte();
        s.Moving = (flags & 1) != 0;
        s.Sprinting = (flags & 2) != 0;
        s.Dead = (flags & 4) != 0;
        s.EternalTrial = (flags & 8) != 0;
        s.Attacks = r.ReadByte();
        s.DamageToPeer = r.ReadSingle();
        s.Weapon = r.ReadString();
        if (s.Weapon == "") s.Weapon = null;
        s.Loadout = new string[r.ReadByte()];
        for (int i = 0; i < s.Loadout.Length; i++) s.Loadout[i] = r.ReadString();
        return s;
    }

    internal static bool SelfCheck()
    {
        var a = new State
        {
            Scene = "x", Pos = new Vector3(1, 2, 3), Yaw = 90f, Sprinting = true, Dead = true, EternalTrial = true,
            Attacks = 7, DamageToPeer = 12.5f, Weapon = "Bow", Loadout = new[] { "Bow", "Perk" },
        };
        var b = Decode(Encode(a));
        return b.Scene == a.Scene && b.Pos == a.Pos && b.Yaw == a.Yaw && b.Sprinting && !b.Moving && b.Dead && b.EternalTrial
            && b.Attacks == 7 && b.DamageToPeer == 12.5f && b.Weapon == "Bow" && b.Loadout.SequenceEqual(a.Loadout);
    }
}
