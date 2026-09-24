using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using CodexToolsHost.Monitor;

namespace CodexToolsHost.Tests
{
    internal static class NetworkSpeedSelfChecks
    {
        public static Dictionary<string, object> Run()
        {
            var result = new Dictionary<string, object>();
            result["networkRateDeltaOk"] = CheckRateDeltaAndFiltering();
            result["networkCounterResetOk"] = CheckCounterReset();
            result["networkEligibilityFilterOk"] = CheckEligibilityFilter();
            result["networkMultiInterfaceSumOk"] = CheckMultiInterfaceSum();
            result["networkAdapterLifecycleOk"] = CheckAdapterLifecycle();
            result["networkTimeRollbackOk"] = CheckTimeRollback();
            result["networkInterfaceReadIsolationOk"] = CheckInterfaceReadIsolation();
            result["networkRateRoundingOk"] = CheckRateRounding();
            result["networkRateSaturationOk"] = CheckRateSaturation();
            result["networkProviderIsolationOk"] = CheckProviderIsolation();
            return result;
        }

        private static bool CheckRateDeltaAndFiltering()
        {
            long now = 1000;
            var physical = new NetworkAdapterCounters
            {
                Id = "physical",
                IsEligible = true,
                BytesReceived = 1000UL,
                BytesSent = 2000UL
            };
            var virtualAdapter = new NetworkAdapterCounters
            {
                Id = "virtual",
                IsEligible = false,
                BytesReceived = ulong.MaxValue,
                BytesSent = ulong.MaxValue
            };
            var counters = new List<NetworkAdapterCounters> { physical, virtualAdapter };
            var sampler = new NetworkSpeedSampler(delegate { return counters; },
                delegate { return now; }, 1000);

            NetworkSpeedSample first = sampler.Read();
            physical.BytesReceived += 1536UL * 1024UL * 2UL;
            physical.BytesSent += 256UL * 1024UL * 2UL;
            now += 2000;
            NetworkSpeedSample second = sampler.Read();

            return !first.Available
                && first.DownloadKiBPerSecond == 0u
                && first.UploadKiBPerSecond == 0u
                && second.Available
                && second.DownloadKiBPerSecond == 1536u
                && second.UploadKiBPerSecond == 256u;
        }

        private static bool CheckCounterReset()
        {
            long now = 5000;
            var adapter = new NetworkAdapterCounters
            {
                Id = "physical",
                IsEligible = true,
                BytesReceived = 8192UL,
                BytesSent = 4096UL
            };
            var counters = new List<NetworkAdapterCounters> { adapter };
            var sampler = new NetworkSpeedSampler(delegate { return counters; },
                delegate { return now; }, 1000);

            sampler.Read();
            adapter.BytesReceived = 128UL;
            adapter.BytesSent = 64UL;
            now += 1000;
            NetworkSpeedSample reset = sampler.Read();

            return !reset.Available
                && reset.DownloadKiBPerSecond == 0u
                && reset.UploadKiBPerSecond == 0u;
        }

        private static bool CheckEligibilityFilter()
        {
            IPAddress[] gateway = { IPAddress.Parse("192.168.1.1") };
            IPAddress[] unusableGateways = {
                IPAddress.Any,
                IPAddress.IPv6Any,
                IPAddress.Loopback,
                IPAddress.IPv6Loopback
            };

            return NetworkSpeedSampler.IsEligibleInterface(
                       OperationalStatus.Up, NetworkInterfaceType.Ethernet, gateway)
                && !NetworkSpeedSampler.IsEligibleInterface(
                       OperationalStatus.Down, NetworkInterfaceType.Ethernet, gateway)
                && !NetworkSpeedSampler.IsEligibleInterface(
                       OperationalStatus.Up, NetworkInterfaceType.Loopback, gateway)
                && !NetworkSpeedSampler.IsEligibleInterface(
                       OperationalStatus.Up, NetworkInterfaceType.Tunnel, gateway)
                && !NetworkSpeedSampler.IsEligibleInterface(
                       OperationalStatus.Up, NetworkInterfaceType.Ethernet,
                       new IPAddress[0])
                && !NetworkSpeedSampler.IsEligibleInterface(
                       OperationalStatus.Up, NetworkInterfaceType.Ethernet,
                       unusableGateways);
        }

        private static bool CheckMultiInterfaceSum()
        {
            long now = 1000;
            var firstAdapter = new NetworkAdapterCounters
            {
                Id = "ethernet",
                IsEligible = true,
                BytesReceived = 1000UL,
                BytesSent = 2000UL
            };
            var secondAdapter = new NetworkAdapterCounters
            {
                Id = "wifi",
                IsEligible = true,
                BytesReceived = 3000UL,
                BytesSent = 4000UL
            };
            var counters = new List<NetworkAdapterCounters>
            {
                firstAdapter,
                secondAdapter
            };
            var sampler = new NetworkSpeedSampler(delegate { return counters; },
                delegate { return now; }, 1000);

            sampler.Read();
            firstAdapter.BytesReceived += 100UL * 1024UL;
            firstAdapter.BytesSent += 20UL * 1024UL;
            secondAdapter.BytesReceived += 200UL * 1024UL;
            secondAdapter.BytesSent += 30UL * 1024UL;
            now += 1000;
            NetworkSpeedSample sample = sampler.Read();

            return sample.Available
                && sample.DownloadKiBPerSecond == 300u
                && sample.UploadKiBPerSecond == 50u;
        }

