using CommandHelp;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using tContentPatch;
using tContentPatch.Patch;
using Terraria.Chat;
using static System.Net.Mime.MediaTypeNames;

namespace NetComm
{
    /// <summary>调试指令, 参数全用整数(字符串参数会被空格拆开, 不好用)。</summary>
    internal class ThisMod : Mod
    {
        public override List<CommandObject> GetCommands()
        {
            List<CommandObject> cos = new List<CommandObject>();

            CommandMethod nc = new CommandMethod("nc");//指令开头
            nc.SubCommand.Add(tContentPatch.Command.Utils.GetCO_OutputCOList(nc.SubCommand));

            CommandMethod debug = new CommandMethod("debug", 1);//打印文本
            debug.SubCommand.Add(new CommandInt());
            debug.Runing += vs => SetDebug(ToInt(vs[0]));
            nc.SubCommand.Add(debug);

            CommandMethod send = new CommandMethod("send", 1);//测试发送
            send.SubCommand.Add(new CommandInt());
            send.Runing += vs => Send(ToInt(vs[0]));
            nc.SubCommand.Add(send);

            CommandMethod stat = new CommandMethod("stat");//部分参数
            stat.Runing += vs => Comm.Print(Comm.Status());
            nc.SubCommand.Add(stat);

            cos.Add(nc);

            return cos;
        }

        private static void SetDebug(int on)
        {
            if (on < 0)
            {
                Comm.Print("用法 nc debug 0|1");
                return;
            }

            NetCommApi.Debug = on != 0;
            Comm.Print("提示已" + (NetCommApi.Debug ? "开启" : "关闭"));
        }

        private static void Send(int length)
        {
            if (length < 1 || length > NetCommApi.MaxDataLength)
            {
                Comm.Print("字节数要在 1 ~ " + NetCommApi.MaxDataLength + " 之间");
                return;
            }

            byte[] data = new byte[length];
            byte[] src = Encoding.UTF8.GetBytes("你知道404-ABCD123456吗？?';、】【‘-=！@#￥%……&*（）");
            for (int i = 0; i < length; ++i) data[i] = src[i % src.Length];

            bool ok = NetCommApi.Send(0, data);
            Comm.Print("通道=0 字节=" + length + " " + (ok ? "已排队(每帧 8 片)" : "失败"));
        }

        private static int ToInt(object v)
        {
            return v is int ? (int)v : -1;
        }
    }
}
