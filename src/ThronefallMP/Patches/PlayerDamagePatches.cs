using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ThronefallMP;

/// Marks damage that the local player's weapon causes without naming the player as attacker
/// (explosions, puddles, traps, Blood Wand bonus hits, curse blasts). While a scope is open,
/// sourceless damage on a puppet is the local player's and DamagePatch forwards it to the host;
/// outside it, sourceless damage comes from our towers/units and is dropped as before.
static class PlayerDamageScope
{
    internal static int Depth;
    internal static bool Active => Depth > 0;

    /// Pooled AoE objects (puddles, heal/damage areas, traps) enabled while a scope was open.
    internal static readonly HashSet<int> Owned = new();

    internal static bool IsLocalPlayer(TaggedObject t)
    {
        var me = PlayerMovement.instance;
        return t != null && me != null && me.Hp != null && t == me.Hp.TaggedObj;
    }
}

/// Everything the player's manual attacks run (Attack, Update, their coroutines), plus the
/// Update of projectiles fired by the player and of AoE objects the player spawned.
[HarmonyPatch]
static class PlayerScopePatch
{
    static readonly AccessTools.FieldRef<AimbotProjectile, TaggedObject> FiredBy =
        AccessTools.FieldRefAccess<AimbotProjectile, TaggedObject>("firedBy");

    static readonly Type[] Lingering = { typeof(HealAndDamageArea), typeof(PuddleDamageDealer), typeof(AttackByTimeOrCollision) };

    static IEnumerable<MethodBase> TargetMethods()
    {
        var types = typeof(ManualAttack).Assembly.GetTypes();
        bool Is<T>(Type t) => typeof(T).IsAssignableFrom(t);
        foreach (var t in types.Where(Is<ManualAttack>))
        {
            foreach (var name in new[] { "Attack", "HandleAttackUpdate", "Update" })
                if (AccessTools.DeclaredMethod(t, name, Type.EmptyTypes) is MethodInfo m && !m.IsAbstract) yield return m;
            // Coroutines (delayed barrage shots etc.) are nested iterator classes.
            foreach (var n in t.GetNestedTypes(AccessTools.all).Where(n => n.Name.Contains(">d__")))
                if (AccessTools.DeclaredMethod(n, "MoveNext") is MethodInfo mn) yield return mn;
        }
        foreach (var t in types.Where(Is<AimbotProjectile>))
            if (AccessTools.DeclaredMethod(t, "Update", Type.EmptyTypes) is MethodInfo m) yield return m;
        foreach (var t in Lingering)
            foreach (var name in new[] { "Update", "OnTriggerEnter" })
                if (AccessTools.DeclaredMethod(t, name) is MethodInfo m) yield return m;
    }

    static void Prefix(object __instance, out bool __state)
    {
        __state = __instance switch
        {
            AimbotProjectile proj => PlayerDamageScope.IsLocalPlayer(FiredBy(proj)),
            UnityEngine.Object o when Lingering.Contains(o.GetType()) => PlayerDamageScope.Owned.Contains(o.GetInstanceID()),
            _ => true, // ManualAttack or one of its coroutines: only the local player has these
        };
        if (__state) PlayerDamageScope.Depth++;
    }

    // Finalizer, not Postfix: a throwing original must not leave the scope open.
    static void Finalizer(bool __state)
    {
        if (__state) PlayerDamageScope.Depth--;
    }
}

/// Direct hits by the player run their additional effects (Blood Wand bonus, curse marks)
/// inside DealDamage without passing the attacker on.
[HarmonyPatch(typeof(Weapon), nameof(Weapon.DealDamage))]
static class PlayerWeaponScopePatch
{
    static void Prefix(TaggedObject _attacker, out bool __state)
    {
        __state = PlayerDamageScope.IsLocalPlayer(_attacker);
        if (__state) PlayerDamageScope.Depth++;
    }

    static void Finalizer(bool __state)
    {
        if (__state) PlayerDamageScope.Depth--;
    }
}

/// Pooled AoE objects are reused by towers too, so ownership is decided on every enable.
[HarmonyPatch]
static class LingeringOwnerPatch
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(HealAndDamageArea), typeof(PuddleDamageDealer), typeof(AttackByTimeOrCollision) }
            .Select(t => AccessTools.DeclaredMethod(t, "OnEnable")).Where(m => m != null);

    static void Postfix(UnityEngine.Object __instance)
    {
        if (PlayerDamageScope.Active) PlayerDamageScope.Owned.Add(__instance.GetInstanceID());
        else PlayerDamageScope.Owned.Remove(__instance.GetInstanceID());
    }
}