        private static bool CheckAdapterLifecycle()
        {
            long now = 1000;
            var adapter = new NetworkAdapterCounters
            {
                Id = "dock",
                IsEligible = true,
                BytesReceived = 1000UL,
                BytesSent = 2000UL
            };
            var counters = new List<NetworkAdapterCounters> { adapter };
            var sampler = new NetworkSpeedSampler(delegate { return counters; },
                delegate { return now; }, 1000);

            sampler.Read();
            counters.Clear();
            now += 1000;
            NetworkSpeedSample removed = sampler.Read();

            adapter.BytesReceived = 900000UL;
            adapter.BytesSent = 800000UL;
            counters.Add(adapter);
            now += 1000;
            NetworkSpeedSample readded = sampler.Read();

            adapter.BytesReceived += 4UL * 1024UL;
            adapter.BytesSent += 2UL * 1024UL;
            now += 1000;
            NetworkSpeedSample resumed = sampler.Read();

            return !removed.Available
                && !readded.Available
                && resumed.Available
                && resumed.DownloadKiBPerSecond == 4u
                && resumed.UploadKiBPerSecond == 2u;
        }

        private static bool CheckTimeRollback()
        {
            long now = 1000;
            var adapter = new NetworkAdapterCounters
            {
                Id = "ethernet",
                IsEligible = true,
                BytesReceived = 0UL,
                BytesSent = 0UL
            };
            var counters = new List<NetworkAdapterCounters> { adapter };
            var sampler = new NetworkSpeedSampler(delegate { return counters; },
                delegate { return now; }, 1000);

            sampler.Read();
            adapter.BytesReceived += 8UL * 1024UL;
            adapter.BytesSent += 4UL * 1024UL;
            now = 900;
            NetworkSpeedSample rolledBack = sampler.Read();

            adapter.BytesReceived += 1024UL;
            adapter.BytesSent += 2048UL;
            now = 1900;
            NetworkSpeedSample recovered = sampler.Read();

            return !rolledBack.Available
                && recovered.Available
                && recovered.DownloadKiBPerSecond == 1u
                && recovered.UploadKiBPerSecond == 2u;
        }

        private static bool CheckInterfaceReadIsolation()
        {
            var valid = new NetworkAdapterCounters
            {
                Id = "wifi",
                IsEligible = true,
                BytesReceived = 123UL,
                BytesSent = 456UL
            };
            IList<NetworkAdapterCounters> counters =
                NetworkSpeedSampler.CollectInterfaceCounters(
                    new Func<NetworkAdapterCounters>[]
                    {
                        delegate { throw new InvalidOperationException("broken adapter"); },
                        delegate { return valid; }
                    });

            return counters.Count == 1
                && object.ReferenceEquals(counters[0], valid);
        }

        private static bool CheckRateRounding()
        {
            return NetworkSpeedSampler.ToUInt32Rate(double.NaN) == 0u
                && NetworkSpeedSampler.ToUInt32Rate(-1.0) == 0u
                && NetworkSpeedSampler.ToUInt32Rate(0.49) == 0u
                && NetworkSpeedSampler.ToUInt32Rate(0.5) == 1u
                && NetworkSpeedSampler.ToUInt32Rate(1.5) == 2u;
        }

        private static bool CheckRateSaturation()
        {
            return NetworkSpeedSampler.ToUInt32Rate(uint.MaxValue - 0.6) ==
                    uint.MaxValue - 1u
                && NetworkSpeedSampler.ToUInt32Rate(uint.MaxValue) == uint.MaxValue
                && NetworkSpeedSampler.ToUInt32Rate(uint.MaxValue + 1024.0) ==
                    uint.MaxValue
                && NetworkSpeedSampler.ToUInt32Rate(double.PositiveInfinity) ==
                    uint.MaxValue;
        }

        private static bool CheckProviderIsolation()
        {
            PcMetricsSnapshot snapshot = PcMetricsSnapshot.CreateUnavailable(
                "", DateTimeOffset.UtcNow);
            var errors = new List<string>();
            WindowsPcMetricsProvider.ApplyNetworkSample(snapshot, errors,
                delegate { throw new InvalidOperationException("network probe failed"); });

            return !snapshot.NetworkSpeedAvailable
                && snapshot.NetworkDownloadKiBPerSecond == 0u
                && snapshot.NetworkUploadKiBPerSecond == 0u
                && errors.Count == 1
                && errors[0] == "network: InvalidOperationException";
        }
    }
}
