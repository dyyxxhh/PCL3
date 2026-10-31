[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="PCl₃" width="80" height="80">

# PCl₃ (三氯化磷啟動器)

**PCl₃**，即三氯化磷（Phosphorus Trichloride），無色透明液體，用於製造有機磷化合物。

基於 [PCL CE](https://github.com/PCL-Community/PCL-CE)（Plain Craft Launcher Community Edition）二次改編的 Minecraft Java 版啟動器。

[![GitHub Release](https://img.shields.io/github/v/release/dyyxxhh/pcl3?label=release&logo=github&style=for-the-badge)](https://github.com/dyyxxhh/pcl3/releases/latest)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/dyyxxhh/pcl3/build-test.yml?style=for-the-badge)](https://github.com/dyyxxhh/pcl3/actions)

[下載最新版](https://github.com/dyyxxhh/pcl3/releases/latest) | [提交問題](https://github.com/dyyxxhh/pcl3/issues/new/choose)

</div>

## 這是什麼

PCl₃ 是 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的個人改編版。上游 PCL CE 自 2.12.x 起對中國大陸區域做了功能限制（地區偵測、帳號限制），本倉庫將其解除，並重新品牌化為 PCl₃。

**請勿向 PCL CE 官方倉庫回報本倉庫的問題。**

## 相比上游新增 / 修改了什麼

| 改動 | 說明 |
|---|---|
| 解除區域限制 | `RegionPolicy` / `RestrictedFeaturePolicy` 判定改為恆不受限，中國大陸可直接下載 Minecraft 本體、整合包與載入器 |
| 解除帳號限制 | 無需正版帳號：離線登入、第三方登入隨時可建立（`ProfileUi.CanCreateOtherProfile` 恆為 `true`） |
| 移除正版購買提示 | 啟動遊戲前不再彈出「正版購買 / 帳號驗證」視窗，受限地區也不再跳過客戶端檔案下載 |
| 品牌重製 | 啟動器名稱、標題列、AUMID、組件資訊、全部 7 種語言檔案統一更名為 PCl₃；更換啟動器圖示 |
| 版本號獨立 | 自 3.0.0 起獨立編號（`metadata.json` 中 `upstream` 記錄所基於的上游版本） |

## 上游（PCL CE）都有什麼

以下功能全部繼承自上游，本倉庫未做刪減：

- **啟動器核心**：Minecraft Java 版本體、整合包（CurseForge / Modrinth / MC 百科等）、載入器（Forge / NeoForge / Fabric / Quilt / OptiFine）下載安裝
- **帳號系統**：微軟正版、離線、第三方認證伺服器（authlib-injector）、統一通行證登入
- **實例管理**：多實例（多版本隔離）、Mod 管理（Jar-in-Jar 依賴分析）、存檔 / 截圖 / 伺服器管理
- **下載源**：官方源、BMCLAPI、官方注入源，支援搜尋 Mod、整合包、資源包、光影
- **其他**：Java 管理、記憶體最佳化、崩潰分析、自訂主題 / 背景音樂 / 特效、聯機（EasyTier）等

詳見 [PCL CE 倉庫](https://github.com/PCL-Community/PCL-CE)。

## 支援平台

| 作業系統 | 支援情況 | 環境需求 |
|---|---|---|
| Windows 10 1809 (17763) 或更高 | ✅ 完整支援 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 8 - Windows 10 1809 (17763) | ⚠️ 理論可執行 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 7 或更低版本 | ❌ 不支援 | / |
| macOS / Linux | 僅交叉編譯開發支援 | [.NET 10 SDK](https://get.dot.net/10) |

Release 提供 x64 與 ARM64 兩個架構的依賴框架版本，需系統已安裝 .NET 10 Desktop Runtime。

## 與上游同步

本倉庫基於上游 `dev` 分支（建置版本見 [Releases](https://github.com/dyyxxhh/pcl3/releases) 頁所註明的上游 commit）。上游更新後，將 PCl₃ 的改動（見上方「相比上游新增 / 修改了什麼」）重新套用即可。

## 授權條款

- `Plain Craft Launcher 2/` 使用 [自訂授權條款](Plain%20Craft%20Launcher%202/LICENCE)（繼承自 PCL CE，禁止商用等）
- `其餘所有目錄` 使用 [Apache License 2.0](LICENSE)

上游 PCL CE 由 [PCL Community](https://github.com/PCL-Community) 維護，原始 PCL 由 [龍騰貓躍](https://github.com/Meloong-Git/PCL) 開發，感謝他們的工作。
