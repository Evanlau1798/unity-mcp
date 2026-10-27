# Evidence and selection

## 讓證據指向實際執行候選

沿用現有紀錄，為每個 run 保存可比較的必要身分；不要掃描受保護或無關檔案。

| 身分 | 需要涵蓋 |
| --- | --- |
| Candidate | 基線與本輪 dirty／變更內容，以及受影響的產品依賴閉包 |
| Tests | 測試 source／assembly、選擇條件、實際 leaf 與斷言版本 |
| Harness | setup、reset、cleanup、時間步進、adapter／oracle 與 runner 契約 |
| Dependencies | Unity、UTF、SDK／MCP 及會影響執行的套件／生成資產 |
| Settings | mode、reload、編譯／物理／場景等與本次行為有關的設定 |
| Fixture／profile | 資源、owner、尺度、數量、步長、刺激與每組初始狀態 |
| Execution | Editor instance、MCP job／可用 native ID、實際載入 assembly／artifact 身分 |

保存必要值或可追溯 digest；只在相關輸入改變時更新。磁碟 source hash 不單獨證明 Unity 已載入該候選；用可用的 assembly／artifact 身分補上連結。
結果、審查及提交均對照此身分。原 FAIL 與 rerun PASS 各保留原回執、候選、run ID 與差異；不得覆寫歷史或把較新結果套到較舊工作。

可重用穩定的契約、helper、身分解析方法與依賴未失效的歷史 evidence；在相關操作邊界重新查證 live dirty、Editor readiness、Console errors 與目前 native job 狀態。歷史 terminal 回執可證明其精確舊 run，不可推出目前沒有新工作。
不要跨 reload／新實例保留 Unity object reference 或 owner／Descriptor／instance ID 作為當前目標。重新從實際實例解析並核對；舊數值只保留作追溯。
沿用現有 checkpoint、候選清單及 affected-tests 對照。用小型精確比對處理失效範圍，避免引入通用快取服務、自動依賴選擇器或第二套驗收系統。

## 失敗分類與有效 RED

| 分類 | 典型判斷 | 驗證動作 |
| --- | --- | --- |
| Fixture／前提 | 身分錯誤、缺少元件、初始狀態未建立 | 以最小真實接線案例修正前提，再執行目標行為 |
| Oracle／量測器 | API 回值無效、座標／單位錯誤、正對照也失敗 | 校準量測器並揭露能證明的範圍 |
| Tool／環境 | 編譯錯誤、傳輸 timeout、初始化或回執失敗 | 查原 job／native 狀態；產品結果可能未知 |
| Cleanup／harness | 原錯誤被蓋掉、卸載未完成、失敗後資源殘留 | 修復真正退出路徑並驗證清理契約 |
| Product | 有效前提下違反凍結的產品行為契約 | 保留目標 RED，最小修正後做必要 GREEN／回歸 |

分類是分析標籤，保留原始 failed／passed／skipped／inconclusive／unknown。原因未確定時保留多個假設，勿為滿足 TDD 將環境失敗稱為產品 RED。
cleanup 修復可有自己的有效 RED→GREEN；不要聲稱因此修復產品功能。更換 oracle 後重核需求覆蓋，避免較弱觀測被當成相同驗收。

## 用小 fixture 減少無效完整執行

在新整合之前，用最小相關 SDK／原生元件確認可疑 ownership、Contact／Grab 輸入、座標與單位、更新 cadence 或量測器。
選能直接反駁該前提的刺激與對照；不要以鏡像實作或理想化 adapter 自我證明。
前提成立後仍執行必要的完整產品矩陣，保留多部件、共享依賴及生命週期的整合風險。
依實測找出昂貴案例，先消除可在小 fixture 發現的失敗。局部序列化／byte 微基準即使提升數十倍，也不能推出完整案例或開發週期同比加速。

## 選測與失效範圍

| 變更 | 保留／補驗原則 |
| --- | --- |
| 無執行行為影響的文件變更 | 保留有效測試；按現有文件要求驗證 |
| 局部產品行為 | 目標 RED→GREEN、相關邊界與受影響呼叫者；依 gate 補整合 |
| 測試斷言／刺激／oracle 改變 | 舊結果不證明新判定；執行受影響 case／profile |
| 僅最終 teardown 加屏障 | 若 profile 邏輯與起點不變，可保留舊物理證據，補失敗與正常退出證據 |
| 共用 setup／profile 間 reset／cleanup 改變 | 分析跨 profile 污染；補代表性的前組退出→後組開始與必要完整範圍 |
| 保存／重匯入／重新載入或依賴改變 | 依影響重新建立產物身分，保留必要的新實例／reload gate |

使用 diff 與依賴證據支持選擇；「產品 source 未變」不能排除 harness 改變測試含義。
同一候選的有效證據可重用。新對話、新 checkpoint、reviewer 輪次或檔名含 final 都不構成重跑理由，也不構成成功證據。

## 合併與計數

將可在同一執行環境可靠重設的 profile 合併，為每組保存輸入、開始、完成與結果。清楚定義失敗後停止或繼續的條件，先完成該組清理。
若驗收目標是新實例、冷啟動、保存重載或故障恢復，保留必要邊界；不要用一次 warm run 代簽。
分開列 requested tests、discovered tests、實際執行 leaf、profile 與 skip；不要以 discovery 數當執行數。
去重統計真正不同的案例／剖面，另記重跑次數。單一 NUnit case 內的多組 profile 不當成多個 NUnit case。
確認 `runSynchronously` 等旗標沒有排除需多幀的 `UnityTest`／coroutine；較少執行不等於同範圍加速。

## 耗時與完成判斷

比較相同候選或明確可比的輸入、範圍與環境。保留端到端 wall time 及可取得的 compile／reload、case、cleanup、回執階段。
保留 NUnit root／leaf 原值；異常時標出矛盾，不擅自用某一值修正另一值，也不疊加已互相包含的區間。
以必要案例成本、重載次數、前提失敗 run 及因結果不明重跑的 run 排序優化。不要將有效產品 RED、必要 regression 或全部 failed run 都歸因於 MCP。
只報已驗證範圍。工具受理、Build 成功、Editor PASS 與完整產品／多人／客戶端完成分開；適用 gates 與需求門檻維持原值。

## 版本查核來源

- [TestRunnerApi — Test Framework 1.1](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/api/UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.html)：核對執行與篩選 API。
- [ExecutionSettings — Test Framework 1.1](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/api/UnityEditor.TestTools.TestRunner.Api.ExecutionSettings.html)：查實際 1.1.33 的 `runSynchronously` 行為與案例限制。
- [AssetDatabase batching — Unity 2022.3](https://docs.unity3d.com/2022.3/Documentation/Manual/AssetDatabaseBatching.html)：查批次匯入、配對結束與依賴界線。
- [RequestScriptCompilation — Unity 2022.3](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Compilation.CompilationPipeline.RequestScriptCompilation.html)：區分增量編譯要求、成功後 reload 與強制完整重編。
