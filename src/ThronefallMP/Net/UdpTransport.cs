using System.Net;
using System.Net.Sockets;

namespace ThronefallMP;

/// Plain UDP (local testing, LAN, port-forwarded hosts). The host has no peer until the first
/// datagram arrives and then answers whoever sent last (one peer, no auth).
class UdpTransport : ITransport
{
    readonly UdpClient udp;
    readonly bool isHost;
    IPEndPoint peer;

    public UdpTransport(UdpClient udp, IPEndPoint peer, bool isHost)
    {
        this.udp = udp;
        this.peer = peer;
        this.isHost = isHost;
    }

    public bool HasPeer => peer != null;
    public string PeerName => peer?.ToString() ?? "none";

    public void Send(byte[] data, int length)
    {
        try { udp.Send(data, length, peer); } catch (SocketException) { }
    }

    public bool TryReceive(out byte[] data)
    {
        while (udp.Available > 0)
        {
            IPEndPoint from = null;
            try { data = udp.Receive(ref from); }
            catch (SocketException) { continue; } // ICMP port unreachable etc.
            if (isHost) peer = from;
            return true;
        }
        data = null;
        return false;
    }

    public void Close() => udp.Close();
}
