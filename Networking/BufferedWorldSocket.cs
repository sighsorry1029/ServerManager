using System;
using System.Collections.Generic;

namespace ServerManager;

/// <summary>
/// Holds core world/control traffic until the authoritative character is applied.
/// Third-party direct RPCs pass during admission so their own handshakes can finish;
/// their handlers remain responsible for authorization. Quarantine and final-save
/// isolation use separate, restricted policies even after character readiness.
/// </summary>
internal sealed class BufferedWorldSocket : ISocket
{
    private const int MaximumHeldInboundBytesPerConnection = 1024 * 1024;
    private const int MaximumHeldInboundPackagesPerConnection = 256;
    private const int MaximumHeldInboundBytesGlobally = 16 * 1024 * 1024;
    private const int MaximumHeldOutboundPackagesPerConnection = 8192;
    private const int MaximumHeldOutboundPackagesGlobally = 32768;
    private const int MaximumHeldOutboundBytesGlobally = 128 * 1024 * 1024;

    private static readonly int ProtocolMethodHash =
        StringExtensionMethods.GetStableHashCode(
            ServerManagerRuntime.ProtocolRpcName);
    private static readonly int DetectionMethodHash =
        StringExtensionMethods.GetStableHashCode(
            ServerManagerRuntime.DetectionRpcName);
    private static readonly int AdminEntitlementMethodHash =
        StringExtensionMethods.GetStableHashCode(
            ServerManagerRuntime.AdminEntitlementRpcName);
    private static readonly int ServerHandshakeMethodHash =
        StringExtensionMethods.GetStableHashCode("ServerHandshake");
    private static readonly object GlobalInboundBudgetGate = new();
    private static readonly object GlobalOutboundBudgetGate = new();
    private static int _globalHeldInboundBytes;
    private static int _globalHeldOutboundBytes;
    private static int _globalHeldOutboundPackages;

    private static readonly HashSet<int> PassOutboundMethodHashes = new()
    {
        ProtocolMethodHash,
        StringExtensionMethods.GetStableHashCode("ClientHandshake"),
        StringExtensionMethods.GetStableHashCode("Disconnect"),
        StringExtensionMethods.GetStableHashCode("Error"),
        StringExtensionMethods.GetStableHashCode("Kicked")
    };

    // Do not use this admission policy for final-save isolation. Direct mod RPCs
    // may synchronize configuration here, but may mutate items during logout.
    private static readonly HashSet<int> HoldOutboundMethodHashes = new()
    {
        DetectionMethodHash,
        AdminEntitlementMethodHash,
        StringExtensionMethods.GetStableHashCode(ServerManagerRuntime.AdminCommandRpcName),
        StringExtensionMethods.GetStableHashCode(ServerManagerRuntime.EventReportRpcName),
        StringExtensionMethods.GetStableHashCode(ServerManagerRuntime.EventDisplayRpcName),
        StringExtensionMethods.GetStableHashCode("PeerInfo"),
        StringExtensionMethods.GetStableHashCode("ServerHandshake"),
        StringExtensionMethods.GetStableHashCode("SavePlayerProfile"),
        StringExtensionMethods.GetStableHashCode("RoutedRPC"),
        StringExtensionMethods.GetStableHashCode("ZDOData"),
        StringExtensionMethods.GetStableHashCode("RefPos"),
        StringExtensionMethods.GetStableHashCode("CharacterID"),
        StringExtensionMethods.GetStableHashCode("ServerSyncedPlayerData"),
        StringExtensionMethods.GetStableHashCode("PlayerList"),
        StringExtensionMethods.GetStableHashCode("AdminList"),
        StringExtensionMethods.GetStableHashCode("RemotePrint"),
        StringExtensionMethods.GetStableHashCode("NetTime"),
        StringExtensionMethods.GetStableHashCode("Kick"),
        StringExtensionMethods.GetStableHashCode("Ban"),
        StringExtensionMethods.GetStableHashCode("Unban"),
        StringExtensionMethods.GetStableHashCode("RPC_RemoteCommand"),
        StringExtensionMethods.GetStableHashCode("Save"),
        StringExtensionMethods.GetStableHashCode("PrintBanned")
    };

