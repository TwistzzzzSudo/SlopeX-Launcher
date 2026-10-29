# SlopeX Launcher 进度报告与实施计划

> 首次生成：2026-10-29 02:32 ｜ 最近更新：2026-10-29 18:56
> 对应产物：`SlopeX.exe` 1.0.0（自包含单文件，139 MB，win-x64）
> 代码规模：32 个 C# / XAML 源文件，7,804 行
> 版本控制：git 分支 `feature/portable`（便携改造），`main` 保持可构建
> 当前状态：**便携改造完成**——配置与日志进 `SlopeXData\`，运行时目录可配；设置页按文案规范完成首轮瘦身
> 下一个执行项 = 微软登录（阻塞在 Azure client_id）或 Forge（B 方案）
> 本轮改动：`Paths` 增加便携数据目录与一次性迁移、运行时目录可配、设置页新增「数据与运行时」并精简存量文案
> **工作量口径**：全文一律以「产出物 / 改动范围」描述，不使用时间估算。

---

## 0. 一句话结论

**已经是一个能真正装、真正启动的启动器**：原版版本安装、Java 自动补齐、版本隔离、离线启动全链路跑通，两条不同世代的版本（1.20.1 / 26.3）都在本机实机进过游戏。
缺的是**账号体系**（微软正版登录）与**模组生态**（Forge/Fabric 安装、模组管理、整合包导入）。
四项决策已全部拍板（详见 §7）：Forge 走 B 方案、微软登录必做、便携走折中。
美术与主题（详见 §6 末）：主题由用户在「设置 → 主题」切换，默认**极简白纸**，深色主题名为**极简墨纸**，支持跟随系统；**现在先打地基（双主题 token 层 + 设计规范），之后才做 Fabric 安装**，统一视觉定案放到 Forge 之后。

---

## 1. 交付物清单

| 路径 | 说明 | 状态 |
|---|---|---|
| `SlopeX.exe` | 免安装单文件，双击即用，无需装 .NET | ✅ |
| `src/SlopeX/` | 完整源码（.NET 8 + WPF） | ✅ |
| `.minecraft/` | 便携游戏目录，1.3 GB，已装 26.3 与 1.20.1 | ✅ |
| `docs/screenshots/` | 10 张界面截图（首页 / 版本 / 下载 / 任务 / 设置 / Java 缺失 / 自动安装 / 深色首页 / 深色设置 / **安装类型选择**） | ✅ |
| `docs/design-language.md` | **设计规范**：双主题色板、间距/圆角/动效刻度、组件清单、7 项组件缺口 | ✅ |
| `README.md` | 功能、架构、构建、限制说明 | ✅ |
| `build.cmd` | 一键构建（纯 ASCII，规避 cmd 编码问题） | ✅ |
| `tools/screenshot.ps1` | 内部工具：启动并截窗口图；`-Page "设置"` 可用 UI 自动化直接切页（不依赖点击坐标，规避 DPI 偏移） | ✅ |
| `.git/` + `.gitignore` | 版本历史基线 `8cc0cd1`，只跟踪 40 个源文件（产物全排除） | ✅ |
| `ico/SlopeX.ico` | 应用图标，9 个尺寸（16–256），由 `tools/make-icon.ps1` 从矢量几何生成 | ✅ |
| `tools/make-icon.ps1` | 图标生成脚本：改标记形状后重新出 ico，避免手改位图 | ✅ |
| `.workbuddy/memory/` | 项目决策与踩坑记录 | ✅ |

---

## 2. 已完成能力

| 能力 | 状态 | 验收证据 |
|---|---|---|
| 版本清单获取（BMCLAPI / 官方源） | ✅ | 镜像不可达时自动回落官方源 |
| 原版安装（含继承链版本） | ✅ | 1.20.1：701.7 MB / 3,598 个资源 / 190 秒装完 |
| 依赖库下载与去重 | ✅ | Maven 坐标含 classifier，natives 变体不再互相覆盖 |
| 资源索引与 legacy 映射 | ✅ | `virtual / map_to_resources` 已处理 |
| 完整性校验 `verify` | ✅ | 逐项检查版本 JSON 声明的库与 client.jar |
| 启动参数拼装 | ✅ | 规则引擎（os / arch / features）、classpath、natives 解压、log4j 参数 |
| 真实启动 | ✅ | 1.20.1 与 26.3 均进游戏（LWJGL、贴图图集、OpenAL、离线账号生效） |
| Java 探测 | ✅ | 注册表 + Program Files / Adoptium / Microsoft / Zulu / Corretto / `.jdks` / PATH |
| Java 版本匹配 | ✅ | 以 JSON `javaVersion` 为权威：1.17→16、1.18+→17、1.20.5+→21、26.x→25、≤1.12→8 |
| **Java 缺失自动安装** | ✅ | 清华 TUNA 55.8 MB / 3.9 秒；四镜像顺序回退 |
| **存档隔离（强制）** | ✅ | 恒用 `versions\<id>`；无开关、不继承、不共享；真机验证 `--gameDir` 指向版本目录 |
| **主题切换** | ✅ | 「设置 → 主题」三选一（跟随系统 / 极简白纸 / 极简墨纸），运行中即时换肤不重启；跟随系统监听 `WM_SETTINGCHANGE` |
| **Fabric 安装** | ✅ | 拉 meta 选最新稳定加载器 → 写 `inheritsFrom` 版本 JSON → 下载加载器依赖；缺原版自动补装；已加载 Sodium 实测 |
| **安装类型选择** | ✅ | 点「安装」弹出类型选择：原版 / Fabric 可选，Forge / NeoForge 置灰标「即将支持」；已安装版本仍可点（用于加装加载器或补齐文件） |
| **便携（折中）** | ✅ | 配置与日志进 `SlopeXData\`，首次启用从 `%APPDATA%` 复制（原文件保留）；Java 运行时目录可配；目录不可写时整体回落 `%APPDATA%` |
| 登录 | ⚠️ | 仅离线登录（自定义玩家名），无正版验证 |
| 任务与日志 | ✅ | 安装进度卡 + 游戏实时日志（增量缓冲刷新） |
| 设置持久化 | ✅ | `<启动器目录>\SlopeXData\config.json`（不可写时回落 `%APPDATA%`），路径存相对形式 |
| 便携模式 | ✅ | exe + `.minecraft` 整个目录搬走仍可用（实测过 `I:\sxtest`） |
| 无界面 CLI 排障 | ✅ | `list / java / java-ensure / manifest / install / verify / plan / launch` |

---

## 3. 架构与技术选型

### 3.1 为什么是 C# 而不是 VB.NET

PCL2 是 VB.NET + WPF（`Meloong-Git/PCL`，`Modules/Mod*` 分层 + `Controls`/`Pages`）。本工程用 **C# + WPF / net8.0-windows**：运行时、XAML、控件模型完全一致，只是宿主语言换成 C#（PCL2 自己的底层库 MeloongCore 本身就是 C#）。
**代码为独立实现，未复制 PCL2 源码**，只借鉴模块划分与启动器行为逻辑。

### 3.2 模块分层

```
ModBase      路径 / 日志 / 配置 / MVVM 基类                        730 行
ModNet       并发下载 / 镜像改写 / 源回落 / 单文件超时 / SHA1        403 行
ModMinecraft 版本扫描 / inheritsFrom 合并 / 强制存档隔离             411 行
ModJava      运行时探测 / 版本匹配 / 自动下载安装                   789 行
ModLaunch    规则引擎 / classpath / natives / 命令行 / 进程         562 行
ModDownload  原版安装 / Fabric 安装 / 任务中心                      747 行
ModCli       无界面模式                                            460 行
UI 层        5 页 + 主窗口 + 控件 + 样式 + 双主题调色板 + ThemeService  3,700 行
```

（模块层 4,104 行 + UI 层 3,700 行 = 7,804 行）

### 3.3 三个关键设计取舍

1. **`GameDir` 与 `RootGameDir` 分离**
   版本隔离后 `--gameDir` 指向 `versions\<id>`，但 `--assetsDir`、classpath、log4j 配置仍从根目录算。混用一个字段会让游戏去 `versions\<id>\assets` 找资源，直接崩。

2. **Java 不做“向下兼容顶替”**
   旧逻辑找不到够高版本就退回列表最高者（用 17 跑 25），结果是进程 1 秒退出、界面无提示。现在低于要求一律返回“无兼容 Java”，交给自动安装。

3. **单文件自包含发布**
   `PublishSingleFile + IncludeNativeLibrariesForSelfExtract`，换来 138.5 MB 体积，但用户零依赖。构建输出直接落到仓库根目录，与 `.minecraft` 同级，天然便携。

---

## 4. 已修复的真实缺陷（都有复现与验证）

| # | 缺陷 | 后果 | 修法 |
|---|---|---|---|
| 1 | UI 线程上同步等待 async | CLI 在 WPF 调度器上死锁，命令永久卡住 | 改到线程池执行 |
| 2 | Maven 去重键漏 classifier | `lwjgl:lwjgl` 与 `:natives-windows` 互相覆盖，依赖从完整掉到 44 个 | 去重键带上 classifier |
| 3 | BMCLAPI 对资源 302 跳境外 CDN，高并发假死 | 下载卡在 80% 十几分钟不动 | 按文件体积算单文件超时 + 失败回落官方源 |
| 4 | Segoe MDL2 部分字形渲染成空心方框 | 导航图标变“□” | 全部改成矢量 Path |
| 5 | 找不到够高 Java 时静默顶替 | 高版本 MC“启动没反应”，退出码 1 | 返回无兼容 + 自动安装 + 秒退诊断 |
| 6 | 配置存绝对路径 | 目录搬走后版本“消失” | 启动器目录内改存相对路径 |
| 7 | 无版本历史 | 改坏了只能靠备份恢复，无法 diff 定位 | 初始化 git，基线 `8cc0cd1`；`.gitignore` 排掉 1.4 GB 产物与运行时 |
| 8 | **存档隔离可被关闭，且会自动继承/导入公用存档** | 版本之间可共用存档与材质包，隔离形同虚设；用户无法预期哪个版本用哪份存档 | 删除 `VersionIsolation` / `IsolationSeeded` 配置项；`Resolve` 去掉"共用根目录"分支（版本 ID 为空直接抛错）；删除 `CopyShared` 自动继承与「导入公共目录内容」按钮；设置页改为只读说明 + 打开存档目录；旧配置残留字段自动清理 |

---

## 5. 当前限制

### 功能缺口
- 仅 Windows（WPF + 注册表探测）
- 无微软正版登录，只能离线账号
- 无 Forge / NeoForge 一键安装（Fabric / Quilt 同类已支持；能启动别家装好的 Forge 版本，但自己不装）
- 无模组管理（批量启停、依赖检查）
- 无整合包导入（CurseForge / Modrinth）
- 存档隔离为强制行为，不提供“材质包共享但存档隔离”这类混合模式；需要共用只能手动复制文件

### 工程风险
- SHA1 校验只对 < 8 MB 文件做（大文件只比大小），极端情况下可能放过损坏包
- 下载重试为“每源 2 次”，断点续传只做到“已完整文件跳过”，不是按字节续传
- 无自动化测试，回归靠 CLI + 截图人工验证
- ~~仓库尚未初始化 git~~（已于 2026-10-29 02:42 解决，见第 4 节 #7）

### 工程基建（已就位）
- **git**：`main` 分支基线 `8cc0cd1`，40 个文件 / 0.64 MB，`.git` 仅 565 KB
- **忽略规则**：`bin/` `obj/` `/SlopeX.exe` `/.minecraft/` `/.workbuddy/` `/SlopeXData/` `/runtime/` `*.sxpart`，`git check-ignore` 逐项验证通过
- **CRLF**：`core.autocrlf=false`，避免 Windows 换行转换产生全文件伪 diff
- **约定**：Forge / 登录等大改动必须在干净工作区开 `feature/*` 分支，`main` 始终保持可构建

### 环境
- Java 运行时（180 MB）默认仍在 `%APPDATA%\SlopeX\runtime`；要做成"一个文件夹全带走"需在设置里改运行时目录
- 根目录 `saves` / `resourcepacks`（16 MB）是旧版共用模式遗留，现已不被任何版本使用；设置页只提示数量与路径，不自动搬移

---

## 6. 后续计划

> **执行顺序（已定 2026-10-29 02:50）**：Fabric 安装 → 便携折中改造 → 微软登录 → Forge（B 方案）。
> 每一步在独立 `feature/*` 分支上做，`main` 始终保持可构建。

### P0 — 补齐“能玩主流整合包”的最小闭环

**1. Fabric / Quilt 安装** ✅ **已完成 2026-10-29 18:16**
- 做法：拉 `fabric-meta` 加载器列表 → 取最新稳定版 → 落盘 `inheritsFrom` 版本 JSON → 按 Maven 坐标补依赖
- 实际产出：`Modules/ModDownload/Fabric.cs`（列表 / profile / 安装）；`InstallTask` 增加 `InstallKind` 与 `BaseVersion`；`Net.Mirror` 增加 `meta.fabricmc.net`、`maven.fabricmc.net` 映射；CLI `fabric-list`、`install-fabric`；下载页每行「Fabric」按钮
- 验收：`install-fabric 1.20.1` → `versions\fabric-loader-0.20.1-1.20.1`（8 个依赖 / 2.9 MB / 3 秒）；`verify` 96 库通过；`plan` mainClass 为 `KnotClient`；真机日志 `Loading Minecraft 1.20.1 with Fabric Loader 0.20.1`；装 Sodium 0.5.13 后 `Loading 10 mods`
- 遗留：Quilt 未做（协议与 Fabric 高度相似，可复用本模块，估工作量约为 Fabric 的 1/3）

**2. Forge / NeoForge 安装**（已定：**B 方案**）
- 做法：下载官方 installer → 用托管 Java 无头执行 `--installClient`；UI 只显示“安装中 + 耗时”，**不解析 `install_profile.json`、不跑 processors**
- 验收：装完 `--cli verify` 通过、能进游戏、崩溃日志无缺失类
- 风险：中（成功率依赖 installer 自身行为，进度不可见；installer 走官方 maven，国内可能慢）
- 备注：若后续需要细粒度进度或离线复装，再升级为 A 方案（自研 processors），B 保留为兜底

**3. 微软正版登录**（已定：**必须做**）
- 做法：OAuth 2.0 设备码流程（`live.com/oauth20_connect.srf`）+ XBL / XSTS 换 token + `/minecraft/profile`
- **阻塞项**：等用户提供自建 Azure 公共客户端的 **client_id**（不提供则无法开工）
- 验收：登录后 skin 正常显示、服务器可进（非正版服仍拒绝属正常）
- 风险：中（token 刷新、设备码轮询超时处理）

### P1 — 从“能启动”到“好用”

**4. 模组管理**：列表 / 启停（改名 `.disabled`）/ 依赖缺失提示
**5. 整合包导入**：解析 `manifest.json`（CurseForge）与 `.mrpack`（Modrinth），映射到隔离目录
**6. 完整性修复**：全量 SHA1 + 一键补齐（复用现有 `Downloader`，只补缺失与损坏）
**7. 多任务队列**：并行安装多个版本、暂停/继续、失败重试队列

### P2 — 打磨

**8. 便携改造（折中方案）** ✅ **已完成 2026-10-29 18:56**
- 实际产出：`Paths.DataDir`（优先 `SlopeXData\`，不可写回落 `%APPDATA%`）+ `MigrateFromAppData` 一次性复制、`Paths.RuntimeDirOverride`、`Config.JavaRuntimeDir`、设置页「数据与运行时」卡片、`ProbeWrite` 静默探测（避免日志递归）
**9. 启动器自更新**：检查 GitHub Release / 自建清单，下载新 exe 并替换
**10. 崩溃诊断**：解析 `crash-reports`，把 `UnsupportedClassVersionError`、缺模组、OOM 翻成人话
**11. 界面细节**：高 DPI 适配、键盘导航、日志搜索与导出
**12. 设置页文案瘦身**：按 `docs/design-language.md` §6 的四类文字分层，削掉教学式长句、把机制解释收进 tooltip/「?」、一次性警告补「不再提示」；**首批修掉「点保存生效」与「切换立即生效」的矛盾**（详见 §6.3 问题清单）

### 建议里程碑

| 里程碑 | 内容 | 完成标志 |
|---|---|---|
| M2 | Fabric + 模组管理 | 主流 Fabric 整合包能装能玩 |
| M3 | 微软登录 | 正版账号可登录、皮肤正常 |
| M4 | Forge / NeoForge + 整合包导入 | 覆盖 90% 玩家实际使用场景 |

### 美术设计排期（三段式，已定 2026-10-29 02:53）

**现状基线**（本轮实测）

| 指标 | 数据 |
|---|---|
| `Themes/Style.xaml` | 456 行；13 个语义色 + 2 个字体族 + 6 级文字样式 + 约 15 个组件样式 |
| 页面硬编码颜色 | 7 个 XAML 合计仅 **2 处**（`PageHome` 1、`MainWindow` 1），令牌化程度已经很高 |
| 缺失的刻度 | 无间距（spacing）体系、无圆角（radius）体系、无动效约定 —— 数值散在各页 `Padding` / `CornerRadius` 里 |
| 缺失的组件 | `ComboBox`、多列列表（模组列表）、`Dialog` 遮罩、`Toast`、空状态、右键菜单、步骤条 |
| 视觉回归 | `tools/screenshot.ps1` 可一键截图逐页比对，改样式后能验证没崩 |

**三段式安排**

| 阶段 | 时机 | 产出范围 | 需你参与 |
|---|---|---|---|
| **S1 地基** ✅ | **已完成 2026-10-29 17:31** | 实际产出：`Palette.Light.xaml` / `Palette.Dark.xaml`（各 17 个语义色）、`Style.xaml` 改为纯刻度层 + 全量 DynamicResource（106 处）、间距（Sp4–Sp32）/ 圆角（R4–R14）/ 动效（DurFast–DurSlow）三套刻度、`ThemeService`（配置 + 注册表 + 系统事件）、设置页主题卡三选一、`docs/design-language.md`、7 项组件缺口清单 | 否 |
| **S2 边做边补** | Fabric / 便携 / 登录期间 | 新页面一律用 token；ComboBox、多列列表、Dialog、Toast、空状态随功能同步设计；**顺手修设置页「保存 vs 立即生效」矛盾文案，并按 §6 文案规范削存量说明** | 否（看截图即可） |
| **S3 视觉定案** | Forge 完成后、整合包导入之前 | 页面形态稳定（5 → 8~9 页）后统一打磨，一次到位 | **是**（唯一需要深度参与的一轮） |

**为什么不在现在全面铺开**
1. **返工按页数算**：现在 5 页，M4 后约 8–9 页；现在重做一轮，新页面还得再对一遍，等于同一件事做两遍。
2. **美术需要反复看图往返**，不宜夹在 Fabric / Forge 排障期间——排障是日志密集型，审美判断力会掉。
3. **晚期大改的风险已可控**：有截图回归工具，能逐页验证。

**已确认（2026-10-29 02:59）**
- 风格保持「极简白纸」家族，不换风格；主题**由用户在「设置 → 主题」中选定**，默认「极简白纸」。
- **要做深浅色跟随系统**；深色主题命名为 **「极简墨纸」**。

### 主题命名与切换（已定 2026-10-29 02:59）

| 主题 | 模式 | 定位 | 一句话描述 |
|---|---|---|---|
| 极简白纸 | 浅色 | **默认主题** | 纸白底、墨黑字，即当前已实现外观 |
| **极简墨纸** | 深色 | 深色主题 | 墨色纸面、浅墨字 |

**取名理由（极简墨纸）**
1. **同一套纸媒隐喻成对**：白纸以墨写字，墨纸以浅色显字，正好是 `Ink` token 的反相，不引入第二套意象。
2. **四字对仗**：与「极简白纸」并列时不显长短不齐。
3. **与现有 token 同源**：浅色主题的文字色就叫 `Ink #1A1A18`，「墨」既是颜色也是材质，名字落在已有的命名体系里。
4. **刻意避开「极简黑」「深色模式」这类直白叫法**：深色底是**暖调近黑**（保留纸感，不用纯黑 `#000`、不用冷蓝灰），叫「黑」会产生"纯黑底"的误导，也丢掉纸的隐喻。
- 备选（若不喜欢可换，改名只改一处字符串）：**极简炭纸**（偏中性灰）、**极简夜纸**（偏蓝黑）。

**切换模型：主题 × 模式 二维**
- **「设置 → 主题」**：列出主题卡片，当前仅一组「极简白纸 / 极简墨纸」，白纸为默认；结构上允许将来扩第三套主题而不改界面骨架。
- **「外观模式」三选一**：跟随系统（默认）/ 仅浅色 / 仅深色。
- **跟随系统实现要点**：读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`；监听 `WM_SETTINGCHANGE` 实时切换，**不重启启动器**；若用户选的是"仅浅色/仅深色"，则忽略系统值变化。
- **对 S1 的影响**：token 必须按「主题 × 模式」二维组织（两套颜色字典 + 运行时 `DynamicResource` 切换），不能写死单套色值；主题与模式分别持久化到 `config.json`。这使 S1 从「改一份 `Style.xaml`」变成「两份颜色字典 + 一个切换服务 + 设置页新区块」。

---

## 7. 决策结论（2026-10-29 02:50 全部确定）

> 四项事项已全部拍板，不再等待确认。**尚未开工**；按 §6 排期，先做美术地基 S1（双主题 token 层，含「极简墨纸」），再做 Fabric 安装。

| # | 事项 | 结论 | 落地方式 |
|---|---|---|---|
| ① | Forge / NeoForge | **要做，采用 B 方案** | 官方 installer + `--installClient` 无头执行，不自研 processors |
| ② | 微软正版登录 | **必须做**（尊重正版玩家，没理由不做） | OAuth 设备码 → XBL/XSTS → `/minecraft/profile`；**待提供 Azure client_id** |
| ③ | 便携 | **折中方案** | config + logs 进 `SlopeXData\`；runtime 留 `%APPDATA%` 但可改位置 |
| ④ | 下一步 | **先做 Fabric 安装** | 见 §6 P0-1 |

### 7.1 ① Forge 为何选 B 方案

- **选它的理由**：官方 installer 自己知道怎么装（processors、srg 映射、client-extra 拆分全都是它的内部逻辑），我们只负责“下载 + 用正确的 Java 跑 + 收集输出”。成功率接近 100%。
- **工作量**：新增 1 个模块（installer 下载与校验、Java 定位、`--installClient` 参数拼装、进程管理与输出流捕获、超时与取消、失败诊断）；1 条 CLI 命令 `install-forge`；下载页 1 个入口 + 1 张安装进度卡（无数值进度，只显示状态与已耗时）。
- **接受的代价**：进度不可见（只能显示“安装中 + 已耗时”，拿不到“正在处理第 3 个库”这类细粒度信息）；installer 自己走官方 maven，国内可能慢，只能靠镜像环境变量缓解。
- **不是终局**：Fabric / Quilt 那套是我们自己拼 JSON，Forge 这块是“外包”给官方。将来若需要离线复装或细粒度进度，再升级到 A 方案（自研 processors），B 保留为兜底路径。

### 7.2 ② 微软登录的阻塞项

- 技术路径已明确：设备码流程 → Xbox Live → XSTS → Minecraft profile。代码侧是新增 `ModAccount` 模块 + token 刷新 + 皮肤渲染。
- **唯一阻塞**：需要一个 Azure 应用注册产出的 **client_id**（公共客户端流，无需密钥、免费）。没有它微软不会认这个启动器。
- 拿到 client_id 之前，这部分**无法开工**，只能先保持离线登录。其余三项不受影响。

### 7.3 ③ 便携折中方案的具体内容

- **迁走**：`config.json`、`logs\`（几十 KB，拷走即带设置和日志）。
- **留下**：`runtime\java-<主版本>\`（180 MB）仍在 `%APPDATA%\SlopeX`，避免每次拷贝多搬 180 MB；但**设置页提供修改入口**，想彻底便携的用户可自行指到启动器目录。
- **回落**：`Core.Paths` 增加 `DataDir`，优先级 `SlopeXData\`（可写）→ `%APPDATA%\SlopeX`。防止启动器被放到 `C:\Program Files` 时因无写权限直接崩。
- **迁移规则**：首次启动若发现旧 `%APPDATA%\SlopeX\config.json` 且 `SlopeXData\` 不存在，则复制过去，**保留原文件**不删除，作为回滚保险。
- **换取的结果**：换机/换盘只需拷启动器目录，设置不丢；代价是 Java 运行时仍需重新解析或手动指定。

### 7.4 ④ 下一步：Fabric 安装的验收标准

- `--cli install-fabric <mc版本>` 生成 `versions\fabric-loader-<loader>-<mc>\`，其 JSON 含 `inheritsFrom` 指向原版。
- `--cli verify` 通过；`--cli launch` 能进游戏；隔离目录下存在 `mods\` 目录。
- 装一个真实模组（Sodium 之类）验证 loader 确实生效，而不只是版本能启动。

### 7.5 已完成项（原第 4 项：git）

- 已 `git init -b main`，基线提交 `8cc0cd1`；身份仅本仓库生效（`SlopeX Dev <dev@slopex.local>`），未改全局配置。
- `.gitignore` 补强：`/SlopeXData/`、`/runtime/`、`*.sxpart`（正是为 §7.3 预埋）、`*.pdb`、`.idea/`、`*.log`、`*.tmp`、`Thumbs.db`、`.DS_Store`。
- 验证：`git status` 干净 0 行，跟踪 40 个文件，`SlopeX.exe` / `.minecraft` / `bin` / `obj` 全部被拦。

---

## 8. 附录

### 8.1 实测记录

| 项目 | 数据 |
|---|---|
| 1.20.1 安装 | 701.7 MB / 3,598 资源 / 190 秒 |
| 1.20.1 启动 | 进游戏，`Setting user: Steve`，OpenAL 初始化，退出码 143（主动结束） |
| 26.3 启动 | Java 25、LWJGL 3.4.3、SDL 建窗、RTX 3060 OpenGL 3.3 |
| Java 25 自动安装 | 清华 TUNA，55.8 MB，3.9 秒，解压到 `runtime\java-25` |
| ~~版本隔离继承~~（旧行为，已移除） | 曾把「新的世界」16 MB + `options.txt`(137 行) 复制到 `versions\1.20.1`；自动继承逻辑已删除 |
| 存档隔离（强制） | `--cli plan` 输出：1.20.1 → `versions\1.20.1\saves`，26.3 → `versions\26.3\saves`，互不可见 |
| 存档隔离真机验证 | 26.3 运行时 java 命令行含 `--gameDir I:\SlopeX Launcher\.minecraft\versions\26.3`；`versions\26.3\saves` 保持空（未继承任何存档），根目录 `saves` 的 `mtime` 未变（未被读取/写入） |
| 旧配置清理 | 启动一次后 `config.json` 中 `VersionIsolation` / `IsolationSeeded` 两字段自动消失 |
| 主题切换（运行中） | 设置页点「极简墨纸」→ 背景/文字即时变化，**不重启**；日志记录 `主题切换为 Dark（深色）` |
| 主题持久化 | 重启后按 `config.json` 的 `Appearance` 生效；旧配置缺字段时自动补写 `Theme` / `Appearance` |
| 深色对比度抽检 | 首页 / 设置 / 任务三页截图：主按钮为浅底深字（`OnInk` 生效）、Logo 勾可见、日志文字可读、卡片描边可辨 |
| 图标生成 | `ico/SlopeX.ico` 16.6 KB，帧尺寸 16/20/24/32/40/48/64/128/256；256 帧目视与原标记一致 |
| 图标嵌入 | 从 `SlopeX.exe` 反提取图标成功（32×32），说明 `ApplicationIcon` 已编进单文件 |
| 窗口图标 | `Icon="pack://…/Assets/SlopeX.ico"` 启动无异常，窗口正常渲染（资源缺失会在 `InitializeComponent` 抛错） |
| Fabric 安装 | `install-fabric 1.20.1` → 8 个依赖 / 2.9 MB / 3 秒；重复执行 1.1 秒（全部命中跳过，幂等） |
| Fabric 校验与命令行 | `verify` 通过（96 个库，继承链 `1.20.1 → fabric-loader-…`）；`plan` 的 mainClass 为 `KnotClient`，classpath 含 fabric-loader / intermediary / sponge-mixin |
| Fabric 真机 | 日志 `Loading Minecraft 1.20.1 with Fabric Loader 0.20.1`、`MIXIN Subsystem 0.8.7 … Service=Knot/Fabric`、`Setting user: Steve` |
| 模组加载验收 | 放入 Sodium 0.5.13 → `Loading 10 mods`（含 sodium 及其内置 fabric-api 模块）、`Loaded configuration file for Sodium: 42 options`、资源重载列表含 sodium |
| 安装类型对话框 | 点任意行「安装」→ 弹出居中卡片 + 遮罩；原版默认选中；Forge / NeoForge 置灰带「即将支持」；Esc 或「取消」关闭 |
| 对话框端到端 | 已安装行 →「安装」→ 选 Fabric →「开始安装」→ 日志 `Fabric 0.20.1（26.3）安装完成：fabric-loader-0.20.1-26.3，依赖 7 个`，版本数 5 → 6 |
| 便携迁移 | 首次运行后 `SlopeXData\config.json` 生成（含新字段 `JavaRuntimeDir` / `DismissLegacySavesHint`），`logs\` 已复制，`%APPDATA%\SlopeX\config.json` 保留未删 |
| 便携实测 | exe 单独拷到 `I://sxportable-test` 运行 → 自动生成该目录的 `SlopeXData\`，`--cli list` 输出 `gameDir = I://sxportable-test//.minecraft`（相对路径正确解析） |
| 运行时位置 | Java 检测仍指向 `%APPDATA%\SlopeX\runtime`（180 MB 未搬），设置页可改到启动器目录内 |
| 设置页瘦身 | 页头改为「主题即时生效；其余项点保存设置后生效」；存档隔离由 4 层文案压到 2 层（机制进 tooltip）；旧存档提示可「不再提示」 |
| 便携实测 | exe 复制到 `I:\sxtest`，正确解析到 `I:\sxtest\.minecraft` |
| 构建 | 0 警告 0 错误 |
| git 基线 | 40 个文件 / 0.64 MB，`.git` 565 KB，忽略规则 5/5 命中，工作区 0 改动 |

### 8.2 命令速查

```bash
SlopeX.exe --cli list                 # 已安装版本
SlopeX.exe --cli java                 # 检测到的 Java
SlopeX.exe --cli java-ensure 25       # 确保存在 Java 25
SlopeX.exe --cli manifest 20          # 可安装版本
SlopeX.exe --cli install 1.20.1       # 安装原版
SlopeX.exe --cli verify 1.20.1        # 完整性校验
SlopeX.exe --cli plan 1.20.1          # 打印启动命令行（不启动）
SlopeX.exe --cli launch 1.20.1        # 启动游戏
SlopeX.exe --cli fabric-list 1.20.1             # 该原版可用的 Fabric 加载器
SlopeX.exe --cli install-fabric 1.20.1          # 装最新稳定版 Fabric（缺原版会先补原版）
SlopeX.exe --cli install-fabric 1.20.1 0.20.1   # 指定加载器版本
build.cmd                             # 构建单文件到仓库根目录
```

### 8.3 关键路径

| 用途 | 位置 |
|---|---|
| 游戏根目录 | `<启动器目录>\.minecraft` |
| 版本隔离目录 | `.minecraft\versions\<版本 ID>` |
| 配置与日志 | `<启动器目录>\SlopeXData\`（不可写时回落 `%APPDATA%\SlopeX\`） |
| 托管 Java 运行时 | 默认 `%APPDATA%\SlopeX\runtime\java-<主版本>`，可在设置中改到启动器目录内 |

### 8.4 git 速查

```bash
git log --oneline              # 提交历史（当前 1 条：8cc0cd1）
git show --stat                # 查看某次提交改了哪些文件
git diff                       # 工作区 vs 暂存区的具体差异
git diff --cached              # 暂存区 vs 上次提交
git status                     # 当前状态（应为 clean）
git add -A && git commit -m "" # 提交全部改动
git checkout -b feature/forge  # 大改动前开分支
git checkout -- <file>         # 丢弃单个文件的改动
git checkout 8cc0cd1 -- <file> # 从基线取回单个文件
```

**注意**：`.minecraft/`、`SlopeX.exe`、`bin/`、`obj/` 永远不入库。如果 `git status` 里出现它们，说明 `.gitignore` 被改坏了，先修规则再提交。
