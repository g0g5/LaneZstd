# LaneZstd

LaneZstd 是一个基于 .NET 10 的 UDP 隧道与中继工具，包含 `edge` 和 `hub` 两种运行模式，使用自定义帧协议在链路上传输数据，并按阈值启用 Zstd 压缩。

## 快速使用

### 适用场景

典型拓扑如下：

```text
UDP client <-> edge <==== framed UDP tunnel ====> hub <-> game server
```

- `edge` 部署在客户端附近，监听本地 UDP 客户端流量。
- `hub` 部署在服务端附近，接收来自多个 `edge` 的隧道流量。
- `hub` 会为每个 edge 会话分配一个独立的本地 UDP 端口，再把流量转发给真正的游戏服务器。

### 环境要求

- .NET 10 SDK
- 可用的 UDP 端口

### 构建

```bash
dotnet build LaneZstd.slnx
```

### 命令结构

```bash
dotnet run --project src/LaneZstd.Cli -- --help
dotnet run --project src/LaneZstd.Cli -- edge --help
dotnet run --project src/LaneZstd.Cli -- hub --help
```

全局参数：

- `--verbose` / `-v`: 输出更详细的包级日志。

### 启动 hub

最小示例：

```bash
dotnet run --project src/LaneZstd.Cli -- hub \
  --game 127.0.0.1:16261 \
  --session-port-range 40000-40031
```

常用参数：

- `--bind`: hub 对外监听地址，默认 `0.0.0.0:38441`
- `--game`: 本地游戏服务器地址，必填
- `--session-port-range`: 每个会话可分配的本地 UDP 端口范围，必填
- `--session-idle-timeout`: 会话空闲超时秒数，默认 `30`
- `--max-sessions`: 最大并发会话数，默认等于端口范围大小
- `--compress-threshold`: 压缩阈值，默认 `96`
- `--compression-level`: Zstd 压缩级别，默认 `3`
- `--max-packet-size`: 最大帧大小，默认 `1200`
- `--stats-interval`: 统计日志间隔秒数，默认 `5`，设为 `0` 可关闭周期统计

约束说明：

- `--bind` 端口不能落在 `--session-port-range` 内。
- `--max-sessions` 不能大于 `--session-port-range` 的端口数。

### 启动 edge

最小示例：

```bash
dotnet run --project src/LaneZstd.Cli -- edge \
  --hub 203.0.113.10:38441 \
  --game-listen 127.0.0.1:38443
```

常用参数：

- `--bind`: edge 本地隧道监听地址，默认 `0.0.0.0:38441`
- `--hub`: hub 地址，必填
- `--game-listen`: 本地面向 UDP 客户端的监听地址，必填
- `--compress-threshold`: 压缩阈值，默认 `96`
- `--compression-level`: Zstd 压缩级别，默认 `3`
- `--max-packet-size`: 最大帧大小，默认 `1200`
- `--stats-interval`: 统计日志间隔秒数，默认 `5`，设为 `0` 可关闭周期统计

约束说明：

- `--bind` 和 `--game-listen` 必须是不同端口。
- `--hub` 与 `--game-listen` 不能使用通配地址，例如 `0.0.0.0`。

### 一组可直接试跑的命令

先在服务端附近启动一个本地 hub：

```bash
dotnet run --project src/LaneZstd.Cli -- hub \
  --bind 0.0.0.0:38441 \
  --game 127.0.0.1:16261 \
  --session-port-range 40000-40015 \
  --stats-interval 5
```

再在客户端附近启动 edge：

```bash
dotnet run --project src/LaneZstd.Cli -- edge \
  --bind 0.0.0.0:38442 \
  --hub 203.0.113.10:38441 \
  --game-listen 127.0.0.1:38443 \
  --stats-interval 5
```

最后把你的 UDP 客户端指向 `127.0.0.1:38443`，流量就会经过 `edge -> hub -> game server`。

### 运行行为说明

- `edge` 启动后会先向 `hub` 注册会话。
- `edge` 会记住第一个接入 `--game-listen` 的 UDP 客户端，并只为这个客户端转发回包。
- `hub` 会为每个 edge 会话分配一个独立的本地会话端口。
- 大于压缩阈值的载荷会尝试使用 Zstd 压缩，小包则直接裸传以减少开销。
- 会话空闲超时后，`hub` 会主动清理该会话。

### 发布

生成可分发产物：

```bash
dotnet publish src/LaneZstd.Cli/LaneZstd.Cli.csproj -c Release -p:PublishDir=./publish/
```

## 本地开发

### 仓库结构

```text
src/
|- LaneZstd.Cli/       # CLI 入口与参数校验
|- LaneZstd.Core/      # edge / hub 运行时、转发逻辑、压缩与解压
\- LaneZstd.Protocol/  # 自定义帧协议与编解码

tests/
\- LaneZstd.Tests/     # xUnit 测试
```

### 常用命令

构建：

```bash
dotnet build LaneZstd.slnx
```

测试：

```bash
dotnet test LaneZstd.slnx
```

完整验证：

```bash
dotnet build LaneZstd.slnx && dotnet test LaneZstd.slnx
```

### 测试覆盖范围

测试项目位于 `tests/LaneZstd.Tests`，目前覆盖：

- CLI 参数解析与校验
- 协议帧编解码
- edge / hub 运行时行为
- 多 edge 集成转发场景

### 开发建议

- 改动 CLI 参数时，同步更新 `src/LaneZstd.Cli/Program.cs` 和 `tests/LaneZstd.Tests/CliTests.cs`
- 改动协议结构时，优先补充 `ProtocolTests`
- 改动转发或会话行为时，优先运行集成测试验证回环与多会话场景

### 调试方式

本地调试时，通常开两个终端：

1. 一个终端运行 `hub`
2. 一个终端运行 `edge`

需要更详细日志时，给任一命令加 `--verbose`。
