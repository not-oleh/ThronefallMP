using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ThronefallMP;

/// Host-authoritative enemies (M3 slice 1).
///  - Host: every enemy in `TagManager.EnemyUnits` gets a network id (per match epoch). Spawns and
///    despawns go as reliable messages; position, yaw and hp of all live enemies, plus the hp of
///    damaged buildings, go in the 20 Hz state packet.
///  - Client: its own waves never spawn (SpawnPatch, while the host is in the scene). Each host
///    enemy becomes a puppet: the same prefab, stripped to visuals, Hp, TaggedObject and the
///    position-driven walk bounce. Puppets are targetable but take no local damage (DamagePatch);
///    a host kill plays the game's own death (VFX, destroy) with coins and on-death spawns removed.
class EnemySync
{
    internal const byte MsgSpawn = 20, MsgDespawn = 21;
    internal const byte StateKind = 1;
    static readonly AccessTools.FieldRef<Hp, Weapon> DeathAttackField = AccessTools.FieldRefAccess<Hp, Weapon>("spwanAttackOnDeath");

    struct Info { public int Epoch; public string Scene, Key; public Vector3 Pos; public bool Elite; public float MaxHp; }
    class Puppet { public GameObject Go; public Hp Hp; public Vector3 Pos; public float Yaw; }

    readonly Plugin p;
    readonly Link link;
    int epoch = Random.Range(1, int.MaxValue);

    // Host
    readonly Dictionary<TaggedObject, (ushort id, Hp hp)> tracked = new();
    readonly Dictionary<ushort, Info> live = new();
    ushort nextId = 1;
    PlayerInteraction trackedFor;
    readonly List<TaggedObject> gone = new();

    // Client
    readonly Dictionary<ushort, Info> known = new();
    readonly Dictionary<ushort, Puppet> puppets = new();
    readonly HashSet<Hp> puppetHps = new();
    readonly Dictionary<string, GameObject> prefabs = new();
    internal bool applying;
    EnemySpawner suppressedLoggedFor;
    int built, killed;

    // Both: building hp, indexed by sorted slot id (identical on both instances).
    List<Hp> buildings;
    PlayerInteraction buildingsFor;
    readonly Dictionary<ushort, float> hostBuildingHp = new();
    bool buildingsFresh;

    public EnemySync(Plugin plugin, Link link)
    {
        p = plugin;
        this.link = link;
        link.PeerRestarted += ResendAll;
    }

    internal void ResendAll()
    {
        if (p.role != "host") return;
        foreach (var kv in live) SendSpawn(kv.Key, kv.Value);
    }

    /// Client: the host owns waves while it is in our scene.
    internal bool SuppressLocalWaves()
    {
        var spawner = EnemySpawner.instance;
        if (spawner == null || !p.Match.FrozenByHost(spawner.gameObject.scene.name)) return false;
        if (suppressedLoggedFor != spawner) { suppressedLoggedFor = spawner; p.Log($"Enemies: local spawning suppressed in {spawner.gameObject.scene.name} (host owns waves)"); }
        return true;
    }

    internal bool IsPuppet(Hp hp) => puppetHps.Contains(hp);

    // ---------- host ----------

