# Lifecycle and recovery

## 原生工作與觀察工作

| 觀察 | 可判定 | 必須保留的界線 |
| --- | --- | --- |
| 工具明確拒絕且證實未派送 | 本請求未啟動原生工作 | 仍核對是否已有先前 job；不要把不同請求混為一談 |
| accepted／job handle 已返回 | 請求已受理 | 未證明案例已開始或成功 |
| 傳輸 timeout／connection lost | 觀察通道失敗 | 原生工作可能仍執行；沿原身分查證 |
| bridge 有回應、Editor state 已陳舊 | bridge 可服務，但畫面／主執行緒狀態未知 | 不據此宣稱崩潰、idle 或已完成 |
| 原生 terminal＋結果身分匹配 | 該 run 已終結 | 另核對清理及缺失的案例 |

保存 Editor instance、MCP job、可用的 native run／request 身分、測試選擇與候選。若工具未暴露 native ID，明說關聯限制，以現有可驗證資訊縮小歧義；不要自己構造。
結果未知時先查原 job／原生 runner／相關原始回執，依現有唯讀能力執行。不要用較新 job 的 PASS 補簽較舊未知工作，也不要讓兩個 runtime owner 競爭。
只有舊工作確認終止或未啟動後才考慮重跑；將新 run 連到原回執及具體理由。取消、重啟、改 transport 或改權限須有當前授權，不能由 timeout 推導。`clear_stuck` 只會處理其實作涵蓋的 tracker 狀態；先證實原生工作／cleanup 狀態與適用授權，不以清掉 job 當成取消完成。

## Refresh 與回執判讀

把 `external_changes_dirty`、Editor ready、compile 完成及 transport 可用分開判讀。核對已安裝實作對 `if_dirty` 的語義；不要僅由參數名稱推定它會跳過 refresh。refresh 已受理卻等待逾時時，先追本次匯入／編譯的完成證據，不重送也不盲清 dirty；刷新期間的新修改不能由較舊完成回覆一併確認。
按工具實際 schema 讀取巢狀 `result`／`success`／`error`；外層 `isError=false` 不保證內層操作成功。保存原始 envelope，明列請求、原生工作與結果交付的各自狀態。

## 已安裝修復版 MCP 的欄位契約

本節僅適用於已安裝實作與實際 schema 確認提供下列欄位及語義的版本，不將修復版行為套到其他版本。
缺少欄位時回到前述有界查證流程；不補造 native 身分，也不假設舊版 UTF 提供新版 activity API。

- **版本對齊：**修復部署的 UPM package 與 Python server 應對應同一 immutable commit SHA。
  核對兩端實際載入來源／產物；數字版本標籤相同不足以證明修復已一致安裝。
- **Operation 續查：**同一 `operation_id` 僅查詢原 operation，不再次發起 mutation。
  已完成時讀取其完成回執；不得因等待逾時換新 ID 重做相同工作。
- **Dirty ACK：**只確認該 operation 捕捉且成功處理的 dirty generation；後續新增的 dirty generation 必須保留。
  依已驗證的 `if_dirty`／完成回執判斷是否還需匯入，不將每個 batch 固定改成 force refresh。
- **Preflight：**failure 或 pending 均不等於前提成立；外層呼叫成功不能將它們升格為可啟動或已完成。
- **Job 查詢：**區分 `get_test_job` 查詢 deadline 與原生工作期限；保留回覆的 stale 標記、最後實際 status 及可用觀測時間。
  stale 回覆中的最後狀態仍是舊觀察，不以查詢逾限推導原生成功、失敗、取消或停止。
- **原生清理：**保留 `native_cleanup_pending` 與 `native_ownership_retained`；任一為 true 時，不以 tracker terminal 放行新 mutation。
  沿原工作確認 cleanup 與擁有權是否已解除；欄位缺失不能推定為 false。
- **未儲存場景：**`unsaved_scene` 是需處理的前提阻塞，保留其原因及相關場景資訊。
  只採已授權的保存／關閉流程；不得自動捨棄未儲存內容，再依需要重查前提。
- **非冪等送達：**launch／clear 等非冪等呼叫遇 timeout、斷線或送達不明時，不由 transport 自動 replay。
- **實際入口：**raw／legacy harness 可能繞過公開 wrapper 的 preflight、等待或重試保護。
  結果須標明實際入口及繞過範圍；底層 harness PASS 不代簽公開工具契約。

## Timeout 與 reload

