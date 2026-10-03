using System;
using System.Collections.Generic;
using System.Text;
using Terraria;

namespace NetComm
{
    /// <summary>
    /// 双端通信对外接口: 把数据塞进 82 号包(伪装成原版粒子模块)发给服务端, 服务端原样中转给其他玩家，TShock不检验
    /// 只在客户端(netMode==1)工作, 服务端不用装加载器
    /// 发: NetCommApi.Send(标记, "文本");  收: 自己轮询 NetCommApi.Receive(标记) 拿文本
    /// 标记就是包头里的通道号(0~31), 一个模组认领一个, 收的时候只会取到自己那个标记的数据, 多模组互不干扰
    /// Debug = true(或指令 nc debug 1) 时, 发送和收到的内容都打印在游戏里
    /// 发包/抽队列/统计这些底层都在 NetComm.cs 的 Comm 里
    /// </summary>
    public static class NetCommApi
    {
        /// <summary>标记(通道)可用范围 0 ~ MaxChannel</summary>
        public const int MaxChannel = Packet.Channels - 1;

        /// <summary>开了就把发出去和收到的内容打到游戏聊天框</summary>
        public static bool Debug = false;

        /// <summary>单条数据的最大字节数(压缩前)</summary>
        public static int MaxDataLength
        {
            get { return Packet.MaxDataLength; }
        }

        /// <summary>待发队列, 一片一个 22 字节载荷</summary>
        internal static readonly Queue<byte[]> queue = new Queue<byte[]>();

        /// <summary>每个标记一条收包队列, 等 Receive(标记) 取</summary>
        internal static readonly Queue<Msg>[] received = new Queue<Msg>[Packet.Channels];

        /// <summary>一条收到的数据: 文本 + 入队时刻(超过 1 分钟没人取就清掉)</summary>
        internal sealed class Msg
        {
            public string text = null;
            public int tick = 0;
        }

        internal static readonly Reassembler rec = new Reassembler();

        /// <summary>发送一段文本到指定标记(0~31)</summary>
        public static bool Send(int channel, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            bool ok = Send(channel, Encoding.UTF8.GetBytes(text));
            if (Debug) Comm.Print((ok ? "发送 通道=" + channel + ": " : "发送失败 通道=" + channel + ": ") + text);

            return ok;
        }

        /// <summary>发一条字节数据到指定标记(0~31), 能压小的自动压缩, 排队后每帧最多发 8 片</summary>
        public static bool Send(int channel, byte[] data)
        {
            if (Main.netMode != 1) return false;
            if (channel < 0 || channel > MaxChannel) return false;

            List<byte[]> bodies = Packet.Split(channel, (byte)Main.myPlayer, data);
            if (bodies == null) return false;

            for (int i = 0; i < bodies.Count; ++i) queue.Enqueue(bodies[i]);

            if (Debug) Comm.Print("已排队 通道=" + channel + " 字节=" + data.Length + " 片数=" + bodies.Count);

            return true;
        }

        /// <summary>取一条自己标记的文本, 没有就返回 null。要一直收就在自己的 Update 里反复调用。超过 1 分钟没取就清掉了</summary>
        public static string Receive(int channel)
        {
            if (channel < 0 || channel > MaxChannel) return null;

            Queue<Msg> q = received[channel];
            if (q == null) return null;

            Comm.DropExpired(q, Environment.TickCount);
            if (q.Count == 0) return null;

            return q.Dequeue().text;
        }
    }
}
