# Open eVision 用的檔案

把下面這些檔案放在這個資料夾，建置時會一起複製到執行目錄。檔名可以在 `appsettings.json` 的 `Vision:OpenEVision` 修改。

| 檔案 | 用途 | 怎麼做 |
|---|---|---|
| `invoice.o2m` | EasyOCR2 模型，辨識傳統發票的號碼、期別、金額 | 在 Open eVision Studio 開啟 EasyOCR2，載入幾張發票照片，標記字元後存成模型 |
| `total_label.png` | EasyMatch 範本：收銀機發票上的「總計」兩個字 | 從一張清楚、拍正的收銀機發票照片裁下「總計」字樣（只要字，不要數字） |
| `einvoice_title.png` | （選用）EasyMatch 範本：「電子發票證明聯」標題 | 從電子發票照片裁下標題 |

沒有這些檔案時，電子發票（QR Code、條碼）仍然可以辨識；傳統發票會跳過文字辨識，改由使用者輸入。
