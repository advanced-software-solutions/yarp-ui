# YARP UI

[![NuGet](https://img.shields.io/nuget/v/YA-RP-UI.svg)](https://www.nuget.org/packages/YA-RP-UI)
[![downloads](https://img.shields.io/nuget/dt/YA-RP-UI.svg)](https://www.nuget.org/packages/YA-RP-UI)
[![docker](https://img.shields.io/docker/v/amrfswalha/yarp-ui.svg?label=docker)](https://hub.docker.com/r/amrfswalha/yarp-ui)
[![docker pulls](https://img.shields.io/docker/pulls/amrfswalha/yarp-ui.svg?label=docker%20pulls)](https://hub.docker.com/r/amrfswalha/yarp-ui)
[![tests](https://img.shields.io/badge/tests-92%20passed%20%202%20skipped-success)](https://github.com/advanced-software-solutions/yarp-ui/actions/workflows/build.yml)

> 🌐 语言：[English](README.md) | [العربية](README.ar.md) | [Español](README.es.md) | **简体中文**

[YARP](https://microsoft.github.io/reverse-proxy/)（Yet Another Reverse Proxy，又一个反向代理）的管理界面。一个应用**同时**是可正常工作的反向代理和它的控制室：

- **路由图**（`/`）— 将每条 路由 → 集群 → 目标 渲染为交互式图形。点击节点可追踪其完整链路并查看其配置；搜索可高亮匹配项。
- **编辑器**（`/editor`）— 创建、编辑和删除路由、集群和目标。保存时会校验配置，**无需重启**即可应用到运行中的代理，并持久化到磁盘。
- **日志**（`/logs`）— 已代理请求的实时视图（方法、路径、状态码、耗时、客户端 IP、路由、集群、选中的目标），最新在前，各列均可排序。路由/集群/目标筛选器和时间范围选择器会搜索全部保留的历史记录，而不仅是实时缓冲区。另有性能面板：按时间绘制的每请求耗时图并按状态类别着色、平均/P95/最大/错误率统计卡片，以及按路由的汇总。

> **版本说明** — 本仓库是**社区版**，基于 Apache-2.0 免费提供。另有一个高级版在其之上增加商业功能，并以商业许可证分发。高级版代码永远不会存在于本仓库中。

## 文档

完整指南位于[项目 Wiki](https://github.com/advanced-software-solutions/yarp-ui/wiki)：

- [入门](https://github.com/advanced-software-solutions/yarp-ui/wiki/Getting-Started)
- [托管模式](https://github.com/advanced-software-solutions/yarp-ui/wiki/Hosting-Modes)
- [配置](https://github.com/advanced-software-solutions/yarp-ui/wiki/Configuration)
- [REST API](https://github.com/advanced-software-solutions/yarp-ui/wiki/REST-API)
- [Docker](https://github.com/advanced-software-solutions/yarp-ui/wiki/Docker)

## 托管模式

本界面以 Razor 类库（[**YA-RP-UI**](https://www.nuget.org/packages/YA-RP-UI) NuGet 包）的形式分发，可通过三种方式托管：

**1. 独立可执行程序** — `YARPUI.Host` 是一个轻量宿主，在单个应用中同时运行代理和管理界面：

```bash
cd YARPUI.Host && dotnet run      # → http://localhost:5080
```

**2. 嵌入到你自己的应用** — 添加包并完成接线（见 `samples/EmbeddedHost`）：

```xml
<PackageReference Include="YA-RP-UI" Version="0.2.1" />
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddYarpUi();               // 代理配置、服务、认证、Razor Pages

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();     // 记录已代理的请求，供日志页面使用
app.MapYarpUi();                   // UI 页面 + /api/yarp/*
app.MapReverseProxy();             // 代理本身（公开）
app.Run();
```

**3. 附加到已配置 YARP 的应用** — 适用于拥有自己的 `LoadFromConfig`/自定义提供器、转换和过滤器的网关。界面会显示应用的全部实时配置，**并且可以编辑它**：保存时将每项更改写回该路由或集群来源的 `appsettings.json` 文件，YARP 会热重载该文件 — 编辑无需重启即可生效，而应用的代码（转换、中间件、自定义管道）保持原样：

```csharp
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(...);            // 你的自定义逻辑仍然完全掌控一切

builder.AttachYarpUi();            // 不注册代理，也不播种配置

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();
app.MapReverseProxy();
app.MapYarpUi();
```

写回式编辑的行为：

- **编辑会合并进现有的 JSON 节点** — 编辑器未建模的字段（如 `RateLimiterPolicy` 或自定义键）保留其原值；文件中的无关内容也会被保留。
- **新建的路由/集群**会添加到 `appsettings.json`；**删除的**会从定义它们的每个 appsettings 文件中移除（包括环境覆盖文件）。
- **备份**：界面第一次修改某个文件时，会在其旁边保留一份 `.yarpui.bak` 副本；*恢复 appsettings 备份* 可将所有被修改的文件回滚。
- 来自**非文件来源**（由数据库、代码等支撑的自定义 `IProxyConfigProvider`）的条目显示为锁定且只读 — 没有可写回的文件。
- 预先存在的配置问题（如路由引用了不存在的集群）不会阻止保存；只有编辑本身引入的问题才会被拒绝。

所有模式读取相同的配置（`YarpUi:Auth` 凭据），并支持 `YarpUi:DataDirectory` 以实现卷持久化。界面使用自己的 Cookie 方案（`YarpUi.Auth`）进行认证，绝不会更改宿主的默认认证方案，因此可以安全地与应用现有的 JWT/Cookie 设置共存。

## 快速开始

```bash
dotnet run
```

打开 http://localhost:5080 并登录。默认凭据（请务必修改！）：

| 设置 | 值 |
| --- | --- |
| 用户名 | `admin` |
| 密码 | `yarp-admin` |

两者都在 `appsettings.json` 的 `YarpUi:Auth` 节下配置。

## Docker

预构建镜像发布在 [Docker Hub（`amrfswalha/yarp-ui`）](https://hub.docker.com/r/amrfswalha/yarp-ui)：

```bash
docker run -d -p 8090:8080 -v yarp-ui-data:/app/data amrfswalha/yarp-ui:latest
```

也可以使用解决方案附带的 `docker-compose.yml` 模板从源码构建：

```bash
docker compose up -d --build
```

界面随后在 **http://localhost:8090** 上提供。所有可变的配置都通过卷持久化在 `./docker-data` 中，因此可以在 `docker compose down` 之后保留：

| 文件 | 用途 |
| --- | --- |
| `docker-data/appsettings.json` | 凭据（`YarpUi:Auth`）和种子 `ReverseProxy` 配置 — 在宿主机上编辑，下次启动时生效 |
| `docker-data/yarp-ui.routes.json` | 每次从界面编辑器保存时自动写入 |
| `docker-data/yarp-ui-logs.db` | 请求日志数据库（SQLite）— 重启后保留，并按保留策略清理 |

在底层，容器会设置 `YarpUi__DataDirectory=/app/data` 并将卷挂载到那里；该目录中的 `appsettings.json` 会覆盖打包进镜像的那份（不用 Docker 时也一样 — 把 `YarpUi:DataDirectory` 指向任意位置即可）。手动构建镜像：在解决方案根目录执行 `docker build -t yarp-ui:0.2.1 .`。

## IIS

在 IIS 下托管可使用默认应用程序池标识（`ApplicationPoolIdentity`），它对站点文件夹拥有**只读**权限。启动时，YARP UI 会检测到内容根目录不可写，并将所有可变状态 — `yarp-ui-logs.db`、`yarp-ui.routes.json`、可选的数据目录 `appsettings.json` — 改存到 `%ProgramData%\YarpUi\<应用程序名>` 下，同时记录一条警告，让这次重定位可见。无需任何配置；代理正常启动，编辑器/日志页面基于该回退文件夹工作。

如果想把状态保存在自己选择的位置，可以将 `YarpUi:DataDirectory` 指向一个可写的文件夹，或者为应用程序池标识授予站点文件夹的写入权限：

```powershell
icacls "<site folder>" /grant "IIS AppPool\<YourAppPool>:(OI)(CI)(M)"
```

显式配置的 `YarpUi:DataDirectory` 永远不会被回退机制覆盖。回退位置本身可以通过 `YarpUi:FallbackDataDirectory` 重定向。

## 配置的工作方式

```
appsettings.json ("ReverseProxy" section)   ← hand-written seed
                │
                ▼  startup
   yarp-ui.routes.json (if present)         ← takes precedence once it exists
                │
                ▼
   InMemoryConfigProvider (live YARP config)
```

- 启动时，应用如果存在 `yarp-ui.routes.json` 就加载它；否则读取 `appsettings.json` 中的 `ReverseProxy` 节。
- 编辑器中的第一次**保存**会将完整配置写入 `yarp-ui.routes.json`。从那一刻起，该文件就是事实来源 — `appsettings.json` 保持不变。
- **重置为 appsettings.json**（编辑器左下角）会删除界面管理的文件并回到种子配置。
- 保存使用 YARP 自带的配置校验器进行校验；无效的配置会被拒绝，代理继续使用最后一个正常配置运行。

## 请求日志

只记录**已代理**的请求（界面/API 请求除外）。条目存储在 SQLite 数据库中（数据目录中的 `yarp-ui-logs.db`，与 `yarp-ui.routes.json` 相邻），并在重启后保留。每个条目记录方法、路径、状态码、耗时、YARP 选择的路由/集群/目标以及客户端 IP。旧版本创建的数据库会在首次启动时原地迁移。

**客户端 IP** 是前置代理提供 `X-Forwarded-For` 时其中最左边的条目，否则为直接连接地址。界面本身不安装 ForwardedHeaders 中间件 — 如果整个应用位于负载均衡器之后，该标头反映的是那个代理转发的内容。由于 `X-Forwarded-For` 可由调用方控制，请将记录的 IP 视为参考信息而非已验证的信息。

日志页面按**最新在前**显示条目，每一列都可排序。路由/集群/目标筛选器和时间范围选择器（最近 15 分钟 … 7 天、自定义范围或全部时间）通过 `GET /api/yarp/logs` 对**整个保留历史执行服务端搜索**，支持 `from`/`to`（Unix 毫秒）、`routeId`、`clusterId`、`destinationId`、`sort`、`desc` 和 `limit`（每次查询最多 1000 条）。不带搜索参数时，该端点保持其实时跟踪契约：`after=<seq>` 以从旧到新的顺序流出新条目。自由文本和状态类别筛选在已加载内容之上应用。

**保留策略**会在日志超过一定时间后自动删除它们：后台任务在启动时以及之后每小时运行。策略从日志页面工具栏管理（*保留日志：永久 / 1 / 7 / 30 / 90 / 365 天*），更改立即生效；初始默认值来自配置中的 `YarpUi:Logs:RetentionDays`（未设置时为 30 天）。你在界面中设置的策略存储在数据库本身中，并优先于配置值。

## 离线 / 无网络

所有 JavaScript 库（Cytoscape.js、dagre、cytoscape-dagre、Chart.js）都在 `wwwroot/lib/` 下本地打包。运行时不使用任何 CDN；界面可完全离线工作。

## 安全说明

- 管理界面需要登录（Cookie 认证）。**代理路由本身是公开的** — 这正是代理的意义所在。
- 凭据以明文形式保存在 `appsettings.json` 中，这对本地/内部工具来说没有问题。如果将此应用暴露到 localhost 之外，请置于 HTTPS 之后，使用强凭据，并考虑通过密码哈希或真正的身份提供者来扩展认证。
- 仅在受信任的网络中使用 HTTP；Cookie 未标记为 `Secure`，以便在开发期间也能在纯 HTTP 上工作。

## 许可证

版权所有 2026 YARP UI 作者。

依据 [Apache License, Version 2.0](LICENSE) 授权。这是 YARP UI 的社区版；高级版单独授权，并从其自己的仓库分发。

“YARP UI”和 YARP UI 徽标是项目商标；本许可证不授予使用它们营销衍生产品的权利。

捆绑的第三方库（YARP、Microsoft.Data.Sqlite、Cytoscape.js、dagre、cytoscape-dagre、Chart.js）均为 MIT 许可 — 详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
