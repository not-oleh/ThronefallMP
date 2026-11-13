using UnityEngine;

namespace ThronefallMP;

/// Snapshot interpolation for one replicated object: shown `Delay` seconds in the past, between
/// the two received snapshots that bracket that moment, so 20 Hz states move smoothly at any FPS.
class Interp
{
    /// ~1.8x the 50 ms send interval: one late or lost packet still has a bracketing pair.
    internal const float Delay = 0.09f, Teleport = 8f;
    const int Size = 8;
    readonly (float t, Vector3 pos, float yaw)[] ring = new (float, Vector3, float)[Size];
    int count, head;

    internal void Clear() => count = 0;

    // shortcut: stamped with receive time, so network jitter becomes motion jitter; add sender
    // timestamps to the State packets if Steam jitter turns out larger than Delay - interval.
    internal void Push(float t, Vector3 pos, float yaw)
    {
        if (count > 0 && (pos - ring[head].pos).sqrMagnitude > Teleport * Teleport) count = 0; // respawn/rejoin: snap
        if (count > 0 && t <= ring[head].t) { ring[head] = (ring[head].t, pos, yaw); return; } // same frame: latest wins
        head = (head + 1) % Size;
        ring[head] = (t, pos, yaw);
        if (count < Size) count++;
    }

    /// False until the first snapshot arrives.
    internal bool Sample(float now, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = Quaternion.identity;
        if (count == 0) return false;
        float t = now - Delay;
        var a = ring[(head - count + 1 + Size) % Size];
        for (int k = count - 2; k >= 0 && a.t < t; k--)
        {
            var b = ring[(head - k + Size) % Size];
            if (b.t >= t)
            {
                float f = (t - a.t) / (b.t - a.t);
                pos = Vector3.Lerp(a.pos, b.pos, f);
                rot = Quaternion.Slerp(Quaternion.Euler(0f, a.yaw, 0f), Quaternion.Euler(0f, b.yaw, 0f), f);
                return true;
            }
            a = b;
        }
        // Before the oldest or past the newest (packets late): hold the nearest end.
        pos = a.pos; rot = Quaternion.Euler(0f, a.yaw, 0f);
        return true;
    }

    internal static bool SelfCheck()
    {
        var i = new Interp();
        if (i.Sample(1f, out _, out _)) return false;
        for (int k = 0; k <= 10; k++) i.Push(k * 0.05f, new Vector3(k, 0f, 0f), k * 10f);
        bool mid = i.Sample(0.325f + Delay, out var p, out var r) && Mathf.Abs(p.x - 6.5f) < 1e-3f
            && Mathf.Abs(Mathf.DeltaAngle(r.eulerAngles.y, 65f)) < 0.1f;
        bool held = i.Sample(9f, out p, out _) && p.x == 10f;
        i.Push(0.55f, new Vector3(50f, 0f, 0f), 0f); // teleport clears the history
        bool snapped = i.Sample(0.6f, out p, out _) && p.x == 50f;
        i.Push(0.55f, new Vector3(51f, 0f, 0f), 0f); // same timestamp replaces
        return mid && held && snapped && i.Sample(9f, out p, out _) && p.x == 51f;
    }
}
