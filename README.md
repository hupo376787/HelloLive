# HelloLive

HelloLive 是一个基于 **.NET 10 + Avalonia 12 + Microsoft.Playwright 1.61.0** 的跨平台直播状态监控工具。首版按 HelloCrab 的整体架构和桌面 UI 布局重新设计：左侧为浏览器/监控设置，中间为指标、当前任务和日志，右侧把原“历史列表”替换为“监控列表”。

当前已落地 **快手 + 抖音**。程序接受作者主页、直播页或平台分享短链，使用一个共享 Chromium 实例按需创建临时 Page；平台适配器可从 JSON 接口或真实媒体请求中解析 FLV/HLS，拿到直播地址后关闭 Page，再由独立录像器持续写盘。这样可以避免“一个作者一个常驻浏览器页面”带来的内存、CPU 和带宽浪费。

## 当前能力

- 一个 Chromium 实例，全局复用。
- 每个检查临时创建一个 Page，检查结束立即关闭。
- 最大并发 Page 默认 `4`，可在 UI 中设置 `1~8`，推荐 `3~5`。
- 可配置轮询间隔、单页检查超时、无头模式。
- 可阻止图片和字体加载，降低资源占用。
- 快手适配器支持：
  - `https://live.kuaishou.com/u/...`
  - `https://v.kuaishou.com/...` 分享短链
  - 正常重定向到的快手/晨钟移动页面
  - `yximgs.com` / `kwaicdn.com` 等直播 CDN 上的 HTTP-FLV / HLS 请求
  - JSON 接口中直接返回的 FLV/HLS URL
- 抖音适配器支持：
  - `https://live.douyin.com/{web_rid}` 直播页
  - `v.douyin.com` 等分享地址的重定向展开
  - `/webcast/room/web/enter` 返回的主播资料、开播状态和多档播放流
  - 优先选取 H.264 蓝光 HTTP-FLV，避免桌面页先请求 H.265 时误选首条媒体流
  - `douyincdn.com` / `smtcdns.net` 等直播 CDN 的 FLV/HLS 兜底识别
- 监控列表持久化到 `monitors.json`，设置保存到 `settings.json`。
- Windows / Linux / macOS 为实际监控主机；Android / iOS / Browser 已增加为远程遥控端，不运行 Playwright。

## 多平台扩展

平台逻辑统一通过 `ILivePlatformAdapter` 隔离。监控调度、Playwright 生命周期、并发限制和 UI 不直接依赖快手规则。

新增平台时，只需新增对应的 `ILivePlatformAdapter` 实现，例如抖音当前位于：

```text
Sites/Douyin/DouyinLiveAdapter.cs
```

并实现：

```csharp
bool CanHandleProfileUrl(string url);
string NormalizeProfileUrl(string url);
bool TryParseStreamRequest(...);
bool TryParseApiResponse(...);
Task<LiveCheckResult?> TryCheckDirectAsync(...);
```

其中 `TryCheckDirectAsync` 专门为未来从 Playwright 迁移到更轻量的 `HttpClient` 直播状态接口预留。如果平台公开接口可以稳定返回开播状态/播放地址，可优先直接查询，浏览器只作为回退方案。

## 资源策略

默认执行流程：

```text
监控列表
   ↓
LiveMonitorCoordinator
   ↓  最大并发 4
一个共享 Chromium / BrowserContext
   ↓
临时 Page 1 / Page 2 / Page 3 / Page 4
   ↓
监听页面请求与 JSON 响应
   ↓
发现 FLV/HLS
   ↓
保存签名 URL → Abort 媒体请求 → Close Page
```

因此 HelloLive **不会持续播放或解码监控对象的直播视频**。抓到播放地址后立即阻止媒体数据继续下载。

## 构建

```bash
dotnet restore HelloLive.sln
dotnet build HelloLive.sln -c Release
```

运行桌面程序：

```bash
dotnet run --project src/HelloLive.Desktop/HelloLive.Desktop.csproj
```

首次使用点击界面中的“安装 / 更新 Chromium”。安装器会调用当前 `Microsoft.Playwright 1.61.0` 自带的安装入口，并优先把 Chromium 放到程序目录的 `chromium/` 中。

## 使用边界

HelloLive 的首版实现只分析普通公开网页在正常浏览器访问过程中产生的请求，不包含：

- 绕过账号登录；
- 绕过验证码；
- 绕过付费、私密或其他访问控制；
- 批量破解或伪造平台鉴权签名。

直播 CDN URL 通常包含时效签名，因此监控列表里保存的流地址只代表**当前检查时刻**抓到的地址，过期后需要重新检查。


## 远程控制端

HelloLive 现在包含 3 个只用于遥控桌面主机的客户端：

```text
HelloLive.Android
HelloLive.iOS
HelloLive.Browser
```

桌面端设置区可开启“远程控制服务器”，默认端口为 `5088`，并显示局域网访问地址和随机访问令牌。远程端连接后可以：

- 查看监控统计、当前任务、桌面端日志和监控列表；
- 开始/停止周期监控；
- 立即检查全部或单个监控对象；
- 添加、启用/停用、移除监控对象。

Android / iOS / Browser 项目不引用 Microsoft.Playwright，也不会在客户端启动 Chromium；所有监控任务始终在桌面 HelloLive 主机执行。


## 实时直播录像

桌面端在“开始监控”后会使用程序目录下的 `Download` 作为录像根目录，并沿用 HelloCrab 的目录命名规则：

```text
Download/
└─ kuaishou/
   └─ 作者昵称(作者ID)/
      └─ 2026-10-06 08-18-30.flv
```

- 平台目录使用 HelloCrab 同样的规范名称（例如 `kuaishou`、后续 `douyin`）。
- 作者目录格式为 `昵称(作者ID)`；昵称变化时优先复用以相同完整作者 ID 结尾的已有目录。
- HTTP-FLV 直播不经过转码，直接顺序写入 `.flv`，每约 2 秒主动 flush，并使用 WriteThrough 降低异常退出时的数据损失。FLV 不依赖文件结束时写入 MP4 的 moov 索引，因此软件异常退出时，已经完整落盘的前部内容仍可播放。
- HLS 流在存在 FFmpeg 时保存为 fragmented MP4（`frag_keyframe + empty_moov + default_base_moof`），避免普通 MP4 在异常退出时因缺失最终 moov 而整体无法打开。
- 正常点击“停止监控”或退出程序时，会停止当前录像并正常关闭输出。
