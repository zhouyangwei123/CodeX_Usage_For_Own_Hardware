using System;
using CodexToolsHost.Monitor;

namespace CodexToolsHost.Protocol
{
    public static class PcMetricsProtocol
    {
        public const byte SchemaVersionV1 = 1;
        public const int PayloadLengthV1 = 14;
        public const byte SchemaVersionV2 = 2;
        public const int PayloadLengthV2 = 20;
        public const byte SchemaVersionV3 = 3;
        public const int PayloadLengthV3 = 28;

        /* 兼容旧测试和旧固件调用方：Encode() 仍生成 v1。 */
        public const byte SchemaVersion = SchemaVersionV1;
        public const int PayloadLength = PayloadLengthV1;
        public const byte FlagCpuValid = 0x01;
        public const byte FlagMemoryValid = 0x02;
        public const byte FlagTemperatureValid = 0x04;
        public const byte FlagStale = 0x08;
        public const byte TemperatureSourceAcpi = 0x10;
        public const byte FlagCpuTemperatureValid = 0x04;
        public const byte FlagGpuLoadValid = 0x08;
        public const byte FlagGpuTemperatureValid = 0x10;
        public const byte FlagMotherboardTemperatureValid = 0x20;
        public const byte FlagStaleV2 = 0x40;
        public const byte FlagNetworkSpeedValid = 0x80;
        public const short UnknownTemperature = unchecked((short)0x8000);

        public static byte[] Encode(PcMetricsSnapshot snapshot)
        {
            return EncodeV1(snapshot);
        }

        public static byte[] EncodeForFirmware(PcMetricsSnapshot snapshot,
                                                int firmwareMajor, int firmwareMinor)
        {
            if (firmwareMajor > 0 || firmwareMinor >= 7) return EncodeV3(snapshot);
            if (firmwareMajor > 0 || firmwareMinor >= 3) return EncodeV2(snapshot);
            return EncodeV1(snapshot);
        }

        public static byte[] EncodeV1(PcMetricsSnapshot snapshot)
        {
            if (snapshot == null)
                snapshot = PcMetricsSnapshot.CreateUnavailable("no snapshot", DateTimeOffset.UtcNow);

            byte[] payload = new byte[PayloadLength];
            payload[0] = SchemaVersion;
            byte flags = 0;
            if (snapshot.CpuLoadPercent >= 0 && snapshot.CpuLoadPercent <= 100)
            {
                flags |= FlagCpuValid;
                payload[2] = (byte)snapshot.CpuLoadPercent;
            }
            else
            {
                payload[2] = 255;
            }

            int memoryPercent = snapshot.MemoryUsedPercent;
            if (memoryPercent >= 0 && memoryPercent <= 100)
            {
                flags |= FlagMemoryValid;
                payload[3] = (byte)memoryPercent;
            }
            else
            {
                payload[3] = 255;
            }

            short temperature = EncodeTemperature(snapshot.CpuTemperatureC);
            if (temperature != UnknownTemperature)
            {
                flags |= FlagTemperatureValid;
                WriteInt16(payload, 4, temperature);
                if (string.Equals(snapshot.TemperatureSource,
                    PcMetricsSnapshot.SourceAcpiThermalZone, StringComparison.OrdinalIgnoreCase))
                    flags |= TemperatureSourceAcpi;
            }
            else
            {
                WriteInt16(payload, 4, UnknownTemperature);
            }

            if (snapshot.IsStale) flags |= FlagStale;
            payload[1] = flags;
            WriteUInt32(payload, 6, ToMegabytes(snapshot.MemoryUsedBytes));
            WriteUInt32(payload, 10, ToMegabytes(snapshot.MemoryTotalBytes));
            return payload;
        }

        public static byte[] EncodeV2(PcMetricsSnapshot snapshot)
        {
            if (snapshot == null)
                snapshot = PcMetricsSnapshot.CreateUnavailable("no snapshot", DateTimeOffset.UtcNow);

            byte[] payload = new byte[PayloadLengthV2];
            payload[0] = SchemaVersionV2;
            byte flags = 0;

            payload[2] = EncodePercent(snapshot.CpuLoadPercent, ref flags, FlagCpuValid);
            payload[3] = EncodePercent(snapshot.MemoryUsedPercent, ref flags, FlagMemoryValid);
            payload[4] = EncodePercent(snapshot.GpuLoadPercent, ref flags, FlagGpuLoadValid);
            payload[5] = 0;

            WriteTemperatureV2(payload, 6, snapshot.CpuTemperatureC,
                ref flags, FlagCpuTemperatureValid);
            WriteTemperatureV2(payload, 8, snapshot.GpuTemperatureC,
                ref flags, FlagGpuTemperatureValid);
            WriteTemperatureV2(payload, 10, snapshot.MotherboardTemperatureC,
                ref flags, FlagMotherboardTemperatureValid);

            if (snapshot.IsStale) flags |= FlagStaleV2;
            payload[1] = flags;
            WriteUInt32(payload, 12, ToMegabytes(snapshot.MemoryUsedBytes));
            WriteUInt32(payload, 16, ToMegabytes(snapshot.MemoryTotalBytes));
            return payload;
        }

        public static byte[] EncodeV3(PcMetricsSnapshot snapshot)
        {
            byte[] payload = new byte[PayloadLengthV3];
            byte[] v2Payload = EncodeV2(snapshot);
            Array.Copy(v2Payload, payload, PayloadLengthV2);
            payload[0] = SchemaVersionV3;
            if (snapshot != null && snapshot.NetworkSpeedAvailable)
            {
                payload[1] |= FlagNetworkSpeedValid;
                WriteUInt32(payload, 20, snapshot.NetworkDownloadKiBPerSecond);
                WriteUInt32(payload, 24, snapshot.NetworkUploadKiBPerSecond);
            }
            return payload;
        }

