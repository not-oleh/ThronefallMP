using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ThronefallMP;

/// `--mp-qa`: drives this instance from a command file so a playtest can be scripted and
/// screenshotted from outside. The file (/tmp/tf_qa_host.cmd or /tmp/tf_qa_client.cmd) is read
/// and deleted; one command per line:
///   move dx dz seconds     walk with stick input (dx right, dz up on screen) for that long
///   build slot [choice]    slot = "castle" or part of a building name; choice = branch index
///   night                  host: start the night
///   panel on|off           F8 Co-op panel
///   hurt fraction          local player takes that share of its max hp (1 = knockout)
///   shot path.png          screenshot incl. overlay, plus a "QA status" log line
static class Qa
{
    internal static bool Enabled;
    internal static Vector2 MoveInput;
    internal static float MoveUntil;
    static float nextPoll;

    internal static void Tick(Plugin p)
    {
        if (!Enabled || Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + 0.2f;
        string file = $"/tmp/tf_qa_{p.role}.cmd";
        if (!File.Exists(file)) return;
        string[] lines;
        try { lines = File.ReadAllLines(file); File.Delete(file); }
        catch (IOException) { return; } // still being written; next poll
        foreach (var line in lines.Where(l => l.Trim() != ""))
        {
            try { Run(p, line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)); }
            catch (Exception e) { p.Log($"QA: '{line}' failed: {e.GetType().Name}: {e.Message}"); }
        }
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    static void Run(Plugin p, string[] a)
    {
        var pi = PlayerInteraction.instance;
        var me = PlayerMovement.instance;
        switch (a[0])
        {
            case "move":
                // PlayerMovement's input vector is (vertical, horizontal).
                MoveInput = new Vector2(F(a[2]), F(a[1]));
                MoveUntil = Time.unscaledTime + F(a[3]);
                p.Log($"QA: move {a[1]} {a[2]} for {a[3]} s from {me.transform.position}");
                break;
            case "build":
                Build(p, pi, a[1], a.Length > 2 ? int.Parse(a[2]) : 0);
                break;
            case "night":
                p.Log($"QA: night (free to call: {pi.IsFreeToCallNight})");
                DayNightCycle.Instance.SwitchToNight();
                break;
            case "panel":
                p.panelOpen = a[1] == "on";
                break;
            case "hurt":
                // Same door the host's forwarded damage uses (DamagePatch blocks local hits on a client).
                p.Players.applyingHostDamage = true;
                try { me.Hp.TakeDamage(me.Hp.maxHp * F(a[1])); } finally { p.Players.applyingHostDamage = false; }
                p.Log($"QA: hurt, hp now {me.Hp.HpValue:0.#} knocked out={me.Hp.KnockedOut}");
                break;
            case "shot":
                ScreenCapture.CaptureScreenshot(a[1]);
                p.Log($"QA status: shot {a[1]} {Screen.width}x{Screen.height}, {Status(p)}");
                break;
            default:
                p.Log($"QA: unknown command '{a[0]}'");
                break;
        }
    }

    static void Build(Plugin p, PlayerInteraction pi, string name, int choice)
    {
        var open = UnityEngine.Object.FindObjectsByType<BuildSlot>(FindObjectsSortMode.None)
            .Where(s => s.CanBeUpgraded && !s.ActivatorUpgradesThis)
            .OrderBy(s => BuildSync.SlotId(s), StringComparer.Ordinal).ToList();
        var slot = name == "castle"
            ? UnityEngine.Object.FindFirstObjectByType<CastleCenter>(FindObjectsInactive.Include)?.GetComponentInParent<BuildSlot>(true)
            : open.FirstOrDefault(s => s.buildingName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        if (slot == null || !slot.CanBeUpgraded)
        {
            p.Log($"QA: no upgradable slot '{name}'; open: {string.Join(", ", open.Select(s => $"{s.buildingName} lv{s.Level}").Distinct())}");
            return;
        }
        var upg = slot.upgrades[slot.Level];
        if (pi.Balance < upg.cost) { p.Log($"QA: {slot.buildingName} costs {upg.cost}, have {pi.Balance}"); return; }
        var branch = upg.upgradeBranches[Mathf.Clamp(choice, 0, upg.upgradeBranches.Count - 1)];
        p.Log($"QA: build {slot.buildingName} lv{slot.Level}->{slot.Level + 1} choice {choice}/{upg.upgradeBranches.Count} cost {upg.cost}");
        if (upg.cost > 0) pi.SpendCoins(upg.cost);
        // Same path as the game's choice UI: the selected upgrade, then the chosen branch.
        BuildSync.SelectedField(slot) = upg;
        slot.OnUpgradeChoiceComplete(branch.choiceDetails);
    }

    static string Status(Plugin p)
    {
        var me = PlayerMovement.instance;
        if (me == null) return "no player";
        var markers = UnityEngine.Object.FindObjectsByType<ScreenMarker>(FindObjectsSortMode.None)
            .Select(m => $"{m.Number}@{m.Position.x:0},{m.Position.y:0}");
        var built = UnityEngine.Object.FindObjectsByType<BuildSlot>(FindObjectsSortMode.None).Where(s => s.Level > 0)
            .OrderBy(s => BuildSync.SlotId(s), StringComparer.Ordinal).Select(s => $"{s.buildingName}{s.Level}[{string.Join("", s.AppliedUpgrades)}]");
        int income = UnityEngine.Object.FindObjectsByType<BuildSlot>(FindObjectsSortMode.None).Where(s => s.State == BuildSlot.BuildingState.Built).Sum(s => s.GoldIncome);
        return $"{DayNightCycle.Instance?.CurrentTimestate} coins={PlayerInteraction.instance.Balance} income={income} score={ScoreManager.Instance?.CurrentScore} hp={me.Hp.HpValue:0.#}"
            + $" me={me.transform.position} avatar={(p.Players.ghost != null && p.Players.ghost.activeSelf ? p.Players.ghost.transform.position.ToString() : "hidden")}"
            + $" {p.Enemies.Status()} markers=[{string.Join(" ", markers)}] built=[{string.Join(" ", built)}]";
    }
}

/// Stick input for `move`, fed where the game hands its own input vector to the controller
/// (PlayerMovementExt calls base, so this covers both). The remote avatar has no PlayerMovement.
[HarmonyPatch(typeof(PlayerMovement), nameof(PlayerMovement.MoveScript))]
static class QaMovePatch
{
    static void Prefix(ref Vector2 inputVector)
    {
        if (Qa.Enabled && Time.unscaledTime < Qa.MoveUntil) inputVector = Qa.MoveInput;
    }
}
