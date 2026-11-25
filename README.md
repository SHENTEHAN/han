# 恭喜發財 InvoiceEye

拍發票就自動記帳＋對獎。手機 App 負責拍照與顯示，插著 Euresys Neo USB Dongle 的電腦當後端，用 **Open eVision** 辨識發票。

```
手機 App（PWA，加到主畫面就像 App）
   │  透過 Wi-Fi 上傳照片，收回 JSON
電腦後端（C# ASP.NET Core，插著 dongle）
   ├ Open eVision：EasyImage / EasyQRCode / EasyBarCode / EasyObject / EasyColor / EasyMatch / EasyOCR2
   ├ 分類、對獎、固定支出、報表
   └ SQLite 資料庫（data/invoiceeye.db）
```

## 功能

| 分頁 | 功能 |
|---|---|
| 掃描 | 拍照、從相簿匯入（可多張）、即時掃描（需 HTTPS）；辨識後顯示確認卡，可修改號碼、期別、金額與分類；沒有發票的支出可手動記一筆 |
| 帳本 | 每月收支明細，標示來源（發票／固定／手動／中獎），可刪除 |
| 對獎 | 輸入每期中獎號碼後自動對獎、顯示兌獎期限，中獎自動記成收入 |
| 固定支出 | 房租、月票、訂閱等，每月扣款日自動記帳，前 3 天內在掃描頁提醒 |
| 報表 | 各分類花費、預算進度（用到 80% 提醒）、每日支出 |

### 發票辨識流程（`src/InvoiceEye.Core/Vision/RecognitionPipeline.cs`）
1. **EasyQRCode** 讀左右 QR Code → 電子發票的號碼、日期、金額、品項
2. 讀不到改讀 **EasyBarCode** 一維條碼（Code 39，19 碼）→ 期別、號碼；有 QR Code 時用來交叉驗證
3. 都沒有 → **EasyObject** 找紙張外框，長寬比 ≥ 2.2 判斷為收銀機發票，否則為手開發票
   - **EasyColor** 找紅色預印號碼 → **EasyOCR2** 辨識，再依「2 英文＋8 數字」校正 0/O、1/I、8/B 等
   - 收銀機發票用 **EasyMatch** 找「總計」字樣，辨識右邊的金額；手開發票金額由使用者輸入

### 自動分類（`src/InvoiceEye.Core/Classification`）
依序：使用者之前選過的品項／店家 → 品名關鍵字 → 賣方統編查行業別 → 判斷不出來就讓使用者從選單選或自行輸入（並記住）。混合發票依品項拆開記帳。

## 在 Windows 上執行（正式使用 Open eVision）

1. 安裝 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 與 Open eVision（含 Neo License Manager），插上 Neo USB Dongle，確認授權有 EasyQRCode、EasyBarCode、EasyOCR2 等函式庫。
2. 找到 Open eVision 的 .NET 組件 `Open_eVision_NetApi.dll`（在 Open eVision 安裝目錄的 Bin64 之類的資料夾，舊版檔名可能帶版本號，例如 `Open_eVision_NetApi_2_11.dll`）。
3. 建置並執行：
   ```powershell
   cd src\InvoiceEye.Api
   dotnet run -p:OpenEVisionDll="C:\Program Files\Euresys\Open eVision\Bin64\Open_eVision_NetApi.dll"
   ```
   主控台出現「已啟用 Open eVision 影像引擎」就代表成功；如果初始化失敗（沒插 dongle、沒授權），會自動改用示範模式並印出原因。
4. 準備傳統發票用的檔案（OCR 模型、「總計」範本），說明見 `src/InvoiceEye.Api/vision/README.md`。

### 手機連線
1. 手機和電腦連同一個 Wi-Fi（或電腦連手機熱點）。
2. 查電腦的區網 IP（`ipconfig`），手機瀏覽器打開 `http://<電腦IP>:5080`。
3. 第一次執行時，Windows 防火牆若詢問，請允許「私人網路」。
4. 用瀏覽器選單「加到主畫面」，之後就像 App 一樣打開。

