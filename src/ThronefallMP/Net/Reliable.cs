using System;
using System.Collections.Generic;

namespace ThronefallMP;

/// Sequenced, acknowledged, in-order delivery for one-off events on top of a lossy datagram
/// channel. Pure C# (time is passed in) so SelfCheck runs without Unity. The transport
/// (UDP today, Steam P2P in M4) only has to carry (seq, payload) one way and an ack number back.
/// shortcut: stop-and-wait style (receiver drops anything ahead of the gap and the sender resends
/// it), no congestion control and no cap on the queue; fine for a few small messages per second.
class Reliable
{
    public const float ResendAfter = 0.25f;
    const int Window = 32;

    ushort sendSeq, expected;
    readonly List<(ushort seq, byte[] data, float sentAt)> pending = new();

    /// Next sequence number we want from the peer; sent back as the ack.
    public ushort Expected => expected;
    public int PendingCount => pending.Count;

    public ushort Queue(byte[] data, float now)
    {
        pending.Add((sendSeq, data, now));
        return sendSeq++;
    }

    /// Messages that were not acknowledged within ResendAfter (oldest first, at most Window).
    public List<(ushort seq, byte[] data)> Due(float now)
    {
        var due = new List<(ushort, byte[])>();
        for (int i = 0; i < pending.Count && i < Window; i++)
        {
            var p = pending[i];
            if (now - p.sentAt < ResendAfter) continue;
            pending[i] = (p.seq, p.data, now);
            due.Add((p.seq, p.data));
        }
        return due;
    }

    /// Peer confirms everything below `next`.
    public void Acked(ushort next) => pending.RemoveAll(p => (short)(p.seq - next) < 0);

    /// True when `seq` is the next message in order and must be delivered; duplicates and
    /// messages that arrived ahead of a gap are dropped (the sender resends them).
    public bool Accept(ushort seq)
    {
        if (seq != expected) return false;
        expected++;
        return true;
    }

    /// The peer restarted: its stream starts at 0 again and our unsent history is meaningless.
    public void Reset()
    {
        sendSeq = 0;
        expected = 0;
        pending.Clear();
    }

    public static bool SelfCheck()
    {
        var a = new Reliable();
        var b = new Reliable();
        var got = new List<int>();
        int packets = 0;
        for (int i = 0; i < 10; i++) a.Queue(new[] { (byte)i }, 0f);
        for (int step = 0; step < 400 && (a.PendingCount > 0 || got.Count < 10); step++)
        {
            float now = step * 0.1f;
            foreach (var (seq, data) in a.Due(now))
            {
                if (++packets % 3 == 0) continue; // every third packet is lost
                if (b.Accept(seq)) got.Add(data[0]);
                if (packets % 4 != 0) a.Acked(b.Expected); // and some acks too
            }
        }
        for (int i = 0; i < got.Count; i++) if (got[i] != i) return false;
        return got.Count == 10 && a.PendingCount == 0;
    }
}
