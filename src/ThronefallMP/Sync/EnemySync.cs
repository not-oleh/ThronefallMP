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
    internal const byte MsgSpawn = 20, MsgDespawn = 21, MsgHit = 22;
    internal const byte StateKind = 1;
    /// Body budget per state datagram: Link adds 6 bytes; stays under a ~1.2 KB MTU.
    internal const int MaxStateBody = 1150;
    const int EnemyBytes = 2 + 12 + 1 + 4;
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
    int built, killed, hitsSent, hitsApplied;

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
    internal List<byte[]> HostSnapshot(PlayerMovement me)
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

        var enemies = tracked.Select(kv => (kv.Value.id, kv.Key.transform.position, (byte)(kv.Key.transform.eulerAngles.y / 360f * 256f), kv.Value.hp.HpValue)).ToList();
        var damaged = Buildings(scene).Select((h, i) => (h, i)).Where(x => x.h != null && x.h.HpValue < x.h.maxHp)
            .Select(x => ((ushort)x.i, Mathf.Max(0f, x.h.HpValue))).ToList();
        return EncodeState(epoch, scene, enemies, damaged);
    }

    /// State body: [kind][epoch][scene][first][buildings, first chunk only][n][enemies]. Split into
    /// as many datagrams as needed so each stays under MaxStateBody.
    internal static List<byte[]> EncodeState(int epoch, string scene, List<(ushort id, Vector3 pos, byte yaw, float hp)> enemies, List<(ushort index, float hp)> buildings)
    {
        var chunks = new List<byte[]>();
        int next = 0;
        do
        {
            bool first = chunks.Count == 0;
            chunks.Add(Write(w =>
            {
                w.Write(StateKind); w.Write(epoch); w.Write(scene); w.Write(first);
                if (first)
                {
                    // shortcut: buildings are not split; ~6 B each, fine up to ~150 damaged slots.
                    w.Write((ushort)buildings.Count);
                    foreach (var (i, hp) in buildings) { w.Write(i); w.Write(hp); }
                }
                w.Flush();
                int n = Mathf.Clamp((MaxStateBody - (int)w.BaseStream.Length - 2) / EnemyBytes, 0, enemies.Count - next);
                w.Write((ushort)n);
                for (int k = 0; k < n; k++, next++)
                {
                    var e = enemies[next];
                    w.Write(e.id); w.Write(e.pos.x); w.Write(e.pos.y); w.Write(e.pos.z); w.Write(e.yaw); w.Write(e.hp);
                }
            }));
        } while (next < enemies.Count);
        return chunks;
    }

    /// Appends the body's enemies; `buildings` is null unless this is the first chunk.
    internal static void DecodeState(byte[] data, out int epoch, out string scene, List<(ushort id, Vector3 pos, byte yaw, float hp)> enemies, out List<(ushort index, float hp)> buildings)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        r.ReadByte();
        epoch = r.ReadInt32();
        scene = r.ReadString();
        buildings = null;
        if (r.ReadBoolean())
        {
            buildings = new List<(ushort, float)>();
            int b = r.ReadUInt16();
            for (int i = 0; i < b; i++) buildings.Add((r.ReadUInt16(), r.ReadSingle()));
        }
        int n = r.ReadUInt16();
        for (int i = 0; i < n; i++)
            enemies.Add((r.ReadUInt16(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadByte(), r.ReadSingle()));
    }

    /// 300 enemies + 40 damaged buildings: every chunk fits the MTU budget and round-trips.
    internal static bool SelfCheck()
    {
        var en = Enumerable.Range(1, 300).Select(i => ((ushort)i, new Vector3(i, 0.5f, -i), (byte)i, i * 1.5f)).ToList();
        var bl = Enumerable.Range(0, 40).Select(i => ((ushort)i, i * 2f)).ToList();
        var chunks = EncodeState(42, "Nordfels", en, bl);
        var got = new List<(ushort id, Vector3 pos, byte yaw, float hp)>();
        int withBuildings = 0;
        foreach (var c in chunks)
        {
            if (c.Length > MaxStateBody) return false;
            DecodeState(c, out var ep, out var sc, got, out var b);
            if (ep != 42 || sc != "Nordfels") return false;
            if (b != null) { withBuildings++; if (!b.SequenceEqual(bl)) return false; }
        }
        var empty = EncodeState(1, "x", new(), new());
        return chunks.Count > 1 && withBuildings == 1 && got.SequenceEqual(en) && empty.Count == 1;
    }

    /// Host: a client's player hit one of our enemies. Applied as a real hit from the avatar,
    /// so death, coins and on-death effects happen here and replicate back.
    void ApplyHit(BinaryReader r, int ep)
    {
        ushort id = r.ReadUInt16();
        float amount = r.ReadSingle();
        if (ep != epoch || float.IsNaN(amount) || amount <= 0f) return;
        var entry = tracked.FirstOrDefault(kv => kv.Value.id == id);
        var hp = entry.Value.hp;
        if (entry.Key == null || hp == null || hp.HpValue <= 0f) return; // already dead or unknown
        amount = Mathf.Min(amount, hp.HpValue + 1f);
        bool died = hp.TakeDamage(amount, p.Players.ghostHp != null ? p.Players.ghostHp.TaggedObj : null, causedByPlayer: true);
        if (++hitsApplied <= 5 || hitsApplied % 20 == 0 || died)
            p.Log($"Enemies: applied client hit #{hitsApplied} on {id} {entry.Key.name}: {amount:0.#} dmg, hp now {hp.HpValue:0.#}{(died ? ", killed" : "")}");
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

    readonly List<(ushort id, Vector3 pos, byte yaw, float hp)> stateBuf = new();

    internal void OnState(byte[] data)
    {
        if (p.role != "client") return;
        stateBuf.Clear();
        DecodeState(data, out int ep, out string scene, stateBuf, out var blds);
        if (!InScene(scene) || ep != CurrentEpoch()) return;
        foreach (var (id, pos, yaw, hp) in stateBuf)
        {
            if (!puppets.TryGetValue(id, out var pu) || pu.Go == null) continue;
            pu.Pos = pos; pu.Yaw = yaw * 360f / 256f;
            if (pu.Hp != null && hp > 0f && Mathf.Abs(pu.Hp.HpValue - hp) > 0.01f) pu.Hp.SetHpTo(hp);
        }
        if (blds == null) return;
        hostBuildingHp.Clear();
        foreach (var (i, hp) in blds) hostBuildingHp[i] = hp;
        buildingsFresh = true;
    }

    /// Client: our player hit a puppet. The host applies it (ApplyHit); we never do.
    internal void SendHit(Hp puppet, float amount)
    {
        var id = puppets.FirstOrDefault(kv => kv.Value.Hp == puppet).Key;
        if (id == 0) return;
        link.SendMessage(Write(w => { w.Write(MsgHit); w.Write(CurrentEpoch()); w.Write(id); w.Write(amount); }));
        if (++hitsSent <= 5 || hitsSent % 20 == 0) p.Log($"Enemies: sent hit #{hitsSent} on {id} for {amount:0.#}");
    }

    /// Test only: the puppet closest to `pos`.
    internal Hp NearestPuppet(Vector3 pos) => puppets.Values.Where(x => x.Go != null && x.Hp != null && x.Hp.HpValue > 0f)
        .OrderBy(x => (x.Go.transform.position - pos).sqrMagnitude).Select(x => x.Hp).FirstOrDefault();

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
        using var r = new BinaryReader(new MemoryStream(data));
        byte kind = r.ReadByte();
        int ep = r.ReadInt32();
        if (p.role == "host") { if (kind == MsgHit) ApplyHit(r, ep); return; }
        if (p.role != "client") return;
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
        ? $"tracked={tracked.Count} nextId={nextId} hitsApplied={hitsApplied}"
        : $"known={known.Count} puppets={puppets.Values.Count(x => x.Go != null)} missing={puppets.Values.Count(x => x.Go == null)} hitsSent={hitsSent}";

    static byte[] Write(System.Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        body(w);
        return ms.ToArray();
    }
}
