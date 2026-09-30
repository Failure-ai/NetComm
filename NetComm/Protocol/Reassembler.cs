using System.Collections.Generic;
using System.IO;

namespace NetComm
{
    /// <summary>收端: 按 (发送者, 通道) 把片拼回整条数据</summary>
    internal sealed class Reassembler
    {
        public const int TimeoutMs = 5000;

        public int OrphanSliceCount = 0;
        public int RestartCount = 0;
        public int TimeoutDropCount = 0;
        public int LimitDropCount = 0;
        public int LengthMismatchCount = 0;
        public int ChecksumFailCount = 0;
        public int DecompressFailCount = 0;
        public int MessageCount = 0;

        /// <summary>最近一条重组完成的数据的通道和发送者 id</summary>
        public int LastChannel = -1;
        public int LastSender = -1;

        private class Pending
        {
            public MemoryStream stream = new MemoryStream();
            public bool compressed = false;
            public int channel = 0;
            public int sender = 0;
            public int total = 0;
            public int slices = 0;
            public int tick = 0;
        }

        private readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        private readonly byte[] slots = new byte[Packet.SlotCount];
        private readonly List<int> keyBuf = new List<int>();

        /// <summary>返回 null 表示还没收完或者被丢弃; 非 null 是整条数据(已解压)</summary>
        public byte[] Feed(int senderKey, byte[] body, int now)
        {
            byte header = Packet.Header(body);
            bool first = Packet.IsFirst(header);
            bool last = Packet.IsLast(header);
            int channel = Packet.Channel(header);
            int key = senderKey * Packet.Channels + channel;

            Packet.Slots(body, slots);

            if (first)
            {
                Pending old;
                if (pending.TryGetValue(key, out old))
                {
                    ++RestartCount;
                    pending.Remove(key);
                }

                pending[key] = new Pending
                {
                    compressed = Packet.Compressed(slots),
                    channel = channel,
                    sender = Packet.Sender(slots),
                    total = Packet.TotalLength(slots),
                    tick = now
                };
            }

            Pending p;
            if (pending.TryGetValue(key, out p) == false)
            {
                ++OrphanSliceCount;
                return null;
            }

            p.tick = now;

            // 末片不足容量的那部分是补零, 按首片报的总长度裁掉
            int room = p.total - (int)p.stream.Length;
            if (room > 0)
            {
                int take = Packet.Capacity(header);
                p.stream.Write(slots, first ? Packet.InfoSlotCount : 0, take > room ? room : take);
            }

            ++p.slices;

            if (p.slices > Packet.MaxSlices || p.stream.Length > Packet.MaxDataLength)
            {
                ++LimitDropCount;
                pending.Remove(key);
                return null;
            }

            if (last == false) return null;

            byte[] raw = p.stream.ToArray();
            pending.Remove(key);

            if (raw.Length != p.total)
            {
                ++LengthMismatchCount;
                return null;
            }

            if (Packet.Checksum(raw) != Packet.ChecksumOf(slots))
            {
                ++ChecksumFailCount;
                return null;
            }

            ++MessageCount;
            LastChannel = channel;
            LastSender = p.sender;

            if (p.compressed == false) return raw;

            byte[] data = Packet.Decompress(raw);
            if (data == null) ++DecompressFailCount;

            return data;
        }

        public void Tick(int now)
        {
            if (pending.Count == 0) return;

            keyBuf.Clear();

            foreach (KeyValuePair<int, Pending> kv in pending)
            {
                if (unchecked(now - kv.Value.tick) > TimeoutMs) keyBuf.Add(kv.Key);
            }

            for (int i = 0; i < keyBuf.Count; ++i)
            {
                ++TimeoutDropCount;
                pending.Remove(keyBuf[i]);
            }
        }

        /// <summary>玩家断开后他剩下的片永远凑不齐了, 直接丢</summary>
        public void ClearSender(int senderKey)
        {
            keyBuf.Clear();

            foreach (int key in pending.Keys)
            {
                if (key / Packet.Channels == senderKey) keyBuf.Add(key);
            }

            for (int i = 0; i < keyBuf.Count; ++i)
            {
                ++TimeoutDropCount;
                pending.Remove(keyBuf[i]);
            }
        }

        public int PendingCount
        {
            get { return pending.Count; }
        }
    }
}
