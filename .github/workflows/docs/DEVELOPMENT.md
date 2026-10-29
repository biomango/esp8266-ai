# 開發文件

> 面向想改程式碼 / 自己折騰硬體的人，普通使用看倉庫根目錄的 [README](../README.md) 即可。

一個 ESP8266 WiFi 時鐘韌體：顯示時間 + Claude Code / Codex CLI 的實時工作狀態和用量。
不需要任何官方賬單 API key —— 資料來自兩處本地已有的來源：

- **工作狀態**（working/idle/offline）：本地會話日誌的新舊程度
  - `~/.claude/projects/**/*.jsonl`（Claude Code 會話記錄）
  - `~/.codex/sessions/**/*.jsonl`（Codex CLI 會話記錄）
- **真實額度**（5h / 周視窗用量百分比 + 重置時間）：複用兩個 CLI 已經存在本機的
  OAuth 登入憑據，直接調各自官方用量介面（做法與
  [CodexBar](https://github.com/steipete/CodexBar) 相同，token 只發給各自官方 API）：
  - Claude：`%USERPROFILE%\.claude\.credentials.json` → `api.anthropic.com/api/oauth/usage`
  - Codex：`%USERPROFILE%\.codex\auth.json` → `chatgpt.com/backend-api/wham/usage`

架構：`windows-app/` 是一個 **C# / .NET 8 WinForms 系統匣程式**（橋接程式），讀日誌、
開一個本地 HTTP 服務；`firmware/` 是 ESP8266 韌體，聯網後每 15 秒輪詢這個服務，把時間 +
狀態畫到 240x240 ST7789 彩屏上。桌寵動畫（GIF）的上傳和解碼**全部在 ESP8266 板子上完成**，
換形象不再需要電腦參與（詳見第 4 節）。

## 目錄結構

```
windows-app/  Windows 系統匣橋接程式 (C# / .NET 8 WinForms，見其 README)
firmware/     ESP8266 韌體 (PlatformIO + Arduino framework)，含板上 GIF 解碼
tools/        GIF -> RGB565 預設精靈圖標頭檔案的轉換指令碼（改編譯進韌體的預設動畫時用）
```

## 1. 跑起 Windows 橋接程式

構建與執行見 [`windows-app/README.md`](../windows-app/README.md)（需要 .NET 8 SDK，
也可以用倉庫裡的 GitHub Actions 工作流直接編譯出 exe）。

系統匣區會出現一個**復古麥金塔小電腦圖示**：

- **左鍵點選** → 彈出 ESP8266 螢幕的**實時映象**：橋接程式用與韌體完全相同的資料
  重繪同一個畫面（方形額度環 + 當前桌寵動畫 + logo + 額度文字），動畫幀直接從裝置
  `GET /sprite/<app>/raw` 拉取（裝置正在用什麼就播什麼，自定義/內建都一樣），
  working 時同步播放走路迴圈，隨裝置 2s/6s 切換同步換角色；底部附
  自動/Claude/Codex/網速/音樂 快速切換。
- **右鍵點選** → 控制選單：完整額度（5h/周 用量 + 重置倒計時）+ 裝置遙控：
  - **自動查詢並配對裝置**：一般不用手動——裝置本來就在輪詢本機的 `/status`，
    橋接程式記下來訪 IP 即完成發現（零掃描）；地址為空時自動配對，裝置 DHCP 換了 IP
    也會自愈。選單項走完整流程：最近來訪 IP → 已配置地址複驗 → 子網 /24 掃描兜底
    （覆蓋"剛配完 WiFi、還沒設過橋接"的全新裝置）。
  - **設定裝置地址…**：手動填時鐘的 IP（開機時螢幕會顯示；有自動配對後基本用不上）
  - **螢幕顯示**：自動（誰在幹活顯示誰）/ 固定 Claude / 固定 Codex / 網速 / 音樂 / 股票
  - **自動輪播**：自己勾選要輪播的頁面（Claude 額度 / Codex 額度 / 網速 / 音樂 / 股票），
    並設定每頁停留秒數（5/10/15/30/60/120 秒或自定義）；手動固定某一頁會暫停輪播
  - **設定自選股…**：逗號分隔的代碼，`tw` 台股（`tw2330`）、`us` 美股（`usAAPL`）、
    `cc` 加密貨幣（`ccBTC`、`ccETH`），裝置最多顯示 4 筆
  - **更換桌寵動畫…**：內建 [petdex.dev](https://petdex.dev) 畫廊（3300+ 開源桌寵），
    搜尋 → 選動畫（待機/跑步/揮手…9 種）→ 預覽 → 一鍵上傳到裝置
  - **恢復預設動畫**：刪掉自定義 GIF，回到韌體內建形象
  - **把本機設為裝置橋接**：一鍵把裝置的 Bridge host 指到這台電腦

驗證服務是否正常：

```powershell
curl.exe -s http://localhost:8765/status
```

已驗證返回示例（真實資料）：

```json
{
  "claude": {"status": "working", "tokens_today": 4868001, "session_min": 26, "session_window_min": 300},
  "codex":  {"status": "offline", "tokens_today": 61471, "primary_pct": 1.0, "primary_window_min": 300,
             "primary_reset_min": 0, "weekly_pct": 2.0, "weekly_window_min": 10080, "weekly_reset_min": 8729}
}
```

想要開機自啟：`Win+R` → `shell:startup` → 把 `AIClockBridge.exe` 的快捷方式放進去。

**注意**：HTTP 服務監聽 `0.0.0.0:8765`，同一區域網內的裝置都能讀到（只是狀態和 token
計數，不含任何 API key），如果在意，建議只在家庭可信網路下跑，或用 Windows 防火牆限制。

### 資料來源與侷限

- **額度（兩家都是真實值）**：橋接程式每 2 分鐘調一次官方用量介面（見開頭），拿到
  5h / 周視窗的已用百分比和重置時間，合併進 `/status` 下發給裝置。介面 429 限流時
  自動退避 5 分鐘並沿用上一次的數值。
- Claude 的 OAuth token 存在 `%USERPROFILE%\.claude\.credentials.json`，橋接程式只讀取
  其中的 access token，且只發給 `api.anthropic.com`。
- 若憑據缺失/過期，額度顯示"?"，工作狀態仍照常工作（來自日誌，不依賴網路）。

## 2. 燒錄 ESP8266 韌體

已確認的硬體：ESP8266EX（ESP-12S 模組）/ 4MB flash / CH340C 轉串列埠，裝置節點
`/dev/cu.usbserial-130`。這是拼多多"WiFi天氣時鐘 MG01"成品板，本質是 oshwhub 上
["SD2/小電視"開源方案](https://oshwhub.com/q21182889/sd2) 的量產版。

**接線是廠家固定的，一體成型無法重接**，網上能搜到的幾份"看起來像"的教程接線圖實測
都是錯的——真正正確的引腳來自該開源專案附帶的廠家參考韌體原始碼
（`TFT_eSPI/User_Setup.h` + `SmallDesktopDisplay.ino`），已在實機驗證點亮：

| 螢幕引腳 | 說明 | ESP-12S | GPIO |
|---|---|---|---|
| SCLK | SPI 時鐘 | D5 | GPIO14（硬體 SPI）|
| MOSI | SPI 資料 | D7 | GPIO13（硬體 SPI）|
| CS   | 片選 | D8 | GPIO15 |
| DC   | 資料/命令選擇 | D3 | GPIO0  |
| RESET| 復位 | D4 | GPIO2  |
| 背光 | LED 背光，**低電平點亮**，廠家韌體用 PWM 調光 | D1 | GPIO5  |
| VCC  | 電源 | 3V3 | - |
| GND  | 地 | GND | - |

驅動型號也有講究：要用 TFT_eSPI 的 `ST7789_2_DRIVER`（一個專門的簡化初始化變體），
用普通的 `ST7789_DRIVER` 配合正確引腳依然點不亮。`platformio.ini` 裡已經按這個組合
配置好了。

如果你買到的是完全不同的板子，改 `firmware/platformio.ini` 裡 `build_flags` 的
`TFT_*` 幾行即可；但如果就是這款"WiFi天氣時鐘 MG01"，直接用現在的配置就行，不用再猜。

```bash
cd firmware
python3 -m venv .pio-venv && source .pio-venv/bin/activate
pip install platformio
pio run -t upload          # 已驗證：編譯成功，Flash 45.7%，RAM 39.7%
```

燒錄後串列埠會列印除錯日誌（這版韌體不再是"靜默"的）：

```
[wifi] starting WiFiManager autoConnect...
*wm:AutoConnect
*wm:No wifi saved, skipping
*wm:StartAP with SSID:  AI-Clock-Setup
*wm:AP IP address: 192.168.4.1
```

首次開機（或 WiFi 配置丟失時）會開一個熱點 `AI-Clock-Setup`，手機連上後自動彈出配置頁
（或手動訪問 `192.168.4.1`），選擇你的 WiFi，並在 "Bridge host (ip:port)" 裡填這台電腦
的區域網 IP，例如 `192.168.1.23:8765`。儲存後裝置會記住配置（存在 LittleFS 裡），下次
開機自動連線。

檢視串列埠日誌：

```bash
pio device monitor -b 115200
```

## 3. 螢幕布局

全屏單應用檢視（不顯示時鐘），一次只顯示 Claude 或 Codex 其中一個，規則：

- **只有一方在工作** → 固定顯示正在工作的那個
- **兩方都在工作** → 每 2 秒交替
- **都空閒** → 每 6 秒慢速交替
- 系統匣選單裡也可以強制固定顯示某一方（`POST /api/display`），固定後忽略上述規則

視覺元素：

- 螢幕中央：對應角色的大幅畫素動畫（Claude = 跑步的 Dario，Codex = 戴耳機的寵物），
  僅在該角色 `working` 時播放動畫，否則停在靜止幀。
- 螢幕四周一圈方形進度環：環的填充長度 = 用量百分比（Claude 用 5 小時滾動視窗已用
  比例近似，Codex 用真實的 5h `primary_pct`）；環的顏色/動畫參考
  [vibecoding-signal-light](https://github.com/starlight36/vibecoding-signal-light)
  的紅綠燈設計：
  - **常亮綠** = 空閒/離線，不需要關注
  - **綠→黃→紅緩慢迴圈** = 正在工作
  - **紅色閃爍**（最高優先順序，覆蓋其他狀態）= 橋接服務連不上或資料過期（超過
    2 個輪詢週期沒更新），需要馬上看一眼
- **整圈邊框紅色閃爍 = 需要你確認操作**：Claude / Codex 彈出許可權/審批選擇時（Claude
  的 `Notification`、Codex 的 `PermissionRequest` hook），裝置整圈邊框紅色閃爍提醒你去
  確認；AUTO 模式還會自動切到那個待審批的角色（優先順序高於音樂自動切換）。你在 CLI 裡
  做出選擇後（下一個工具呼叫/回合事件到達）自動停止閃爍，5 分鐘無響應也會自動超時清除。
  映象視窗同步顯示紅色邊框閃爍。

## 4. 自定義桌寵形象

兩個入口，推薦系統匣選單（petdex 畫廊 + 預覽），裝置網頁作為兜底：

1. **系統匣選單 →「更換桌寵動畫…」**：從 [petdex.dev](https://petdex.dev) 的公開
   manifest（`assets.petdex.dev/manifests/petdex-v1.json`，3300+ 開源桌寵）搜尋選擇。
   每個桌寵是一張 1536x1872 的 WebP 精靈圖（8 列 x 9 行，每幀 192x208，每行一種動畫：
   待機/左右跑/揮手/跳躍/失敗/等待/原地跑/思考）。app 在本地裁出所選動畫行、縮放到
   目標插槽尺寸、合成黑底迴圈 GIF，然後 POST 到裝置的 `/sprite/claude|codex`。
2. **裝置網頁** `http://<裝置IP>/`：手動上傳任意 `.gif`，適合用自己的圖。

兩條路最終都走同一條鏈路：裝置收到 GIF 後**自己在板上解碼並縮放**，立刻替換該角色的
動畫，重啟後也記得，**不需要重新編譯或燒錄韌體**。

### 裝置 HTTP API（橋接程式用的就是這套）

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `/api/info` | 裝置狀態 JSON：ip/ssid/bridge/顯示模式/當前顯示/自定義精靈標記 |
| POST | `/api/display` | `mode=auto\|claude\|codex\|net\|music` 切換螢幕顯示（net=網速曲線頁，music=音樂播放頁）|
| POST | `/api/bridge` | `host=ip:port` 設定橋接地址 |
| POST | `/sprite/claude`、`/sprite/codex` | multipart 上傳 GIF 並板上解碼替換 |
| POST | `/sprite/claude/reset`、`/sprite/codex/reset` | 刪除自定義動畫，恢復內建形象 |
| GET | `/sprite/claude/raw`、`/sprite/codex/raw` | 當前生效動畫的原始幀流 `[1B幀數][RGB565大端幀...]`（映象視窗用）|

`/api/info` 裡的 `sprite_rev` 在每次上傳/重置動畫後自增，映象端據此決定是否重新拉幀。

## 5. 網速曲線頁（橋接程式 + 裝置同步顯示）

橋接程式每 250ms（4Hz）讀一次網絡卡位元組計數（只統計物理乙太網 / WiFi 網絡卡，
避免 VPN / 虛擬網絡卡重複計數），保留 3 分鐘歷史。

顯示模式切到「網速曲線」後，裝置端**渲染與網路完全解耦**：`GET /net` 返回帶全域性
序號（seq）的最近 12 個 250ms 樣本，裝置每 2 秒拉一次補進佇列（按 seq 去重），
渲染以固定 250ms/列的節奏消費佇列，HTTP 延遲不會體現在畫面上。

介面是工作管理員風格的**滾動填充面積圖**（224 列 x 128px，56 秒視窗，最新在右）：
下載 = 暗綠填充 + 亮綠頂邊，上傳 = 2px 黃線，背景 25/50/75% 暗格線；整圖共享一個
1/2/5 階梯的"整數量程"（10K/20K/50K/…），高度全圖可比，量程標籤顯示在圖外右上。
每幀從 224 個樣本的資料環逐行合成畫素、每行一次 `pushImage` 整行寫屏——沒有
"先清屏再畫"的過程，所以完全無閃爍。頂部大號 DL/UL 讀數取 1 秒平均，只在數值
變化時區域性重繪。映象視窗用同一套資料模型和佈局同步渲染。

## 6. 音樂播放頁（橋接程式 + 裝置同步顯示，自動切換）

**AUTO 模式下自動切換**：電腦一有音訊在播放（放歌、看影片都算），裝置自動切到音樂
頁；停止後自動切回桌寵/額度頁。機制：`/status` 帶 `music_playing` 欄位（狀態輪詢
5 秒一次 → 開始播放約 5 秒內切入），音樂頁顯示時靠 `/music` 的 2 秒輪詢快速檢測停止
（約 2 秒切回）。僅 AUTO 生效；手動固定某模式（含固定「音樂播放」）時不受影響。
`/api/info` 裡 `mode` 是配置的模式、`effective` 是當前實際渲染的模式，映象視窗按
`effective` 跟隨。


顯示模式切到「音樂播放」（彈窗底部分段控制元件 / 右鍵選單 / `POST /api/display mode=music`）
後，橋接程式透過系統 Now Playing 資訊讀取當前播放的歌曲、歌手、播放進度和專輯封面，
橋接服務提供：

- `GET /music`：歌曲、歌手、專輯、播放狀態、進度、封面版本號
- `GET /music/cover.raw`：128x128 RGB565 封面原始畫素，裝置逐行讀取後直接繪製

裝置每 2 秒重新整理一次音樂資訊；封面只有在版本號變化時重新拉取。映象視窗同樣顯示
音樂頁，方便不用看裝置也能確認佈局。

## 7. Hooks 實時狀態（秒級，參考 clawd-on-desk 的做法）

除了日誌 mtime 輪詢（保留為兜底），bridge 還接收兩個 CLI 官方 hooks 的事件推送，
狀態切換從"最多遲滯 20 秒"變成"毫秒級"：

- bridge 新增 `POST /event`，body：`{"agent":"claude|codex","event":"PreToolUse"}`
- `~/.claude/settings.json` 已註冊 8 個事件的 curl hook（UserPromptSubmit/PreToolUse/
  PostToolUse/Stop/SessionEnd/Notification/PreCompact/SubagentStop，每條 `-m 1` 超時，
  不會拖慢 Claude Code；與已有 hooks 共存，靠命令裡的 `8765/event` 標記冪等）
- 對映：UserPromptSubmit/Pre/PostToolUse 等 → working（TTL 10 分鐘，覆蓋長工具呼叫）；
  Stop/Notification 等 → idle（TTL 60 秒，只用來立刻壓掉 mtime 的"工作尾巴"）
- Codex 側已寫入 `~/.codex/hooks.json` + `config.toml [features] hooks = true`，
  但 Codex 要求在 TUI 裡跑一次 `/hooks` 信任新命令後才生效；未信任前走 mtime 兜底。
- 侷限：事件是全域性的不分會話——A 會話 Stop 會把還在幹活的 B 會話壓成 idle 最多 60 秒
  （B 的下一個工具呼叫事件會立刻翻回 working）。

### GIF 上傳架構

架構：GIF 透過 `ESP8266WebServer` 的 multipart 檔案上傳（`HTTPUpload` 回撥）邊收邊流式
寫進 LittleFS 的臨時檔案（`/c.gif` / `/x.gif`），然後韌體用
[AnimatedGIF](https://github.com/bitbank2/AnimatedGIF) 庫**逐行解碼**成裝置要的 RGB565
幀，寫入 `/c.bin` / `/x.bin`（格式 `[1位元組幀數][各幀畫素...]`），最後刪掉臨時 GIF。

ESP8266 總共只有 ~80KB RAM，一幀 120x120 的 RGB565 就 ~28KB，AnimatedGIF 自己也要
~24KB，兩個大緩衝塞不下，所以整條鏈路都是**逐行流式、不常駐整幀**：

- 上傳：multipart 分塊寫檔案，不把整個 body 攢進一個 `String`（那樣體積必炸記憶體）。
- 解碼：AnimatedGIF 逐行回撥，只用兩條「一行」緩衝把源行最近鄰縮放到目標尺寸，直接
  逐行寫進 `.bin`；不覆蓋到的區域用**上一幀**補齊（讀回剛寫進 `.bin` 的上一幀），
  這樣被最佳化器裁成小矩形的 GIF（disposal method 1）也能拼對。解碼期間才在堆上
  臨時 `new` 出 AnimatedGIF，用完就 `delete`。
- 顯示：每次也只把「當前要畫的一幀」從 LittleFS 逐行讀出來 `pushImage`，不整幀駐留記憶體。
- 沒有自定義素材時，退回韌體裡編譯好的預設動畫（`firmware/include/img/*.h`）。

**注意事項 / 侷限**：

- GIF 太大（尺寸很大、顏色/幀很多）可能因記憶體不足解碼失敗，頁面會報錯，換小一點的即可。
- 目標插槽尺寸固定：Claude 111x120、Codex 120x120，板上會最近鄰縮放匹配（質量不如
  PIL 的 LANCZOS，畫素風 GIF 通常沒問題）。
- 最多取 GIF 的**前 8 幀**（沒有整體幀數資訊，就不做均勻抽幀了）。
- disposal method 2（"恢復到背景色"）沒有單獨區分，未覆蓋畫素保留上一幀而不是清空；
  對迴圈角色動畫來說無所謂。
- WiFi 上傳大檔案偶爾會瞬時掉線（broken pipe 之類），失敗重新上傳一次即可。

## 已知限制 / TODO

- 參考專案裡的“黃色閃爍-需要處理”“紅色閃爍-需要批准”這類更細的狀態，目前本地日誌
  拿不到可靠訊號，沒有實現，只做了 working/idle-離線/橋接離線 三檔。
- 橋接程式無鑑權，HTTP `/status` 監聽 `0.0.0.0:8765`，僅建議在可信區域網使用；
  裝置的 HTTP API 同樣無鑑權。
- Claude 用量介面有較嚴格的服務端限流（429），app 已做 60s 節流 + 429 後 5 分鐘退避 +
  沿用舊值，偶爾選單裡額度會顯示為幾分鐘前的資料。
- 開機自啟需自己把快捷方式放進啟動資料夾（見第 1 節）。
- 改**預設**編譯進韌體的動畫（`firmware/include/img/claude_sprite.h` /
  `codex_sprite.h`）仍可用 `tools/convert_sprites.py` 生成新的 `.h` 後 `pio run -t upload`；
  日常換形象用系統匣選單的 petdex 選擇器或裝置網頁即可，無需燒錄。