    private static readonly HashSet<int> PrePeerInfoInboundMethodHashes = new()
    {
        ProtocolMethodHash,
        DetectionMethodHash,
        StringExtensionMethods.GetStableHashCode("ServerHandshake"),
        StringExtensionMethods.GetStableHashCode("PeerInfo"),
        StringExtensionMethods.GetStableHashCode("Disconnect")
    };

    private static readonly HashSet<int> PreReadyInboundMethodHashes = new()
    {
        ProtocolMethodHash,
        DetectionMethodHash,
        StringExtensionMethods.GetStableHashCode("Disconnect")
    };

    private static readonly HashSet<int> DetectionOnlyInboundMethodHashes = new()
    {
        DetectionMethodHash,
        StringExtensionMethods.GetStableHashCode("Disconnect")
    };

    private static readonly HashSet<int> FinalSaveOnlyInboundMethodHashes = new()
    {
        ProtocolMethodHash,
        DetectionMethodHash,
        StringExtensionMethods.GetStableHashCode("Disconnect")
    };

    private static readonly HashSet<int> UnsafeInboundMethodHashes = new()
    {
        StringExtensionMethods.GetStableHashCode(ServerManagerRuntime.AdminCommandRpcName),
        AdminEntitlementMethodHash,
        StringExtensionMethods.GetStableHashCode("PeerInfo"),
        StringExtensionMethods.GetStableHashCode("ServerHandshake"),
        StringExtensionMethods.GetStableHashCode("SavePlayerProfile"),
        StringExtensionMethods.GetStableHashCode("RoutedRPC"),
        StringExtensionMethods.GetStableHashCode("ZDOData"),
        StringExtensionMethods.GetStableHashCode("RefPos"),
        StringExtensionMethods.GetStableHashCode("CharacterID"),
        StringExtensionMethods.GetStableHashCode("ServerSyncedPlayerData"),
        StringExtensionMethods.GetStableHashCode("PlayerList"),
        StringExtensionMethods.GetStableHashCode("AdminList"),
        StringExtensionMethods.GetStableHashCode("RemotePrint"),
        StringExtensionMethods.GetStableHashCode("NetTime"),
        StringExtensionMethods.GetStableHashCode("Kick"),
        StringExtensionMethods.GetStableHashCode("Ban"),
        StringExtensionMethods.GetStableHashCode("Unban"),
        StringExtensionMethods.GetStableHashCode("RPC_RemoteCommand"),
        StringExtensionMethods.GetStableHashCode("Save"),
        StringExtensionMethods.GetStableHashCode("PrintBanned")
    };

    // Preserve the existing bounded replay of our events and wrong-direction
    // vanilla messages, without classifying arbitrary mod RPCs as gameplay.
    private static readonly HashSet<int> HeldInboundMethodHashes = new()
    {
        StringExtensionMethods.GetStableHashCode(ServerManagerRuntime.EventReportRpcName),
        StringExtensionMethods.GetStableHashCode(ServerManagerRuntime.EventDisplayRpcName),
        StringExtensionMethods.GetStableHashCode("ClientHandshake"),
        StringExtensionMethods.GetStableHashCode("Error"),
        StringExtensionMethods.GetStableHashCode("Kicked")
    };

    private readonly object _gate = new();
    private readonly List<ZPackage> _heldOutboundPackages = new();
    private readonly Queue<ZPackage> _heldInboundPackages = new();
    private readonly int _maximumBufferedBytes;
    private int _bufferedBytes;
    private int _heldInboundBytes;
    private int _heldOutboundBytes;
    private bool _serverHandshakeSeen;
    private bool _peerInfoAdmitted;
    private bool _released;
    private bool _discarded;
    private bool _detectionOnlyInbound;
    private bool _finalSaveOutboundRestricted;
    private bool _finalSaveInboundRestricted;

