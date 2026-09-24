using System;

namespace CodexToolsHost.Protocol
{
    /// <summary>与固件一致的 A5 5A 帧编解码（CRC8, poly 0x07）</summary>
    public static class FrameCodec
    {
        public const byte Hdr1 = 0xA5;
        public const byte Hdr2 = 0x5A;
        public const int MaxPayload = 128;
        public const int MaxFrame = MaxPayload + 6;

        public const byte MsgStatus = 0x01;
        public const byte MsgRgbSet = 0x02;
        public const byte MsgOledPage = 0x03;
        public const byte MsgOledText = 0x04;
        public const byte MsgCfgReq = 0x05;
        public const byte MsgLed0Set = 0x06;
        public const byte MsgPing = 0x07;
        public const byte MsgOledCfg = 0x08;
        public const byte MsgBtnInject = 0x09;
        public const byte MsgRgbStatus = 0x0A; /* 6 种状态颜色，每种 3 字节 RGB，共 18 字节 */
        public const byte MsgPcMetrics = 0x0B; /* PC 监控快照：CPU/内存/温度 */

        public const byte MsgInfo = 0x81;
        public const byte MsgEvtButton = 0x82;
        public const byte MsgEvtEncoder = 0x83;
        public const byte MsgAck = 0x84;
        public const byte MsgPong = 0x85;

        public static byte Crc8(byte[] data, int start, int count)
        {
            byte crc = 0;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[start + i];
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
            }
            return crc;
        }

        public static byte[] Encode(byte type, byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            if (payload.Length > MaxPayload) throw new ArgumentException("payload too long");
            byte[] frame = new byte[5 + payload.Length];
            frame[0] = Hdr1;
            frame[1] = Hdr2;
            frame[2] = type;
            frame[3] = (byte)payload.Length;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = Crc8(frame, 2, 2 + payload.Length);
            return frame;
        }

        /// <summary>从缓冲中提取下一帧；返回 false 表示数据不足或未找到帧头。</summary>
        public static bool TryExtract(byte[] buffer, ref int offset, out byte type, out byte[] payload)
        {
            type = 0;
            payload = null;
            int n = buffer.Length - offset;
            if (n < 5) return false;

            int pos = offset;
            while (pos <= buffer.Length - 5)
            {
                if (buffer[pos] == Hdr1 && buffer[pos + 1] == Hdr2) break;
                pos++;
            }
            if (pos > buffer.Length - 5)
            {
                offset = buffer.Length - 1; /* 保留可能的半个帧头 */
                return false;
            }

            int len = buffer[pos + 3];
            int frameLen = 5 + len;
            if (pos + frameLen > buffer.Length) return false; /* 等待更多数据 */

            byte expected = Crc8(buffer, pos + 2, 2 + len);
            if (expected != buffer[pos + frameLen - 1])
            {
                /* CRC 错误：跳过一字节继续找 */
                offset = pos + 1;
                return TryExtract(buffer, ref offset, out type, out payload);
            }

            type = buffer[pos + 2];
            payload = new byte[len];
            Array.Copy(buffer, pos + 4, payload, 0, len);
            offset = pos + frameLen;
            return true;
        }
    }
}
