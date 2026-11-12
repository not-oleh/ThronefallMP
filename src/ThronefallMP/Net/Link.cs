using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ThronefallMP;

/// UDP transport for two peers. Every datagram: [Protocol][kind][sender session u32] + body.
///   State (0)    unreliable, latest wins (player position etc.), resent by the caller each tick
///   Message (1)  [seq u16][payload], reliable and in order (see Reliable)
///   Ack (2)      [for-session u32][next-expected u16]
/// The session id is random per run; a changed id means the peer restarted, so both stream
/// counters restart too. A Steam P2P transport (M4) can replace this class: Plugin only uses
/// SendState / SendMessage / Pump / Peer.
class Link
{
    public const byte Protocol = 7;
    const byte KState = 0, KMessage = 1, KAck = 2;

    readonly UdpClient udp;
    readonly Func<float> clock;
    readonly Reliable reliable = new();
    readonly uint session = (uint)Guid.NewGuid().GetHashCode();
    uint peerSession;
    bool hasPeerSession;

    /// Host: learned from the first datagram (one peer, last sender wins, no auth).
    public IPEndPoint Peer;
    /// Raised when the peer restarted (not on first contact). Callers resend their full state.
    public event Action PeerRestarted;

    public Link(UdpClient udp, IPEndPoint peer, Func<float> clock)
    {
        this.udp = udp;
        Peer = peer;
        this.clock = clock;
    }

    public void SendState(byte[] body) => Send(KState, null, body);

    public void SendMessage(byte[] payload)
    {
        ushort seq = reliable.Queue(payload, clock());
        Transmit(seq, payload);
    }

    void Transmit(ushort seq, byte[] payload) => Send(KMessage, w => w.Write(seq), payload);

    void Send(byte kind, Action<BinaryWriter> header, byte[] body)
    {
        if (Peer == null) return;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Protocol); w.Write(kind); w.Write(session);
        header?.Invoke(w);
        if (body != null) w.Write(body);
        w.Flush();
        try { udp.Send(ms.GetBuffer(), (int)ms.Length, Peer); } catch (SocketException) { }
    }

    /// Reads everything waiting, resends unacknowledged messages. True if anything arrived.
    public bool Pump(bool isHost, Action<byte[]> onState, Action<byte[]> onMessage)
    {
        bool heard = false;
        while (udp.Available > 0)
        {
            IPEndPoint from = null;
            try
            {
                var data = udp.Receive(ref from);
                using var r = new BinaryReader(new MemoryStream(data));
                if (r.ReadByte() != Protocol) throw new FormatException("protocol mismatch");
                byte kind = r.ReadByte();
                uint sess = r.ReadUInt32();
                if (hasPeerSession && sess != peerSession)
                {
                    reliable.Reset();
                    PeerRestarted?.Invoke();
                }
                peerSession = sess;
                hasPeerSession = true;
                if (isHost) Peer = from;
                heard = true;

                int rest() => data.Length - (int)r.BaseStream.Position;
                switch (kind)
                {
                    case KState:
                        onState(r.ReadBytes(rest()));
                        break;
                    case KMessage:
                        ushort seq = r.ReadUInt16();
                        var payload = r.ReadBytes(rest());
                        if (reliable.Accept(seq)) onMessage(payload);
                        Send(KAck, w => { w.Write(peerSession); w.Write(reliable.Expected); }, null);
                        break;
                    case KAck:
                        uint forSession = r.ReadUInt32();
                        ushort next = r.ReadUInt16();
                        if (forSession == session) reliable.Acked(next);
                        break;
                }
            }
            catch (Exception e) when (e is SocketException || e is IOException || e is FormatException)
            {
                // Malformed or ICMP-rejected datagram: ignore, state is resent every 50 ms.
            }
        }

        if (Peer != null)
            foreach (var (seq, payload) in reliable.Due(clock()))
                Transmit(seq, payload);
        return heard;
    }

    public void Close() => udp.Close();
}
