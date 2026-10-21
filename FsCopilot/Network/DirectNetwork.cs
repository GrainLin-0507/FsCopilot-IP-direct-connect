namespace FsCopilot.Network;

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

/// <summary>
/// Server-less transport: dials a literal "ip[:port]" (or host[:port]) that the user typed
/// into the Join box, instead of asking the public rendezvous server for endpoints.
///
/// Why this works over a virtual LAN (n2n, ZeroTier, Radmin, WireGuard, ...):
/// - the socket binds to 0.0.0.0, so the OS routing table picks the tunnel adapter as source;
/// - no STUN / no NAT punch / no relay is involved at all;
/// - protocol-wise it is the same LiteNetLib connection the P2P layer uses, with the same
///   codec schema check, so the rest of the app (Coordinator, streams, PeerTags) is unchanged.
///
/// Since there is no rendezvous server, mesh formation is done here: every connected peer is
/// announced (id + the endpoint we observe for it) to everybody else, and whoever is missing a
/// link dials it. That reproduces what the public server does for 3+ players upstream.
/// </summary>
public sealed class DirectNetwork : INetwork, IDisposable
{
    private static readonly TimeSpan DialCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LateDialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GossipInterval = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _cts = new();
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Codecs _codecs = new Codecs()
        .Add<PeerTags, PeerTags.Codec>()
        .Add<PeerAddresses, PeerAddresses.Codec>();
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectionResult>> _waiters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _recentDials = new(StringComparer.Ordinal);
    private readonly string _peerId;
    private readonly string _selfName;
    private readonly int _configuredPort;

    /// <summary>Actual bound UDP port (may differ from the requested one if it was busy).</summary>
    public int Port => _net.LocalPort;

    /// <summary>Port configured for direct mode; used when the user omits the port.</summary>
    public int DefaultPort => _configuredPort;

    public IObservable<ICollection<Peer>> Peers { get; }