        public static PcMetricsSnapshot Decode(byte[] payload, DateTimeOffset sampledAtUtc)
        {
            if (payload != null && payload.Length >= PayloadLengthV3
                && payload[0] == SchemaVersionV3)
                return DecodeV3(payload, sampledAtUtc);

            if (payload != null && payload.Length >= PayloadLengthV2
                && payload[0] == SchemaVersionV2)
                return DecodeV2(payload, sampledAtUtc);

            if (payload == null || payload.Length < PayloadLengthV1
                || payload[0] != SchemaVersionV1)
                return PcMetricsSnapshot.CreateUnavailable("invalid metrics payload", sampledAtUtc);

            byte flags = payload[1];
            uint usedMb = ReadUInt32(payload, 6);
            uint totalMb = ReadUInt32(payload, 10);
            short encodedTemperature = ReadInt16(payload, 4);
            double? temperature = null;
            if ((flags & FlagTemperatureValid) != 0 && encodedTemperature != UnknownTemperature)
                temperature = encodedTemperature / 10.0;

            string source = (flags & TemperatureSourceAcpi) != 0
                ? PcMetricsSnapshot.SourceAcpiThermalZone
                : PcMetricsSnapshot.SourceUnavailable;

            return new PcMetricsSnapshot
            {
                CpuLoadPercent = (flags & FlagCpuValid) != 0 ? payload[2] : -1,
                MemoryUsedBytes = usedMb * 1024UL * 1024UL,
                MemoryTotalBytes = totalMb * 1024UL * 1024UL,
                CpuTemperatureC = temperature,
                TemperatureSource = source,
                SampledAtUtc = sampledAtUtc,
                IsStale = (flags & FlagStale) != 0,
                ErrorText = ""
            };
        }

        private static PcMetricsSnapshot DecodeV3(byte[] payload, DateTimeOffset sampledAtUtc)
        {
            PcMetricsSnapshot snapshot = DecodeV2(payload, sampledAtUtc);
            if ((payload[1] & FlagNetworkSpeedValid) != 0)
            {
                snapshot.NetworkSpeedAvailable = true;
                snapshot.NetworkDownloadKiBPerSecond = ReadUInt32(payload, 20);
                snapshot.NetworkUploadKiBPerSecond = ReadUInt32(payload, 24);
            }
            else
            {
                snapshot.NetworkSpeedAvailable = false;
                snapshot.NetworkDownloadKiBPerSecond = 0u;
                snapshot.NetworkUploadKiBPerSecond = 0u;
            }
            return snapshot;
        }

        private static PcMetricsSnapshot DecodeV2(byte[] payload, DateTimeOffset sampledAtUtc)
        {
            byte flags = payload[1];
            uint usedMb = ReadUInt32(payload, 12);
            uint totalMb = ReadUInt32(payload, 16);
            return new PcMetricsSnapshot
            {
                CpuLoadPercent = (flags & FlagCpuValid) != 0 ? payload[2] : -1,
                MemoryUsedBytes = usedMb * 1024UL * 1024UL,
                MemoryTotalBytes = totalMb * 1024UL * 1024UL,
                GpuLoadPercent = (flags & FlagGpuLoadValid) != 0 ? payload[4] : -1,
                CpuTemperatureC = DecodeTemperatureV2(payload, 6, flags,
                    FlagCpuTemperatureValid),
                GpuTemperatureC = DecodeTemperatureV2(payload, 8, flags,
                    FlagGpuTemperatureValid),
                MotherboardTemperatureC = DecodeTemperatureV2(payload, 10, flags,
                    FlagMotherboardTemperatureValid),
                TemperatureSource = PcMetricsSnapshot.SourceLibreHardwareMonitor,
                GpuSource = PcMetricsSnapshot.SourceLibreHardwareMonitor,
                MotherboardTemperatureSource = PcMetricsSnapshot.SourceLibreHardwareMonitor,
                SampledAtUtc = sampledAtUtc,
                IsStale = (flags & FlagStaleV2) != 0,
                ErrorText = ""
            };
        }

        private static byte EncodePercent(int value, ref byte flags, byte validFlag)
        {
            if (value < 0 || value > 100) return 255;
            flags |= validFlag;
            return (byte)value;
        }

        private static void WriteTemperatureV2(byte[] payload, int offset, double? value,
                                               ref byte flags, byte validFlag)
        {
            short encoded = EncodeTemperature(value);
            WriteInt16(payload, offset, encoded);
            if (encoded != UnknownTemperature) flags |= validFlag;
        }

        private static double? DecodeTemperatureV2(byte[] payload, int offset,
                                                   byte flags, byte validFlag)
        {
            short encoded = ReadInt16(payload, offset);
            if ((flags & validFlag) == 0 || encoded == UnknownTemperature) return null;
            return encoded / 10.0;
        }

        private static short EncodeTemperature(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                return UnknownTemperature;
            if (value.Value < -40.0 || value.Value > 150.0) return UnknownTemperature;
            return (short)Math.Round(value.Value * 10.0, MidpointRounding.AwayFromZero);
        }

        private static uint ToMegabytes(ulong bytes)
        {
            ulong megabytes = bytes / (1024UL * 1024UL);
            return megabytes > uint.MaxValue ? uint.MaxValue : (uint)megabytes;
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static short ReadInt16(byte[] buffer, int offset)
        {
            return unchecked((short)(buffer[offset] | (buffer[offset + 1] << 8)));
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
    }
}
