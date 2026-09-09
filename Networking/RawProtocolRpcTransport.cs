#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;

namespace ServerManager
{
    public delegate void RawProtocolPacketHandler(ZRpc rpc, ZPackage protocolPackage);

    public delegate void RawProtocolTransportErrorHandler(
        ZRpc rpc,
        ProtocolRejection rejection);

    internal delegate bool RawProtocolPreflightHandler(
        ZRpc rpc,
        byte[] source,
        int offset,
        int length,
        out ProtocolRejection rejection);

    /// <summary>
    /// Dispatches ServerManager RPC bodies before Valheim's private RPC-method
    /// table deserializes parameters. This preserves the bounded raw wire format
    /// without implementing or directly accessing ZRpc's private RpcMethodBase.
    /// Register, unregister, send, and receive run on the game/main thread.
    /// </summary>
    public static class RawProtocolRpcTransport
    {
        private const int MaximumDebugMethodNameBytes = 4 * 1024;

        private sealed class RawRegistration
        {
            internal RawRegistration(
                int maximumBytes,
                RawProtocolPacketHandler packetHandler,
                RawProtocolTransportErrorHandler transportErrorHandler)
            {
                MaximumBytes = maximumBytes;
                Handler = packetHandler;
                ErrorHandler = transportErrorHandler;
            }

            internal int MaximumBytes { get; }
            internal RawProtocolPacketHandler Handler { get; }
            internal RawProtocolTransportErrorHandler ErrorHandler { get; }
        }

        private sealed class PreflightRegistration
        {
            internal PreflightRegistration(
                int maximumBytes,
                RawProtocolPreflightHandler validator,
                RawProtocolTransportErrorHandler transportErrorHandler)
            {
                MaximumBytes = maximumBytes;
                Validator = validator;
                ErrorHandler = transportErrorHandler;
            }

            internal int MaximumBytes { get; }
            internal RawProtocolPreflightHandler Validator { get; }
            internal RawProtocolTransportErrorHandler ErrorHandler { get; }
        }

        private sealed class RegistrationTable
        {
            internal readonly Dictionary<int, RawRegistration> Raw = new();
            internal readonly Dictionary<int, PreflightRegistration> Preflights = new();
        }

        private static readonly object RegistrationGate = new();
        private static readonly ConditionalWeakTable<ZRpc, RegistrationTable>
            Registrations = new();

        public static void Register(
            ZRpc rpc,
            string methodName,
            ConnectionProtocolLimits limits,
            RawProtocolPacketHandler handler,
            RawProtocolTransportErrorHandler errorHandler = null)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            Register(
                rpc,
                methodName,
                limits.MaxPacketBytes,
                handler,
                errorHandler);
        }

        public static void Register(
            ZRpc rpc,
            string methodName,
            int maximumPacketBytes,
            RawProtocolPacketHandler handler,
            RawProtocolTransportErrorHandler errorHandler = null)
        {
            ValidateRegistration(rpc, methodName);
            if (maximumPacketBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPacketBytes));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            int methodHash = GetMethodHash(methodName);
            if (ValheimPrivateAccess.ContainsRpcMethod(rpc, methodHash))
            {
                throw new InvalidOperationException(
                    "The protocol RPC stable hash was already registered by Valheim or another mod.");
            }

