using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace NetComm
{
    /// <summary>
    /// 82 号包(MessageID.NetModules)伪装成粒子模块的协议: 字段布局 + 切片 + 压缩, 发收两端都用这里
    /// <para/>载荷定长 22 字节 = 1(粒子类型, 恒 255) + 4 个 float(每个 3 数据字节) + 4(int, 第 0 字节是头) + 1(发起玩家字节)
    /// <para/>头占 int 的第 0 字节; 发起玩家字节固定放发送者 id; 其余 15 槽放数据, 首片让出前 3 槽放总长度和压缩位, 末片让出第 14 槽放校验和
    /// </summary>
    internal static class Packet
    {
        public const int BodyLength = 22;
        public const int SlotCount = 16;

        public const int SenderSlot = 15;
        public const int ChecksumSlot = 14;
        public const int InfoSlotCount = 3;

        public const int MaxSlices = 8192;

        /// <summary>单条数据的字节上限: 首片 12 + 中间每片 15 + 末片 14, 共 MaxSlices 片</summary>
        public const int MaxDataLength = (MaxSlices - 2) * (SlotCount - 1) + 12 + 14;

        public const byte FlagMarker = 0x80;
        public const byte FlagFirst = 0x40;
        public const byte FlagLast = 0x20;
        public const byte ChannelMask = 0x1F;
        public const int Channels = ChannelMask + 1;

        /// <summary>首片信息槽: 低 17 位是整条数据的字节数, 第 17 位是压缩标志</summary>
        public const int TotalLengthMask = 0x1FFFF;
        public const int CompressedBit = 17;

        // 四个 float 的指数固定为 150, 于是值恒等于 [2^23, 2^24) 里的精确整数(ulp=1), 不可能出现
        // 一个 float 只有 24 位能放数据: 指数最低位落在第 3 字节的最高位(恒 0), 符号位落在第 4 字节的最高位(当数据用)。
        private const byte ExpHighBits = 0x4B;   // 150 >> 1, 占第 4 字节的低 7 位
        private const byte ExpLowBit = 0x80;     // 150 & 1 == 0, 落在第 3 字节的最高位

        private const int TypePos = 0;

        public const byte ParticleType = (byte)255;
        private const int HeaderPos = 17;
        private const int IntDataPos = 18;
        private const int FreePos = 21;
        private const int FloatSlotCount = 12;
        private const int CompressMinLength = 64;

        //-------------------- 发送 --------------------

        /// <summary>一条数据切成若干 22 字节片, 压不压在这里决定。数据不合法返回 null</summary>
        public static List<byte[]> Split(int channel, byte senderId, byte[] data)
        {
            if (data == null || data.Length < 1 || data.Length > MaxDataLength) return null;

            byte[] zipped = Compress(data);
            bool compressed = zipped != null;
            byte[] payload = compressed ? zipped : data;

            List<byte[]> bodies = new List<byte[]>();
            int length = payload.Length;
            byte sum = Checksum(payload);
            int offset = 0;

            while (true)
            {
                bool first = offset == 0;
                int remaining = length - offset;
                bool last = remaining <= Capacity(first, true);
                int take = last ? remaining : Capacity(first, false);

                byte[] body = new byte[BodyLength];
                WriteBody(body, MakeHeader(channel, first, last), senderId, length, compressed, sum, payload, offset, take);
                bodies.Add(body);

                offset += take;
                if (last) return bodies;
            }
        }

        /// <summary>压完不省字节就返回 null 表示不压</summary>
        public static byte[] Compress(byte[] data)
        {
            if (data.Length < CompressMinLength) return null;

            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    ds.Write(data, 0, data.Length);
                }

                if (ms.Length >= data.Length) return null;

                return ms.ToArray();
            }
        }

        /// <summary>解压, 结果超过上限或数据坏了返回 null</summary>
        public static byte[] Decompress(byte[] data)
        {
            try
            {
                using (MemoryStream src = new MemoryStream(data, 0, data.Length, false))
                using (DeflateStream ds = new DeflateStream(src, CompressionMode.Decompress))
                using (MemoryStream dst = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int read;

                    while ((read = ds.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, read);
                        if (dst.Length > MaxDataLength) return null;
                    }

                    return dst.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        public static byte Checksum(byte[] data)
        {
            int sum = 0;

            for (int i = 0; i < data.Length; ++i) sum += data[i];

            return (byte)sum;
        }

        //-------------------- 接收 --------------------

        /// <summary>检查这个 22 字节载荷是不是本模组的包(别人的真粒子包, 不能读)</summary>
        public static bool Match(byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || offset + BodyLength > buffer.Length) return false;
            if (buffer[offset + TypePos] != ParticleType) return false;
            if ((buffer[offset + HeaderPos] & FlagMarker) == 0) return false;

            for (int f = 0; f < 4; ++f)
            {
                int p = offset + FloatBase(f);
                if ((buffer[p + 2] & ExpLowBit) != 0) return false;
                if ((buffer[p + 3] & 0x7F) != ExpHighBits) return false;
            }

            return true;
        }

        public static byte Header(byte[] body)
        {
            return body[HeaderPos];
        }

        public static int Channel(byte header)
        {
            return header & ChannelMask;
        }

        public static bool IsFirst(byte header)
        {
            return (header & FlagFirst) != 0;
        }

        public static bool IsLast(byte header)
        {
            return (header & FlagLast) != 0;
        }

        public static int Capacity(byte header)
        {
            return Capacity(IsFirst(header), IsLast(header));
        }

        /// <summary>把 22 字节载荷里的 16 个槽解出来</summary>
        public static void Slots(byte[] body, byte[] slots)
        {
            for (int i = 0; i < SlotCount; ++i) slots[i] = GetSlot(body, i);
        }

        public static int Sender(byte[] slots)
        {
            return slots[SenderSlot];
        }

        /// <summary>发送者 id 每片都占最后一个槽, 不用整片解出来就能读</summary>
        public static int SenderOf(byte[] body)
        {
            return body[FreePos];
        }

        public static int TotalLength(byte[] slots)
        {
            return (slots[0] | (slots[1] << 8) | (slots[2] << 16)) & TotalLengthMask;
        }

        public static bool Compressed(byte[] slots)
        {
            return ((slots[0] | (slots[1] << 8) | (slots[2] << 16)) >> CompressedBit) != 0;
        }

        public static byte ChecksumOf(byte[] slots)
        {
            return slots[ChecksumSlot];
        }

        //-------------------- 字段读写 --------------------

        private static int Capacity(bool first, bool last)
        {
            int cap = SlotCount - 1;
            if (first) cap -= InfoSlotCount;
            if (last) cap -= 1;
            return cap;
        }

        internal static byte MakeHeader(int channel, bool first, bool last)
        {
            return (byte)(FlagMarker | (channel & ChannelMask) | (first ? FlagFirst : 0) | (last ? FlagLast : 0));
        }

        internal static void WriteBody(byte[] body, byte header, byte senderId, int totalLength, bool compressed, byte checksum, byte[] data, int offset, int count)
        {
            bool first = IsFirst(header);
            bool last = IsLast(header);
            int slotBase = first ? InfoSlotCount : 0;
            int slotEnd = last ? ChecksumSlot - 1 : ChecksumSlot;
            int info = (totalLength & TotalLengthMask) | (compressed ? (1 << CompressedBit) : 0);

            body[TypePos] = ParticleType;

            if (first)
            {
                SetSlot(body, 0, (byte)info);
                SetSlot(body, 1, (byte)(info >> 8));
                SetSlot(body, 2, (byte)(info >> 16));
            }

            for (int i = slotBase; i <= slotEnd; ++i)
            {
                SetSlot(body, i, At(data, offset, count, i - slotBase));
            }

            if (last) SetSlot(body, ChecksumSlot, checksum);

            SetSlot(body, SenderSlot, senderId);
            body[HeaderPos] = header;
        }

        private static void SetSlot(byte[] body, int slot, byte v)
        {
            if (slot < FloatSlotCount)
            {
                int p = FloatBase(slot / 3) + slot % 3;

                if (slot % 3 == 2)
                {
                    body[p] = (byte)(v & 0x7F);
                    body[p + 1] = (byte)(ExpHighBits | (v & 0x80));
                }
                else
                {
                    body[p] = v;
                }

                return;
            }

            if (slot < SlotCount - 1)
            {
                body[IntDataPos + slot - FloatSlotCount] = v;
                return;
            }

            body[FreePos] = v;
        }

        private static byte GetSlot(byte[] body, int slot)
        {
            if (slot < FloatSlotCount)
            {
                int p = FloatBase(slot / 3) + slot % 3;

                if (slot % 3 == 2) return (byte)((body[p] & 0x7F) | (body[p + 1] & 0x80));

                return body[p];
            }

            if (slot < SlotCount - 1) return body[IntDataPos + slot - FloatSlotCount];

            return body[FreePos];
        }

        private static int FloatBase(int f)
        {
            return TypePos + 1 + f * 4;
        }

        private static byte At(byte[] data, int offset, int count, int index)
        {
            if (index >= 0 && index < count)
            {
                int i = offset + index;
                if (data != null && i >= 0 && i < data.Length) return data[i];
            }

            return 0;
        }
    }
}
