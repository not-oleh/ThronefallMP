namespace ThronefallMP;

/// Raw, unreliable datagrams to and from the one peer. Link adds framing, sessions and the
/// reliable stream on top, so a transport only moves bytes and knows who the peer is.
interface ITransport
{
    bool HasPeer { get; }
    /// For logs only.
    string PeerName { get; }
    void Send(byte[] data, int length);
    /// Next waiting datagram from the peer, or false when none is left this frame.
    bool TryReceive(out byte[] data);
    void Close();
}
