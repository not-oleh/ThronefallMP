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
            // shortcut: sourceless player effects (explosions, burns, Blood Wand bonus) are not
            // forwarded; tag them by attacker if players miss that damage.
            if (!__instance.KnockedOut && me != null && me.Hp != null && _damageComingFrom != null && _damageComingFrom == me.Hp.TaggedObj)
                p.Enemies.SendHit(__instance, _amount);
            return false;
        }

        if (p.role == "client" && me != null && __instance == me.Hp)
        {
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
[HarmonyPatch(typeof(PlayerInteraction))]
static class TreasuryPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.AddCoin))]
    static void Add(int amount) => Plugin.Instance.Match.LocalChange(amount, 0);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.SpendCoins))]
    static void Spend(int amount) => Plugin.Instance.Match.LocalChange(-amount, 0);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.AddEnergyCores))]
    static void AddCores(int amount) => Plugin.Instance.Match.LocalChange(0, amount);

    [HarmonyPostfix, HarmonyPatch(nameof(PlayerInteraction.SpendEnergyCores))]
    static void SpendCores(int amount) => Plugin.Instance.Match.LocalChange(0, -amount);
}


/// Every completed build/upgrade (incl. the branch chosen) goes through here; BuildSync shares it.
/// BuildSlotExt overrides the method but calls base, so patching BuildSlot covers both.
[HarmonyPatch(typeof(BuildSlot), nameof(BuildSlot.OnUpgradeChoiceComplete))]
static class BuildPatch
{
    static void Prefix(BuildSlot __instance, out int __state) => __state = BuildSync.LevelField(__instance);

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
