# AIClockBridge for Windows

Windows 系統系統匣橋接程式：讀取 Claude Code / Codex CLI 的狀態與額度、網速、音樂播放、
自選行情，透過區域網提供給 ESP8266 小螢幕。

- **左鍵系統匣圖示** → ESP8266 螢幕實時映象（額度環 + 桌寵動畫 + 網速圖 + 音樂頁，
  與裝置渲染同一份資料），底部附 自動/Claude/Codex/網速/音樂 快速切換
- **右鍵系統匣圖示** → 控制選單：Claude/Codex 完整額度（5h/周 + 重置倒計時）、
  自動查詢並配對裝置、設定裝置地址、螢幕顯示模式、**自動輪播**、**設定自選股**、
  petdex 桌寵畫廊、恢復預設動畫、把本機設為裝置橋接、橋接服務地址
- **自動輪播**：勾選要輪播的頁面（Claude 額度 / Codex 額度 / 網速 / 音樂 / 股票），
  設定每頁停留秒數；子選單第一行顯示目前狀態。手動固定某一頁會暫停輪播
- **自選行情**：`tw2330`（台股，上市/上櫃自動判斷，資料來自證交所 MIS）、
  `usAAPL`（美股，騰訊行情）、`ccBTC`（加密貨幣，幣安公開 24h 行情，對 USDT 交易對）
- 本地 HTTP 服務 `0.0.0.0:8765`：`/status`、`/net`、`/music`、`/stock`、`/music/cover.raw`、
  `/music/text.raw`、`POST /event`（Claude Code / Codex hooks 秒級狀態推送）
- 資料來源：`%USERPROFILE%\.claude\projects` / `%USERPROFILE%\.codex\sessions`
  的 JSONL 日誌 + 各自官方用量介面（憑據讀
  `%USERPROFILE%\.claude\.credentials.json` 和 `%USERPROFILE%\.codex\auth.json`，
  token 只發給各自官方 API）
- 音樂頁讀系統級 Now Playing（WinRT `GlobalSystemMediaTransportControlsSessionManager`，
  Spotify / 瀏覽器 / 本地播放器都能識別）；網速取物理網絡卡（乙太網/WiFi）位元組計數，
  4Hz 取樣，排除 VPN/虛擬網絡卡
- 無韌體刷寫入口（刷寫請用網頁版刷寫工具）
- 唯一的第三方依賴是 [ImageSharp](https://github.com/SixLabors/ImageSharp)——
  System.Drawing 解不了 petdex 的 WebP 精靈圖、也編不了多幀 GIF

## 構建 / 執行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 10
19041+ / Windows 11）：

```powershell
cd windows-app\AIClockBridge
dotnet run                # 前景執行（系統匣出現小電腦圖示）
# 或釋出單檔案（目標電腦不需要另外裝 .NET）：
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# 產物在 bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AIClockBridge.exe
```

沒有本機 .NET 環境時，也可以把倉庫推到 GitHub，用 `.github/workflows/build-windows.yml`
在雲端編譯，從 Actions 的 Artifacts 下載 exe。

首次啟動 Windows 會彈防火牆授權（HTTP 服務監聽 0.0.0.0:8765，裝置要從區域網訪問，
選"允許"）。

**開機自啟**：`Win+R` → `shell:startup` → 把 `AIClockBridge.exe` 的快捷方式放進去。

**Hooks 實時狀態**（可選，同主 README §7）：Claude Code / Codex 的 hooks 往
`http://127.0.0.1:8765/event` POST 事件即可，Windows 下 curl 自帶。

## 驗證

```powershell
curl.exe -s http://localhost:8765/status
```

配置持久化在 `%APPDATA%\AIClockBridge\settings.json`（裝置地址、自選股、輪播設定等）。

## 程式碼結構

| 檔案 | 說明 |
|---|---|
| `Program.cs` | 入口 + 路由表 + 被動發現 |
| `TrayAppContext.cs` | 系統匣圖示 + 控制選單 |
| `AutoRotation.cs` | 自動輪播（頁面勾選 + 間隔設定） |
| `MirrorForm.cs` | 240x240 螢幕映象彈窗 |
| `PetPickerForm.cs` | petdex 桌寵選擇器 |
| `PetdexService.cs` | manifest / 精靈圖 / GIF 合成 |
| `StatusService.cs` | JSONL 日誌掃描 + hook 事件 |
| `UsageFetcher.cs` | 官方額度介面 |
| `StockMonitor.cs` | 台股 / 美股 / 加密貨幣行情 |
| `NetSpeedMonitor.cs` | 4Hz 網速取樣環 |
| `NowPlayingMonitor.cs` | 系統 Now Playing + 封面/文字條 RGB565 |
| `DeviceClient.cs` | 裝置 HTTP API + 自動配對/子網掃描 |
| `MiniHttpServer.cs` | 0.0.0.0:8765 極簡 HTTP 服務 |
| `Rgb565.cs` | RGB565 大端編解碼 |
