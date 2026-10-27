# Unity validation procedure

使用已安裝 MCP／Unity／UTF 的實際介面，保留專案 gate、數值門檻與真機界線。只增加能改變當前判斷的驗證。

## 先選工作與閱讀範圍

純文件、回顧或 skill 維護不啟動 Unity preflight、refresh、tests、Build 或 Goal。需要 runtime 時才讀相應狀態與工具；已讀且身分未變的規範直接重用。

| 本次需要解決的問題 | 按需讀取 |
| --- | --- |
| 候選比對、選測、前提／量測失敗、驗收語意、耗時 | [Evidence and selection](evidence-and-selection.md) |
| timeout、斷線、reload、原生 ownership、清理或 harness 生命週期 | [Lifecycle and recovery](lifecycle-and-recovery.md) |
| 工具參數、場景／腳本操作、版本相容性 | [工具索引](tools-reference.md) 與實際工具 schema |

## 以完整行為範圍安排一輪工作

- 沿用現有 checkpoint，先定義本輪可接受的行為、影響的 caller／邊界及退出條件；相關缺口一起處理，不把每個新測試或 finding 都拆成獨立結案。
- 新案例先用四項事實對齊：**前提、刺激、觀測相位、不變式**。核對初始化、owner、SDK API、座標／單位與刺激強度；先用最小真實正負對照建立量測有效性，再擴大矩陣。簡化 fixture 不能代簽必要整合。
- 重複沒有取得指定 witness 時，先檢查相位、可達性與需求語意；提出能區分原因的下一個對照。不能繼續相同刺激等到綠，也不能把未觀察到說成永遠不可達。更改接受契約須有適用人類決策，原樣本與未驗分支保留。
- 只有有效前提下違反產品契約才是產品 RED。編譯、tool、fixture、oracle、cleanup 與產品問題分列，原始 FAIL／unknown 不改寫。行為修正依專案 TDD；文件或保持行為的重構不製造假 RED。
- 根據變更影響選最小重現、相關正負向與必要回歸；候選穩定後才跑既定廣泛 gate。識別 source、test、harness、依賴、設定、fixture／profile 及執行產物；新 commit、reviewer 或對話不自動使有效證據失效。

## 一位 Unity owner，必要工作啟動一次

1. 核對 exact project／Editor instance、已安裝版本、狀態觀測時間及 compile／reload／test／build。查既有工作與身分，再決定是否能開始。原場景、使用者資產與 dirty 所有權按影響保護；不要每輪重建全專案紀錄。
2. 同一 Editor／AssetDatabase／SDK job 只由一位 mutation owner 操作。其他代理只做有明確輸入／輸出／allowlist 的獨立離線工作；**所有代理 Fresh、不得 Fork 或繼承主 Agent 上下文**，啟動參數依實際 dispatch schema 明確關閉繼承。
3. 沒有活動 job 時合併相容編輯，再做必要匯入／編譯。編輯工具已啟動 import 時沿原工作等待；不重複 refresh。批次 RPC 不代表 AssetDatabase 批次匯入；不為加速關閉必要 reload、改全域物理或增加掩蓋等待。
4. 啟動前以可重用 discovery 核對精確案例與選擇語意；不以跑全 assembly 來找案例。保留必要冷啟動、新實例、保存／reload 與故障恢復邊界；不能用 `runSynchronously` 等旗標省略 coroutine 案例。
5. 啟動一次並保存 request／operation／job／native ID 中實際提供的身分，以及候選與選測條件。區分受理、原生開始、原生終態；不補造 ID，外層成功不代替內層結果。
6. timeout／斷線／stale 只表示觀察限制。沿原 ID 有界等待，無變化時退避；優先摘要，診斷或結案才取需要的 details／原始輸出。新 run 需先證明舊工作已終結或未啟動，並記具體重跑理由；不擅自取消、重啟或清 tracker。
7. 原生終態後核對 cleanup、ownership 與必要環境還原，再釋放 owner。任一未知保持未知；不要在清理仍進行時啟動下一個 mutation。

## 有效證據與必要審查

- 重用相符的 runner／cleanup 契約；只在缺證據或相關 Unity／UTF／mode／reload／harness 契約改變時補 probe。保留原始失敗與清理失敗，非同步卸載要觀察完成；完整細節按需讀 lifecycle。
- 可可靠重設的 profiles 共用昂貴 setup／Play session，各自保存輸入、開始與結果；共享 reset／cleanup 改變時檢查前組退出到後組起始的污染。保留必要冷啟動邊界。
- 對照 requested／實際 leaf／profile／skip，明列少跑、缺失與原始結果。Editor、adapter、保存產物與 client／多人各自驗收；不以 Build 或局部 PASS 代簽。
- 專案要求 review 時，必要驗證後才交**全新且無父上下文**的 reviewer；只提供需求、凍結候選、變更／必要依賴與原始證據，不傳主對話或開發者推理。按 `agent-handoff-discipline` 的 review 規則執行，不另加固定輪數、模型或全量重跑要求。

## 回報與效能判讀

先寫本輪證明的行為、失敗分類及剩餘缺口，再附既有 evidence 路徑、候選／job 身分、distinct cases／profiles／skip、重用／補驗理由與清理結果。

沿既有紀錄分開可取得的首次有效案例前準備、compile／reload、原生案例、cleanup、review 與等待時間；並行區間不相加。保留 NUnit root／leaf 原值，口徑不一致時不把 root 當整個 job 時間。用相同範圍的 wall time、無新資訊重跑與重載次數評估改善，不以微基準、測試數或勾選百分比宣稱整案加速。
