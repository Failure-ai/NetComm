using System;
using System.Collections.Generic;
using System.Text;
using Terraria;

namespace NetComm
{
    /// <summary>
    /// 双端通信对外接口: 把数据塞进 82 号包(伪装成原版粒子模块)发给服务端, 服务端原样中转给其他玩家，TShock不检验
    /// 只在客户端(netMode==1)工作, 服务端不用装加载器
    /// 发: NetCommApi.Send("文本");  收: 自己轮询 NetCommApi.Receive() 拿文本
    /// Debug = true(或指令 nc debug 1) 时, 发送和收到的内容都打印在游戏里
    /// 发包/抽队列/统计这些底层都在 NetComm.cs 的 Comm 里
    /// </summary>
    public static class NetCommApi
    {
        /// <summary>开了就把发出去和收到的内容打到游戏聊天框</summary>
        public static bool Debug = false;

        /// <summary>单条数据的最大字节数(压缩前)</summary>
        public static int MaxDataLength
        {
            get { return Packet.MaxDataLength; }
        }

        /// <summary>待发队列, 一片一个 22 字节载荷</summary>
        internal static readonly Queue<byte[]> queue = new Queue<byte[]>();

        /// <summary>收到的文本, 等 Receive() 取</summary>
        internal static readonly Queue<string> received = new Queue<string>();

        internal static readonly Reassembler rec = new Reassembler();

        /// <summary>发送一段文本, 走 0 号通道</summary>
        public static bool Send(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            bool ok = Send(0, Encoding.UTF8.GetBytes(text));
            if (Debug) Comm.Print((ok ? "发送: " : "发送失败: ") + text);

            return ok;
        }

        /// <summary>发一条字节数据到指定通道(0~31), 能压小的自动压缩, 排队后每帧最多发 8 片</summary>
        public static bool Send(int channel, byte[] data)
        {
            if (Main.netMode != 1) return false;
            if (channel < 0 || channel >= Packet.Channels) return false;

            List<byte[]> bodies = Packet.Split(channel, (byte)Main.myPlayer, data);
            if (bodies == null) return false;

            for (int i = 0; i < bodies.Count; ++i) queue.Enqueue(bodies[i]);

            if (Debug) Comm.Print("已排队 通道=" + channel + " 字节=" + data.Length + " 片数=" + bodies.Count);

            return true;
        }

        /// <summary>取一条收到的文本, 没有就返回 null。要一直收就在自己的 Update 里反复调用</summary>
        public static string Receive()
        {
            return received.Count == 0 ? null : received.Dequeue();
        }
    }
}
