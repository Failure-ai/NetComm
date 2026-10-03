# NetComm

tPlainModLoader（TPM）1.4.5.8 的双端通信模组：把自定义数据塞进 **82 号包**（`MessageID.NetModules`）里伪装成原版粒子模块发给服务端，服务端会按粒子包原样中转给其他玩家。

- **只在客户端工作**（`Main.netMode == 1`），联机进服就能用。
- **服务端不用装加载器**，它看到的只是一个普通粒子包，会照常转发（TShock 同样不检验这条包）。
- 一条数据超过一片的容量时自动**切片 + 收端重组**，能压小的自动 **Deflate 压缩**。

---

## 一、安装


1. [Releases（发布版本）](https://github.com/Failure-ai/NetComm/releases/latest)：里面有编译好的版本可以直接使用

```
tPlainModLoader\Mods\NetComm\
    ico.png
    info.json
    loadConfig.json
    NetComm.dll
```

2. `loadConfig.json` 里 `isEnable` 当前是 `false`，改成 `true` 或者进游戏在模组管理里启用都行。

---

## 二、调用（引用 NetComm.dll）

在你的模组里加对 `NetComm.dll` 的引用（`Private` 设为 false），然后三个方法够用。**标记**（0~31）就是包头里的通道号，用来区分数据归谁：发的时候挑一个，收的时候只能取到自己那个标记的。

```csharp
using NetComm;

// 1) 发送文本，标记 5
NetCommApi.Send(5, "你好");

// 2) 发送字节数据，标记 3
NetCommApi.Send(3, System.Text.Encoding.UTF8.GetBytes("任意二进制"));

// 3) 接收：自己轮询取自己标记的文本，没有就返回 null
string text = NetCommApi.Receive(5);

// 提示开关：开了以后发送和收到的内容都打印（控制台 + 游戏聊天框）
NetCommApi.Debug = true;

// 标记范围 0 ~ NetCommApi.MaxChannel(31)；单条数据的字节上限（压缩前）= 122876
int max = NetCommApi.MaxDataLength;
```

收包是**按标记分队列**的：收到一条完整的就转成文本存进它自己标记的那条队列，`Receive(标记)` 取走一条就少一条（先进先出）。每条队列最多缓存 32 条，满了挤掉该标记最旧的一条；**放进去超过 1 分钟没人取的数据会被自动清掉**

### 在自己的模组里轮询接收

```csharp
internal class MyReceive : tContentPatch.PatchMain
{
    public override void UpdatePostfix(Microsoft.Xna.Framework.GameTime gameTime)
    {
        string s;
        while ((s = NetCommApi.Receive(5)) != null)
        {
            Main.NewText("收到: " + s);
        }
    }
}
```

### 标记（通道）怎么用

- 一个模组认领一个固定标记，别和别的模组撞号（0~31 共 32 个）。`nc send` 这类调试数据走 0 号标记。
- 收端按 **(发送者 id, 标记)** 攒片，所以两个人同时在同一标记发不会串；同一个人在两个标记上同时发也不会串。
- 同一标记同一时刻只传一条消息（TCP 保序 + 服务端逐包同步中转，所以不需要序号）。

---

## 三、不引用 dll，用反射调

```csharp
System.Type t = System.Type.GetType("NetComm.NetCommApi, NetComm");
if (t != null)
{
    // Send(int 标记, string 文本)
    t.GetMethod("Send", new[] { typeof(int), typeof(string) })
        .Invoke(null, new object[] { 5, "你好" });

    // Receive(int 标记) -> string, 没有返回 null
    string got = (string)t.GetMethod("Receive", new[] { typeof(int) })
        .Invoke(null, new object[] { 5 });

    t.GetField("Debug").SetValue(null, true);
}
```

反射名固定：类 `NetComm.NetCommApi`，程序集 `NetComm`。

---

## 四、调试指令


| 指令 | 作用 |
| --- | --- |
| `nc debug 1` / `nc debug 0` | 开 / 关收发的打印提示 |
| `nc send <字节数>` | 发一条自动填充的测试数据（走 0 号标记），用来压测分包 |
| `nc stat` | 打印统计：发片数、失败、发送队列长度、待取条数、各类坏包丢弃计数 |


---

## 五、必须知道的限制

- **发送者自己收不到**：服务端转发时会跳过发送者本人。要给自己看请在本地另走一条逻辑。
- **不是机密**：数据经服务端中转给房间里**所有**玩家，任何装了本模组的客户端都能收到，别传敏感内容。
- **发送者 id 可被伪造**：客户端侧只能认包里自称的 id（中转后 `whoAmI` 只代表"服务端那条连接"，认不出原始发送者）。
- 单条数据上限 **122876 字节**（压缩前）；超了 `Send` 直接返回 false。
- 每帧最多发 **8 片**（82 号包走 `Socket.AsyncSend`，绕过原版发送队列，所以自己限速）。一条 1KB 数据 ≈ 68 片 ≈ 9 帧。
- 只在 `netMode == 1` 有效：单人模式和开服端调用一律返回 false / 不处理。
- **收到但没人取的数据会被丢掉**：每个标记最多攒 32 条（满了挤掉最旧的），并且放进去超过 1 分钟没被 `Receive` 取走就自动清除 ⇒ 一定要在自己的 Update 里持续轮询，别指望攒着晚点取。
- 没装本模组的原版玩家会收到这些"粒子包"，因为粒子类型固定为 255 且世界坐标被约束在 ±840 万~1670 万格，屏幕上看不到。

---
 
## 六、代码结构

```
NetComm/
  NetCommApi.cs          对外 API: Send(标记,文本/字节) / Receive(标记) / Debug / MaxChannel / MaxDataLength
  NetComm.cs             底层: Comm(发包·抽队列·拼包入口·提示·统计) + CommSend + CommReceive 两个钩子
  ThisMod.cs             指令注册 (Mod.GetCommands)
  Protocol/Packet.cs     22 字节载荷布局 + 切片 + 压缩 + 校验和
  Protocol/Reassembler.cs 收端按 (发送者, 通道) 重组、超时与断连清理
```

