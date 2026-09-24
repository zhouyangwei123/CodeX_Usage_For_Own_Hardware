using System;
using System.Collections.Generic;

namespace CodexToolsHost.Protocol
{
    internal static class SerialPortSelection
    {
        public const string Auto = "auto";

        public static string Normalize(string value)
        {
            string text = (value ?? "").Trim();
            if (string.IsNullOrEmpty(text)
                || text.Equals(Auto, StringComparison.OrdinalIgnoreCase))
                return Auto;

            text = text.ToUpperInvariant();
            if (text.StartsWith("COM", StringComparison.Ordinal)
                && text.Length > 3)
            {
                int number;
                if (int.TryParse(text.Substring(3), out number) && number > 0)
                    return "COM" + number;
            }
            return text;
        }

        public static bool IsAuto(string value)
        {
            return Normalize(value) == Auto;
        }

        public static bool TryFindPort(string[] availablePorts, string requested,
            out string actualPort)
        {
            actualPort = null;
            string normalized = Normalize(requested);
            if (IsAuto(normalized) || availablePorts == null) return false;
            foreach (string port in availablePorts)
            {
                if (string.Equals(Normalize(port), normalized,
                    StringComparison.OrdinalIgnoreCase))
                {
                    actualPort = port;
                    return true;
                }
            }
            return false;
        }

        public static string[] OrderCandidates(string[] availablePorts,
            string lastVerifiedPort)
        {
            var unique = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (availablePorts != null)
            {
                foreach (string port in availablePorts)
                {
                    string normalized = Normalize(port);
                    if (IsAuto(normalized) || unique.ContainsKey(normalized)) continue;
                    unique[normalized] = normalized;
                }
            }

            var ordered = new List<string>(unique.Values);
            ordered.Sort(ComparePorts);

            string last = Normalize(lastVerifiedPort);
            if (!IsAuto(last))
            {
                int index = ordered.FindIndex(delegate(string port)
                {
                    return string.Equals(port, last, StringComparison.OrdinalIgnoreCase);
                });
                if (index > 0)
                {
                    string preferred = ordered[index];
                    ordered.RemoveAt(index);
                    ordered.Insert(0, preferred);
                }
            }
            return ordered.ToArray();
        }

        private static int ComparePorts(string left, string right)
        {
            int leftNumber = GetComNumber(left);
            int rightNumber = GetComNumber(right);
            int numberCompare = leftNumber.CompareTo(rightNumber);
            return numberCompare != 0
                ? numberCompare
                : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetComNumber(string port)
        {
            if (port.StartsWith("COM", StringComparison.Ordinal))
            {
                int number;
                if (int.TryParse(port.Substring(3), out number) && number > 0)
                    return number;
            }
            return int.MaxValue;
        }
    }
}
