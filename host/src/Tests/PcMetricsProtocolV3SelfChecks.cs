using System;
using System.Collections.Generic;
using System.Reflection;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;

namespace CodexToolsHost.Tests
{
    internal static class PcMetricsProtocolV3SelfChecks
    {
        public static Dictionary<string, object> Run()
        {
            var result = new Dictionary<string, object>();
            var snapshot = new PcMetricsSnapshot
            {
                CpuLoadPercent = 25,
                GpuLoadPercent = 50,
                MemoryUsedBytes = 8UL * 1024 * 1024 * 1024,
                MemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
                NetworkSpeedAvailable = true,
                NetworkDownloadKiBPerSecond = 0x01020304u,
                NetworkUploadKiBPerSecond = 0xA0B0C0D0u
            };

            byte[] payload = PcMetricsProtocol.EncodeV3(snapshot);
            bool layoutOk = payload.Length == PcMetricsProtocol.PayloadLengthV3
                && payload[0] == PcMetricsProtocol.SchemaVersionV3
                && (payload[1] & PcMetricsProtocol.FlagNetworkSpeedValid) != 0
                && payload[20] == 0x04 && payload[21] == 0x03
                && payload[22] == 0x02 && payload[23] == 0x01
                && payload[24] == 0xD0 && payload[25] == 0xC0
                && payload[26] == 0xB0 && payload[27] == 0xA0;
            result["pcMetricsV3LayoutOk"] = layoutOk;

            PcMetricsSnapshot decoded = PcMetricsProtocol.Decode(payload, DateTimeOffset.UtcNow);
            result["pcMetricsV3RoundTripOk"] = decoded.NetworkSpeedAvailable
                && decoded.NetworkDownloadKiBPerSecond == 0x01020304u
                && decoded.NetworkUploadKiBPerSecond == 0xA0B0C0D0u;

            snapshot.NetworkSpeedAvailable = false;
            byte[] unavailablePayload = PcMetricsProtocol.EncodeV3(snapshot);
            PcMetricsSnapshot unavailable = PcMetricsProtocol.Decode(unavailablePayload,
                DateTimeOffset.UtcNow);
            PcMetricsSnapshot v2 = PcMetricsProtocol.Decode(PcMetricsProtocol.EncodeV2(snapshot),
                DateTimeOffset.UtcNow);
            bool unavailableLayoutOk = (unavailablePayload[1]
                & PcMetricsProtocol.FlagNetworkSpeedValid) == 0;
            for (int offset = 20; offset < 28; offset++)
                unavailableLayoutOk = unavailableLayoutOk && unavailablePayload[offset] == 0;
            result["pcMetricsV3InvalidOk"] = unavailableLayoutOk
                && !unavailable.NetworkSpeedAvailable
                && unavailable.NetworkDownloadKiBPerSecond == 0u
                && unavailable.NetworkUploadKiBPerSecond == 0u
                && !v2.NetworkSpeedAvailable
                && v2.NetworkDownloadKiBPerSecond == 0u
                && v2.NetworkUploadKiBPerSecond == 0u;

            byte[] v1Payload = PcMetricsProtocol.Encode(snapshot);
            result["pcMetricsEncodeV1CompatibilityOk"] = BytesEqual(v1Payload,
                PcMetricsProtocol.EncodeV1(snapshot))
                && v1Payload.Length == PcMetricsProtocol.PayloadLengthV1
                && v1Payload[0] == PcMetricsProtocol.SchemaVersionV1;

            result["pcMetricsFirmwareSchemaSelectionOk"] = HasSchema(snapshot, 0, 2,
                PcMetricsProtocol.SchemaVersionV1, PcMetricsProtocol.PayloadLengthV1)
                && HasSchema(snapshot, 0, 3, PcMetricsProtocol.SchemaVersionV2,
                    PcMetricsProtocol.PayloadLengthV2)
                && HasSchema(snapshot, 0, 6, PcMetricsProtocol.SchemaVersionV2,
                    PcMetricsProtocol.PayloadLengthV2)
                && HasSchema(snapshot, 0, 7, PcMetricsProtocol.SchemaVersionV3,
                    PcMetricsProtocol.PayloadLengthV3)
                && HasSchema(snapshot, 1, 0, PcMetricsProtocol.SchemaVersionV3,
                    PcMetricsProtocol.PayloadLengthV3);

            var v1Snapshot = new PcMetricsSnapshot
            {
                CpuLoadPercent = 25,
                MemoryUsedBytes = 8UL * 1024 * 1024 * 1024,
                MemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
                CpuTemperatureC = 42.5,
                TemperatureSource = PcMetricsSnapshot.SourceAcpiThermalZone,
                IsStale = true
            };
            PcMetricsSnapshot v1Decoded = PcMetricsProtocol.Decode(
                PcMetricsProtocol.EncodeV1(v1Snapshot), DateTimeOffset.UtcNow);
            result["pcMetricsV1DecodeCompatibilityOk"] = v1Decoded.CpuLoadPercent == 25
                && v1Decoded.MemoryUsedBytes == v1Snapshot.MemoryUsedBytes
                && v1Decoded.MemoryTotalBytes == v1Snapshot.MemoryTotalBytes
                && v1Decoded.CpuTemperatureC == 42.5
                && v1Decoded.TemperatureSource == PcMetricsSnapshot.SourceAcpiThermalZone
                && v1Decoded.IsStale;

            byte[] v2Payload = PcMetricsProtocol.EncodeV2(v1Snapshot);
            byte[] v3Payload = PcMetricsProtocol.EncodeV3(v1Snapshot);
            bool v3PrefixMatchesV2 = v2Payload.Length == PcMetricsProtocol.PayloadLengthV2
                && v3Payload.Length == PcMetricsProtocol.PayloadLengthV3
                && v2Payload[0] == PcMetricsProtocol.SchemaVersionV2
                && v3Payload[0] == PcMetricsProtocol.SchemaVersionV3;
            for (int offset = 1; offset < PcMetricsProtocol.PayloadLengthV2; offset++)
                v3PrefixMatchesV2 = v3PrefixMatchesV2 && v3Payload[offset] == v2Payload[offset];
            result["pcMetricsV3PrefixCompatibilityOk"] = v3PrefixMatchesV2;

            result["bridgePcMetricsV3ContractOk"] = CheckBridgePcMetricsV3Contract();
            return result;
        }

