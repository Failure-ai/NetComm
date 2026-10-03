using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.NetModules;
using Terraria.ID;
using Terraria.Net;
using tContentPatch;

namespace NetComm
{
    /// <summary>底层: 发包、抽队列、清理、提示和统计</summary>
    internal static class Comm
    {
        private static int sentPackets = 0;
        private static int failedPackets = 0;

        /// <summary>每帧抽发送队列, 顺带清理超时没收完的数据和 1 分钟没人取的文本</summary>
        internal static void Tick()
        {
            int now = Environment.TickCount;

            for (int i = 0; i < 8 && NetCommApi.queue.Count > 0; ++i)
            {
                if (TrySend(NetCommApi.queue.Dequeue())) ++sentPackets;
                else ++failedPackets;
            }

            NetCommApi.rec.Tick(now);

            for (int c = 0; c < NetCommApi.received.Length; ++c)
            {
                if (NetCommApi.received[c] != null) DropExpired(NetCommApi.received[c], now);
            }
        }

        /// <summary>一片一个 82 号包, 借用 NetMessage.buffer[256] 写完直接 AsyncSend</summary>
        private static bool TrySend(byte[] body)
        {
            try
            {
                if (Netplay.Connection.IsConnected() == false) return false;
                if (NetManager.Instance.GetModule<NetParticlesModule>() == null) return false;

                ushort moduleId = NetManager.Instance.GetId<NetParticlesModule>();
                MessageBuffer buffer = NetMessage.buffer[256];

                lock (buffer)
                {
                    if (buffer.writer == null) buffer.ResetWriter();
                    BinaryWriter writer = buffer.writer;
                    writer.BaseStream.Position = 0L;
                    long position = writer.BaseStream.Position;
                    writer.BaseStream.Position += 2L;
                    writer.Write(MessageID.NetModules);
                    writer.Write(moduleId);
                    writer.Write(body);

                    int size = (int)writer.BaseStream.Position;
                    writer.BaseStream.Position = position;
                    writer.Write((ushort)size);
                    writer.BaseStream.Position = size;

                    ushort num = BitConverter.ToUInt16(buffer.writeBuffer, 0);
                    Netplay.Connection.Socket.AsyncSend(buffer.writeBuffer, 0, num, Netplay.Connection.ClientWriteCallBack);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>收包处喂进来一片: 拼包, 收完一条就按标记(通道)存着给 NetCommApi.Receive(标记) 取</summary>
        internal static void Feed(int sender, byte[] body)
        {
            byte[] data = NetCommApi.rec.Feed(sender, body, Environment.TickCount);
            if (data == null) return;

            int channel = NetCommApi.rec.LastChannel;

            Queue<NetCommApi.Msg> q = NetCommApi.received[channel];
            if (q == null) NetCommApi.received[channel] = q = new Queue<NetCommApi.Msg>();

            // 每个标记最多攒 32 条, 满了挤掉最旧的, 免得没人取的模组把内存顶爆
            if (q.Count >= ReceivedMax) q.Dequeue();

            string text = Encoding.UTF8.GetString(data);
            q.Enqueue(new NetCommApi.Msg { text = text, tick = Environment.TickCount });

            if (NetCommApi.Debug) Print("收到 来自玩家=" + sender + " 通道=" + channel + " 内容=" + text);
        }

        private const int ReceivedMax = 32;

        /// <summary>超过 1 分钟没人取的数据直接清掉(队列先进先出, 过期的只可能在头部)</summary>
        internal static void DropExpired(Queue<NetCommApi.Msg> q, int now)
        {
            while (q.Count > 0 && now - q.Peek().tick > ReceivedLiveMs) q.Dequeue();
        }

        private const int ReceivedLiveMs = 60000;

        /// <summary>玩家断开, 他剩下的片凑不齐了, 直接丢</summary>
        internal static void ClearSender(int playerIndex)
        {
            NetCommApi.rec.ClearSender(playerIndex);
        }

        internal static void Print(string text)
        {
            ContentPatch.PrintTry("[c/00ff00:NetComm] " + text);
            Main.NewText("[c/00ff00:NetComm] " + text);
        }

        internal static string Status()
        {
            Reassembler rec = NetCommApi.rec;

            int wait = 0;
            for (int i = 0; i < NetCommApi.received.Length; ++i)
            {
                if (NetCommApi.received[i] != null) wait += NetCommApi.received[i].Count;
            }

            return "发片:" + sentPackets + " 失败:" + failedPackets
                + " 队列:" + NetCommApi.queue.Count + " 待取:" + wait
                + " | 丢弃 校验:" + rec.ChecksumFailCount + " 长度:" + rec.LengthMismatchCount
                + " 无首片:" + rec.OrphanSliceCount + " 重开:" + rec.RestartCount
                + " 超时:" + rec.TimeoutDropCount + " 超限:" + rec.LimitDropCount
                + " 解压:" + rec.DecompressFailCount;
        }
    }

    /// <summary>每帧抽发送队列</summary>
    internal class CommSend : PatchMain
    {
        public override void UpdatePostfix(GameTime gameTime)
        {
            if (Main.netMode != 1) return;

            Comm.Tick();
        }
    }

    /// <summary>收 82 号包: 认出我们的包就喂给 NetCommApi 拼包</summary>
    internal class CommReceive : PatchMessageBuffer
    {
        public override void GetDataPostfix(MessageBuffer This, int start, int length, int messageType)
        {
            if (Main.netMode != 1) return;
            if (messageType != MessageID.NetModules) return;
            if (length < 3 + Packet.BodyLength) return;

            This.reader.BaseStream.Position = start + 1;

            if (This.reader.ReadUInt16() != NetManager.Instance.GetId<NetParticlesModule>()) return;

            byte[] body = This.reader.ReadBytes(Packet.BodyLength);
            if (Packet.Match(body, 0) == false) return;

            // 中转过的包在这边 whoAmI 只是"服务端那条连接", 所以发送者取包里自称的 id
            Comm.Feed(Packet.SenderOf(body), body);
        }

        public override void OnPlayerDisconnect(int playerIndex)
        {
            Comm.ClearSender(playerIndex);
        }
    }
}
