using System.IO;
using HarmonyLib;
using Thronefall.SaveServices;
using UnityEngine;

namespace ThronefallMP;

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
    static void Postfix(PlayerMovement __instance) => Plugin.Instance.Players.BuildGhost(__instance);
}

[HarmonyPatch(typeof(PlayerAttackAnimator), nameof(PlayerAttackAnimator.TriggerAnimations))]
static class AttackPatch
{
    // The avatar has no PlayerAttackAnimator, so every call is the local player attacking.
    static void Postfix() => Plugin.Instance.Players.attacks++;
}

/// The host owns damage. Hits on the avatar are not applied to it but counted and sent to
/// the player it stands for; the client's own enemies cannot hurt the client's player.
[HarmonyPatch(typeof(Hp), nameof(Hp.TakeDamage))]
static class DamagePatch
{
    static bool Prefix(Hp __instance, float _amount, TaggedObject _damageComingFrom)
    {
        var p = Plugin.Instance;
        var players = p.Players;
        if (_amount <= 0f) return true; // healing

        if (players.ghostHp != null && __instance == players.ghostHp)
        {
            if (p.role == "host")
            {
                if (players.damageToPeer == 0f)
                    p.Log($"Avatar was attacked by {(_damageComingFrom != null ? _damageComingFrom.name : "unknown")} for {_amount}");
                players.damageToPeer += _amount;
            }
            return false;
        }

        // Client: host enemies are puppets; only the host's kill (EnemySync) may hurt them. Our
        // player's hits go to the host; our towers/units are mirrored there, so theirs are dropped.
        var me = PlayerMovement.instance;
        if (p.Enemies.IsPuppet(__instance))
        {
            if (p.Enemies.applying) return true;
            // Ours: named as attacker, or sourceless inside our weapon's code (PlayerDamageScope).
            bool direct = PlayerDamageScope.IsLocalPlayer(_damageComingFrom);
            bool indirect = _damageComingFrom == null && PlayerDamageScope.Active;
            if (!__instance.KnockedOut && (direct || indirect)) p.Enemies.SendHit(__instance, _amount, !direct);
            return false;
        }

        if (p.role == "client" && p.PeerAlive && me != null && __instance == me.Hp)
        {
            // Self-inflicted weapon costs (Blood Wand) stay local; other local damage is the host's.
            if (PlayerDamageScope.Active && !players.applyingHostDamage) return true;
            if (!players.applyingHostDamage) return false;
            players.NoteHostDamage(_amount);
        }
        return true;
    }
}

/// Client: its own day/night cycle stands still while the host is in the same scene; the host's
/// state is applied by MatchSync.
[HarmonyPatch(typeof(DayNightCycle), "Update")]
static class TimePatch
{
    static bool Prefix(DayNightCycle __instance) =>
        !Plugin.Instance.Match.FrozenByHost(__instance.gameObject.scene.name);
}

/// Every treasury change on this instance is reported to MatchSync (client forwards it).
/// World coin/core pickups and dusk/dawn perks are host-authoritative: suppressed on the client
/// unless the coin/core was spawned by CostDisplay.CancelFill (refunding a cancelled build fill).
[HarmonyPatch(typeof(PlayerInteraction))]
static class TreasuryPatch
{
    [HarmonyPrefix, HarmonyPatch(nameof(PlayerInteraction.AddCoin))]
    static bool PreAdd(PlayerInteraction __instance) => !Plugin.Instance.Match.ShouldSuppressClientGain(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.AddCoin))]
    static void Add(int amount) => Plugin.Instance.Match.LocalChange(amount, 0);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.SpendCoins))]
    static void Spend(int amount) => Plugin.Instance.Match.LocalChange(-amount, 0);

    [HarmonyPrefix, HarmonyPatch(nameof(PlayerInteraction.AddEnergyCores))]
    static bool PreAddCores(PlayerInteraction __instance) => !Plugin.Instance.Match.ShouldSuppressClientGain(__instance);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.AddEnergyCores))]
    static void AddCores(int amount) => Plugin.Instance.Match.LocalChange(0, amount);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.SpendEnergyCores))]
    static void SpendCores(int amount) => Plugin.Instance.Match.LocalChange(0, -amount);
}

[HarmonyPatch]
static class CurrencyPickupScopePatch
{
    [HarmonyPrefix, HarmonyPatch(typeof(CostDisplay), nameof(CostDisplay.CancelFill))]
    static void EnterCancelFill() => MatchSync.inCancelFill = true;

    [HarmonyFinalizer, HarmonyPatch(typeof(CostDisplay), nameof(CostDisplay.CancelFill))]
    static void ExitCancelFill() => MatchSync.inCancelFill = false;

    [HarmonyPostfix, HarmonyPatch(typeof(Coin), nameof(Coin.SetTarget))]
    static void NoteRefundCoin(Coin __instance)
    {
        if (MatchSync.inCancelFill) MatchSync.refundCoins.Add(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Coin), "OnCurrencyCollected")]
    [HarmonyPatch(typeof(EnergyCore), "Update")]
    static void EnterPickup(Coin __instance) => MatchSync.currentPickup = __instance;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Coin), "OnCurrencyCollected")]
    [HarmonyPatch(typeof(EnergyCore), "Update")]
    static void ExitPickup() => MatchSync.currentPickup = null;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InterestPerk), nameof(InterestPerk.OnDusk))]
    [HarmonyPatch(typeof(PerkInterestPerCycle), nameof(PerkInterestPerCycle.OnDusk))]
    [HarmonyPatch(typeof(EnemySpawner), nameof(EnemySpawner.OnStartOfTheDay))]
    static void EnterAutoGain() => MatchSync.suppressGainDepth++;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(InterestPerk), nameof(InterestPerk.OnDusk))]
    [HarmonyPatch(typeof(PerkInterestPerCycle), nameof(PerkInterestPerCycle.OnDusk))]
    [HarmonyPatch(typeof(EnemySpawner), nameof(EnemySpawner.OnStartOfTheDay))]
    static void ExitAutoGain() => MatchSync.suppressGainDepth--;
}


/// Every completed build/upgrade (incl. the branch chosen) goes through here; BuildSync shares it.
/// BuildSlotExt overrides the method but calls base, so patching BuildSlot covers both.
[HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnUpgradeChoiceComplete))]
static class BuildPatch
{
    static bool Prefix(BuildSlot __instance, Choice _choiceMade, out int __state)
    {
        __state = BuildSync.LevelField(__instance);
        return !Plugin.Instance.Builds.InterceptClientChoice(__instance, _choiceMade);
    }

    static void Postfix(BuildSlot __instance, Choice _choiceMade, int __state)
    {
        if (BuildSync.LevelField(__instance) > __state) Plugin.Instance.Builds.Completed(__instance, _choiceMade);
    }
}

/// Client: while the host is in our scene its waves are the only ones; ours never spawn.
[HarmonyPatch(typeof(Spawn), nameof(Spawn.Update))]
static class SpawnPatch
{
    static bool Prefix() => !Plugin.Instance.Enemies.SuppressLocalWaves();
}
