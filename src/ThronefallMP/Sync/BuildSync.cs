using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ThronefallMP;

/// Shared buildings. Every build/upgrade in the game ends in `BuildSlot.OnUpgradeChoiceComplete`
/// with a concrete branch (picked in the choice UI, or at random for `_presentChoice: false`), so
/// that one call is the sync point and also carries upgrade choices:
///  - Host: every completed upgrade is logged and sent as (slot, new level, branch). The log is
///    resent on reconnect, so a late client catches up.
///  - Client: its own upgrades are applied locally at once (the choice UI ran here) and sent to the
///    host as a request; the host applies it and echoes it in its log.
/// Coins are not handled here: the interactor spends them coin by coin through the treasury
/// (MatchSync), and applying a remote upgrade charges nothing. Updates are idempotent by level.
class BuildSync
{
    internal const byte MsgBuilt = 10, MsgRequest = 11;
    internal static readonly AccessTools.FieldRef<BuildSlot, int> LevelField = AccessTools.FieldRefAccess<BuildSlot, int>("level");
    static readonly AccessTools.FieldRef<BuildSlot, BuildSlot.Upgrade> SelectedField =
        AccessTools.FieldRefAccess<BuildSlot, BuildSlot.Upgrade>("upgradeSelected");
    static readonly AccessTools.FieldRef<BuildSlot, PlayerInteraction> PlayerField =
        AccessTools.FieldRefAccess<BuildSlot, PlayerInteraction>("playerInteractionSelected");
    static readonly System.Reflection.MethodInfo OnTargetUpgradeMethod =
        AccessTools.Method(typeof(BuildingInteractor), "OnTargetUpgrade");

    struct Built { public int Epoch; public string Scene, Slot; public int Level, Branch; public bool Paid; }

    readonly Plugin p;
    readonly Link link;

    // Host: this match's upgrades in order. Client: the host's, applied up to `applied`.
    readonly List<Built> log = new();
    int epoch = Random.Range(1, int.MaxValue);
    PlayerInteraction logFor, appliedTo;
    int applied;
    float appliedSince;
    bool applying;

    Dictionary<string, BuildSlot> slots;
    PlayerInteraction slotsFor;

    public BuildSync(Plugin plugin, Link link)
    {
        p = plugin;
        this.link = link;
        link.PeerRestarted += ResendAll;
    }

    internal void ResendAll()
    {
        if (p.role != "host") return;
        foreach (var e in log) Send(MsgBuilt, e);
    }