    /// Host: called every state tick; returns the enemy state body (null when not in a match).
    internal byte[] HostSnapshot(PlayerMovement me)
    {
        if (p.role != "host" || me == null || TagManager.instance == null) return null;
        var pi = PlayerInteraction.instance;
        if (trackedFor != pi)
        {
            trackedFor = pi; tracked.Clear(); live.Clear(); nextId = 1; epoch = Random.Range(1, int.MaxValue);
        }
        string scene = me.gameObject.scene.name;

        foreach (var tag in TagManager.instance.EnemyUnits)
        {
            if (tag == null || tracked.ContainsKey(tag) || !tag.gameObject.activeInHierarchy || tag.gameObject.scene.name != scene) continue;
            var hp = tag.GetComponentInChildren<Hp>();
            if (hp == null) continue;
            ushort id = nextId++;
            tracked[tag] = (id, hp);
            var info = new Info
            {
                Epoch = epoch, Scene = scene, Key = KeyOf(tag.gameObject), Pos = tag.transform.position,
                Elite = hp.Elite, MaxHp = hp.maxHp,
            };
            live[id] = info;
            SendSpawn(id, info);
            if (id <= 5 || id % 20 == 0) p.Log($"Enemies: sent spawn {id} {info.Key} at {info.Pos} hp={info.MaxHp}");
        }

        gone.Clear();
        foreach (var kv in tracked)
            if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy || kv.Value.hp == null || kv.Value.hp.HpValue <= 0f) gone.Add(kv.Key);
        foreach (var tag in gone)
        {
            var (id, hp) = tracked[tag];
            // Dead enemies are destroyed (Hp.TakeDamage), so a missing object means a kill.
            bool killed = tag == null || hp == null || hp.HpValue <= 0f;
            tracked.Remove(tag);
            live.Remove(id);
            link.SendMessage(Write(w => { w.Write(MsgDespawn); w.Write(epoch); w.Write(id); w.Write(killed); }));
        }