### iPhone
1. 用 **Safari** 打開 `http://<電腦IP>:5080`。
2. 點下方的「分享」按鈕 →「加入主畫面」→「加入」，主畫面就會出現「恭喜發財」圖示。
3. 「拍照」會直接開啟相機；「從相簿匯入」選 HEIC 照片也可以，App 會先轉成 JPEG 再上傳。
4. 即時掃描需要 HTTPS（見下一節）。iPhone 上最簡單的做法是用 Cloudflare 的免費通道取得正式憑證的網址：
   ```powershell
   cloudflared tunnel --url http://localhost:5080
   ```
   會得到一個 `https://xxxx.trycloudflare.com` 網址，iPhone 用這個網址打開即可使用鏡頭；手機用行動網路也連得到。
   注意：這個網址任何人都打得開，而 App 沒有登入功能，只在展示時開啟，用完就關掉（Ctrl+C）。

### 即時掃描（需要 HTTPS）
手機瀏覽器只允許 HTTPS 網頁使用鏡頭串流。執行 `dotnet dev-certs https` 後用 `dotnet run --launch-profile https`，手機改開 `https://<電腦IP>:5443`，出現憑證警告時選擇繼續。沒有 HTTPS 時「拍照」與「從相簿匯入」照常可用。

## 示範模式（沒有 Open eVision 也能跑）

不傳 `OpenEVisionDll` 直接 `dotnet run`，或在 `appsettings.json` 設 `"Vision": { "Engine": "Mock" }`。這時任何照片都會產生一張範例電子發票（茶葉蛋、御飯糰、鮮奶共 85 元），用來展示記帳、分類、對獎流程；畫面右上角會顯示「示範模式」。同一張照片會得到同一個號碼，可以測試重複發票檢查。

## 其他設定
- `Database:Path`：資料庫檔案位置（預設 `data/invoiceeye.db`）。
- `Vision:OpenEVision:Topology`：EasyOCR2 的字元規則，依你訓練的模型調整。
- 店家行業別：從政府資料開放平台下載財政部「全國營業(稅籍)登記資料集」CSV，用 `POST /api/registry/import`（multipart，欄位名 `file`）匯入，就能依賣方統編自動分類。

## API

| 方法 | 路徑 | 用途 |
|---|---|---|
| GET | `/api/status` | 影像引擎、今天日期 |
| POST | `/api/recognize` | 上傳照片（multipart 欄位 `photo`），回傳辨識結果與分類建議 |
| POST | `/api/invoices` | 儲存確認後的發票 |
| GET / POST / DELETE | `/api/transactions` | 帳本明細、手動記帳、刪除 |
| GET / PUT | `/api/categories` | 分類與預算 |
| GET / POST / PUT / DELETE | `/api/fixed-expenses` | 固定支出；`/upcoming` 為扣款提醒 |
| GET | `/api/lottery` | 所有發票的對獎結果 |
| GET / PUT | `/api/winning-numbers/{期別}` | 中獎號碼（期別例：`11512` = 115 年 11-12 月） |
| GET | `/api/report?month=2026-11` | 月報表 |
| POST | `/api/registry/import` | 匯入店家行業資料 |

## 專案結構
```
src/InvoiceEye.Core   發票解析、辨識流程、分類、對獎、固定支出、資料庫（不依賴 Open eVision，可測試）
src/InvoiceEye.Api    Web API、Open eVision 轉接層（OpenEVisionEngine.cs）、手機 App（wwwroot）
tests/InvoiceEye.Tests  單元與整合測試：dotnet test
```

## 目前狀態與限制
- 解析、辨識流程、分類、對獎、固定支出、帳本與報表都有自動測試（`dotnet test`，55 個測試）；手機介面在示範模式下跑過完整流程。
- **`OpenEVisionEngine.cs` 還沒有在真正的 Open eVision 上執行過**：開發環境沒有 SDK 和 dongle。它是依官方文件片段寫的，版本間名稱可能不同的呼叫用 `dynamic`／反射處理，名稱不符時只會在主控台記錄錯誤並改走備用做法，不會讓程式編譯失敗。第一次在 Windows 執行時，請看主控台訊息，依你安裝的版本調整。
- 照片轉正（EasyGauge 找紙張邊緣）尚未實作，目前只依照片的 EXIF 方向轉正，拍照時請盡量拍正。
- 中獎號碼需手動依財政部公告輸入；對獎與兌獎期限規則以財政部最新公告為準。