    /// Stable across instances: the slot's save GUID (serialized in the scene), else its hierarchy
    /// path + position.
    internal static string SlotId(BuildSlot s)
    {
        var sle = s.GetComponent<SaveLoadEntity>();
        var guid = sle != null ? sle.GUID : null;
        if (!string.IsNullOrEmpty(guid)) return guid;
        var path = s.name;
        for (var t = s.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
        var pos = s.transform.position;
        return $"{path}@{pos.x:F2},{pos.y:F2},{pos.z:F2}";
    }

    BuildSlot Find(string id)
    {
        var pi = PlayerInteraction.instance;
        if (slots == null || slotsFor != pi)
        {
            slotsFor = pi;
            slots = new Dictionary<string, BuildSlot>();
            string scene = pi != null ? pi.gameObject.scene.name : null;
            foreach (var s in Object.FindObjectsByType<BuildSlot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (s.gameObject.scene.name != scene) continue;
                var key = SlotId(s);
                if (slots.ContainsKey(key)) p.Log($"Build: duplicate slot id {key} ({s.buildingName}), sync for it is ambiguous");
                else slots[key] = s;
            }
            p.Log($"Build: indexed {slots.Count} build slots in {scene}");
        }
        return slots.TryGetValue(id, out var found) ? found : null;
    }

    /// Client: do not mutate the slot locally before the host orders it; send MsgRequest and let
    /// the host's authoritative MsgBuilt apply the upgrade so simultaneous upgrades never desync
    /// branches or irreversibly trigger local-only upgrade OnEnable effects.
    internal bool InterceptClientChoice(BuildSlot slot, Choice choice)
    {
        if (p.role != "client" || applying || choice == null || !p.Match.FrozenByHost(slot.gameObject.scene.name)) return false;
        int nextLevel = LevelField(slot) + 1;
        var upg = SelectedField(slot) ?? (nextLevel <= slot.upgrades.Count ? slot.upgrades[nextLevel - 1] : null);
        if (upg == null) return false;
        int branch = upg.upgradeBranches.FindIndex(b => b.choiceDetails == choice);
        if (branch < 0) branch = 0;
        var bi = slot.buildingInteractor;
        bool paid = bi != null && (bi.IsWaitingForChoice || (bi.costDisplay != null && bi.costDisplay.CompletelyFilled));
        if (bi != null)
        {
            bi.buildingIsCurrentlyBusyAndCantBeUpgraded = true;
            if (bi.IsWaitingForChoice) OnTargetUpgradeMethod?.Invoke(bi, null);
            p.After(2f, () => { if (bi != null && bi.buildingIsCurrentlyBusyAndCantBeUpgraded) { bi.buildingIsCurrentlyBusyAndCantBeUpgraded = false; bi.UpdateInteractionState(); } });
        }
        var e = new Built { Epoch = epoch, Scene = slot.gameObject.scene.name, Slot = SlotId(slot), Level = nextLevel, Branch = branch, Paid = paid };
        Send(MsgRequest, e);
        p.Log($"Build: {slot.buildingName} -> level {nextLevel} branch {branch} requested from host");
        return true;
    }

    /// Dev/CI test: simulate the client having paid for and requested a duplicate upgrade on a
    /// slot the host already upgraded at the same instant, verifying refund + branch preservation.
    internal void TestSimultaneousRace(BuildSlot slot)
    {
        var pi = PlayerInteraction.instance;
        if (p.role != "client" || pi == null || slot == null || slot.Level < 2) return;
        int lvl = slot.Level;
        var upg = slot.upgrades[lvl - 1];
        int altBranch = (slot.AppliedUpgrades[lvl - 1] + 1) % upg.upgradeBranches.Count;
        if (upg.cost > 0) pi.SpendCoins(upg.cost);
        Send(MsgRequest, new Built { Epoch = epoch, Scene = slot.gameObject.scene.name, Slot = SlotId(slot), Level = lvl, Branch = altBranch, Paid = upg.cost > 0 });
        p.Log($"Build: test simultaneous race on {slot.buildingName} level {lvl} branch {altBranch} (paid coins={upg.cost})");
    }

    /// Patch hook: a build/upgrade just completed on this instance (`slot` is now at `level`).
    internal void Completed(BuildSlot slot, Choice choice)
    {
        if (p.role == "off") return;
        int level = LevelField(slot);
        var e = new Built
        {
            Epoch = epoch, Scene = slot.gameObject.scene.name, Slot = SlotId(slot), Level = level,
            Branch = slot.upgrades[level - 1].upgradeBranches.FindIndex(b => b.choiceDetails == choice),
        };
        if (p.role == "host")
        {
            if (logFor != PlayerInteraction.instance) { logFor = PlayerInteraction.instance; log.Clear(); epoch = Random.Range(1, int.MaxValue); e.Epoch = epoch; }
            log.Add(e);
            Send(MsgBuilt, e);
            p.Log($"Build: {slot.buildingName} -> level {level} branch {e.Branch} sent to peer");
        }
        else if (!applying)
        {
            Send(MsgRequest, e);
            p.Log($"Build: {slot.buildingName} -> level {level} branch {e.Branch} requested from host");
        }
    }

    internal void Tick(PlayerMovement me)
    {
        if (p.role != "client" || me == null) return;
        var pi = PlayerInteraction.instance;
        var gs = LocalGamestate.Instance;
        if (pi == null || gs == null || gs.CurrentState != LocalGamestate.State.InMatch) return;
        if (appliedTo != pi) { appliedTo = pi; applied = 0; appliedSince = Time.unscaledTime; }
        // Slots deactivate themselves in Start; upgrading before that would be undone.
        if (Time.unscaledTime - appliedSince < 1f) return;
        string scene = me.gameObject.scene.name;
        for (; applied < log.Count; applied++)
            if (log[applied].Scene == scene && Apply(log[applied])) p.Log($"Build: follows host -> {log[applied].Slot} level {log[applied].Level} branch {log[applied].Branch}");
    }

    bool Apply(Built e)
    {
        var s = Find(e.Slot);
        if (s == null) { p.Log($"Build: no slot {e.Slot} here"); return false; }
        var bi = s.buildingInteractor;
        if (bi != null) bi.buildingIsCurrentlyBusyAndCantBeUpgraded = false;
        int own = LevelField(s);
        if (own >= e.Level) return false; // already there (our own upgrade echoed back)
        if (own != e.Level - 1 || e.Branch < 0 || e.Branch >= s.upgrades[own].upgradeBranches.Count)
        {
            p.Log($"Build: {s.buildingName} is level {own}, cannot apply level {e.Level} branch {e.Branch}");
            return false;
        }
        if (p.role == "client" && bi != null)
        {
            if (bi.IsWaitingForChoice && ChoiceManager.instance != null && ChoiceManager.instance.currentOriginBuildSlot == s)
            {
                ChoiceManager.instance.CancelChoice();
                UIFrameManager.instance?.CloseAllFrames();
                s.OnUpgradeCancel.Invoke();
            }
            else if (bi.costDisplay != null && !bi.costDisplay.CompletelyEmpty)
                bi.costDisplay.CancelFill(PlayerInteraction.instance);
        }
        if (s.ActivatorUpgradesThis) s.gameObject.SetActive(true); // as TryToBuildOrUpgradeAndPay does
        applying = true;
        try
        {
            SelectedField(s) = s.upgrades[own];
            PlayerField(s) = PlayerInteraction.instance;
            s.OnUpgradeChoiceComplete(s.upgrades[own].upgradeBranches[e.Branch].choiceDetails);
            if (bi != null) bi.UpdateInteractionState();
        }
        finally { applying = false; }
        return true;
    }

    internal void OnMessage(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        byte kind = r.ReadByte();
        var e = new Built
        {
            Epoch = r.ReadInt32(), Scene = r.ReadString(), Slot = r.ReadString(),
            Level = r.ReadInt32(), Branch = r.ReadInt32(),
            Paid = r.BaseStream.Position < r.BaseStream.Length && r.ReadBoolean(),
        };
        if (kind == MsgBuilt && p.role == "client")
        {
            if (log.Count > 0 && (log[0].Epoch != e.Epoch || log[0].Scene != e.Scene)) { log.Clear(); applied = 0; }
            // Resent on every snapshot request; the log is replayed from the start per scene load anyway.
            if (!log.Exists(x => x.Slot == e.Slot && x.Level == e.Level)) log.Add(e);
            var s = Find(e.Slot);
            if (s != null && s.buildingInteractor != null) s.buildingInteractor.buildingIsCurrentlyBusyAndCantBeUpgraded = false;
        }
        else if (kind == MsgRequest && p.role == "host")
        {
            var pi = PlayerInteraction.instance;
            if (pi == null || pi.gameObject.scene.name != e.Scene) { p.Log($"Build: peer request for {e.Scene} ignored (host is elsewhere)"); return; }
            var s = Find(e.Slot);
            if (s != null && LevelField(s) >= e.Level)
            {
                int refundCoins = 0, refundCores = 0;
                if (e.Paid && !s.ActivatorUpgradesThis && e.Level > 0 && e.Level <= s.upgrades.Count)
                {
                    var u = s.upgrades[e.Level - 1];
                    refundCoins = u.cost;
                    refundCores = DlcConfiguration.IsDlcAvailable(IDlcProvider.DlcId.CRAAGHELM) ? u.energyCoreCost : 0;
                    if (refundCoins > 0) pi.AddCoin(refundCoins);
                    if (refundCores > 0) pi.AddEnergyCores(refundCores);
                }
                var auth = log.Find(x => x.Slot == e.Slot && x.Level == e.Level);
                if (auth.Slot != null) Send(MsgBuilt, auth);
                p.Log($"Build: peer request {e.Slot} level {e.Level} rejected (already level {LevelField(s)}); refunded coins={refundCoins} cores={refundCores}");
                return;
            }
            if (!Apply(e)) p.Log($"Build: peer request {e.Slot} level {e.Level} rejected");
            else p.Log($"Build: peer built {e.Slot} level {e.Level} branch {e.Branch}");
        }
    }

    internal static bool SelfCheck()
    {
        var orig = new Built { Epoch = 42, Scene = "Nordfels", Slot = "castle-guid", Level = 2, Branch = 3, Paid = true };
        var bytes = Write(w => { w.Write(MsgRequest); w.Write(orig.Epoch); w.Write(orig.Scene); w.Write(orig.Slot); w.Write(orig.Level); w.Write(orig.Branch); w.Write(orig.Paid); });
        using var r = new BinaryReader(new MemoryStream(bytes));
        return r.ReadByte() == MsgRequest
            && r.ReadInt32() == 42
            && r.ReadString() == "Nordfels"
            && r.ReadString() == "castle-guid"
            && r.ReadInt32() == 2
            && r.ReadInt32() == 3
            && r.ReadBoolean();
    }

    void Send(byte kind, Built e) => link.SendMessage(Write(w =>
    {
        w.Write(kind); w.Write(e.Epoch); w.Write(e.Scene); w.Write(e.Slot); w.Write(e.Level); w.Write(e.Branch); w.Write(e.Paid);
    }));

    static byte[] Write(System.Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        body(w);
        return ms.ToArray();
    }
}
