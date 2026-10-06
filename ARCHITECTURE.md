# HelloLive Architecture

## 依赖边界

```text
HelloLive.Core
  ├─ Models
  │   ├─ LiveMonitorTarget
  │   ├─ LiveCheckResult
  │   └─ LiveMonitorOptions
  ├─ Sites
  │   ├─ ILivePlatformAdapter
  │   ├─ LivePlatformRegistry
  │   └─ KuaishouLiveAdapter
  ├─ Services
  │   ├─ Browser/ILiveBrowserService
  │   ├─ Monitoring/LiveMonitorCoordinator
  │   ├─ Monitoring/MonitorStore
  │   └─ Settings/SettingsService
  ├─ ViewModels/MainWindowViewModel
  └─ Views/MainWindow

HelloLive.Desktop -> HelloLive.Core
  ├─ Playwright/PlaywrightLiveBrowserService
  ├─ Chromium/PlaywrightChromiumInstaller
  ├─ App
  └─ Program
```

`Core` 不引用 `Microsoft.Playwright`。平台适配器只负责“识别平台 + 识别流 + 解析接口响应”，因此将来可以增加抖音、B站或其他直播平台，而不用改动调度器。

## 浏览器生命周期

```text
第一次需要检查
  └─ EnsureBrowserAsync
      ├─ 查找 Chromium
      ├─ 创建 IPlaywright
      ├─ 启动 1 个 Chromium
      └─ 创建 1 个 BrowserContext

检查作者 A
  └─ NewPageAsync
      ├─ Route 监听请求
      ├─ Response 监听 JSON
      ├─ 发现 FLV/HLS -> 保存 URL + Abort
      └─ CloseAsync(Page)

检查作者 B/C/D
  └─ 共享同一个 BrowserContext，分别使用临时 Page
```

Chromium 不按作者创建。最大并发由 `LiveMonitorCoordinator` 控制，默认 4。

## 平台扩展接口

### TryParseStreamRequest

用于真实媒体请求已经出现时快速抓取 URL。快手目前识别 HTTP-FLV 和 HLS。

### TryParseApiResponse

用于平台先通过 JSON API 返回播放地址、播放器稍后才发媒体请求的场景。浏览器层负责读取小型文本响应，平台层负责判断其中是否存在自己的直播 URL。

### TryCheckDirectAsync

默认返回 `null`，表示使用 Playwright。后续如果分析出稳定、公开、无需绕过访问控制的状态接口，可以在适配器中直接用 `HttpClient` 返回 `LiveCheckResult`，减少浏览器页面数量和资源开销。

## 持久化

Windows 默认采用便携模式：

```text
HelloLive.exe
settings.json
monitors.json
chromium/
```

macOS / Linux 默认写入用户应用数据目录，避免程序安装目录不可写。


## Remote 控制架构

```text
HelloLive.Desktop
  └─ RemoteApiHostService (Kestrel, LAN HTTP)
       ├─ /api/health
       ├─ /api/snapshot
       ├─ /api/actions/*
       └─ /api/monitors/*

HelloLive.Android ─┐
HelloLive.iOS     ─┼─> HelloLive.Core.Remote.RemoteApp
HelloLive.Browser ─┘        └─ RemoteLiveClient -> Desktop API
```

Android、iOS 与 Browser 均为纯远程控制器，不创建 Playwright、Chromium 或直播解析任务。桌面端是唯一的实际监控执行主机。


## Live Recording

```text
LiveMonitorCoordinator
  └─ live result
      └─ ILiveStreamRecorder
          ├─ HTTP-FLV -> direct streaming FileStream (.flv)
          └─ HLS      -> FFmpeg fragmented MP4 (.mp4)

Download/
  └─ <platform>/
      └─ <nickname>(<authorId>)/
          └─ yyyy-MM-dd HH-mm-ss.<ext>
```

录制连接与 Playwright 页面分离：Playwright 只负责获取当前签名流地址，随后立即关闭临时 Page；录制器使用独立连接持续落盘。这样不会为每个正在录制的作者保留一个 Chromium Page。