        return Write(w =>
        {
            w.Write(StateKind); w.Write(epoch); w.Write(scene);
            // shortcut: full floats, ~20 bytes per enemy; a 200-enemy night is ~4 KB per datagram
            // (fine on loopback/LAN). Quantize or split before the Steam transport (MTU ~1.2 KB).
            w.Write((ushort)tracked.Count);
            foreach (var kv in tracked)
            {
                var (id, hp) = kv.Value;
                var t = kv.Key.transform;
                w.Write(id); w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                w.Write((byte)(t.eulerAngles.y / 360f * 256f)); w.Write(hp.HpValue);
            }
            var damaged = Buildings(scene).Select((h, i) => (h, i)).Where(x => x.h != null && x.h.HpValue < x.h.maxHp).ToList();
            w.Write((ushort)damaged.Count);
            foreach (var (h, i) in damaged) { w.Write((ushort)i); w.Write(Mathf.Max(0f, h.HpValue)); }
        });
    }

    void SendSpawn(ushort id, Info e) => link.SendMessage(Write(w =>
    {
        w.Write(MsgSpawn); w.Write(e.Epoch); w.Write(id); w.Write(e.Scene); w.Write(e.Key);
        w.Write(e.Pos.x); w.Write(e.Pos.y); w.Write(e.Pos.z); w.Write(e.Elite); w.Write(e.MaxHp);
    }));

    static string KeyOf(GameObject go) => go.name.Replace("(Clone)", "").Trim();

    List<Hp> Buildings(string scene)
    {
        var pi = PlayerInteraction.instance;
        if (buildings == null || buildingsFor != pi)
        {
            buildingsFor = pi;
            buildings = Object.FindObjectsByType<BuildSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(s => s.gameObject.scene.name == scene)
                .OrderBy(BuildSync.SlotId, System.StringComparer.Ordinal)
                .Select(s => s.BuildingParent != null ? s.BuildingParent.GetComponent<Hp>() : null)
                .ToList();
        }
        return buildings;
    }

    // ---------- client ----------

    internal void OnState(byte[] data)
    {
        if (p.role != "client") return;
        using var r = new BinaryReader(new MemoryStream(data));
        r.ReadByte();
        int ep = r.ReadInt32();
        string scene = r.ReadString();
        if (!InScene(scene) || ep != CurrentEpoch()) return;
        int n = r.ReadUInt16();
        for (int i = 0; i < n; i++)
        {
            ushort id = r.ReadUInt16();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float yaw = r.ReadByte() * 360f / 256f;
            float hp = r.ReadSingle();
            if (!puppets.TryGetValue(id, out var pu) || pu.Go == null) continue;
            pu.Pos = pos; pu.Yaw = yaw;
            if (pu.Hp != null && hp > 0f && Mathf.Abs(pu.Hp.HpValue - hp) > 0.01f) pu.Hp.SetHpTo(hp);
        }
        hostBuildingHp.Clear();
        int b = r.ReadUInt16();
        for (int i = 0; i < b; i++) hostBuildingHp[r.ReadUInt16()] = r.ReadSingle();
        buildingsFresh = true;
    }

    int CurrentEpoch() => known.Count > 0 ? known.Values.First().Epoch : 0;

    bool InScene(string scene)
    {
        var me = PlayerMovement.instance;
        var gs = LocalGamestate.Instance;
        return me != null && gs != null && gs.CurrentState == LocalGamestate.State.InMatch && me.gameObject.scene.name == scene;
    }

    internal void Tick(PlayerMovement me)
    {
        if (p.role != "client" || me == null) return;
        string scene = me.gameObject.scene.name;
        bool inMatch = InScene(scene);

        foreach (var kv in known)
        {
            if (!inMatch || kv.Value.Scene != scene) continue;
            if (!puppets.TryGetValue(kv.Key, out var pu) || pu.Go == null)
            {
                if (pu != null) continue; // built once and since destroyed by the scene: do not rebuild
                puppets[kv.Key] = Build(kv.Key, kv.Value) ?? new Puppet();
            }
        }

        float dt = Time.unscaledDeltaTime;
        foreach (var pu in puppets.Values)
        {
            if (pu.Go == null) continue;
            var t = pu.Go.transform;
            t.position = Vector3.Lerp(t.position, pu.Pos, 15f * dt);
            t.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(t.eulerAngles.y, pu.Yaw, 15f * dt), 0f);
        }

        if (inMatch && buildingsFresh) ApplyBuildings(scene);
    }

    Puppet Build(ushort id, Info e)
    {
        var prefab = Prefab(e.Key);
        if (prefab == null) { p.Log($"Enemies: no prefab '{e.Key}' here, enemy {id} not shown"); return null; }
        var holder = new GameObject("MP Enemy holder");
        holder.SetActive(false);
        var go = Object.Instantiate(prefab, e.Pos, Quaternion.identity, holder.transform);
        go.name = $"MP Enemy {id} {e.Key}";

        var hp = go.GetComponentInChildren<Hp>();
        var tag = go.GetComponent<TaggedObject>();
        // Keep visuals, health (+ its bar), tags, and the bounce that reads transform motion.
        bool Keep(Component c) => c is Hp || c is TaggedObject || c is SimpleWalk || c is Healthbar || c is HealthbarMulti;
        var all = go.GetComponentsInChildren<Component>(true);
        foreach (var c in all)
            if (c != null && c is MonoBehaviour && !Keep(c) && c.GetType().Assembly == typeof(Hp).Assembly) Object.DestroyImmediate(c);
        // Then A* pathfinding (Seeker, RVO); UI (health bar images) and rendering stay.
        foreach (var c in all)
            if (c != null && c is MonoBehaviour && (c.GetType().Namespace ?? "").StartsWith("Pathfinding")) Object.DestroyImmediate(c);
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;

        if (hp != null)
        {
            hp.maxHp = e.MaxHp;
            hp.coinCount = 0;
            hp.enemyToSpawnOnDeath = null;
            DeathAttackField(hp) = null;
            puppetHps.Add(hp);
        }
        if (e.Elite)
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.sharedMaterial != null && mr.sharedMaterial.name == "Enemy") mr.sharedMaterial = ColorAndLightManager.EliteEnemyMaterial;

        go.transform.SetParent(null, true);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, PlayerMovement.instance.gameObject.scene);
        Object.Destroy(holder);
        go.SetActive(true);
        if (built++ < 5 || built % 20 == 0)
            p.Log($"Enemies: puppet #{built} {go.name} at {e.Pos} hp={e.MaxHp}{(e.Elite ? " elite" : "")}, scripts=[{string.Join(",", go.GetComponentsInChildren<MonoBehaviour>(true).Select(c => c.GetType().Name).Distinct())}]");
        return new Puppet { Go = go, Hp = hp, Pos = e.Pos };
    }

    /// Prefabs referenced by the loaded scene are in memory; prefer the asset over a scene copy.
    GameObject Prefab(string key)
    {
        if (prefabs.TryGetValue(key, out var cached) && cached != null) return cached;
        GameObject found = null;
        foreach (var t in Resources.FindObjectsOfTypeAll<TaggedObject>())
        {
            if (t.gameObject.name != key || t.transform.parent != null || t.GetComponent<Hp>() == null) continue;
            if (!t.gameObject.scene.IsValid()) { found = t.gameObject; break; }
            found ??= t.gameObject;
        }
        prefabs[key] = found;
        return found;
    }

    void Kill(ushort id, bool killed)
    {
        known.Remove(id);
        if (!puppets.TryGetValue(id, out var pu)) return;
        puppets.Remove(id);
        if (pu.Go == null) return;
        if (pu.Hp != null) puppetHps.Remove(pu.Hp);
        if (killed && pu.Hp != null && pu.Hp.HpValue > 0f)
        {
            applying = true;
            try { pu.Hp.TakeDamage(pu.Hp.HpValue + 1f); }
            finally { applying = false; }
            if (++this.killed <= 5 || this.killed % 20 == 0) p.Log($"Enemies: host killed {pu.Go.name} (kill #{this.killed})");
        }
        if (pu.Go != null) Object.Destroy(pu.Go);
    }

    void ApplyBuildings(string scene)
    {
        buildingsFresh = false;
        var list = Buildings(scene);
        for (int i = 0; i < list.Count; i++)
        {
            var h = list[i];
            if (h == null || !h.gameObject.activeInHierarchy) continue;
            float target = hostBuildingHp.TryGetValue((ushort)i, out var v) ? v : h.maxHp;
            if (target <= 0f)
            {
                if (!h.KnockedOut) { h.TakeDamage(h.HpValue + 1f); p.Log($"Enemies: building {h.name} destroyed on host"); }
            }
            else if (h.KnockedOut) { h.Revive(false, Mathf.Clamp01(target / h.maxHp)); p.Log($"Enemies: building {h.name} revived on host"); }
            else if (Mathf.Abs(h.HpValue - target) > 0.01f) h.SetHpTo(Mathf.Min(target, h.maxHp));
        }
    }

    internal void OnMessage(byte[] data)
    {
        if (p.role != "client") return;
        using var r = new BinaryReader(new MemoryStream(data));
        byte kind = r.ReadByte();
        int ep = r.ReadInt32();
        if (kind == MsgSpawn)
        {
            ushort id = r.ReadUInt16();
            var e = new Info { Epoch = ep, Scene = r.ReadString(), Key = r.ReadString(), Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), Elite = r.ReadBoolean(), MaxHp = r.ReadSingle() };
            if (known.Count > 0 && CurrentEpoch() != ep) Clear();
            if (known.Count == 0) p.Log($"Enemies: following host enemies in {e.Scene} (epoch {ep})");
            known[id] = e;
            if (id <= 5 || id % 20 == 0) p.Log($"Enemies: got spawn {id} {e.Key} in {e.Scene}");
        }
        else if (kind == MsgDespawn && ep == CurrentEpoch())
        {
            ushort id = r.ReadUInt16();
            Kill(id, r.ReadBoolean());
        }
    }

    void Clear()
    {
        foreach (var id in puppets.Keys.ToList()) Kill(id, false);
        known.Clear();
        puppets.Clear();
        puppetHps.Clear();
        prefabs.Clear();
    }

    /// Test status line.
    internal string Status() => p.role == "host"
        ? $"tracked={tracked.Count} nextId={nextId}"
        : $"known={known.Count} puppets={puppets.Values.Count(x => x.Go != null)} missing={puppets.Values.Count(x => x.Go == null)}";

    static byte[] Write(System.Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        body(w);
        return ms.ToArray();
    }
}