    public DirectNetwork(string peerId, string name, int port, string bind)
    {
        _peerId = peerId;
        _selfName = name;
        _configuredPort = port;

        _net = new(_listener)
        {
            NatPunchEnabled = false,
            UnconnectedMessagesEnabled = false,
            DisconnectTimeout = 15000
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;

        Start(bind, port);

        var peers = new List<NetPeer>();
        Peers = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ =>
            {
                _net.GetPeersNonAlloc(peers, ConnectionState.Connected);
                return peers
                    .Where(p => p.Tag is string)
                    .Select(p =>
                    {
                        var id = (string)p.Tag;
                        return new Peer(
                            id,
                            _names.TryGetValue(id, out var peerName) ? peerName : string.Empty,
                            p.Ping,
                            Peer.TransportKind.Direct);
                    })
                    .ToArray();
            })
            .Publish()
            .RefCount();

        Task.Run(() => Loop(_cts.Token), _cts.Token);
        Task.Run(() => GossipLoop(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Periodic address gossip. Joins are already announced on connect, this is the self-healing
    /// path: a link that failed to form (dropped packet, peer restarted, third player joined
    /// later) gets re-announced and re-dialled without anybody pressing Join again.
    /// </summary>
    private async Task GossipLoop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(GossipInterval, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var addresses = BuildAddressList();
                if (addresses.Entries.Length > 0)
                {
                    foreach (var peer in ConnectedPeers())
                        SendAddresses(peer, addresses);
                }

                await Task.Delay(GossipInterval, ct);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Direct] Gossip error"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try { _net.Stop(); } catch { /* ignore */ }

        foreach (var subject in _streams.Values)
        {
            var onCompleted = subject.GetType().GetMethod("OnCompleted");
            onCompleted?.Invoke(subject, null);
        }

        foreach (var waiter in _waiters)
            waiter.Value.TrySetResult(ConnectionResult.Failed);
    }

    private void Start(string bind, int port)
    {
        var started = TryStart(bind, port);

        if (!started && port != 0)
        {
            Log.Warning("[Direct] Port {Port} is busy, falling back to an ephemeral port", port);
            started = TryStart(bind, 0);
        }

        if (!started)
            throw new InvalidOperationException($"[Direct] Failed to start on port {port}");

        Log.Information("[Direct] BIND {Bind}:{Port}", bind, _net.LocalPort);
    }

    private bool TryStart(string bind, int port)
    {
        try
        {
            return IPAddress.TryParse(bind, out var address)
                ? _net.Start(address, IPAddress.IPv6Any, port)
                : _net.Start(port);
        }
        catch (Exception e)
        {
            // A busy port surfaces as SocketException on some platforms.
            Log.Debug("[Direct] Start on {Port} failed: {Error}", port, e.Message);
            return false;
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _net.PollEvents();
                await Task.Delay(15, ct);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Direct] Loop error"); }
        }
    }

    // ------------------------------------------------------------------ connections

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var token = request.Data.GetString();
        var parts = token.Split('|');

        if (parts.Length < 2 || !_codecs.Schema.Equals(parts[1], StringComparison.Ordinal))
        {
            Log.Warning("[Direct] REJ {Address} VERSION_MISMATCH", request.RemoteEndPoint);
            request.Reject(NetDataWriter.FromString("SCHEMA_MISMATCH"));
            return;
        }

        var peer = request.Accept();
        if (peer is null) return;

        peer.Tag = parts[0];
        Log.Information("[Direct] ACCEPT {Peer} from {Address}", parts[0], request.RemoteEndPoint);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        Log.Information("[Direct] CON {Address}:{Port}", peer.Address, peer.Port);

        // Learn each other's id + username immediately (same packet the P2P layer uses).
        SendTags(peer);

        // Mesh formation: tell the newcomer about everybody else, and tell everybody else
        // about the newcomer. The endpoint we announce is the one we observe for that peer,
        // which is the same UDP socket it listens on.
        SendAddresses(peer, BuildAddressList(exclude: peer));
        AnnouncePeer(peer);

        var endpointKey = new IPEndPoint(peer.Address, peer.Port).ToString();
        if (_waiters.TryGetValue(endpointKey, out var endpointWaiter))
            endpointWaiter.TrySetResult(ConnectionResult.Success);

        if (peer.Tag is string id && _waiters.TryGetValue(id, out var idWaiter))
            idWaiter.TrySetResult(ConnectionResult.Success);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        var key = peer.Tag as string ?? string.Empty;
        var endpointKey = new IPEndPoint(peer.Address, peer.Port).ToString();
        var result = info.Reason == DisconnectReason.ConnectionRejected
            ? ConnectionResult.Rejected
            : ConnectionResult.Failed;

        foreach (var waiter in _waiters)
        {
            if (waiter.Key == key || waiter.Key == endpointKey)
                waiter.Value.TrySetResult(result);
        }

        Log.Information("[Direct] DIS {Peer} ({Reason})", key, info.Reason);
    }

    public async Task<ConnectionResult> Connect(string target, CancellationToken ct)
    {
        var value = (target ?? string.Empty).Trim();
        if (value.Length == 0) return ConnectionResult.Failed;

        IPEndPoint? endpoint;
        try
        {
            endpoint = await Resolve(value, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ConnectionResult.Failed;
        }
        catch (Exception e)
        {
            Log.Warning("[Direct] Invalid target '{Target}': {Error}", value, e.Message);
            return ConnectionResult.Failed;
        }

        if (endpoint is null)
        {
            Log.Warning("[Direct] Cannot resolve '{Target}'", value);
            return ConnectionResult.Failed;
        }

        var key = endpoint.ToString();

        // Already connected? Match both by known peer id and by the endpoint itself
        // (the tag is the peer id once PeerTags has been exchanged).
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Connected);
        if (peers.Any(p => p.Tag as string == key ||
                           (p.Address.Equals(endpoint.Address) && p.Port == endpoint.Port)))
            return ConnectionResult.Success;

        var waiter = new TaskCompletionSource<ConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(key, waiter))
            return ConnectionResult.Failed;

