using System;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace ThronefallMP;

/// Steam P2P (SteamNetworking packets, relayed by Steam: no port forwarding). The host opens a
/// friends-only lobby; the client joins it by id or through a Steam invite, and the lobby owner
/// is its peer. The host accepts P2P sessions only from members of its lobby and answers
/// whoever sent last (one peer, like UdpTransport).
/// Steam itself is initialised and pumped (RunCallbacks) by the game's SteamManager; nothing
/// happens here until that is done.
class SteamTransport : ITransport
{
    const string LobbyKey = "thronefallmp";
    static readonly FieldInfo ManagerInstance = AccessTools.Field(typeof(SteamManager), "s_instance");

    readonly bool isHost;
    // Host reads channel 0, client channel 1: with one Steam account on one Mac (testing) both
    // instances share a SteamID and would otherwise read each other's and their own packets.
    readonly int sendChannel, receiveChannel;
    readonly Action<string> log;
    readonly ulong lobbyToJoin;
    CSteamID lobby = CSteamID.Nil, peer = CSteamID.Nil;
    bool started;

    // Held so the callbacks are not garbage collected.
    Callback<P2PSessionRequest_t> onSessionRequest;
    Callback<LobbyEnter_t> onLobbyEnter;
    Callback<GameLobbyJoinRequested_t> onInvite;
    CallResult<LobbyCreated_t> onLobbyCreated;

    /// Client: `lobbyToJoin` 0 waits for a Steam invite.
    public SteamTransport(bool isHost, ulong lobbyToJoin, Action<string> log)
    {
        this.isHost = isHost;
        receiveChannel = isHost ? 0 : 1;
        sendChannel = 1 - receiveChannel;
        this.lobbyToJoin = lobbyToJoin;
        this.log = log;
    }

    public bool HasPeer => peer != CSteamID.Nil;
    public string PeerName => HasPeer ? "steam:" + peer.m_SteamID : "none";

    // SteamManager.Instance would create a second manager if the game has not made one yet.
    static bool SteamReady => ManagerInstance.GetValue(null) != null && SteamManager.Initialized;

    void Start()
    {
        started = true;
        onSessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
        onLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
        onInvite = Callback<GameLobbyJoinRequested_t>.Create(r => { if (!isHost) Join(r.m_steamIDLobby); });
        if (isHost)
        {
            onLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            onLobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
            log($"Steam: creating lobby as {SteamUser.GetSteamID().m_SteamID}");
        }
        else if (lobbyToJoin != 0) Join(new CSteamID(lobbyToJoin));
        else log("Steam: waiting for an invite (friend list -> Join game)");
    }

    void Join(CSteamID id)
    {
        log($"Steam: joining lobby {id.m_SteamID}");
        SteamMatchmaking.JoinLobby(id);
    }

    void OnLobbyCreated(LobbyCreated_t r, bool ioFailure)
    {
        if (ioFailure || r.m_eResult != EResult.k_EResultOK) { log($"Steam: lobby creation failed ({r.m_eResult})"); return; }
        lobby = new CSteamID(r.m_ulSteamIDLobby);
        SteamMatchmaking.SetLobbyData(lobby, LobbyKey, Link.Protocol.ToString());
        log($"Steam: lobby {lobby.m_SteamID} open; invite a friend from the Steam overlay or give them "
            + $"--mp-steam-join {lobby.m_SteamID}");
    }

    void OnLobbyEnter(LobbyEnter_t r)
    {
        if (isHost) return; // the host enters its own lobby on creation
        var id = new CSteamID(r.m_ulSteamIDLobby);
        if (r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            log($"Steam: could not enter lobby {id.m_SteamID} (response {r.m_EChatRoomEnterResponse})");
            return;
        }
        string theirs = SteamMatchmaking.GetLobbyData(id, LobbyKey);
        if (theirs != Link.Protocol.ToString())
        {
            log($"Steam: lobby {id.m_SteamID} is not a Thronefall MP {Link.Protocol} lobby (protocol '{theirs}'); leaving");
            SteamMatchmaking.LeaveLobby(id);
            return;
        }
        if (lobby != CSteamID.Nil && lobby != id) SteamMatchmaking.LeaveLobby(lobby);
        lobby = id;
        peer = SteamMatchmaking.GetLobbyOwner(id);
        log($"Steam: in lobby {id.m_SteamID}, host is {PeerName}");
    }

    void OnSessionRequest(P2PSessionRequest_t r)
    {
        var from = r.m_steamIDRemote;
        bool ok = from == peer || InLobby(from);
        if (ok) SteamNetworking.AcceptP2PSessionWithUser(from);
        log($"Steam: P2P session from {from.m_SteamID} {(ok ? "accepted" : "ignored (not in our lobby)")}");
    }

    bool InLobby(CSteamID user)
    {
        if (lobby == CSteamID.Nil) return false;
        int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < n; i++)
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == user) return true;
        return false;
    }

    public void Send(byte[] data, int length)
    {
        if (HasPeer) SteamNetworking.SendP2PPacket(peer, data, (uint)length, EP2PSend.k_EP2PSendUnreliable, sendChannel);
    }

    public bool TryReceive(out byte[] data)
    {
        data = null;
        if (!started)
        {
            if (!SteamReady) return false;
            Start();
        }
        while (SteamNetworking.IsP2PPacketAvailable(out uint size, receiveChannel))
        {
            var buffer = new byte[size];
            if (!SteamNetworking.ReadP2PPacket(buffer, size, out uint read, out CSteamID from, receiveChannel)) continue;
            // Sessions are only accepted from the lobby, so any sender is a member.
            if (isHost) peer = from;
            else if (from != peer) continue;
            if (read != size) Array.Resize(ref buffer, (int)read);
            data = buffer;
            return true;
        }
        return false;
    }

    public void Close()
    {
        if (!started) return;
        if (HasPeer) SteamNetworking.CloseP2PSessionWithUser(peer); // closes all channels
        if (lobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(lobby);
    }
}