分別觀測 request admission、native preparation、first test、case execution、cleanup、terminal receipt。初始期限涵蓋的階段依實作確認，不把增大期限描述為執行速度改善。
使用已量測的成本與任務等待界線，保留有界等待和通訊；沒有可用測量時揭露不確定性。不要將某專案的秒數或分鐘數寫成通用政策。
若 callback 在 reload 後遺失，檢查重註冊與 job 恢復，而非直接重啟測試。官方 Test Framework callbacks 可收到其他 run 的事件；必須關聯目前工作，避免任意 `RunFinished` 完成錯誤 job。
檢查已安裝 UTF 的 API；不要把新版本的 native activity API 假定存在於舊版。

## 驗證實際 runner 契約

建立一份小型可重用 probe，識別 Unity／UTF 版本、EditMode／PlayMode、相關 run flags、reload 設定，以及 harness 的生命週期契約版本。
沒有該契約的有效 probe，或上述契約有變更時才執行；一般產品邏輯或無關 harness 修改不要求重跑。
透過正式使用的 test runner 啟動 probe；只在普通 C# 呼叫端手動 `MoveNext()` 無法證明 runner 的行為。

明列證據強度：官方契約說明支援的介面；受控 catch／no-Dispose 模型加真 fixture／teardown，證明模型所涵蓋的清理；真正故意失敗的 NUnit case 由實際 runner 驅動，才證明該失敗路徑的端到端排程。前一層不得替後一層簽署。
若現有證據只有受控模型，保存有效的有限結論，將真實 runner failure、取消、進程中斷等未驗分支列出。不要為補齊表格而反覆注入所有故障；僅針對目前依賴且缺乏有效證據的契約補驗。

至少用最小案例覆蓋本 harness 實際依賴的路徑：

- **巢狀 iterator 失敗**：外層持有資源，內層 throw；記錄內外 `finally`／`Dispose` 及 teardown 是否執行。可用 no-Dispose 模型驗證 cleanup owner 的容錯，但另標實際 runner 是否已證明。
- **部分 construction**：第一項資源已建立，第二項取得前或取得後失敗；驗證已取得資源已登記、未取得資源不誤刪。
- **非同步卸載**：讓卸載尚未完成的狀態可被觀測，驗證在 `isDone` 與必要場景後置條件成立前，不產生 `ExitPlayMode` 或釋放所需執行環境。
- **原始錯誤保留**：在 primary failure 後觸發 cleanup failure，驗證兩者可辨識，整體仍失敗，沒有吞錯或以 cleanup PASS 覆蓋。
- **跨 reload 恢復**：僅當 harness 支援此路徑時，驗證重載後仍找得到正確的資源身分及清理擁有權。

保存事件順序與原始結果，將 probe 的結論限制在該契約。不要假定所有 UTF 版本、平台或 run mode 的 teardown／iterator 行為相同。

## Cleanup 擁有權

以唯一 owner 管理取得與釋放；取得一項即登記，避免 constructor 完成後才登記而漏掉半成品。
為自有資源保存足以重新定位的識別；跨 reload 不依賴舊 managed object reference。只清理該 job 擁有的資源，避免以廣泛場景或名稱搜尋刪除使用者物件。
跨 reload／新實例重新解析目前物件及其 owner／Descriptor／instance ID，保留舊 ID 僅供證據關聯。若來源是 Avatar Descriptor，每次綁定從實際 Descriptor 取得 owner 身分，不沿用先前的數字。
讓正常與失敗路徑使用相同清理契約，並使重入／重複收尾可辨識。不要在尚有原生工作持有資源時提前清理。
`finally`／`Dispose()` 只能證明同步控制流程；非同步 scene unload 必須由 runner 能繼續驅動的路徑等待。
若使用 `UnityTearDown`，以 probe 證明它在實際失敗路徑的行為及與 `ExitPlayMode` 的先後；不能把屬性名稱當作保證。
分列 test terminal、cleanup terminal 與原環境 restoration；任何一項未知都保持未知，不自動轉成 PASS。

## 版本查核來源

- [Test Framework 1.1 callbacks／IErrorCallbacks](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/manual/extension-get-test-results.html)：查重載後註冊、全域 callbacks 與啟動錯誤。
- [TestRunnerApi — 1.1](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/api/UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.html)：核對已安裝 1.1.33 的 API 與原生執行身分。
- [UnityTearDown — 1.1](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/api/UnityEngine.TestTools.UnityTearDownAttribute.html)：核對正式契約，再用本機實際 runner probe 驗證依賴的細節。
- [Domain Reloading — Unity 2022.3](https://docs.unity3d.com/2022.3/Documentation/Manual/DomainReloading.html)：核對 static／event 狀態及停用 reload 的語義。

把上游 PR（例如 [unity-mcp #1460](https://github.com/CoplayDev/unity-mcp/pull/1460)）視為待核對候選。PR 討論、作者測試或未合併 patch 不能證明修正已發布、已安裝，或已在 Windows／Unity 2022.3／UTF 1.1.33 上通過。先比對 merge／release／本機 bytes 與對應環境證據，再採用結論。