        try
        {
            var peer = _net.Connect(endpoint, $"{_peerId}|{_codecs.Schema}");
            if (peer is null) return ConnectionResult.Failed;

            peer.Tag = key;
            Log.Information("[Direct] DIAL {Peer}", key);

            using var reg = ct.Register(
                static state => ((TaskCompletionSource<ConnectionResult>)state!).TrySetResult(ConnectionResult.Failed),
                waiter);

            return await waiter.Task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, "[Direct] Connect failed");
            return ConnectionResult.Failed;
        }
        finally
        {
            _waiters.TryRemove(key, out _);
        }
    }

    private async Task<IPEndPoint?> Resolve(string value, CancellationToken ct)
    {
        // Fast path: "1.2.3.4:38510", "1.2.3.4", "[::1]:38510", "host:port"
        if (TryParseTarget(value, _configuredPort, out var known)) return known;

        // Slow path: a bare host name needs DNS.
        var host = value;
        var port = _configuredPort;

        if (!value.Contains("::", StringComparison.Ordinal))
        {
            var index = value.LastIndexOf(':');
            if (index > 0 &&
                int.TryParse(value[(index + 1)..], out var explicitPort) &&
                explicitPort is > 0 and <= 65535)
            {
                host = value[..index];
                port = explicitPort;
            }
        }

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault();

        return address is null ? null : new IPEndPoint(address, port);
    }

    /// <summary>
    /// Parses what the user typed into the Join box without touching DNS:
    /// "10.0.0.2" -> 10.0.0.2:&lt;defaultPort&gt;, "10.0.0.2:38600" -> 10.0.0.2:38600,
    /// "[::1]:38600" / "[::1]". Returns false for host names (caller resolves them).
    /// </summary>
    public static bool TryParseTarget(string value, int defaultPort, [NotNullWhen(true)] out IPEndPoint? endpoint)
    {
        endpoint = null;

        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        // NOTE: IPEndPoint.TryParse("10.0.0.2") succeeds with port 0 - that must not become
        // the dial target, otherwise the user is forced to type ":38510" every single time.
        if (IPEndPoint.TryParse(text, out var parsed) && parsed.Port is > 0 and <= 65535)
        {
            endpoint = parsed;
            return true;
        }

        if (IPAddress.TryParse(text.Trim('[', ']'), out var ip))
        {
            endpoint = new IPEndPoint(ip, defaultPort);
            return true;
        }

        // "host:port" - port only, the host still needs DNS.
        if (!text.Contains("::", StringComparison.Ordinal))
        {
            var index = text.LastIndexOf(':');
            if (index > 0 &&
                int.TryParse(text[(index + 1)..], out var port) &&
                port is > 0 and <= 65535)
            {
                var host = text[..index].Trim('[', ']');
                if (IPAddress.TryParse(host, out var hostIp))
                {
                    endpoint = new IPEndPoint(hostIp, port);
                    return true;
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ mesh

    private static bool IsPeerId(string value) => value.Length > 0 && value.IndexOf(':') < 0;

    private PeerAddresses BuildAddressList(NetPeer? exclude = null)
    {
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Connected);

        var entries = new List<PeerAddresses.Entry>();
        foreach (var peer in peers)
        {
            if (ReferenceEquals(peer, exclude)) continue;
            if (peer.Tag is not string id || id == _peerId || !IsPeerId(id)) continue;

            entries.Add(new PeerAddresses.Entry(id, new IPEndPoint(peer.Address, peer.Port).ToString()));
        }

        return new PeerAddresses(entries.ToArray());
    }

    private void SendAddresses(NetPeer peer, PeerAddresses addresses)
    {
        if (addresses.Entries.Length == 0) return;

        var data = _codecs.Encode(addresses);
        if (data.Length == 0) return;

        peer.Send(data, DeliveryMethod.ReliableOrdered);
        Log.Verbose("[Direct] MESH share {Count} peer(s) with {Address}:{Port}",
            addresses.Entries.Length, peer.Address, peer.Port);
    }

    private void AnnouncePeer(NetPeer newcomer)
    {
        if (newcomer.Tag is not string id || !IsPeerId(id)) return;

        var entry = new PeerAddresses.Entry(id, new IPEndPoint(newcomer.Address, newcomer.Port).ToString());
        var address = new PeerAddresses([entry]);

        foreach (var peer in ConnectedPeers())
        {
            if (ReferenceEquals(peer, newcomer)) continue;
            if (peer.Tag is not string other || !IsPeerId(other)) continue;
            SendAddresses(peer, address);
        }
    }

    private List<NetPeer> ConnectedPeers()
    {
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Connected);
        return peers;
    }

    private bool IsEndpointConnected(IPEndPoint endpoint) =>
        ConnectedPeers().Any(p => p.Address.Equals(endpoint.Address) && p.Port == endpoint.Port);

    private void OnAddresses(PeerAddresses addresses)
    {
        foreach (var entry in addresses.Entries)
        {
            if (entry.PeerId == _peerId) continue;
            if (!IPEndPoint.TryParse(entry.Endpoint, out var endpoint)) continue;

            if (IsEndpointConnected(endpoint))
            {
                // Already in a room with this peer; nothing to do.
                continue;
            }

            // Deterministic tie-break: the lexicographically smaller peer id dials first so two
            // peers that learn about each other simultaneously do not race. The other side retries
            // shortly after, in case the first attempt was dropped.
            var immediate = string.CompareOrdinal(_peerId, entry.PeerId) < 0;
            var delay = immediate ? TimeSpan.Zero : LateDialDelay;

            Log.Information("[Direct] MESH learn {Peer} at {Endpoint} ({When})",
                entry.PeerId, endpoint, immediate ? "dial" : "wait");

            _ = Task.Run(() => DialMeshAsync(endpoint, delay));
        }
    }

    private async Task DialMeshAsync(IPEndPoint endpoint, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _cts.Token).ConfigureAwait(false);

            var key = endpoint.ToString();
            if (IsEndpointConnected(endpoint)) return;

            var now = DateTime.UtcNow;
            if (_recentDials.TryGetValue(key, out var last) && now - last < DialCooldown) return;
            _recentDials[key] = now;

            Log.Information("[Direct] MESH dial {Endpoint}", endpoint);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await Connect(key, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception e) { Log.Warning("[Direct] MESH dial {Endpoint} failed: {Error}", endpoint, e.Message); }
    }

    // ------------------------------------------------------------------ packets

    private void SendTags(NetPeer peer)
    {
        var data = _codecs.Encode(new PeerTags([new PeerTags.Tag(_peerId, _selfName)]));
        if (data.Length == 0) return;
        peer.Send(data, DeliveryMethod.ReliableOrdered);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var packet = _codecs.Decode(reader);
        if (packet is null)
        {
            Log.Error("[Direct] An error occurred while decoding message");
            return;
        }

        if (packet is PeerTags tags) ApplyTags(peer, tags);
        else if (packet is PeerAddresses addresses) OnAddresses(addresses);

        if (!_streams.TryGetValue(packet.GetType(), out var subject)) return;
        subject.GetType().GetMethod("OnNext")!.Invoke(subject, [packet]);
    }

    private void ApplyTags(NetPeer peer, PeerTags tags)
    {
        foreach (var tag in tags.Peers)
        {
            if (tag.PeerId == _peerId) continue;
            _names.AddOrUpdate(tag.PeerId, tag.Name, (_, _) => tag.Name);
        }

        // Replace the temporary endpoint tag with the real peer id (both directions).
        var sender = tags.Peers.FirstOrDefault(t => t.PeerId != _peerId);
        if (sender is not null) peer.Tag = sender.PeerId;
    }

    public void Disconnect() => _net.DisconnectAll();

    public void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull
    {
        var data = _codecs.Encode(packet);
        if (data.Length == 0) return;

        _net.SendToAll(data, unreliable ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered);
    }

    public void RegisterPacket<TPacket, TCodec>()
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new() =>
        _codecs.Add<TPacket, TCodec>();

    public IObservable<TPacket> Stream<TPacket>() =>
        ((IObservable<TPacket>)_streams.GetOrAdd(typeof(TPacket), _ => new Subject<TPacket>()))
        .ObserveOn(TaskPoolScheduler.Default);
}

/// <summary>
/// Address gossip used to build a full mesh without a rendezvous server:
/// "these peers exist and here is the endpoint I observe for each of them".
/// </summary>
public record PeerAddresses(PeerAddresses.Entry[] Entries)
{
    public const int MaxEntries = 64;

    public record Entry(string PeerId, string Endpoint);

    public sealed class Codec : IPacketCodec<PeerAddresses>
    {
        public void Encode(PeerAddresses packet, BinaryWriter bw)
        {
            bw.Write(packet.Entries.Length);
            foreach (var entry in packet.Entries)
            {
                bw.Write(entry.PeerId);
                bw.Write(entry.Endpoint);
            }
        }

        public PeerAddresses Decode(BinaryReader br)
        {
            var count = br.ReadInt32();
            if (count is < 0 or > MaxEntries)
                throw new InvalidDataException($"Peer address count out of range: {count}");

            var entries = new Entry[count];
            for (var i = 0; i < count; i++)
                entries[i] = new(br.ReadString(), br.ReadString());

            return new(entries);
        }
    }
}
