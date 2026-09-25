using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexToolsHost.Updates
{
    internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
    {
        private static readonly Regex Pattern = new Regex(@"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
        private readonly int _major, _minor, _patch;
        private readonly string[] _prerelease;
        public bool IsPrerelease { get { return _prerelease != null; } }
        private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
        { _major = major; _minor = minor; _patch = patch; _prerelease = prerelease; }
        public static ReleaseVersion FromVersion(Version version)
        { return new ReleaseVersion(version.Major, version.Minor, Math.Max(0, version.Build), null); }
        public static bool TryParse(string value, out ReleaseVersion version)
        {
            version = null;
            if (String.IsNullOrEmpty(value) || value.Length > 128) return false;
            Match match = Pattern.Match(value);
            int major, minor, patch;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
                || !Int32.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)
                || !Int32.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
            string[] prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : null;
            if (prerelease != null)
                foreach (string part in prerelease)
                    if (IsNumeric(part) && part.Length > 1 && part[0] == '0') return false;
            version = new ReleaseVersion(major, minor, patch, prerelease);
            return true;
        }
        private static bool IsNumeric(string value)
        {
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }
        public int CompareTo(ReleaseVersion other)
        {
            if (other == null) return 1;
            int result = _major.CompareTo(other._major);
            if (result == 0) result = _minor.CompareTo(other._minor);
            if (result == 0) result = _patch.CompareTo(other._patch);
            if (result != 0) return result;
            if (_prerelease == null) return other._prerelease == null ? 0 : 1;
            if (other._prerelease == null) return -1;
            for (int i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
            {
                string left = _prerelease[i], right = other._prerelease[i];
                bool leftNumeric = IsNumeric(left), rightNumeric = IsNumeric(right);
                if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
                result = leftNumeric ? left.Length.CompareTo(right.Length) : 0;
                if (result == 0) result = String.CompareOrdinal(left, right);
                if (result != 0) return result;
            }
            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }
    }
    internal static class ReleaseLink
    {
        private const string PathPrefix = "/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/";
        public static bool TryGet(string value, string tag, out Uri uri)
        {
            uri = null;
            ReleaseVersion version;
            Uri candidate;
            if (String.IsNullOrEmpty(value) || value.Length > 1024 || value != value.Trim()
                || value.IndexOfAny(new[] { '\r', '\n', '\t', '\\' }) >= 0
                || !ReleaseVersion.TryParse(tag, out version) || version.IsPrerelease
                || !Uri.TryCreate(value, UriKind.Absolute, out candidate)
                || candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "github.com"
                || !candidate.IsDefaultPort || candidate.UserInfo.Length != 0
                || candidate.Query.Length != 0 || candidate.Fragment.Length != 0
                || !candidate.AbsolutePath.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            string tagPath = candidate.AbsolutePath.Substring(PathPrefix.Length);
            if (!String.Equals(Uri.UnescapeDataString(tagPath), tag, StringComparison.Ordinal)) return false;
            uri = candidate;
            return true;
        }
    }
}
