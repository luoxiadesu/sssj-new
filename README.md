# SssjServer

基于对已停服手游《双生视界》（少女咖啡枪2，`com.bilibili.gcg2.bili`，Unity 2018.4.19f1 + il2cpp）客户端协议分析、从零实现的服务端，使用 C# / .NET 10 编写。与客户端原生协议互通，可本地跑通登录到游玩的完整闭环：TCP 网关 + HTTP 引导（serverlist / checkupdate / 资源下发）+ 基于客户端共享 LuaCall 接口实现的各功能域游戏逻辑。

> 当前服务端能力并非完全复原原服，尚有缺口待后续补齐。

近期进度见[2026-10-24 还原记录](docs/restoration-progress-2026-10-24.md)。本地卡池轮换工具见[使用说明](tools/卡池轮换使用说明.md)。

> **本仓库不含任何游戏资产**：不分发游戏的 Lua 脚本、配置表、美术、音频，也不包含原服代码。运行服务端所需的游戏配置表需自行准备（见[配置表](#配置表)一节）。
>
> 本项目是独立的互操作实现，与 bilibili 及原开发方**无任何关联或背书**。

## 功能特性

- **完整登录链路**（对未修改的客户端）：TCP 握手（pf 1102）→ 会话 3DES（链式 CBC）→ 登录（pf 1001）→ 玩家数据同步（pf 1005）→ 登录响应（pf 1002）→ 心跳与 LuaCall RPC（pf 1022/1023/1024）。
- **30+ 游戏功能域**，以 LuaCall / 字符串命令面实现：背包、角色卡、武器、符文、零件、抽卡、商店、邮件、好友、社团、聊天、咖啡馆（经营 / 场景 / 宠物）、任务、挑战塔、佣兵、私信、誓约等。
- **资源管道**：HTTP 引导兼容 `serverlist` / `checkupdate`；可选资源下发（`/Bundles/...`）；`--asset-build-version auto` 支持热更流程。
- **开发 / 验收工具**：服务端控制台命令、向在线客户端投递 Lua、确定性机器人好友与社团（生产环境用 `--bots false` 关闭）。
- **测试**：60+ 测试文件，覆盖协议回环、封包编解码、会话加密、配置表解析探针与各功能域行为。

## 目录结构

```
src/
  SssjServer.Protocol/   协议运行时 + 生成的消息类
  SssjServer.Core/       传输层：封包编解码、会话加密、网关、HTTP 引导、运行参数
  SssjServer.Game/       业务层：配置表加载、各功能域 handler、玩家/世界状态
  SssjServer.App/        可执行入口
tests/
  SssjServer.Game.Tests/     功能域与集成测试
  SssjServer.Tests/          传输层 / HTTP 引导测试
  SssjServer.Protocol.Tests/ 协议回环测试
tools/
  ProtoGen/              代码生成器：schema JSON → 生成的消息类
```

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 游戏客户端（可选；实际游玩需要）。原游戏已停服、CDN 已下线，运行客户端需本地缓存资源与一个指向本服务端的客户端包。

## 构建

```bash
dotnet build SssjServer.sln -c Release
```

## 运行

```bash
dotnet run --project src/SssjServer.App -c Release -- \
  --gate-host 10.0.2.2 --gate-port 6600 --http-port 8080 \
  --setting-dir /path/to/config-tables
```

进程启动两个监听：

- **TCP 网关**（`--gate-host` / `--gate-port`，默认 `6600`）——客户端连接的游戏服端口；
- **HTTP 引导**（`--http-port`，默认 `8080`）——提供 `serverlist` / `getstate` / `checkupdate`，可选 `/Bundles/...` 资源下发。

玩家存档按账号写入 `--data-dir`（默认 `data/`）下的 JSON 文件。

### 命令行参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--gate-host` | `10.0.2.2` | 下发给客户端的 TCP 网关地址 |
| `--gate-port` | `6600` | TCP 游戏服端口 |
| `--http-port` | `8080` | HTTP 引导端口 |
| `--bind` | `0.0.0.0` | HTTP 监听地址 |
| `--data-dir` | `data` | 玩家存档目录 |
| `--setting-dir` | `extracted/setting` | 游戏配置表目录（必需） |
| `--skip-tutorial` | `true` | 新账号预置教程任务值 |
| `--grant-starter` | `true` | 新账号发放初始角色卡与编队 |
| `--bots` | `true` | 生成机器人好友/社团（真实多人环境请关闭） |
| `--asset-dir` | *(空)* | 从该目录提供游戏资源（`/Bundles/...`） |
| `--asset-build-version` | `0` | 资源清单版本；`auto` = 每次现读 `version.data` |
| `--spend-vigour` | `true` | 进关是否扣体力 |
| `--refill-vigour` | `true` | 登录时把体力补满到上限 |
| `--start-gold` / `--start-diamond` | `100000` / `3000` | 新账号初始货币 |
| `--gm-file` | `work/gm.lua` | Lua 投递文件（推给在线客户端执行） |
| `--gm-accounts` | `""` | 逗号分隔的 GM 账号白名单；`*` = 所有账号 |
| `--gm-full-unlock` | `true` | GM 账号登录时自动全解锁 |

## 配置表

服务端启动时读取游戏的配置表（如 `Item/ItemExp.txt`、`Chapter.txt`、`PlayerExp.txt`）。没有这些表服务端无法正确解释游戏；而这些表是游戏版权数据，**不随本仓库分发**。

请从本地游戏资产（`setting.u` 包）解出配置表，将解出的目录通过 `--setting-dir` 传入。多数配置表加载在缺表时会降级（如不扣体力、经验为 0），但游戏逻辑依赖表内的正确数值。

## 测试

```bash
dotnet test SssjServer.sln -c Release
```

加载配置表的测试会相对仓库根解析 `extracted/setting`（见 `tests/SssjServer.Game.Tests/TestPaths.cs`），请在包含配置表的检出目录中运行（`extracted/setting` 位于 `server/` 的上级）。不依赖配置表的测试（协议回环、编解码、加密）无需任何外部数据。

## 许可

[MIT License](LICENSE)。本仓库仅包含自研服务端源码，不含任何游戏资产或原厂代码。

*双生视界*（Girl Cafe Gun 2）是各自权利人的商标。本项目为独立作品，与 bilibili 及原开发方无关联、不背书。文中游戏相关名称仅用于识别与互操作。
