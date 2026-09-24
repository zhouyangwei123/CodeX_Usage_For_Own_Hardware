using System;

namespace CodexToolsHost.Protocol
{
    /// <summary>记录协议心跳；连续未收到匹配 PONG 时判定链路失效。</summary>
    internal sealed class SerialHeartbeatTracker
    {
        private readonly int _missLimit;
        private byte[] _expected;
        private int _misses;

        public SerialHeartbeatTracker(int missLimit)
        {
            if (missLimit < 1) throw new ArgumentOutOfRangeException("missLimit");
            _missLimit = missLimit;
        }

        public bool RegisterPing(byte[] token)
        {
            if (token == null || token.Length != 4)
                throw new ArgumentException("PING token must contain four bytes", "token");
            if (_expected != null)
            {
                _misses++;
                if (_misses >= _missLimit) return false;
            }
            _expected = (byte[])token.Clone();
            return true;
        }

        public bool AcceptPong(byte[] payload)
        {
            if (!Matches(_expected, payload)) return false;
            _expected = null;
            _misses = 0;
            return true;
        }

        public void Reset()
        {
            _expected = null;
            _misses = 0;
        }

        internal static bool Matches(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null
                || expected.Length != 4 || actual.Length != 4)
                return false;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) return false;
            return true;
        }
    }
}