            lock (RegistrationGate)
            {
                RegistrationTable table = Registrations.GetOrCreateValue(rpc);
                if (table.Raw.ContainsKey(methodHash) ||
                    table.Preflights.ContainsKey(methodHash))
                {
                    throw new InvalidOperationException(
                        "The protocol RPC stable hash was already registered.");
                }

                table.Raw.Add(
                    methodHash,
                    new RawRegistration(
                        maximumPacketBytes,
                        handler,
                        errorHandler));
            }
        }

        internal static void RegisterPreflight(
            ZRpc rpc,
            string methodName,
            int maximumBodyBytes,
            RawProtocolPreflightHandler validator,
            RawProtocolTransportErrorHandler errorHandler)
        {
            ValidateRegistration(rpc, methodName);
            if (maximumBodyBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
            }

            if (validator == null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            int methodHash = GetMethodHash(methodName);
            if (!ValheimPrivateAccess.ContainsRpcMethod(rpc, methodHash))
            {
                throw new InvalidOperationException(
                    "The RPC preflight must be installed after the vanilla handler is registered.");
            }

            lock (RegistrationGate)
            {
                RegistrationTable table = Registrations.GetOrCreateValue(rpc);
                if (table.Raw.ContainsKey(methodHash) ||
                    table.Preflights.ContainsKey(methodHash))
                {
                    throw new InvalidOperationException(
                        "The protocol RPC stable hash already has a preflight or raw handler.");
                }

                table.Preflights.Add(
                    methodHash,
                    new PreflightRegistration(
                        maximumBodyBytes,
                        validator,
                        errorHandler));
            }
        }

        public static void Unregister(ZRpc rpc, string methodName)
        {
            if (rpc == null || string.IsNullOrWhiteSpace(methodName))
            {
                return;
            }

            int methodHash = StringExtensionMethods.GetStableHashCode(methodName);
            lock (RegistrationGate)
            {
                if (!Registrations.TryGetValue(
                        rpc,
                        out RegistrationTable table))
                {
                    return;
                }

                table.Raw.Remove(methodHash);
                table.Preflights.Remove(methodHash);
                if (table.Raw.Count == 0 && table.Preflights.Count == 0)
                {
                    Registrations.Remove(rpc);
                }
            }
        }

        /// <summary>
        /// Called by the ZRpc.HandlePackage prefix. False means ServerManager
        /// consumed or rejected the packet and vanilla dispatch must be skipped.
        /// </summary>
        internal static bool BeforeHandlePackage(ZRpc rpc, ZPackage package)
        {
            if (rpc == null || package == null)
            {
                return true;
            }

            RegistrationTable table;
            lock (RegistrationGate)
            {
                if (!Registrations.TryGetValue(rpc, out table))
                {
                    return true;
                }
            }

            int size;
            int start;
            try
            {
                size = package.Size();
                start = package.GetPos();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                return true;
            }

            if (start < 0 || start > size - sizeof(int))
            {
                return true;
            }

            int methodHash;
            try
            {
                methodHash = package.ReadInt();
                package.SetPos(start);
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                try
                {
                    package.SetPos(start);
                }
                catch (Exception restoreException) when (
                    !IntegrityCanonical.IsFatal(restoreException))
                {
                    // Vanilla will reject or close an already-invalid package.
                }

                return true;
            }

            RawRegistration raw;
            PreflightRegistration preflight;
            lock (RegistrationGate)
            {
                table.Raw.TryGetValue(methodHash, out raw);
                table.Preflights.TryGetValue(methodHash, out preflight);
            }

            if (raw == null && preflight == null)
            {
                return true;
            }

            int bodyAndDebugLength = size - start - sizeof(int);
            int maximumBodyLength = raw?.MaximumBytes ?? preflight.MaximumBytes;
            long maximumBeforeDebugParse = maximumBodyLength;
            if (ValheimPrivateAccess.IsRpcDebugEnabled())
            {
                maximumBeforeDebugParse += MaximumDebugMethodNameBytes + 5L;
            }

            if (bodyAndDebugLength < 0 ||
                bodyAndDebugLength > maximumBeforeDebugParse)
            {
                Reject(
                    rpc,
                    raw?.ErrorHandler ?? preflight?.ErrorHandler,
                    new ProtocolRejection(
                        ProtocolRejectCode.PayloadTooLarge,
                        "The protocol RPC body exceeded the allowed size."));
                return false;
            }

            byte[] source;
            try
            {
                source = package.GetArray();
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                Reject(
                    rpc,
                    raw?.ErrorHandler ?? preflight?.ErrorHandler,
                    new ProtocolRejection(
                        ProtocolRejectCode.MalformedPacket,
                        "The protocol RPC body could not be read."));
                return false;
            }

            if (source.Length != size)
            {
                Reject(
                    rpc,
                    raw?.ErrorHandler ?? preflight?.ErrorHandler,
                    new ProtocolRejection(
                        ProtocolRejectCode.MalformedPacket,
                        "The protocol RPC body size was inconsistent."));
                return false;
            }

            if (!TryGetBodyOffset(
                    source,
                    start + sizeof(int),
                    out int bodyOffset,
                    out ProtocolRejection framingRejection))
            {
                Reject(
                    rpc,
                    raw?.ErrorHandler ?? preflight?.ErrorHandler,
                    framingRejection);
                return false;
            }

            int bodyLength = source.Length - bodyOffset;
            if (raw != null)
            {
                if (bodyLength < 0 || bodyLength > raw.MaximumBytes)
                {
                    Reject(
                        rpc,
                        raw.ErrorHandler,
                        new ProtocolRejection(
                            ProtocolRejectCode.PayloadTooLarge,
                            "The protocol RPC body exceeded the allowed size."));
                    return false;
                }

                try
                {
                    byte[] bounded = new byte[bodyLength];
                    if (bodyLength != 0)
                    {
                        Buffer.BlockCopy(
                            source,
                            bodyOffset,
                            bounded,
                            0,
                            bodyLength);
                    }

                    raw.Handler(rpc, new ZPackage(bounded));
                }
                catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
                {
                    Reject(
                        rpc,
                        raw.ErrorHandler,
                        new ProtocolRejection(
                            ProtocolRejectCode.MalformedPacket,
                            "The protocol RPC body could not be inspected."));
                }

                return false;
            }

            try
            {
                if (!preflight.Validator(
                        rpc,
                        source,
                        bodyOffset,
                        bodyLength,
                        out ProtocolRejection rejection))
                {
                    Reject(rpc, preflight.ErrorHandler, rejection);
                    return false;
                }
            }
            catch (Exception exception) when (!IntegrityCanonical.IsFatal(exception))
            {
                Reject(
                    rpc,
                    preflight.ErrorHandler,
                    new ProtocolRejection(
                        ProtocolRejectCode.MalformedPacket,
                        "The RPC preflight could not inspect the packet."));
                return false;
            }

            return true;
        }

        public static bool Send(
            ZRpc rpc,
            string methodName,
            ZPackage protocolPackage,
            ConnectionProtocolLimits limits)
        {
            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            return Send(
                rpc,
                methodName,
                protocolPackage,
                limits.MaxPacketBytes);
        }

        public static bool Send(
            ZRpc rpc,
            string methodName,
            ZPackage protocolPackage,
            int maximumPacketBytes)
        {
            if (rpc == null)
            {
                throw new ArgumentNullException(nameof(rpc));
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException(
                    "A protocol RPC name is required.",
                    nameof(methodName));
            }

            if (protocolPackage == null)
            {
                throw new ArgumentNullException(nameof(protocolPackage));
            }

            if (maximumPacketBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPacketBytes));
            }

            int payloadSize = protocolPackage.Size();
            if (payloadSize < 0 || payloadSize > maximumPacketBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(protocolPackage),
                    "The protocol RPC body exceeded the allowed size.");
            }

            byte[] payload = protocolPackage.GetArray();
            if (payload.Length != payloadSize)
            {
                throw new InvalidDataException(
                    "The protocol RPC body size was inconsistent.");
            }

            if (!rpc.IsConnected())
            {
                return false;
            }

            byte[] outerBytes;
            using (MemoryStream stream = new MemoryStream(
                       checked(payloadSize + sizeof(int) + methodName.Length + 8)))
            using (BinaryWriter writer = new BinaryWriter(
                       stream,
                       Encoding.UTF8,
                       true))
            {
                writer.Write(StringExtensionMethods.GetStableHashCode(methodName));
                if (ValheimPrivateAccess.IsRpcDebugEnabled())
                {
                    writer.Write(methodName);
                }

                writer.Write(payload);
                writer.Flush();
                outerBytes = stream.ToArray();
            }

            ZPackage outer = new ZPackage(outerBytes);
            ValheimPrivateAccess.AddSentPackageStatistics(rpc, outer.Size());
            rpc.GetSocket().Send(outer);
            return true;
        }

        private static void ValidateRegistration(ZRpc rpc, string methodName)
        {
            if (rpc == null)
            {
                throw new ArgumentNullException(nameof(rpc));
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException(
                    "A protocol RPC name is required.",
                    nameof(methodName));
            }
        }

        private static int GetMethodHash(string methodName)
        {
            int methodHash = StringExtensionMethods.GetStableHashCode(methodName);
            if (methodHash == 0)
            {
                throw new ArgumentException(
                    "The protocol RPC name collides with the reserved ping hash.",
                    nameof(methodName));
            }

            return methodHash;
        }

        private static bool TryGetBodyOffset(
            byte[] source,
            int offset,
            out int bodyOffset,
            out ProtocolRejection rejection)
        {
            bodyOffset = offset;
            rejection = null;
            if (!ValheimPrivateAccess.IsRpcDebugEnabled())
            {
                return true;
            }

            if (!TryRead7BitEncodedInt(
                    source,
                    ref bodyOffset,
                    out int byteLength) ||
                byteLength < 0 ||
                byteLength > MaximumDebugMethodNameBytes ||
                byteLength > source.Length - bodyOffset)
            {
                rejection = new ProtocolRejection(
                    ProtocolRejectCode.MalformedPacket,
                    "The protocol RPC debug method name was malformed.");
                return false;
            }

            bodyOffset += byteLength;
            return true;
        }

        private static bool TryRead7BitEncodedInt(
            byte[] source,
            ref int offset,
            out int value)
        {
            value = 0;
            for (int index = 0; index < 5; ++index)
            {
                if (offset >= source.Length)
                {
                    return false;
                }

                byte current = source[offset++];
                if (index == 4 && (current & 0xF0) != 0)
                {
                    return false;
                }

                value |= (current & 0x7F) << (index * 7);
                if ((current & 0x80) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static int ReadInt32LittleEndian(byte[] source, int offset)
        {
            return source[offset] |
                   source[offset + 1] << 8 |
                   source[offset + 2] << 16 |
                   source[offset + 3] << 24;
        }

        private static void Reject(
            ZRpc rpc,
            RawProtocolTransportErrorHandler errorHandler,
            ProtocolRejection rejection)
        {
            errorHandler?.Invoke(
                rpc,
                rejection ?? new ProtocolRejection(
                    ProtocolRejectCode.MalformedPacket,
                    "The protocol RPC body was rejected."));
        }
    }

    [HarmonyPatch(typeof(ZRpc), "HandlePackage")]
    internal static class RawProtocolHandlePackagePatch
    {
        private static bool Prefix(ZRpc __instance, ZPackage package)
        {
            return RawProtocolRpcTransport.BeforeHandlePackage(
                __instance,
                package);
        }
    }
}