        private static bool HasSchema(PcMetricsSnapshot snapshot, int firmwareMajor,
                                      int firmwareMinor, byte schema, int payloadLength)
        {
            byte[] payload = PcMetricsProtocol.EncodeForFirmware(snapshot,
                firmwareMajor, firmwareMinor);
            return payload.Length == payloadLength && payload[0] == schema;
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int offset = 0; offset < first.Length; offset++)
                if (first[offset] != second[offset]) return false;
            return true;
        }

        private static bool CheckBridgePcMetricsV3Contract()
        {
            var link = new MockDeviceLink { EmitInfoOnConnect = false };
            var monitor = new PcMonitorService(new FixedPcMetricsProvider(), 500);
            BridgeService bridge = null;
            try
            {
                bridge = new BridgeService(AppConfig.CreateDefault(), link,
                    new FakeCodexSource(), null, new FakeDeepSeekSource(), monitor);
                link.Start();
                MethodInfo send = typeof(BridgeService).GetMethod("SendPcMetrics",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (send == null) return false;
                send.Invoke(bridge, null);
                byte[] payload = link.LastPcMetrics;
                return link.PcMetricsCount == 1 && payload != null
                    && payload.Length == PcMetricsProtocol.PayloadLengthV3
                    && payload[0] == PcMetricsProtocol.SchemaVersionV3;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (bridge != null) bridge.Dispose();
                else monitor.Dispose();
                link.Dispose();
            }
        }

        private sealed class FixedPcMetricsProvider : IPcMetricsProvider
        {
            public PcMetricsSnapshot Read()
            {
                return PcMetricsSnapshot.CreateUnavailable("test", DateTimeOffset.UtcNow);
            }
        }
    }
}