    internal BufferedWorldSocket(ISocket original, int maximumBufferedBytes)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        if (maximumBufferedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedBytes));
        }

        _maximumBufferedBytes = maximumBufferedBytes;
    }

    internal ISocket Original { get; }

    internal bool Overflowed { get; private set; }

    internal bool InboundViolation { get; private set; }

    internal bool Released
    {
        get
        {
            lock (_gate)
            {
                return _released;
            }
        }
    }

    internal bool Quarantined
    {
        get
        {
            lock (_gate)
            {
                return _discarded;
            }
        }
    }

    internal int BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bufferedBytes;
            }
        }
    }

    internal void MarkPeerInfoAdmitted()
    {
        lock (_gate)
        {
            if (_released || _discarded || _peerInfoAdmitted)
            {
                throw new InvalidOperationException(
                    "The PeerInfo gate was already finalized.");
            }

            _peerInfoAdmitted = true;
        }
    }

    internal bool TryRelease(out string failure)
    {
        List<ZPackage> packages;
        lock (_gate)
        {
            failure = string.Empty;
            if (_released || _discarded)
            {
                failure = "The world synchronization gate was already finalized.";
                return false;
            }

            if (Overflowed)
            {
                failure = "World synchronization exceeded the configured buffer.";
                return false;
            }

            if (InboundViolation)
            {
                failure = "Unsafe vanilla traffic arrived before character readiness.";
                return false;
            }

            _released = true;
            packages = new List<ZPackage>(_heldOutboundPackages);
            foreach (ZPackage package in packages)
            {
                _bufferedBytes -= package.Size();
            }

            _heldOutboundPackages.Clear();
            ReleaseGlobalOutbound(
                _heldOutboundBytes,
                packages.Count);
            _heldOutboundBytes = 0;
        }

        foreach (ZPackage package in packages)
        {
            Original.Send(package);
        }

        Original.Flush();

        return true;
    }

    internal void Discard()
    {
        lock (_gate)
        {
            if (_discarded)
            {
                return;
            }

            _discarded = true;
            ClearHeldPackagesLocked();
        }
    }

    internal bool TryRestrictInboundToDetection()
    {
        lock (_gate)
        {
            if (_discarded || !_released)
            {
                return false;
            }

            ClearHeldInboundLocked();
            _detectionOnlyInbound = true;
            return true;
        }
    }

    // Used only when a freshly authenticated administrator cancels a pending
    // numerical-cap kick. Leave final-save, overflow and quarantine gates intact.
    internal void ReleaseDetectionOnlyInbound()
    {
        lock (_gate)
        {
            if (_released && !_discarded) _detectionOnlyInbound = false;
        }
    }

    // Phase one: order FinalSaveReady after all server gameplay already sent,
    // then prevent later server traffic from reaching the exiting client.
    internal bool TryRestrictOutboundForFinalSave()
    {
        lock (_gate)
        {
            if (_discarded ||
                !_released ||
                _detectionOnlyInbound ||
                _finalSaveOutboundRestricted ||
                _finalSaveInboundRestricted)
            {
                return false;
            }

            _finalSaveOutboundRestricted = true;
            return true;
        }
    }

    // Phase two: the first final character fragment follows every client world
    // mutation drained after Ready. From this point only bounded control/save
    // traffic may enter from the peer.
    internal bool TryCompleteFinalSaveRestriction()
    {
        lock (_gate)
        {
            if (_discarded ||
                !_released ||
                _detectionOnlyInbound ||
                !_finalSaveOutboundRestricted ||
                _finalSaveInboundRestricted)
            {
                return false;
            }

            ClearHeldInboundLocked();
            _finalSaveInboundRestricted = true;
            return true;
        }
    }

    public bool IsConnected() => Original.IsConnected();

    public void Send(ZPackage pkg)
    {
        if (pkg == null)
        {
            throw new ArgumentNullException(nameof(pkg));
        }

        lock (_gate)
        {
            if (_discarded)
            {
                return;
            }

            if (_released)
            {
                if (!_finalSaveOutboundRestricted ||
                    ShouldPassOutbound(pkg))
                {
                    Original.Send(pkg);
                }

                return;
            }
        }

        if (ShouldPassBeforeReady(pkg))
        {
            lock (_gate)
            {
                // A concurrent release must not let the admission exception
                // bypass a subsequent final-save restriction or quarantine.
                if (!_discarded &&
                    (!_finalSaveOutboundRestricted || ShouldPassOutbound(pkg)))
                {
                    Original.Send(pkg);
                }
            }

            return;
        }

        byte[] packageBytes = pkg.GetArray();
        lock (_gate)
        {
            if (_released)
            {
                if (!_finalSaveOutboundRestricted ||
                    ShouldPassOutbound(pkg))
                {
                    Original.Send(pkg);
                }

                return;
            }

            if (_discarded || Overflowed)
            {
                return;
            }

            if (packageBytes.Length > _maximumBufferedBytes - _bufferedBytes ||
                _heldOutboundPackages.Count >=
                    MaximumHeldOutboundPackagesPerConnection)
            {
                Overflowed = true;
                ClearHeldPackagesLocked();
                return;
            }

            ZPackage copy = new(packageBytes);
            copy.SetPos(pkg.GetPos());
            if (!TryReserveGlobalOutbound(packageBytes.Length))
            {
                Overflowed = true;
                ClearHeldPackagesLocked();
                return;
            }

            _heldOutboundPackages.Add(copy);
            _bufferedBytes += packageBytes.Length;
            _heldOutboundBytes += packageBytes.Length;
        }
    }

    private static bool ShouldPassBeforeReady(ZPackage pkg)
    {
        return TryReadMethodHash(pkg, out int methodHash) &&
               !HoldOutboundMethodHashes.Contains(methodHash);
    }

    private static bool ShouldPassOutbound(ZPackage pkg)
    {
        int position = pkg.GetPos();
        try
        {
            if (pkg.Size() < sizeof(int))
            {
                return false;
            }

            pkg.SetPos(0);
            int methodHash = pkg.ReadInt();
            return methodHash == 0 ||
                   PassOutboundMethodHashes.Contains(methodHash);
        }
        finally
        {
            pkg.SetPos(position);
        }
    }

    public ZPackage Recv()
    {
        lock (_gate)
        {
            if (_discarded)
            {
                return DrainQuarantinedInbound();
            }

            if (_detectionOnlyInbound)
            {
                return ReceiveDetectionOnlyInbound();
            }

            if (_finalSaveInboundRestricted)
            {
                return ReceiveFinalSaveOnlyInbound();
            }

            if (_released && _heldInboundPackages.Count > 0)
            {
                ZPackage held = _heldInboundPackages.Dequeue();
                int heldSize = held.Size();
                _bufferedBytes -= heldSize;
                _heldInboundBytes -= heldSize;
                ReleaseGlobalInboundBytes(heldSize);
                return held;
            }

            if (_released)
            {
                return Original.Recv();
            }
        }

        const int maximumDiscardedPerPoll = 32;
        for (int discarded = 0; discarded < maximumDiscardedPerPoll; ++discarded)
        {
            ZPackage package = Original.Recv();
            if (package == null)
            {
                return null!;
            }

            int methodHash;
            if (!TryReadMethodHash(package, out methodHash))
            {
                MarkInboundViolation();
                continue;
            }

            bool peerInfoAdmitted;
            bool duplicateServerHandshake = false;
            lock (_gate)
            {
                if (_released)
                {
                    return package;
                }

                peerInfoAdmitted = _peerInfoAdmitted;
                if (!peerInfoAdmitted &&
                    methodHash == ServerHandshakeMethodHash)
                {
                    if (_serverHandshakeSeen)
                    {
                        duplicateServerHandshake = true;
                    }
                    else
                    {
                        _serverHandshakeSeen = true;
                    }
                }
            }

            if (duplicateServerHandshake)
            {
                MarkInboundViolation();
                continue;
            }

            HashSet<int> allowed = peerInfoAdmitted
                ? PreReadyInboundMethodHashes
                : PrePeerInfoInboundMethodHashes;
            if (methodHash == 0 || allowed.Contains(methodHash))
            {
                return package;
            }

            if (UnsafeInboundMethodHashes.Contains(methodHash))
            {
                MarkInboundViolation();
                continue;
            }

            if (!HeldInboundMethodHashes.Contains(methodHash))
            {
                // Configuration/version exchanges must not wait for the very
                // PeerInfo/Ready transition that depends on those exchanges.
                return package;
            }

            if (!HoldInbound(package))
            {
                return null!;
            }
        }

        return null!;
    }

    private ZPackage ReceiveDetectionOnlyInbound()
    {
        return ReceiveRestrictedInbound(DetectionOnlyInboundMethodHashes);
    }

    private ZPackage ReceiveFinalSaveOnlyInbound()
    {
        return ReceiveRestrictedInbound(FinalSaveOnlyInboundMethodHashes);
    }

    private ZPackage ReceiveRestrictedInbound(HashSet<int> allowedMethodHashes)
    {
        const int maximumDiscardedPerPoll = 32;
        for (int discarded = 0; discarded < maximumDiscardedPerPoll; ++discarded)
        {
            ZPackage package = Original.Recv();
            if (package == null)
            {
                return null!;
            }

            if (!TryReadMethodHash(package, out int methodHash))
            {
                continue;
            }

            if (methodHash == 0 ||
                allowedMethodHashes.Contains(methodHash))
            {
                return package;
            }
        }

        return null!;
    }

    private ZPackage DrainQuarantinedInbound()
    {
        const int maximumDiscardedPerPoll = 32;
        for (int discarded = 0; discarded < maximumDiscardedPerPoll; ++discarded)
        {
            if (Original.Recv() == null)
            {
                break;
            }
        }

        return null!;
    }

    private bool HoldInbound(ZPackage package)
    {
        int size;
        try
        {
            size = package.Size();
        }
        catch
        {
            MarkInboundViolation();
            return false;
        }

        lock (_gate)
        {
            if (_released)
            {
                return false;
            }

            if (_discarded || Overflowed)
            {
                return false;
            }

            if (size < 0 ||
                size > _maximumBufferedBytes - _bufferedBytes ||
                size > MaximumHeldInboundBytesPerConnection - _heldInboundBytes ||
                _heldInboundPackages.Count >=
                    MaximumHeldInboundPackagesPerConnection)
            {
                Overflowed = true;
                ClearHeldPackagesLocked();
                return false;
            }
        }

        byte[] bytes;
        try
        {
            bytes = package.GetArray();
        }
        catch
        {
            MarkInboundViolation();
            return false;
        }

        if (bytes.Length != size)
        {
            MarkInboundViolation();
            return false;
        }

        lock (_gate)
        {
            if (_released || _discarded || Overflowed)
            {
                return false;
            }

            if (bytes.Length > _maximumBufferedBytes - _bufferedBytes)
            {
                Overflowed = true;
                ClearHeldPackagesLocked();
                return false;
            }

            if (bytes.Length >
                    MaximumHeldInboundBytesPerConnection - _heldInboundBytes ||
                _heldInboundPackages.Count >=
                    MaximumHeldInboundPackagesPerConnection ||
                !TryReserveGlobalInboundBytes(bytes.Length))
            {
                Overflowed = true;
                ClearHeldPackagesLocked();
                return false;
            }

            ZPackage copy = new(bytes);
            copy.SetPos(package.GetPos());
            _heldInboundPackages.Enqueue(copy);
            _bufferedBytes += bytes.Length;
            _heldInboundBytes += bytes.Length;
            return true;
        }
    }

    private void ClearHeldPackagesLocked()
    {
        ClearHeldInboundLocked();

        if (_heldOutboundBytes > 0)
        {
            ReleaseGlobalOutbound(
                _heldOutboundBytes,
                _heldOutboundPackages.Count);
        }

        _heldOutboundPackages.Clear();
        _bufferedBytes = 0;
        _heldOutboundBytes = 0;
    }

    private void ClearHeldInboundLocked()
    {
        if (_heldInboundBytes > 0)
        {
            ReleaseGlobalInboundBytes(_heldInboundBytes);
            _bufferedBytes -= _heldInboundBytes;
            if (_bufferedBytes < 0)
            {
                _bufferedBytes = 0;
            }
        }

        _heldInboundPackages.Clear();
        _heldInboundBytes = 0;
    }

    private static bool TryReserveGlobalInboundBytes(int byteCount)
    {
        if (byteCount < 0)
        {
            return false;
        }

        lock (GlobalInboundBudgetGate)
        {
            if (byteCount >
                MaximumHeldInboundBytesGlobally - _globalHeldInboundBytes)
            {
                return false;
            }

            _globalHeldInboundBytes += byteCount;
            return true;
        }
    }

    private static void ReleaseGlobalInboundBytes(int byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        lock (GlobalInboundBudgetGate)
        {
            _globalHeldInboundBytes -= byteCount;
            if (_globalHeldInboundBytes < 0)
            {
                _globalHeldInboundBytes = 0;
            }
        }
    }

    private static bool TryReserveGlobalOutbound(int byteCount)
    {
        if (byteCount < 0)
        {
            return false;
        }

        lock (GlobalOutboundBudgetGate)
        {
            if (byteCount >
                    MaximumHeldOutboundBytesGlobally -
                    _globalHeldOutboundBytes ||
                _globalHeldOutboundPackages >=
                    MaximumHeldOutboundPackagesGlobally)
            {
                return false;
            }

            _globalHeldOutboundBytes += byteCount;
            ++_globalHeldOutboundPackages;
            return true;
        }
    }

    private static void ReleaseGlobalOutbound(
        int byteCount,
        int packageCount)
    {
        if (byteCount <= 0 && packageCount <= 0)
        {
            return;
        }

        lock (GlobalOutboundBudgetGate)
        {
            _globalHeldOutboundBytes -= byteCount;
            _globalHeldOutboundPackages -= packageCount;
            if (_globalHeldOutboundBytes < 0)
            {
                _globalHeldOutboundBytes = 0;
            }

            if (_globalHeldOutboundPackages < 0)
            {
                _globalHeldOutboundPackages = 0;
            }
        }
    }

    private void MarkInboundViolation()
    {
        lock (_gate)
        {
            InboundViolation = true;
        }
    }

    private static bool TryReadMethodHash(
        ZPackage package,
        out int methodHash)
    {
        methodHash = 0;
        int position = package.GetPos();
        try
        {
            if (package.Size() < sizeof(int))
            {
                return false;
            }

            package.SetPos(0);
            methodHash = package.ReadInt();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            package.SetPos(position);
        }
    }

    public int GetSendQueueSize() => Original.GetSendQueueSize();

    public int GetCurrentSendRate() => Original.GetCurrentSendRate();

    public bool IsHost() => Original.IsHost();

    public void Dispose()
    {
        Discard();
        Original.Dispose();
    }

    public bool GotNewData()
    {
        lock (_gate)
        {
            if (_released && _heldInboundPackages.Count > 0)
            {
                return true;
            }
        }

        return Original.GotNewData();
    }

    public void Close()
    {
        Discard();
        Original.Close();
    }

    public string GetEndPointString() => Original.GetEndPointString();

    public void GetAndResetStats(out int totalSent, out int totalRecv) =>
        Original.GetAndResetStats(out totalSent, out totalRecv);

    public void GetConnectionQuality(
        out float localQuality,
        out float remoteQuality,
        out int ping,
        out float outByteSec,
        out float inByteSec) =>
        Original.GetConnectionQuality(
            out localQuality,
            out remoteQuality,
            out ping,
            out outByteSec,
            out inByteSec);

    public ISocket Accept() => Original.Accept();

    public int GetHostPort() => Original.GetHostPort();

    public bool Flush() => Original.Flush();

    public string GetHostName() => Original.GetHostName();

    public void VersionMatch() => Original.VersionMatch();
}
