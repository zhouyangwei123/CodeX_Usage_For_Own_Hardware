using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace CodexToolsHost.Monitor
{
    internal sealed class NetworkAdapterCounters
    {
        public string Id { get; set; }
        public bool IsEligible { get; set; }
        public ulong BytesReceived { get; set; }
        public ulong BytesSent { get; set; }
    }

    internal sealed class NetworkSpeedSample
    {
        public bool Available { get; set; }
        public uint DownloadKiBPerSecond { get; set; }
        public uint UploadKiBPerSecond { get; set; }
    }

    internal sealed class NetworkSpeedSampler
    {
        private readonly Func<IList<NetworkAdapterCounters>> _counterReader;
        private readonly Func<long> _timestampReader;
        private readonly long _timestampFrequency;
        private readonly Dictionary<string, Baseline> _baselines =
            new Dictionary<string, Baseline>(StringComparer.Ordinal);

        public NetworkSpeedSampler()
            : this(ReadDefaultCounters, Stopwatch.GetTimestamp, Stopwatch.Frequency)
        {
        }

        internal NetworkSpeedSampler(
            Func<IList<NetworkAdapterCounters>> counterReader,
            Func<long> timestampReader,
            long timestampFrequency)
        {
            if (counterReader == null) throw new ArgumentNullException("counterReader");
            if (timestampReader == null) throw new ArgumentNullException("timestampReader");
            if (timestampFrequency <= 0)
                throw new ArgumentOutOfRangeException("timestampFrequency");

            _counterReader = counterReader;
            _timestampReader = timestampReader;
            _timestampFrequency = timestampFrequency;
        }

        public NetworkSpeedSample Read()
        {
            IList<NetworkAdapterCounters> counters = _counterReader()
                ?? new List<NetworkAdapterCounters>();
            long now = _timestampReader();
            var presentIds = new HashSet<string>(StringComparer.Ordinal);
            double downloadKiBPerSecond = 0.0;
            double uploadKiBPerSecond = 0.0;
            bool available = false;

            foreach (NetworkAdapterCounters current in counters)
            {
                if (current == null || !current.IsEligible || string.IsNullOrEmpty(current.Id))
                    continue;

                presentIds.Add(current.Id);
                Baseline previous;
                if (_baselines.TryGetValue(current.Id, out previous)
                    && now > previous.Timestamp
                    && current.BytesReceived >= previous.BytesReceived
                    && current.BytesSent >= previous.BytesSent)
                {
                    double elapsedSeconds = (now - previous.Timestamp)
                        / (double)_timestampFrequency;
                    if (elapsedSeconds > 0.0)
                    {
                        downloadKiBPerSecond += (current.BytesReceived - previous.BytesReceived)
                            / elapsedSeconds / 1024.0;
                        uploadKiBPerSecond += (current.BytesSent - previous.BytesSent)
                            / elapsedSeconds / 1024.0;
                        available = true;
                    }
                }

                _baselines[current.Id] = new Baseline
                {
                    BytesReceived = current.BytesReceived,
                    BytesSent = current.BytesSent,
                    Timestamp = now
                };
            }

            var removedIds = new List<string>();
            foreach (string id in _baselines.Keys)
                if (!presentIds.Contains(id)) removedIds.Add(id);
            foreach (string id in removedIds) _baselines.Remove(id);

            return new NetworkSpeedSample
            {
                Available = available,
                DownloadKiBPerSecond = available ? ToUInt32Rate(downloadKiBPerSecond) : 0u,
                UploadKiBPerSecond = available ? ToUInt32Rate(uploadKiBPerSecond) : 0u
            };
        }

        internal static uint ToUInt32Rate(double rate)
        {
            if (double.IsNaN(rate) || rate <= 0.0) return 0u;
            if (double.IsInfinity(rate) || rate >= uint.MaxValue) return uint.MaxValue;

            double rounded = Math.Round(rate, MidpointRounding.AwayFromZero);
            return rounded >= uint.MaxValue ? uint.MaxValue : (uint)rounded;
        }

        private static IList<NetworkAdapterCounters> ReadDefaultCounters()
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var readers = new List<Func<NetworkAdapterCounters>>(interfaces.Length);
            foreach (NetworkInterface networkInterface in interfaces)
            {
                NetworkInterface currentInterface = networkInterface;
                readers.Add(delegate { return ReadInterfaceCounters(currentInterface); });
            }
            return CollectInterfaceCounters(readers);
        }

        internal static IList<NetworkAdapterCounters> CollectInterfaceCounters(
            IEnumerable<Func<NetworkAdapterCounters>> readers)
        {
            var counters = new List<NetworkAdapterCounters>();
            if (readers == null) return counters;

            foreach (Func<NetworkAdapterCounters> reader in readers)
            {
                if (reader == null) continue;
                try
                {
                    NetworkAdapterCounters current = reader();
                    if (current != null) counters.Add(current);
                }
                catch (Exception)
                {
                    /* 单块异常网卡不能使其他网卡的采样失效。 */
                }
            }
            return counters;
        }

        internal static bool IsEligibleInterface(
            OperationalStatus operationalStatus,
            NetworkInterfaceType interfaceType,
            IEnumerable<IPAddress> gatewayAddresses)
        {
            if (operationalStatus != OperationalStatus.Up
                || interfaceType == NetworkInterfaceType.Loopback
                || interfaceType == NetworkInterfaceType.Tunnel
                || gatewayAddresses == null)
                return false;

            foreach (IPAddress address in gatewayAddresses)
            {
                if (address != null
                    && !IPAddress.Any.Equals(address)
                    && !IPAddress.IPv6Any.Equals(address)
                    && !IPAddress.None.Equals(address)
                    && !IPAddress.IPv6None.Equals(address)
                    && !IPAddress.IsLoopback(address))
                    return true;
            }
            return false;
        }

        private static NetworkAdapterCounters ReadInterfaceCounters(
            NetworkInterface networkInterface)
        {
            if (networkInterface == null) return null;

            OperationalStatus status = networkInterface.OperationalStatus;
            NetworkInterfaceType type = networkInterface.NetworkInterfaceType;
            if (status != OperationalStatus.Up
                || type == NetworkInterfaceType.Loopback
                || type == NetworkInterfaceType.Tunnel)
                return null;

            IPInterfaceProperties properties = networkInterface.GetIPProperties();
            var gatewayAddresses = new List<IPAddress>();
            foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                gatewayAddresses.Add(gateway == null ? null : gateway.Address);
            if (!IsEligibleInterface(status, type, gatewayAddresses)) return null;

            IPInterfaceStatistics statistics = networkInterface.GetIPStatistics();
            if (statistics.BytesReceived < 0 || statistics.BytesSent < 0) return null;

            return new NetworkAdapterCounters
            {
                Id = networkInterface.Id,
                IsEligible = true,
                BytesReceived = (ulong)statistics.BytesReceived,
                BytesSent = (ulong)statistics.BytesSent
            };
        }

        private sealed class Baseline
        {
            public ulong BytesReceived { get; set; }
            public ulong BytesSent { get; set; }
            public long Timestamp { get; set; }
        }
    }
}
