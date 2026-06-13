# M3U8 Downloader / M3U8 下载器

A lightweight Windows desktop application for downloading M3U8 (HLS) video streams and converting them to MP4.

一款轻量级 Windows 桌面应用，用于下载 M3U8（HLS）视频流并转换为 MP4 格式。

![.NET](https://img.shields.io/badge/.NET%2010-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-purple?logo=windows)
![FFmpeg](https://img.shields.io/badge/FFmpeg-007808?logo=ffmpeg&logoColor=white)

---

## ✨ Features / 功能特性

- **M3U8 Playlist Parsing** — Supports both Master and Media playlists with automatic quality selection.  
  **M3U8 播放列表解析** — 支持 Master 和 Media 播放列表，自动选择最佳画质。

- **AES-128/256 Decryption** — Built-in AES-CBC and AES-CTR decryption for encrypted streams.  
  **AES-128/256 解密** — 内置 AES-CBC 和 AES-CTR 解密，支持加密视频流。

- **Concurrent Downloads** — Configurable concurrent segment downloads (default: 8) and task downloads (default: 1).  
  **并发下载** — 可配置的并发片段下载数（默认 8）和任务并发数（默认 1）。

- **Independent Merge Queue** — Separate download and merge queues; tasks can download while others are merging.  
  **独立拼合队列** — 下载队列与拼合队列相互独立，拼合时不影响其他任务下载。

- **FFmpeg Integration** — Automatic FFmpeg download on first launch, GPU encoding enabled by default (NVENC/AMF/QSV).  
  **FFmpeg 集成** — 首次启动自动下载 FFmpeg，默认启用 GPU 编码（NVENC/AMF/QSV）。

- **Post-Download Merge & Transcode** — Merges downloaded segments and transcodes to MP4 (x264/AAC) via FFmpeg.  
  **下载后拼合与转码** — 通过 FFmpeg 将片段合并并转码为 MP4（x264/AAC）。

- **Custom HTTP Headers** — Set custom User-Agent, Referer, and Cookies per task or globally.  
  **自定义 HTTP 请求头** — 支持为每个任务或全局设置自定义 User-Agent、Referer 和 Cookie。

- **Proxy Support** — HTTP, SOCKS4, SOCKS5 proxy configuration.  
  **代理支持** — 支持 HTTP、SOCKS4、SOCKS5 代理配置。

- **Per-Segment Retry** — Retry individual failed segments without restarting the entire task.  
  **逐片段重试** — 可单独重试失败的片段，无需重新开始整个任务。

- **Speed Limit** — Optional download speed throttling (KB/s).  
  **速度限制** — 可选的下载速度限制（KB/s）。

---

## 📋 Requirements / 系统要求

- Windows 10 or later / Windows 10 及以上
- .NET 10 Runtime (SDK required for building)  
  .NET 10 运行时（构建需要 SDK）

---

## 🚀 Getting Started / 快速开始

### Build / 构建

```bash
# Clone the repository / 克隆仓库
git clone https://github.com/fodangithub/m3u8-downloader.git
cd m3u8-downloader

# Restore dependencies / 恢复依赖
dotnet restore

# Build / 构建
dotnet build --configuration Release

# Run / 运行
dotnet run --project M3U8Downloader.csproj
```

### Publish (Standalone) / 发布（独立应用）

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

---

## 📖 Usage / 使用说明

1. **Add a Task / 添加任务** — Paste an M3U8 URL into the "Add URL" input and click "Add".  
   在"添加 URL"输入框中粘贴 M3U8 链接并点击"添加"。

2. **Configure / 配置** — Open Settings to adjust concurrency, proxy, FFmpeg path, or HTTP headers.  
   打开"设置"可调整并发数、代理、FFmpeg 路径或 HTTP 请求头。

3. **Monitor / 监控** — Click "View" on a task to open the detail page with per-segment progress.  
   点击任务的"查看"按钮打开详情页，查看每个片段的下载进度。

4. **Retry / 重试** — Failed segments can be retried individually or all at once.  
   失败的片段可单独重试或全部重试。

5. **Wait for Merge / 等待拼合** — After download completes, FFmpeg automatically merges and transcodes to MP4.  
   下载完成后，FFmpeg 自动将片段合并并转码为 MP4。

---

## 🏗️ Architecture / 架构

| Directory / 目录     | Description / 描述                                      |
|----------------------|---------------------------------------------------------|
| `Models/`            | Data models, settings, enums                            |
| `Views/`             | WPF windows and user controls                           |
| `ViewModels/`        | MVVM view models (CommunityToolkit.Mvvm)                |
| `Services/`          | Core services: parser, download engine, FFmpeg, merge   |
| `Helpers/`           | Utility functions (file naming, constants)              |
| `Converters/`        | WPF value converters                                    |

---

## 📝 License / 许可证

MIT License

---

## 🙏 Credits / 致谢

- [FFmpeg](https://ffmpeg.org/) — Video and audio processing  
  视频和音频处理
- [BtbN FFmpeg Builds](https://github.com/BtbN/FFmpeg-Builds) — Pre-built FFmpeg binaries  
  FFmpeg 预编译版本
