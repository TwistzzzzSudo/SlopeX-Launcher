# SlopeX Launcher 进度报告与实施计划

> 首次生成：2026-10-29 02:32 ｜ 最近更新：2026-11-01 17:20
> 对应产物：`SlopeX.exe` 1.0.0（自包含单文件，win-x64）
> 代码规模：74 个 C# / XAML 源文件，≈18,343 行
> 版本控制：分支 `main`，远端公开 `github.com/TwistzzzzSudo/SlopeX-Launcher`
> 当前状态：**S3 视觉定案**（7 个组件里补完 4 个，剩 `ComboBox` / `ContextMenu` / `Steps` 待有真实场景再补）；四条加载器线 + 整合包导入 + 模组增删与中英文搜索（Modrinth / CurseForge / 双源三选一）+ 光影 / 材质包下载 + 法律声明（首启确认）全部可用；**微软登录已通过 Mojang 白名单，实测登录 / 静默刷新 / 档案接口全部正常（2026-11-01）**
> 下一个执行项 = **继续完善启动器**（集中补既有链路，不铺新功能面），候选待办见 §6.2
> 本轮改动：⓪设置页两处体验修正——(a) 滚轮改为平滑滚动（新增 `Controls/ScrollAssist`）：步长从系统默认的 16 px/行提到不低于 88 px/格并加缓动，同时接管内层 ListBox 吞掉滚轮的问题，"鼠标停在 Java 列表上整页滚不动"就是它；(b) 选 `32` / `64` 线程时先弹风险确认框（新增通用 `Controls/ConfirmDialog`，取消 / Esc 退回原档，确认后才点亮并提示保存）；①新增分段下载引擎（`Modules/ModNet/Downloader.cs`）——单文件按 256 KB–8 MB 切片、默认 16 线程并发（可调 8/16/32/64），失败断点续传、整体失败回退单流；接入模组 / 光影 / 材质三条下载链路，主因是 `cdn.modrinth.com` 单连接实测只有 35 KB/s（详见 §6「分段下载与下载进度」）；②模组安装与资源包安装加进度条：百分比 + 已下载/总大小 + 实时速度，装完自动收起；③模组搜索加来源筛选——`全部` / `Modrinth` / `CurseForge` 三选一；CurseForge 元数据经社区镜像 MCIM（官方接口要 API Key），文件仍直连 forgecdn；`Modrinth` 单选走官方接口，`全部` 时两源统一走镜像；中文搜索按词条里登记的链接同时支持两个平台（详见 §6.3）；④版本内「资源包」上线——光影 / 材质包两个页签，每行一个「安装」、点中版本即下载，搜索不过滤 MC 版本（规格与实测见 §6.1）；⑤模组搜索支持中文（MC百科 检索 → 词条链接 → 项目核对版本与加载器）；⑥修掉跨线程写 UI 导致模组安装必定失败的问题；⑦主题背景音乐默认音量 20% → 15%；⑧法律声明内置进 exe（设置页底部可查看），源文件留在 `docs/LegalNotice.md` 作为备份；⑨首次启动的简版确认框（勾选后才能点「确认」，同意后写入配置不再弹出）；⑩文档整改：已完成项与待办分家、命令速查补全；⑪新增命令行自检 `selftest`——用 5 条固定样本 + 1 条账号档案断言，覆盖「中文 → MC百科 → 平台」链路（机械动力 → `LNytGWDc`、苹果皮 → `EsAfCjCV`、钠在 forge 下必须被滤掉、「全部」落到 CurseForge、英文直搜两源存活），任一失败返回退出码 1（见 §6「命令行自检」）；⑫**微软登录通过 Mojang 白名单**（2026-11-01）——`/minecraft/profile` 返回 200、皮肤 ACTIVE、2 个披风；人为过期后静默刷新成功、双令牌轮换；诊断文案里「还在等审核」的说法同步改掉；⑬**真机启动后发现皮肤退回默认，根因是本地存的 UUID 只有 28 位**——`Msa.FormatUuid` 把最后一段写成 `Substring(20, 4)`（应为 12），游戏拿到残缺 UUID 查不到档案就套默认皮肤，**日志里没有任何报错**；修好格式化并加一次性自愈（启动读账号时比对长度，不足 36 位就用现成令牌重拉档案写回），`selftest` 补第 6 条断言盯住它（见 §4-14、§6「命令行自检」、§8.1「正版启动真机」）；⑭设置页「关于」卡片右下角加署名 `Program By ShakableCat · DeepSeek`（与「打开配置目录」同一行、右对齐，用 `TCaption` 随主题走）；⑮`--cli account` 的令牌文案从「有效（到期 …）」改为「… 到期，自动续期，无需重新登录」——原句没有说清过期后会怎样，容易被读成账号要失效（见 §7.2）；⑯模组依赖改为**按声明锁定版本**——Modrinth 依赖项带 `version_id` 时直接装那一版，不再一律装「该项目最新适配版」；新增 `--cli mods-deps` 打印依赖解析结果（见 §6「依赖版本锁定」）。以上改动 `dotnet build -c Release` 通过（0 警告 0 错误）
> **工作量口径**：全文一律以「产出物 / 改动范围」描述，不使用时间估算。

---

## 0. 一句话结论

**已经是一个能真正装、真正启动的启动器**：原版 / Fabric / Forge / NeoForge 安装、整合包导入、Java 自动补齐、版本隔离、离线启动、模组增删与中英文搜索、光影与材质包下载全部跑通；两条不同世代的版本（1.20.1 / 26.3）都在本机实机进过游戏。

**现在缺的是"打磨"，不是"补洞"**：模组没有更新检查、不校验依赖版本区间；存档、整合包内的自定义资源仍要手动放文件；启动器自身没有升级通道。

**当前阶段：完善启动器**，不铺新功能面（见 §6）。

---

## 1. 交付物清单

| 路径                          | 说明                                                                                          | 状态  |
| --------------------------- | ------------------------------------------------------------------------------------------- | --- |
| `SlopeX.exe`                | 免安装单文件，双击即用，无需装 .NET                                                                        | ✅   |
| `src/SlopeX/`               | 完整源码（.NET 8 + WPF）                                                                          | ✅   |
| `.minecraft/`               | 便携游戏目录，1.3 GB，已装 26.3 与 1.20.1                                                              | ✅   |
| `docs/screenshots/`         | 15 张界面截图（首页 / 版本 / 下载 / 任务 / 设置 / Java 缺失 / 自动安装 / 深色首页 / 深色设置 / 安装类型选择 / 登录 / 模组面板 / 模组搜索 / 光影包 / 材质包） | ✅   |
| `docs/design-language.md`   | **设计规范**：双主题色板、间距/圆角/动效刻度、组件清单（7 项，已补 4 项）                                                      | ✅   |
| `docs/LegalNotice.md`       | **法律声明正文**（唯一来源，编译时嵌入 exe；设置页底部可查看）                                                         | ✅   |
| `docs/LegalNotice-Brief.md` | **首次启动确认框用的简版**（不含仓库地址与联系方式，同样嵌入 exe）                                                       | ✅   |
| `README.md`                 | 功能、架构、构建、限制说明                                                                               | ✅   |
| `build.cmd`                 | 一键构建（纯 ASCII，规避 cmd 编码问题）                                                                   | ✅   |
| `tools/screenshot.ps1`      | 内部工具：启动并截窗口图；`-Page "设置"` 可用 UI 自动化直接切页（不依赖点击坐标，规避 DPI 偏移）                                  | ✅   |
| `.git/` + `.gitignore`      | 版本历史基线 `8cc0cd1`，只跟踪源文件（当前 65 个，产物与运行时全排除）                                                  | ✅   |
| `ico/SlopeX.ico`            | 应用图标，9 个尺寸（16–256），由 `tools/make-icon.ps1` 从矢量几何生成                                          | ✅   |
| `tools/make-icon.ps1`       | 图标生成脚本：改标记形状后重新出 ico，避免手改位图                                                                 | ✅   |
| `.workbuddy/memory/`        | 项目决策与踩坑记录                                                                                   | ✅   |

---

## 2. 已完成能力

| 能力                    | 状态          | 验收证据                                                                                                                                                                                                                                                 |
| --------------------- | ----------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 版本清单获取（BMCLAPI / 官方源） | ✅           | 镜像不可达时自动回落官方源                                                                                                                                                                                                                                        |
| 原版安装（含继承链版本）          | ✅           | 1.20.1：701.7 MB / 3,598 个资源 / 190 秒装完                                                                                                                                                                                                                |
| 依赖库下载与去重              | ✅           | Maven 坐标含 classifier，natives 变体不再互相覆盖                                                                                                                                                                                                                |
| 资源索引与 legacy 映射       | ✅           | `virtual / map_to_resources` 已处理                                                                                                                                                                                                                     |
| 完整性校验 `verify`        | ✅           | 逐项检查版本 JSON 声明的库与 client.jar                                                                                                                                                                                                                         |
| 启动参数拼装                | ✅           | 规则引擎（os / arch / features）、classpath、natives 解压、log4j 参数                                                                                                                                                                                             |
| 真实启动                  | ✅           | 1.20.1 与 26.3 均进游戏（LWJGL、贴图图集、OpenAL、离线账号生效）                                                                                                                                                                                                         |
| Java 探测               | ✅           | 注册表 + Program Files / Adoptium / Microsoft / Zulu / Corretto / `.jdks` / PATH                                                                                                                                                                        |
| Java 版本匹配             | ✅           | 以 JSON `javaVersion` 为权威：1.17→16、1.18+→17、1.20.5+→21、26.x→25、≤1.12→8                                                                                                                                                                                 |
| **Java 缺失自动安装**       | ✅           | 清华 TUNA 55.8 MB / 3.9 秒；四镜像顺序回退                                                                                                                                                                                                                      |
| **存档隔离（强制）**          | ✅           | 恒用 `versions\<id>`；无开关、不继承、不共享；真机验证 `--gameDir` 指向版本目录                                                                                                                                                                                               |
| **主题切换**              | ✅           | 「设置 → 主题」三选一（跟随系统 / 极简白纸 / 极简墨纸），运行中即时换肤不重启；跟随系统监听 `WM_SETTINGCHANGE`                                                                                                                                                                                |
| **主题背景音乐**            | ✅           | 插画主题自带曲目，默认音量 **15%**（滑块可调，落盘 `MusicVolume`）；启动游戏自动暂停、退出恢复，用户手动暂停过则不抢状态；素材不在包里时按钮自动隐藏                                                                                                                                                                |
| **法律声明**              | ✅           | 正文唯一来源 `docs/LegalNotice.md`（仓库可 diff/备份），编译时 `EmbeddedResource` 进 exe，单文件分发也不依赖外部文件；「设置 → 底部 → 法律声明 → 查看」打开完整版模态，自带极简 Markdown 渲染（标题 / 引用 / 有序无序列表 / 分隔线 / 粗斜体 / 链接），链接交给系统浏览器                                                                      |
| **首启法律确认**            | ✅           | 首次启动弹**简版**确认框（`docs/LegalNotice-Brief.md`，不含仓库地址与联系方式，提示完整版在设置里）；不勾选「在您使用SlopeX Launcher时，将默认您同意以上所有法律条款。」时「确认」禁用，不同意只能「退出」结束程序；同意后写 `Config.LegalAccepted` / `LegalAcceptedAt` 落盘（`SlopeXData\config.json`），之后不再弹；`--cli legal brief` 可核对简版内容与配置状态 |
| **Fabric 安装**         | ✅           | 拉 meta 选最新稳定加载器 → 写 `inheritsFrom` 版本 JSON → 下载加载器依赖；缺原版自动补装；已加载 Sodium 实测                                                                                                                                                                           |
| **Forge 安装**          | ✅           | 取官方推荐版本 → 下 installer → 用合适 Java 无头执行 `--installClient` → 校验产物；缺原版自动补装；已加载 AppleSkin 实测                                                                                                                                                              |
| **NeoForge 安装**       | ✅           | MC 版本推导版本号 → 镜像取 installer → 预取 install_profile 依赖 → 无头执行 → 补运行期库；已加载 AppleSkin（NeoForge 版）实测                                                                                                                                                        |
| **登录错误提示**            | ✅           | 403 未授权 / 401 / 429 / 5xx / 网络异常各有中文说法；403 那条直接告知「无状态查询页、约一周、再登录不报即通过」                                                                                                                                                                               |
| **圆角窗口**              | ✅           | 四角 14px 圆角，最大化归零；Win10 走分层窗口 + 几何裁剪，新增 `WindowBg` 令牌保证窗口底始终不透明                                                                                                                                                                                       |
| **启动失败诊断**            | ✅           | 9 条规则（内存 / Java / 缺依赖 / 重复模组 / Mixin / 版本不匹配 / 账号 / 驱动 / 端口）+ 未命中时给日志位置；`diagnose` CLI 可复现                                                                                                                                                           |
| **版本补全**              | ✅           | `VersionRepair.Check/RepairAsync`：库 / 客户端 jar / 资源索引 / 资源对象；启动失败自动提示一键补全并重试；CLI `repair`                                                                                                                                                             |
| **模组管理**              | ✅           | 本地：列表（jar 元数据）/ 启停 / 删除 / 打开目录；在线：**三个来源可选**——`全部`（Modrinth + CurseForge，同名去重）/ `Modrinth`（官方接口）/ `CurseForge`（经 MCIM 镜像，官方接口要 API Key）；必需依赖按来源各自补齐；**中文搜索**走 MC百科 词条映射（`modlist.html?key=` 检索 → 词条里 base64 的 Modrinth / CurseForge 链接 → 项目核对版本/加载器）；UIA 与 CLI 双向实测 |
| **整合包导入**             | ✅           | `.mrpack` / CurseForge zip / MultiMC 实例 / 普通 zip；自动补原版与加载器、下载模组、解压 overrides；已导入 Fabulously Optimized（149 模组）实测                                                                                                                                      |
| **光影 / 材质包下载**        | ✅           | 版本行「资源包」→ 光影包 / 材质包两页签；`project_type` 参数化走 `/v2/search`，**不做版本过滤**、由用户挑包版本；落 `shaderpacks\` / `resourcepacks\` 且已存在跳过；实测 Complementary Reimagined r5.9.3（553 KB）与 Stay True Amendment3（40.8 KB）|
| **安装类型选择**            | ✅           | 点「安装」弹出类型选择：原版 / Fabric / Forge / NeoForge 四选一；已安装版本仍可点（用于加装加载器或补齐文件）                                                                                                                                                                                |
| **便携（折中）**            | ✅           | 配置与日志进 `SlopeXData\`，首次启用从 `%APPDATA%` 复制（原文件保留）；Java 运行时目录可配；目录不可写时整体回落 `%APPDATA%`                                                                                                                                                                 |
| **微软正版登录**            | ✅ 2026-11-01 实测可用 | 设备码 → Xbox Live → XSTS → 档案；令牌存 `SlopeXData\accounts.json`，24 小时过期后静默刷新（refresh_token 同步轮换）。**Mojang 白名单已于 2026-11-01 通过**：`/minecraft/profile` 返回 200、皮肤 ACTIVE、2 个披风；任一环节失败自动回落**离线登录**（自定义玩家名），不阻塞启动 |
| 任务与日志                 | ✅           | 安装进度卡 + 游戏实时日志（增量缓冲刷新）                                                                                                                                                                                                                               |
| 设置持久化                 | ✅           | `<启动器目录>\SlopeXData\config.json`（不可写时回落 `%APPDATA%`），路径存相对形式                                                                                                                                                                                         |
| 便携模式                  | ✅           | exe + `.minecraft` 整个目录搬走仍可用（实测过 `I:\sxtest`）                                                                                                                                                                                                        |
| 无界面 CLI 排障            | ✅           | 20+ 命令：`list / java / verify / plan / launch / install-* / mods-* / legal / diagnose / repair`，完整清单见 §8.2                                                                                                                                            |

---

## 3. 架构与技术选型

### 3.1 为什么是 C# 而不是 VB.NET

PCL2 是 VB.NET + WPF（`Meloong-Git/PCL`，`Modules/Mod*` 分层 + `Controls`/`Pages`）。本工程用 **C# + WPF / net8.0-windows**：运行时、XAML、控件模型完全一致，只是宿主语言换成 C#（PCL2 自己的底层库 MeloongCore 本身就是 C#）。
**代码为独立实现，未复制 PCL2 源码**，只借鉴模块划分与启动器行为逻辑。

### 3.2 模块分层

```
ModBase       路径 / 日志 / 配置 / MVVM 基类 / 主题音乐 / 法律声明渲染    1,336 行
ModNet        并发下载 / 镜像改写 / 源回落 / 单文件超时 / SHA1            462 行
ModMinecraft  版本扫描 / inheritsFrom 合并 / 强制存档隔离                 419 行
ModJava       运行时探测 / 版本匹配 / 自动下载安装                        789 行
ModLaunch     规则引擎 / classpath / natives / 命令行 / 进程              786 行
ModDownload   原版 / Fabric / Forge / NeoForge / 整合包 / 任务中心        2,631 行
ModMods       模组扫描 / 元数据 / 启停 / Modrinth·CurseForge·MC百科 搜索 / 光影材质包 1,999 行
ModAccount    设备码登录 / XBL / XSTS / 档案 / 令牌刷新                   532 行
ModCli        无界面模式                                                  1,249 行
UI 层         5 页 + 主窗口 + 控件 + 样式 + 双主题调色板 + ThemeService   6,628 行
```

（模块层 10,212 行 + UI 层 6,628 行 = 16,840 行）

### 3.3 三个关键设计取舍

1. **`GameDir` 与 `RootGameDir` 分离**
   版本隔离后 `--gameDir` 指向 `versions\<id>`，但 `--assetsDir`、classpath、log4j 配置仍从根目录算。混用一个字段会让游戏去 `versions\<id>\assets` 找资源，直接崩。

2. **Java 不做“向下兼容顶替”**
   旧逻辑找不到够高版本就退回列表最高者（用 17 跑 25），结果是进程 1 秒退出、界面无提示。现在低于要求一律返回“无兼容 Java”，交给自动安装。

3. **单文件自包含发布**
   `PublishSingleFile + IncludeNativeLibrariesForSelfExtract`，换来 138.5 MB 体积，但用户零依赖。构建输出直接落到仓库根目录，与 `.minecraft` 同级，天然便携。

---

## 4. 已修复的真实缺陷（都有复现与验证）

| #   | 缺陷                                                                                                | 后果                                                                                                                                  | 修法                                                                                                                                                 |
| --- | ------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | UI 线程上同步等待 async                                                                                  | CLI 在 WPF 调度器上死锁，命令永久卡住                                                                                                             | 改到线程池执行                                                                                                                                            |
| 2   | Maven 去重键漏 classifier                                                                             | `lwjgl:lwjgl` 与 `:natives-windows` 互相覆盖，依赖从完整掉到 44 个                                                                                | 去重键带上 classifier                                                                                                                                   |
| 3   | BMCLAPI 对资源 302 跳境外 CDN，高并发假死                                                                     | 下载卡在 80% 十几分钟不动                                                                                                                     | 按文件体积算单文件超时 + 失败回落官方源                                                                                                                              |
| 4   | Segoe MDL2 部分字形渲染成空心方框                                                                            | 导航图标变“□”                                                                                                                            | 全部改成矢量 Path                                                                                                                                        |
| 5   | 找不到够高 Java 时静默顶替                                                                                  | 高版本 MC“启动没反应”，退出码 1                                                                                                                 | 返回无兼容 + 自动安装 + 秒退诊断                                                                                                                                |
| 6   | 配置存绝对路径                                                                                           | 目录搬走后版本“消失”                                                                                                                         | 启动器目录内改存相对路径                                                                                                                                       |
| 7   | 无版本历史                                                                                             | 改坏了只能靠备份恢复，无法 diff 定位                                                                                                               | 初始化 git，基线 `8cc0cd1`；`.gitignore` 排掉 1.4 GB 产物与运行时                                                                                                 |
| 8   | **存档隔离可被关闭，且会自动继承/导入公用存档**                                                                        | 版本之间可共用存档与材质包，隔离形同虚设；用户无法预期哪个版本用哪份存档                                                                                                | 删除 `VersionIsolation` / `IsolationSeeded` 配置项；`Resolve` 去掉"共用根目录"分支（版本 ID 为空直接抛错）；删除 `CopyShared` 自动继承与「导入公共目录内容」按钮；设置页改为只读说明 + 打开存档目录；旧配置残留字段自动清理 |
| 9   | **模组安装回调跨线程写 UI**（`ModInstaller` 全程 `ConfigureAwait(false)`，对话框却传 `msg => StatusText.Text = msg`） | 点「安装」秒报 `The calling thread cannot access this object because a different thread owns it`，被当成"安装失败"，实际一个文件都没下                       | 对话框新增 `SetStatus()`（`Dispatcher.CheckAccess` + `BeginInvoke`）；`ModInstaller` 增加 `Report()` 包一层 try/catch，界面文字刷不出来不再拖垮下载                            |
| 10  | Forge 把 `${version_name}` 展开成子版本 ID，但实际加载的是父版本 jar                                                | `-DignoreList` 名字对不上，原版 jar 被 BootstrapLauncher 当成自动模块 `_1._20._1`，与 FML 的 `minecraft` 模块撞包（`java.lang.module.ResolutionException`） | 拼完参数后把**实际 jar 文件名**补进 ignoreList（幂等）                                                                                                              |
| 11  | 资源包面板首次进入是一整张空白卡片；切换版本后仍显示上一个版本的搜索结果（UIA 截图核对时发现）                                              | 用户不知道要做什么；在 A 版本搜到的包，进 B 版本还挂在列表里，容易装错目录                                                                                     | `Load()` 先清 `ItemsSource`；`ApplyKind()` 在列表为空时亮出空状态引导                                                                                               |
| 12  | 版本浮层里的版本点不动：主题的 `RowItem` 把 `ListBoxItem` 的 `Focusable` 设成 `False`，WPF 无焦点即不选中，`SelectionChanged` 永不触发（把两个按钮合成一个时用真实鼠标点才暴露） | 库里的版本列表**点了没反应**，只能干瞪眼；旧版靠"选中 → 再点安装"的交互因此根本走不通，只是当时没测到这一步                                                              | 浮层每一项改成 `Button`（自带 hover/pressed 模板），点中即下载；顺带删掉选中态与 `_suppressSelection` 这套补丁式状态                                                                  |
| 13  | CurseForge 的 `slug` 不唯一：查 `create` 会同时返回 Create+（id 372026）和 Create 本体（id 328085），按顺序取第一条拿到的是 Create+（CLI 实测中文搜「机械动力」时本体消失）              | 中文搜「机械动力」出来的全是附属，本体不见了；用户点安装会装错项目                                                                                                | 同 slug 候选按 `downloadCount` 排序取最高，再退回第一条匹配                                                                                                        |
| 14  | **正版账号 UUID 被截成 28 位**：`Msa.FormatUuid` 五段里前四段都对，最后一段写成 `Substring(20, 4)`（应为 12 位），拼出来是 `xxxxxxxx-xxxx-xxxx-xxxx-xxxx` 这种缺尾巴的串（真机 `--cli launch` 才暴露） | 游戏拿残缺 UUID 去查档案查不到，**皮肤静默退回默认皮肤**（`Kai` 那个红上衣），日志里没有任何报错；单机照常进服，只有"我的人物怎么不是我的皮肤"能看出来                                                    | 改成 `Substring(20, 12)`；`EnsureFreshAsync` 加一次性自愈（长度 ≠ 36 就用现有令牌重拉 `/minecraft/profile` 写回，用户不必重新登录）；`selftest` 第 6 条把 UUID 格式变成断言，以后再截断当天就红 |

---

## 5. 当前限制

### 功能缺口

- 仅 Windows（WPF + 注册表探测）
- 模组：增删启停与搜索安装（含中文）都有；依赖会**按声明锁定版本**（见 §6「依赖版本锁定」），但**没有更新检查**，也**不做区间校验**——已装模组的元数据不参与判断，冲突仍以游戏日志为准
- 光影 / 材质包：有搜索下载入口（见 §6.1），但**只覆盖 Modrinth 上架的项目**，只发在 CurseForge 的包搜不到；装完仍需自己装光影加载器（面板里有常驻提示）
- 存档：**没有界面入口**，只能手动往 `versions\<id>\saves\` 放文件
- 中文搜索受限于百科词条：词条里**两个平台都没登记**的模组映射不到，会被过滤掉；百科搜索接口有限流，连搜两次可能提示"太频繁"
- CurseForge 搜索依赖社区镜像 MCIM（`mod.mcimirror.top`）：元数据经镜像，文件下载仍直连 `edge / mediafilez.forgecdn.net`（镜像的文件端点只做 302，不代理内容）。镜像非 CurseForge 官方授权渠道，可能被限流或停服；挂掉时切到 `Modrinth` 不受影响。镜像若加 UA 白名单，需要去它的 GitHub 提交启动器信息
- 存档隔离为强制行为，不提供“材质包共享但存档隔离”这类混合模式；需要共用只能手动复制文件

### 工程风险

- SHA1 校验只对 < 8 MB 文件做（大文件只比大小），极端情况下可能放过损坏包
- 下载重试为“每源 2 次”，断点续传只做到“已完整文件跳过”，不是按字节续传
- 自动化覆盖只有 `--cli selftest`（5 条搜索 + 1 条账号档案）；界面、启动、下载、整合包导入仍靠 CLI 输出与截图人工验证

### 工程基建（已就位）

- **git**：`main` 分支基线 `8cc0cd1`（当时 40 个文件 / 0.64 MB）；当前跟踪 108 个文件（`src/` 下 75 个），工作区干净，改动均已提交
- **忽略规则**：`bin/` `obj/` `/SlopeX.exe` `/.minecraft/` `/.workbuddy/` `/SlopeXData/` `/runtime/` `*.sxpart`，`git check-ignore` 逐项验证通过
- **CRLF**：`core.autocrlf=false`，避免 Windows 换行转换产生全文件伪 diff
- **约定**：Forge / 登录等大改动必须在干净工作区开 `feature/*` 分支，`main` 始终保持可构建

### 环境

- Java 运行时（180 MB）默认仍在 `%APPDATA%\SlopeX\runtime`；要做成"一个文件夹全带走"需在设置里改运行时目录
- 根目录 `saves` / `resourcepacks`（16 MB）是旧版共用模式遗留，现已不被任何版本使用；设置页只提示数量与路径，不自动搬移

---

## 6. 后续计划

> **当前阶段：完善启动器**——集中补既有链路，不铺新功能面。原计划中唯一新增的界面入口「资源包」已在 2026-10-31 落地（见下节），后续继续在既有功能上打磨。
> 大改动仍在独立 `feature/*` 分支上做，`main` 始终保持可构建。

### 6.1 已完成（原 P0 / P1 / P2 中落地的部分，做法与实测见 §2、§8.1）

| 项              | 状态           | 备注                                                                                                   |
| -------------- | ------------ | ---------------------------------------------------------------------------------------------------- |
| Fabric 安装      | ✅ 2026-10-29 | 拉 `fabric-meta` → 写 `inheritsFrom` 版本 JSON → 补加载器依赖。**遗留：Quilt 未做**（协议与 Fabric 高度相似，可复用本模块，工作量约 1/3） |
| Forge 安装（B 方案） | ✅ 2026-10-29 | 官方 installer `--installClient` 无头执行，不自研 processors；取舍是拿不到细粒度进度，只显示「已用时长」。顺带修的撞包问题见 §4 #10            |
| 微软正版登录         | ✅ 2026-11-01 | 设备码 → XBL / XSTS / 档案；Mojang 白名单已通过，登录与静默刷新均实测正常（见 §7.2）                                          |
| 便携（折中）         | ✅ 2026-10-29 | config + logs 进 `SlopeXData\`；Java 运行时留 `%APPDATA%` 但可改位置                                            |
| 模组管理           | ✅            | 列表 / 启停 / 删除 / 打开目录 + 三源搜索安装（Modrinth / CurseForge / 全部，含中文，2026-11-01）                        |
| 光影 / 材质包下载     | ✅ 2026-10-31 | 版本行「资源包」→ 两页签搜索安装，落 `shaderpacks\` / `resourcepacks\`；**不按 MC 版本过滤**，版本由用户挑，不一致只提示 |
| 整合包导入          | ✅            | `.mrpack` / CurseForge zip / MultiMC / 普通 zip                                                        |
| 崩溃诊断           | ✅            | `diagnose` 9 条规则，未命中时给日志位置                                                                           |
| 设置页文案瘦身        | ✅            | 页头改为「主题即时生效；其余点保存」；存档隔离由 4 层文案压到 2 层；旧存档提示可「不再提示」                                                    |
| 法律声明 + 首启确认    | ✅ 2026-10-31 | 完整版在设置页底部可查看；首次启动弹简版，勾选后才可确认，同意结果落盘                                                                  |
| 命令行自检           | ✅ 2026-11-01 | `selftest` 5 条搜索样本 + 1 条账号档案断言，失败返回 1；把「站点改版静默失效」变成当天可见（见 §6「命令行自检」）                                      |

### 6.2 待办（按优先级）

> **当前主线 = 继续打磨启动器**（2026-11-01 02:42 定）。1–5 是打磨项，按需取用；第 6 项是被主动搁置的验证项，不占主线。

1. **模组更新检查 + 依赖版本区间提示**：现在只补 `required` 依赖，不判断「要求 X ≥ 2.0」这类冲突
2. **启动器自更新**：单文件 exe 没有升级通道，换了新版只能手动替换文件
3. **完整性修复升级**：≥ 8 MB 的大文件现在只比大小、不校验 SHA1（见 §5 工程风险）
4. **多任务队列**：并发安装已可用，缺「暂停 / 继续」与失败重试队列
5. **界面细节**：高 DPI 适配、键盘导航、日志搜索与导出
6. **真实服务器联机验证（⏸ 已搁置）**：目前只验到令牌 / 档案 / 皮肤三层，`Setting user: ShakableCat` 只在单机日志里出现过，**没有连过一台强制正版验证的服务器**。真正能验的是「服务器接受这个会话」——`--uuid` 与 `session.minecraft.net/join` 都走通才算数。需要一台开 online-mode 的服务器（用户自开或给地址）；不用改代码，属于验收项而非开发项，所以排到主线之外

### 建议里程碑

| 里程碑 | 内容                       | 状态                  |
| --- | ------------------------ | ------------------- |
| M2  | Fabric + 模组管理            | ✅ 已完成               |
| M3  | 微软登录                     | ✅ 已完成（2026-11-01 白名单通过） |
| M4  | Forge / NeoForge + 整合包导入 | ✅ 已完成               |

### 资源包 / 光影 / 材质（✅ 2026-10-31 落地）

> **状态：已完成**。本阶段主线是完善启动器，这是其中唯一新增的界面入口。

**背景**：版本管理页每行原来只有 启动 / 目录 / 模组 / 删除，换光影或材质包只能自己开文件夹。模组已经有完整的"搜索 → 选版本 → 安装"面板，资源包是同一套东西的延伸，复用成本低。

**界面**

- 版本行按钮顺序：启动 / 目录 / 模组 / **资源包** / 删除
- 「资源包」整页替换版本列表（与模组面板一致，不是弹模态窗口），内部两个页签：**光影包** / **材质包**；每个页签一条搜索框 + 结果表（名称 / 支持版本 / 更新 / 操作）
- 每行只留一个「安装 ⌄」：点开是包版本列表，**点中哪个版本就直接开始下载**（2026-10-31 晚按用户要求把原来的「选择版本 + 安装」两个按钮合成一个，避免"选了版本忘了点安装"的空转）

**行为约定（关键：不做版本限制）**

- 搜索**不按 MC 版本 / 加载器过滤**，与模组面板相反。光影与材质对版本的约束比模组松得多，硬过滤会大面积漏结果 —— 由用户自己在结果里挑包版本
- 因此每行给出判断依据：支持的版本范围、最后更新日期、下载量
- 选中版本后，若该包声明的 MC 版本与当前版本不一致，给一条**非阻断**提示（允许装，只是提醒）
- 落盘：`versions\<版本 ID>\shaderpacks\`、`versions\<版本 ID>\resourcepacks\`（`VersionEnv.Dirs` 已包含这两个目录，隔离逻辑不用改）
- 不自动补依赖，也不许装进 `mods\`：光影 / 材质在 Modrinth 上没有 `required` 依赖
- 状态行只显示过程消息（搜索 / 下载 / 结果），已装数量放在页头副标题——否则装完那句"已安装到 …"会被计数覆盖

**光影加载器提示（常驻在光影页签顶部，不用 Toast）**

> 光影需要额外的加载器才生效：**Fabric** 装 Iris + Sodium；**Forge / NeoForge** 装 Oculus（一般配 Embeddium），或直接用 OptiFine。只把 `.zip` 放进 shaderpacks 是看不到光影的。

**技术实现**

- 数据源：Modrinth `project_type` = `shader` / `resourcepack`，仍是 `/v2/search`；`ModrinthSource.SearchAsync` 增加 `projectType` 参数（模组链路行为不变）
- 版本列表：`/project/{id}/version` 取全部版本 → `PackVersionRef`（版本号 / 声明支持的 MC 范围 / 文件名 / 大小 / 日期），选择浮层贴按钮下方弹出，不弹对话框
- 浮层里的每一项是**按钮**而不是 `ListBoxItem`：主题的 `RowItem` 把 `Focusable` 设成 `False`，WPF 的 `ListBoxItem` 没焦点就不会被鼠标选中（`SelectionChanged` 永不触发）。改成按钮后"点中即下载"，也不需要维护选中态
- 下载：`PackInstaller.InstallAsync` 取该 version 的 primary 文件 → `Net.DownloadToFileAsync`（`.sxpart` + SHA1 + 超时），**已存在则跳过**（幂等）
- 文件名里的 `§` 颜色码（作者故意加的，游戏里显示彩色）在状态行显示前会剥掉，盘上文件名保持原样
- 细节：换版本、换页签都清空上一批结果（结果与版本无关，但"支不支持当前 MC"的判断依赖版本）；首次进入显示空状态引导，不留一张空白卡片

**实测（2026-10-31，Debug + CLI + UIA 截图）**

- `packs-search complementary` → 20 个光影结果，`版本区间`、更新日期、下载量齐全
- `packs-versions complementary-reimagined` → r5.9.3 / r5.2 / r5.1 / r5.0（新的在前）
- `packs-install 1.20.1-forge-test shader complementary-reimagined` → `versions\1.20.1-forge-test\shaderpacks\ComplementaryReimagined_r5.9.3.zip`（553,397 字节；zip 内 `shaders\*.properties`、`block.properties` 齐全，是真包不是错误页）
- `packs-install … resourcepack stay-true-amendments` → `resourcepacks\Stay_True_Amendment3.zip`（40.8 KB，含 `assets\minecraft\…`）；再执行一次输出「已存在，跳过」
- UI：1100px 宽下版本行 5 个按钮不换行；光影页 20 条、材质页 11 条；版本浮层不越窗口边界；切到材质包页签后常驻提示自动隐藏
- UI 全链路（UIA 真实鼠标点击）：点「安装」→ 浮层列出 4 个版本 → 点 `v1.4` → 开始下载 `photon_v1.4.zip`；材质页同链路点 `4` → 落盘 `resourcepacks\Stay True x §7Better§bOres§93D§0.zip`（544,122 字节），状态行显示"已安装 … 注意：这个包声明支持 1.21 – 26.2，与当前 1.20.1 不一致"（`§` 已剥成 `BetterOres3D`）
- 截图：`docs/screenshots/packs-search.png`、`docs/screenshots/packs-material.png`

**有意没做**

- 不在面板里代装光影加载器：Iris / Oculus 本质是模组，走模组面板，避免两处入口互相打架（提示语已说明去哪里装）
- 不解析 zip 里的 `pack.mcmeta` / 光影版本做二次校验，只按 Modrinth 声明提示——多一层解析带来的误判风险大于收益

**工作量口径**：新增 `Controls/PackPanel.xaml`（239 行）+ `.xaml.cs`（366 行）、`Modules/ModMods/PackInstaller.cs`（178 行）；`Modrinth.cs` 参数化 1 处 + 新增全量版本查询；`PageVersions` 加按钮与整页切换 1 处；`Cli.cs` 加 3 条命令（`packs-search` / `packs-versions` / `packs-install`）；文档同步 2 份 + 截图 2 张。

### 模组搜索来源：Modrinth / CurseForge / 全部（✅ 2026-11-01 落地）

**背景**：早年砍掉 CurseForge 搜索的原因是官方接口强制 `x-api-key`，无 Key 一律 403。社区镜像 MCIM（`mod.mcimirror.top`）在服务端替客户端持有 Key、按官方路径缓存响应，这条路重新变得可行——代价是多了一个第三方依赖。

**先决验证（都是实打实请求过的，不是文档推断）**

| 端点 | 结果 |
| --- | --- |
| `GET /curseforge/v1/mods/search` | 200 / 89 KB；`gameId` / `classId` / `gameVersion` / `modLoaderType` / `sortField` 全部生效，**不需要 x-api-key** |
| `GET /curseforge/v1/mods/{id}/files?gameVersion=&modLoaderType=` | 200，返回 `downloadUrl` + `hashes`（`algo=1` 即 SHA1）+ `dependencies`（`relationType=3` 是必需） |
| `GET /curseforge/v1/mods/{id}/files/{fid}/download-url` | 200，作者禁止第三方分发时用来补下载地址 |
| `GET /files/{a}/{b}/{name}` | **302 → `mediafilez.forgecdn.net`**（镜像不代理文件内容，只跳转） |
| `GET /modrinth/v2/search` | 200，`facets` 与官方完全兼容 |
| 分类 ID | `6` Mods / `12` Resource Packs / `6552` Shaders / `4471` Modpacks / `6945` Data Packs |

**路由规则（对应界面三个 Chip）**

| 选择 | Modrinth 侧 | CurseForge 侧 |
| --- | --- | --- |
| `Modrinth` | 官方 `api.modrinth.com`（数据最新） | 不搜 |
| `CurseForge` | 不搜 | 镜像 |
| `全部`（默认） | **镜像**（与 CF 共用一个出口） | 镜像 |

**中文关键词**：三条路都先问 MC百科；词条页一次读完，同时解析出 Modrinth slug 与 CurseForge slug（原来只解 Modrinth，分两次读会把详情页请求量翻倍）。`全部` 模式优先试 CurseForge，拿不到再回落 Modrinth——这次要捞回来的主要就是只在 CF 上架的模组。

**合并与去重**：Modrinth 在前、CurseForge 补充在后，按「标题去空格小写」去重。不让 CF 参与混排是因为它的搜索只能按热度排，相关度明显不如 Modrinth。

**实测**

- `mods-search … jei modrinth` → 12 条，第一条 `Just Enough Items (JEI)`（id `u6dRKJwZ`）
- `mods-search … jei curseforge` → 12 条，第一条 JEI（id `238222`，下载量 642.5M）
- `mods-search … create all` → Modrinth 12 + CurseForge 12，去重后 18
- `mods-search … 机械动力 all` → 5 条可安装，本体 `机械动力 (Create)` 落在 CurseForge `328085`
- `mods-install … jei curseforge` → 下载 `jei-1.20.1-forge-15.74.0.232.jar`（4.5 MB）+ 依赖 `mezz_config-1.20.1-forge-0.6.10.jar`，**CF 的依赖解析也是通的**
- `mods-install … jei modrinth` 回归 → 15.74.0.233 + 同一个依赖，Modrinth 链路未受影响
- UI（UIA 真实鼠标点击 + 截图）：三个 Chip 互斥、来源胶囊标签、状态行、中文结果全部核对

**有意没做**

- 不让光影 / 材质包走 CF 镜像：现在这条链路只锚 Modrinth 单一源，接第二个源要重做"D 版本过滤 + 版本浮层 + 目录计数"三处，而收益（CF 独占的光影/材质）远小于模组
- 不把 CF 的文件下载也改成镜像路径：镜像该端点只做 302，多一跳没有收益，反而多一个失败点
- 不做双源结果混排：理由见上，相关度质量差距太大

**工作量口径**：新增 `Modules/ModMods/CurseForge.cs`（约 380 行）；`Modrinth.cs` 加官方/镜像双基址 + `SourceText` / `DedupeKey` / `Mirror` 三处模型字段 + `ModFileRef.RequiredDepIds`；`ModSearch.cs` 重写为来源路由（新增 `ModSourceFilter`）；`ModInstaller.cs` 按来源分派文件与依赖；`Net.cs` 加 `PostJsonAsync`；`ModSearchDialog` 加三个 Chip 与来源胶囊；`Cli.cs` 三条命令加来源参数；文档同步 2 份。

### 分段下载与下载进度（✅ 2026-11-01 落地）

**问题**：光影 / 材质包 / 模组走的都是 `Net.DownloadToFileAsync` 的单条连接 `CopyToAsync`，而 `cdn.modrinth.com` 对单连接限速很低。本机实测同一个 7.9 MB 文件：

| 连接数 | 聚合速度 | 7.9 MB 耗时 |
| --- | --- | --- |
| 1 | 35 KB/s（1 MB 分段实测 29.9 s） | ~4 分钟 |
| 8 | 322 KB/s | ~26 s |
| 16 | 1.24 MB/s | ~7 s |
| 32 | 2.83 MB/s | ~3 s |

结论：瓶颈不在源站，在**每条连接 35 KB/s** 这一层，换镜像、加缓存都没用，只能把并发连接开出来。PCL2 也是直接连 `cdn.modrinth.com` / `mediafilez.forgecdn.net`（日志里 411 次 CF、37 次 Modrinth），它快的原因是 `ModNet.vb` 里的分段引擎：`IsNoSplit` 阈值 1 MB、`FilePieceLimit = 256 KB`、线程上限 = 设置项 + 1（默认 63 + 1 = 64）、速度低于 256 KB/s 才继续加线程。

**实现**（新增 `Modules/ModNet/Downloader.cs`，465 行）

| 环节 | 做法 |
| --- | --- |
| 探测 | 先 HEAD 取 `Content-Length` + `Accept-Ranges`；HEAD 被拒或没有该头时，用 `Range: bytes=0-0` 实探 |
| 跳转 | 用 `resp.RequestMessage.RequestUri` 拿**跳转后的真实地址**再切分片。CF 的 `edge.forgecdn.net` 带 Range 会直接 404，只有它跳到 `mediafilez.forgecdn.net` 才正常支持分段——这是接入 CF 后踩到的坑 |
| 切片 | 目标约 `2 × 线程数` 段，单片 256 KB–8 MB；小于 1 MB 或大小未知走单流 |
| 并发 | `File.OpenHandle` + `RandomAccess.WriteAsync` 直接写目标文件的绝对偏移，**不产生一堆临时分片、也不需要合并**；线程数取 `Config.DownloadThreads`（1–64，默认 16） |
| 重试 | 单段三次重试（从已写到的 `pos` 续传，进度不倒退），三次仍失败或返回 200 则整体回退单流 |
| 校验 | 落位前照旧校验总长度 + SHA1，失败删 `.sxpart` |
| 进度 | 250 ms 采样一次，`DownloadProgress` 带 done / total / 速度；界面按 `42% · 3.3 MB / 7.9 MB · 1.2 MB/s` 显示 |

**实测**

- `packs-install … rethinking-voxels`（7.9 MB）：16 线程 / 32 段，**日志间隔 8.1 s**，SHA1 通过；改动前同一文件 120 s 只下到 4.0 MB
- `mods-install … create modrinth`（18.2 MB）：32 线程 65 段 15.8 s；64 线程 73 段 9.8 s
- `mods-install … jei curseforge`（4.6 MB）：解析跳转后 19 段，0.7 s（CF 单连接本来就快，分段只是不拖后腿）
- UI（UIA 真实点击 + PrintWindow 截图）：`8/16/32/64` 档位默认 16 高亮；模组对话框与资源包面板都能看到进度条、百分比、已下载/总大小与实时速度；下载结束自动收起

**高并发档位的风险确认（2026-11-01 补）**

`32` / `64` 这两档在部分家用路由器上会把 NAT 表压满，表现为整屋断网或下载反复重试，所以点这两档时先弹一次确认框（`Controls/ConfirmDialog.xaml`，通用组件：标题 + 说明 + 风险条目 + 备注 + 取消/确认）：

- 32 档列三条代价（连接数压力、同网络卡顿、可能被限频），确认按钮「确认使用 32」
- 64 档措辞更强（整屋断网要重启路由器、占满无线信道、CDN 单 IP 并发被拒后退回单流），确认按钮「仍然使用 64」
- 取消或 Esc 退回原档位；确认后才点亮新档并 toast 提示「点保存设置后生效」（写盘仍只在「保存设置」时发生）
- `8` / `16` 是安全区，直接切换；点已选中的档只还原高亮，不弹窗

两个必须记住的实现细节：

1. `ToggleButton` 在 `Click` 之前就把 `IsChecked` 翻过来了，所以界面要单独记一份 `_threads`（当前档位）——否则"取消后回退"没有依据，还会出现新旧两档同时高亮的画面。
2. `ShowModal` 刚把内容塞进视觉树时 `Focus()` 还不生效，弹窗自己的 `FocusInitial()` 会落空、**Esc 无人接管**（实测按 Esc 完全没反应）。改成在 `Loaded` 里补一次焦点才修好。

**实测**（UIA 真实鼠标点击）：点 32 → 弹窗只有 16 高亮（不是两档同亮）→ 确认 → 32 高亮 + toast；点 64 → 取消 → 回到 16；点 32 → Esc → 弹窗关闭、仍是 16；点 16（已选中）→ 不弹窗、高亮保持。

**限制**

- 单连接被限速的链路收益最大；本来就快的源（forgecdn、BMCLAPI）分段只增加连接开销，所以 < 1 MB 不拆
- 线程数开太大（64）会压路由器 NAT 表，个别家用网络会出现"下到一半网络异常"，这也是做成可调而不是写死的原因
- 服务端若不支持 Range 或对 Range 请求返回 200/403/404，会自动退回单流，不会比原来更慢

**工作量口径**：新增 `Modules/ModNet/Downloader.cs`（465 行，含 `DownloadProgress` 类型与分段引擎）；`Net.cs` 的 `DownloadToFileAsync` 收口到新引擎并加 `progress` 参数；`Config` 加 `DownloadThreads`（1–64，默认 16）；`PackInstaller` / `ModInstaller` 透传进度回调（模组状态行顺带显示 `2/3`）；`PackPanel`、`ModSearchDialog` 各加一行进度区（文字 + `Bar` 进度条）；设置页加「下载线程」卡片（4 档 Chip）；新增通用确认框 `Controls/ConfirmDialog.xaml(.cs)`（114 行）并接在 32 / 64 档上；README 与本文档同步。

### 设置页滚轮（✅ 2026-11-01 落地）

现象：设置页滚轮"不顺畅、有时卡住"。

**两个独立原因，都在 WPF 默认行为里**

1. **步长太小且没有过渡**。WPF 的 `ScrollViewer` 一档滚轮只走 `WheelScrollLines × 16` px：本机系统设置是 2 行，也就是**一格 32 px**，820 px 高的窗口要滚二十多下；而且是瞬移，没有缓动，观感就是一顿一顿。
2. **内层控件把滚轮吃掉**。WPF 的 `ScrollViewer` 收到滚轮后，**不管自己还能不能滚，都会把事件标成 Handled**。设置页里 `JavaList`（MaxHeight 150）、`InputJava` 等自带 ScrollViewer，鼠标停在它们上面时滚轮被内层收走，外层整页完全不动——这就是"卡住"。同理也影响首页的「最近启动」列表。

**做法**：新增 `Controls/ScrollAssist.cs`（161 行），附加属性 `ScrollAssist.Smooth="True"` 挂在页面级 `ScrollViewer` 上（`PageSettings`、`PageHome`）：

- 在**隧道阶段**（`PreviewMouseWheel`）接管：先看事件源到外层之间有没有内层 `ScrollViewer`；内层这个方向还能滚就放行，滚到头（或根本不能滚）才由外层平滑滚动，并 `e.Handled = true` 阻止内层再吃一次。
- 步长按系统行数换算但不低于 88 px（2 行 → 88、默认 3 行 → 96），高精度滚轮按 `|Delta| / 120` 等比缩小，触控板不会一抖就跳一段。
- 缓动用影子附加属性 `Offset` + `DoubleAnimation`（OutCubic，110–260 ms，距离越远稍长），`VerticalOffset` 只读，所以动画跑在影子上、回调里 `ScrollToVerticalOffset` 落地。
- 250 ms 内的连续滚轮在上一次**目标值**上累加，避免每档都从当前位置重新缓动（那样会很黏）；到顶/到底自动 clamp。系统里关掉动画（`ClientAreaAnimation=false`）时直接落位。

**实测**（UIA 注入真实滚轮事件，同一台机器、同一窗口位置，`WheelScrollLines=2`）

| 场景 | 旧版（发布版 exe） | 新版 |
| --- | --- | --- |
| 鼠标停在 Java 列表上滚 3 格 | 30.0% → **30.0%（纹丝不动）** | 30.0% → 47.4% |
| 鼠标停在普通卡片上滚 3 格 | 30.0% → 33.2%（一格仅 16 px 观感） | 47.4% → 64.7% |
| 再往回滚 2 格 | 33.2% → **33.2%（又卡住）** | 64.7% → 53.1% |

每一步的"立即读数"都小于"400 ms 后读数"（如 39.3% → 47.4%），说明确实是过渡动画而不是瞬移。

**工作量口径**：新增 `Controls/ScrollAssist.cs`（161 行）；`PageSettings.xaml` / `PageHome.xaml` 各加一处 xmlns 与 `ScrollAssist.Smooth="True"`；无其它改动，列表控件自身的滚动逻辑没动。

### 命令行自检 selftest（✅ 2026-11-01 落地）

**要解决的问题**：模组中文搜索是抓 `www.mcmod.cn` 的 HTML 再用正则解析（`Modules/ModMods/McMod.cs`）。站点改版时不会抛异常，只会**安静地返回空列表**——等用户反馈才有人知道，中间可能已经坏了好几天。此前这套验证只以「一次性手工测试记录」的形式躺在 §8.1（「中文搜索（MC百科）」那一行），不是断言，没人跑也不会报警。

**做法**：新增 `Modules/ModCli/SelfTest.cs`，入口 `SlopeX.exe --cli selftest [--mc 1.20.1] [--loader forge] [-v]`，跑 5 条搜索查询，末尾再加 1 条本机账号档案断言：

| # | 样本 | 断言 | 覆盖的环节 |
| --- | --- | --- | --- |
| 1 | 机械动力（Modrinth） | 结果含 `LNytGWDc` | 百科检索 + 词条链接解析 + Modrinth 官方接口 |
| 2 | 苹果皮（Modrinth） | 结果含 `EsAfCjCV` | 中文别名 → 项目映射（与 #1 走的是不同词条） |
| 3 | 钠（forge） | **不得**含 `AANobbMI`，且百科条目数 > 0 | 版本 / 加载器过滤；条目数为 0 直接判失败，避免「解析崩了」被当成「过滤正确」 |
| 4 | 机械动力（全部） | 含 CurseForge `328085` | 「全部」优先 CF：CF slug 解析 + 同 slug 候选择高下载量 + CF 文件列表接口 |
| 5 | jei（全部） | Modrinth ≥ 5 条、CurseForge ≥ 5 条，且含 `u6dRKJwZ` | 两个源同时存活（英文直搜不走百科） |
| 6 | 本机账号（已登录才跑） | UUID 必须 36 位、能被 `Guid` 解析且是标准 8-4-4-4-12 | 账号档案完整性。少了这条，UUID 被截断只能靠"皮肤怎么不对"发现（§4-14）；未登录时只 SKIP，不算失败 |

失败时打印原因 + 实际前 5 条（`来源 ID「标题」`）+ 计数（百科 N 条 / 核对 N / 可安装 N），最后返回**退出码 1**；全部通过返回 0。加 `-v` 时连通过的条目也打印完整结果。

**实测**

```
# 正常（默认 1.20.1 / forge）
[1/5] PASS    3.9s  中文 → Modrinth 官方  「机械动力」
[2/5] PASS    2.4s  中文 → 别名短名  「苹果皮」
[3/5] PASS    4.3s  版本过滤（Sodium 只支持 Fabric）  「钠」
[4/5] PASS    3.9s  全部 → 优先 CurseForge  「机械动力」
[5/5] PASS    1.1s  英文直搜 → 两源存活  「jei」
[6/6] PASS  正版账号档案  ShakableCat  xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
6 项检查：6 通过，0 失败，共 15.6 s        → exit 0
```

红路径用 `--mc 1.7.10`（Create / AppleSkin 都不支持该版本）验证过：4 条搜索失败、逐条打印原因与实际结果、`exit 1` —— 不会出现「链路已经坏了，自检还是绿的」（第 6 条账号检查与 `--mc` 无关，这时仍是绿的）。样本答案是按 **1.20.1 / forge** 核对的，换 `--mc` / `--loader` 时断言不一定仍成立，这是预期行为。

**为什么不挂进 `build.cmd`**：它依赖外网与 MC百科 的在线状态，把网络抖动变成构建失败没有意义。要挂就放在发布**之后**单独执行、失败只警告不阻断；这条按需要再加。

**工作量口径**：新增 `Modules/ModCli/SelfTest.cs`（255 行）；`Cli.cs` 加 1 个 `case`、1 行 help，`Out` 由 `private` 提为 `internal` 供复用；README 与本文档同步。

### 依赖版本锁定（✅ 2026-11-01 落地）

**问题**：装模组时自动补必需依赖，但取的是「**这个依赖项目的最新适配版**」。作者验证过的往往是某个具体版本，最新的那个可能已经越过范围。

**实测证据**（不是推测）：Iris Shaders `1.7.6+1.20.1` 的必需依赖 Sodium 声明为 `version_id = ryOMVRuG`（`0.5.12-beta.2`），而 Sodium 在 1.20.1 / fabric 上的最新版是 `0.5.13`（`OihdIimA`）。旧逻辑装 0.5.13，新逻辑装 0.5.12-beta.2。

**做法**

- `ModFileRef` 增加 `VersionId`（`ListFilesAsync` 顺手填上，CurseForge 侧留空）
- `ModrinthSource.RequiredDepsAsync` 改为直接 `GET /v2/version/{id}` 取依赖，不再按 `version_number` 反查整个项目版本列表——少一次全量请求，也不会因版本号重复认错；返回三元组 `(projectId, versionId, name)`
- 新增 `ModrinthSource.FileOfVersionAsync`：按 `version_id` 精确取主文件，并校验它的 `game_versions` / `loaders` 是否覆盖当前目标
- `ModInstaller.FilesForAsync`：有 `version_id` 就装那一版；**声明版本不适配当前 MC 版本 / 加载器时回落到最新适配版**并记日志（宁可变通，也不要装一个明确不兼容的文件）
- 队列项带上 `Pinned`，已访问集合改成 `Dictionary<key, pinned>`：同一个前置被两个模组要求不同版本时保留先到的并 `Log.Warn`

**覆盖面（重要，别高估）**：扫了 26 个 required 依赖，只有 1 个带 `version_id`（约 4%）。Modrinth 上多数作者只标项目不标版本，所以这条改动的收益集中在「严格配对的模组」（Iris ↔ Sodium 这类），不是普适收益。真正的普适问题得靠下一步的**元数据体检**——读已装 jar 的 `fabric.mod.json` / `mods.toml`，拿实际 modid 与版本做区间校验。

**工具**：`SlopeX.exe --cli mods-deps <版本> <项目ID|slug>` 打印该模组的必需依赖，标出哪些锁定了版本、最终解析到哪个文件。

```
$ SlopeX.exe --cli mods-deps 1.20.1 iris
目标：1.20.1 → MC 1.20.1，加载器 原版
项目文件：1.7.6+1.20.1  iris-1.7.6+mc1.20.1.jar
  versionId：s5eFLITc
必需依赖：1 个
  AANobbMI  [锁定 ryOMVRuG]
      → mc1.20.1-0.5.12-beta.2-fabric  sodium-fabric-0.5.12-beta.2+mc1.20.1.jar
```

**工作量口径**：`Modrinth.cs` 加 `VersionId` 字段、`FileOfVersionAsync`、`StrArr`，重写 `RequiredDepsAsync`；`ModInstaller.cs` 队列加 `Pinned`、新增 `FilesForAsync`、已访问集合改字典；`Cli.cs` 加 `mods-deps` 子命令与 1 行 help。

**未验证**：回落分支（声明版本不含当前 MC / 加载器）没找到真实样本，只做了代码审查——真要复现得手工构造一个错配的 `version_id`。

### 美术与主题（结论，2026-10-31 精简）

**进度**：S1 地基（双主题 token + 间距 / 圆角 / 动效刻度 + `ThemeService`，2026-10-29 完成）→ S2 边做边补 → 当前 **S3 视觉定案**。`docs/design-language.md` §5 还差 3 个组件（`ComboBox`、`ContextMenu`、`Steps`），都缺真实使用场景，等有地方用再补——提前设计等于按想象中的用法做一版没人用的东西。

**命名**：浅色「极简白纸」（默认）、深色「极简墨纸」。沿用纸媒隐喻——白纸以墨写字，墨纸以浅色显字，正好是 `Ink` token 的反相；深色底是暖调近黑，刻意不叫「极简黑」，避免"纯黑底"的误导。

**切换模型**：主题 × 模式二维。主题卡片选白纸 / 墨纸；「外观模式」三选一（跟随系统 / 仅浅色 / 仅深色）。跟随系统读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`，监听 `WM_SETTINGCHANGE` 实时切换、不重启；选了固定模式就忽略系统变化。

---

## 7. 决策结论（2026-10-29 拍板，均已落地）

> 保留当时的判断依据，方便日后回看"为什么这么选"；落地状态见 §6.1。

| #   | 事项               | 结论                                      | 落地情况                       |
| --- | ---------------- | --------------------------------------- | -------------------------- |
| ①   | Forge / NeoForge | **要做，采用 B 方案**（不自研 processors）          | ✅ 已落地，理由见 §7.1             |
| ②   | 微软正版登录           | **必须做**（尊重正版玩家）                         | ✅ 2026-11-01 实测可用，见 §7.2 |
| ③   | 便携               | **折中方案**（config + logs 进 `SlopeXData\`） | ✅ 已落地，细节见 §7.3             |

### 7.1 ① Forge 为何选 B 方案

- **选它的理由**：官方 installer 自己知道怎么装（processors、srg 映射、client-extra 拆分全是它的内部逻辑），我们只负责"下载 + 用正确的 Java 跑 + 收集输出"，成功率接近 100%。
- **接受的代价**：进度不可见（只能显示"安装中 + 已耗时"，拿不到"正在处理第 3 个库"这类细粒度信息）；installer 走官方 maven，国内可能慢，只能靠镜像环境变量缓解。
- **不是终局**：Fabric 那套是我们自己拼 JSON，Forge 这块是"外包"给官方。将来若要离线复装或细粒度进度，再升级到 A 方案（自研 processors），B 保留为兜底路径。

### 7.2 ② 微软登录（✅ 2026-11-01 白名单通过）

- 技术路径：设备码 → Xbox Live → XSTS → `login_with_xbox` → `/minecraft/profile`，代码在 `ModAccount/Msa.cs`。
- 令牌模型：Mojang 下发的 access token 只有 **24 小时**（取响应里的 `expires_in`，86400 s），过期后由 `refresh_token` 静默换新、refresh 自身也一并轮换，**用户不需要重新登录**；`Expired` 判定提前 2 分钟，避免卡在整点边界。只有改密码 / 撤销授权 / 长期不用才会要求重登。到期时刻只在 `--cli account` 里显示，**界面刻意不显示**——普通用户看到「有效期至 …」容易误以为账号要到期。
- **Mojang 白名单已通过**（[说明](https://help.minecraft.net/hc/en-us/articles/16254801392141)，申请入口 <https://aka.ms/mce-reviewappid>，2026-11-01 放行）。此前卡了数天：XBL / XSTS 两段一直是通的，只有最后一步 `login_with_xbox` 固定 403 `Invalid app registration`。
- 通过后的实测（发布版 exe）：
  - `--cli account` → `正版账号：ShakableCat`，令牌有效；
  - 用缓存令牌调 `/minecraft/profile` → **200**，`name=ShakableCat`、`id=xxxxxxxx…`、皮肤 `ACTIVE`、2 个披风；
  - 把 `accounts.json` 的 `ExpiresAt` 人为改到 2020 年触发刷新 → `--cli plan 1.20.1` 输出「账号：ShakableCat（正版）」，access_token 与 refresh_token **都已轮换**，到期顺延 24 小时，新令牌再取档案仍是 200。
  - 真机启动 `--cli launch 1.20.1`：`Setting user: ShakableCat` → 单人存档 `logged in with entity id 1` → 关窗口 `退出码 0`。**第一轮人物是默认皮肤**，顺着命令行查出本地 UUID 只有 28 位（§4-14）；补全后同一存档换成账号真实皮肤，详见 §8.1「正版启动真机 / 皮肤修复复核」。
  - 仍未验证：连一台**强制正版验证**的服务器（需要对方的 session 校验，单机只走到档案与皮肤）。此项已搁置，见 §6.2 第 6 条。
- 保留的兜底：任一环节失败自动回落离线登录并给出中文原因，不阻塞启动；403 `Invalid app registration` 的解释文案留在代码里，作为「ClientId 被吊销 / 换应用」时的排障提示。

### 7.3 ③ 便携折中方案的具体内容

- **迁走**：`config.json`、`logs\`（几十 KB，拷走即带设置和日志）。
- **留下**：`runtime\java-<主版本>\`（180 MB）仍在 `%APPDATA%\SlopeX`，避免每次拷贝多搬 180 MB；设置页提供修改入口，想彻底便携可自行指到启动器目录。
- **回落**：`DataDir` 优先级 `SlopeXData\`（可写）→ `%APPDATA%\SlopeX`，防止启动器被放到 `C://Program Files` 时因无写权限直接崩。
- **迁移规则**：首次启动若发现旧 `%APPDATA%\SlopeX\config.json` 且 `SlopeXData\` 不存在，复制过去但**保留原文件**作为回滚保险。

---

## 8. 附录

### 8.1 实测记录

| 项目                | 数据                                                                                                                                                                                                                                                              |
| ----------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.20.1 安装         | 701.7 MB / 3,598 资源 / 190 秒                                                                                                                                                                                                                                     |
| 1.20.1 启动         | 进游戏，`Setting user: Steve`，OpenAL 初始化，退出码 143（主动结束）                                                                                                                                                                                                              |
| 26.3 启动           | Java 25、LWJGL 3.4.3、SDL 建窗、RTX 3060 OpenGL 3.3                                                                                                                                                                                                                  |
| Java 25 自动安装      | 清华 TUNA，55.8 MB，3.9 秒，解压到 `runtime\java-25`                                                                                                                                                                                                                     |
| 存档隔离（强制）          | `--cli plan` 输出：1.20.1 → `versions\1.20.1\saves`，26.3 → `versions\26.3\saves`，互不可见                                                                                                                                                                              |
| 存档隔离真机验证          | 26.3 运行时 java 命令行含 `--gameDir I:\SlopeX Launcher\.minecraft\versions\26.3`；`versions\26.3\saves` 保持空（未继承任何存档），根目录 `saves` 的 `mtime` 未变（未被读取/写入）                                                                                                                 |
| 旧配置清理             | 启动一次后 `config.json` 中 `VersionIsolation` / `IsolationSeeded` 两字段自动消失                                                                                                                                                                                            |
| 主题切换（运行中）         | 设置页点「极简墨纸」→ 背景/文字即时变化，**不重启**；日志记录 `主题切换为 Dark（深色）`                                                                                                                                                                                                             |
| 主题持久化             | 重启后按 `config.json` 的 `Appearance` 生效；旧配置缺字段时自动补写 `Theme` / `Appearance`                                                                                                                                                                                         |
| 深色对比度抽检           | 首页 / 设置 / 任务三页截图：主按钮为浅底深字（`OnInk` 生效）、Logo 勾可见、日志文字可读、卡片描边可辨                                                                                                                                                                                                    |
| 图标生成              | `ico/SlopeX.ico` 16.6 KB，帧尺寸 16/20/24/32/40/48/64/128/256；256 帧目视与原标记一致                                                                                                                                                                                         |
| 图标嵌入              | 从 `SlopeX.exe` 反提取图标成功（32×32），说明 `ApplicationIcon` 已编进单文件                                                                                                                                                                                                       |
| 窗口图标              | `Icon="pack://…/Assets/SlopeX.ico"` 启动无异常，窗口正常渲染（资源缺失会在 `InitializeComponent` 抛错）                                                                                                                                                                               |
| Fabric 安装         | `install-fabric 1.20.1` → 8 个依赖 / 2.9 MB / 3 秒；重复执行 1.1 秒（全部命中跳过，幂等）                                                                                                                                                                                            |
| Fabric 校验与命令行     | `verify` 通过（96 个库，继承链 `1.20.1 → fabric-loader-…`）；`plan` 的 mainClass 为 `KnotClient`，classpath 含 fabric-loader / intermediary / sponge-mixin                                                                                                                     |
| Fabric 真机         | 日志 `Loading Minecraft 1.20.1 with Fabric Loader 0.20.1`、`MIXIN Subsystem 0.8.7 … Service=Knot/Fabric`、`Setting user: Steve`                                                                                                                                     |
| 模组加载验收            | 放入 Sodium 0.5.13 → `Loading 10 mods`（含 sodium 及其内置 fabric-api 模块）、`Loaded configuration file for Sodium: 42 options`、资源重载列表含 sodium                                                                                                                             |
| Forge 安装（CLI）     | `install-forge 1.20.1` → 推荐 47.4.28；installer 8.4 MB / 102.6 秒 / 退出码 0；生成 `1.20.1-forge-47.4.28.json`（inheritsFrom 1.20.1、mainClass BootstrapLauncher、29 库）                                                                                                     |
| Forge 校验与启动       | `verify` 117 库通过、Java 要求 17；`plan` 含 `-p` 模块路径与 `--launchTarget forgeclient`；真机 `ModLauncher 10.0.9` → `MinecraftForge v47.4.28 Initialized` → 进主菜单                                                                                                             |
| Forge 模组加载验收      | 放入 AppleSkin Forge 版 → 生成 `config/appleskin-client.toml`（1361 字节），说明模组已加载并注册配置                                                                                                                                                                                  |
| Forge 界面链路        | 点「安装」→ Forge → 开始安装 → 状态栏「正在安装 Forge 66.1.0（26.3）」、行内变「安装中…」、installer 开始下载                                                                                                                                                                                     |
| 整合包识别             | `pack-info fo.mrpack` → Modrinth / Fabulously Optimized 5.4.1 / MC 1.20.1 / fabric 0.14.23 / 51 个文件                                                                                                                                                             |
| 整合包导入（Modrinth）   | 51/51 下载成功、解压 27 个覆盖文件；`versions\Fabulously-Optimized-5.4.1` 下 48 个 jar；启动后 `Loading 149 mods` 并进主菜单                                                                                                                                                            |
| 整合包导入（CurseForge） | 合成包（Sodium 394468/6260639）→ cfwidget 反查文件名成功；jar 971,552 字节 / 750 条目 / 含 `fabric.mod.json`，overrides 已解压                                                                                                                                                        |
| 整合包标签             | 版本页对整合包版本显示「Fabric」+「整合包」两个标签（此前误标为「原版」，因标签只看版本自身 ID 与 libraries）                                                                                                                                                                                               |
| NeoForge 版本解析     | `neoforge-list 26.3` → 21 个版本、最新 26.3.0.99；`neoforge-list 1.21.1` → 260 个、最新 21.1.265；`1.20.1` 明确拒绝（该线为 net.neoforged:forge）                                                                                                                                    |
| NeoForge 安装       | 26.3 → 7.8 秒（依赖已预取）；`verify` 140 库通过、Java 25；真机 `Mod List: AppleSkin / Minecraft 26.3 / NeoForge 26.3.0.99`，`config/appleskin-client.toml` 已生成                                                                                                                  |
| 依赖补齐              | 首次安装后 `verify` 报 5 个缺失库，`fix-libs` 一次性补 63 个（含运行期库），补后校验通过                                                                                                                                                                                                      |
| 登录错误文案            | `msa-explain 403 "Invalid app registration…"` → 输出「没有状态查询页 / 约一周 / 再登录不报即通过 / 可先用离线登录」；401、429、503 各自命中对应分支                                                                                                                                                     |
| 空状态组件             | 三处列表统一为「标题 + 提示」两行；任务页浅色、深色截图均清晰（深色下提示为 `Muted`，仍可读）                                                                                                                                                                                                            |
| Toast             | 设置页点「保存设置」→ 底部居中深色提示条「设置已保存」，350ms 内截到淡入中间态                                                                                                                                                                                                                     |
| 模态动效              | 用临时环境变量钩子开/关模态各截一张：开启后卡片居中不透明、背景被遮罩压暗；700ms 后关闭，遮罩与卡片完全消失、页面恢复（钩子验完已删除）                                                                                                                                                                                         |
| 模组元数据解析           | `mods-list` 实测：AppleSkin 3.0.10+mc26.3（NeoForge，neoforge.mods.toml + MANIFEST 兜底）、AppleSkin 2.5.1+mc1.20.1（Forge，mods.toml）均正确解析                                                                                                                                |
| 模组启停闭环            | 点「禁用」→ 磁盘改名 `.jar.disabled`、状态翻转、Toast「已禁用 Sodium」；点「启用」→ 文件名恢复。两方向均实测                                                                                                                                                                                          |
| 主题音乐              | `music-test` 客观验证：主题 RetroFuture、音量 25%（当轮默认值，后调整为 20% → 15%）、3 秒后播放位置 00:02、`HasAudio=True`；点击按钮日志显示暂停/继续切换                                                                                                                                                    |
| 默认音量              | `_musicVolume` 初值 0.15；界面滑块、`music-test` 与 `ThemeMusic` 都只读 `Config.Current.MusicVolume`，无第二处硬编码。旧配置缺该键时按 15% 起，已保存过音量的用户不被覆盖（`JsonSerializer` 反序列化缺失字段保留字段初值）                                                                                                  |
| 法律声明              | `--cli legal` → 内置资源 1,355 字符 / 41 行，渲染 16 个块 / 正文 1,222 字符；纯文本转储与原文逐条对照：标题、引用、四级有序列表、缩进续行（邮箱那行）、粗斜体结尾、脚注两行都在，Markdown 标记全部褪掉。相对链接 `[MIT](LICENSE)` 降级为纯文本（单文件分发无落点），绝对链接正常生成 Hyperlink                                                                         |
| 首启确认框             | `--cli legal brief` → 简版 656 字符 / 24 行，渲染 10 块 / 正文 629 字符，正文里没有仓库地址与邮箱；配置往返实测：手工把 `LegalAccepted: true` + `LegalAcceptedAt` 写进 `SlopeXData\config.json` → 再跑 `legal` 读到 `True / 2026-10-31 19:12:00`，说明 `JsonSerializer` 的键名映射正确（反过来，缺这个键的老配置就是 `False`，会补问一次） |
| 无素材构建             | 临时移走 `art/` 与 `music/` 后发布：编译通过（exe 139 MB→无素材版），程序正常启动、背景退化为渐变、`music-test` 报「有曲目：否」；恢复素材后 exe 155 MB、`music-test` 报「有曲目：是、播放位置 00:02」                                                                                                                         |
| 圆角窗口              | 整屏截图取样：窗口四角 (1,1)/(10,10) 为壁纸色、内侧为窗口底色，确认圆角真的透出桌面                                                                                                                                                                                                               |
| 启动失败诊断            | 4 组样本实测：真实 Better MC 日志 **不误报**（走兜底并指出未生成崩溃报告）；`OutOfMemoryError`、`Missing or mandatory dependencies`、`Failed to verify username … premium account` 分别命中内存 / 缺依赖 / 账号规则                                                                                         |
| 外部版本补全            | 把 Better MC 版本目录直接复制进 `versions\`（未带 libraries）→ 启动提示缺 2 个 Forge 库 → 一键补全后启动成功（整合包自身要求正版账号，属预期）                                                                                                                                                                 |
| 模组搜索              | 「添加模组」对话框输入 jei → 20 条结果（作者 + 下载量），状态行「已按 Minecraft 1.20.1 / fabric 过滤」                                                                                                                                                                                         |
| 中文搜索（MC百科）        | `mods-search 1.20.1-forge-47.4.28 机械动力` → 百科命中 32、核对 12、可安装 5，首条「机械动力 (Create)」→ Modrinth `LNytGWDc`；`钠` 在 forge 下正确滤掉 Sodium（只支持 Fabric），`苹果皮` 命中 AppleSkin（`EsAfCjCV`）；耗时 4.0 s（12 个详情页 6 并发）                                                                 |
| 中文搜索 → 安装         | 临时游戏目录 + 假版本 `1.20.1-forge-test`：`mods-install 苹果皮` → 下载 `appleskin-forge-mc1.20.1-2.5.1.jar`（46.3 KB），未误取 Fabric/26.3 版本                                                                                                                                       |
| 依赖自动补齐（Forge）     | `mods-install 1.20.1-forge-47.4.28 jei` → JEI 15.74.0.233 + MezzConfig 0.6.10 两个文件，游戏内 JEI 日志出现 `mezz.jei.library.load.PluginCaller`                                                                                                                            |
| 依赖自动补齐（Fabric）    | `mods-install fabric-loader-0.20.1-1.20.1 jei` → JEI + MezzConfig + Fabric API；二次执行跳过已存在文件；启动 `Loading 61 mods` 进主菜单                                                                                                                                            |
| 安装类型对话框           | 点任意行「安装」→ 弹出居中卡片 + 遮罩；原版默认选中；Forge / NeoForge 置灰带「即将支持」；Esc 或「取消」关闭                                                                                                                                                                                             |
| 对话框端到端            | 已安装行 →「安装」→ 选 Fabric →「开始安装」→ 日志 `Fabric 0.20.1（26.3）安装完成：fabric-loader-0.20.1-26.3，依赖 7 个`，版本数 5 → 6                                                                                                                                                           |
| 便携迁移              | 首次运行后 `SlopeXData\config.json` 生成（含新字段 `JavaRuntimeDir` / `DismissLegacySavesHint`），`logs\` 已复制，`%APPDATA%\SlopeX\config.json` 保留未删                                                                                                                             |
| 便携实测              | exe 单独拷到 `I://sxportable-test` 运行 → 自动生成该目录的 `SlopeXData\`，`--cli list` 输出 `gameDir = I://sxportable-test//.minecraft`（相对路径正确解析）                                                                                                                                |
| 运行时位置             | Java 检测仍指向 `%APPDATA%\SlopeX\runtime`（180 MB 未搬），设置页可改到启动器目录内                                                                                                                                                                                                   |
| 设置页瘦身             | 页头改为「主题即时生效；其余项点保存设置后生效」；存档隔离由 4 层文案压到 2 层（机制进 tooltip）；旧存档提示可「不再提示」                                                                                                                                                                                            |
| 设备码申请             | `--cli login` → 网址 `https://www.microsoft.com/link`、8 位代码、有效期 900 秒；主日志 `已申请设备码：…`                                                                                                                                                                              |
| 登录对话框             | 「设置 → 账号 → 登录微软账号」弹出模态：8 位代码、网址、打开登录页 / 复制代码、轮询状态、取消；渲染与布局正常                                                                                                                                                                                                    |
| 正版链路（实测）          | 真实账号授权成功（微软发出"新登录"通知，服务名 `SlopeX Launcher`）→ 设备码通过 → **XBL 通过** → **XSTS 通过** → `login_with_xbox` **403 Invalid app registration**；前两次还遇到 `XErr 2148916233`（账号无 Xbox 档案），创建档案后已跨过                                                                               |
| Mojang 白名单        | 官方文档确认：新第三方应用必须人工审核加入白名单（https://aka.ms/mce-reviewappid）；未通过前该步必然 403。申请提交后审核周期以天/周计                                                                                                                                                                            |
| 白名单通过后的验证        | 2026-11-01 放行：`--cli account` 读到 `ShakableCat`；缓存令牌调 `/minecraft/profile` → **200**（`name=ShakableCat`、皮肤 ACTIVE、2 个披风）；令牌尾号与设备码登录时那次不同，说明是 `login_with_xbox` 真签发的，不是本地伪造                                                                                          |
| 静默刷新              | 人为把 `accounts.json` 的 `ExpiresAt` 改到 2020-01-01 → `--cli plan 1.20.1` 触发刷新：access_token 与 refresh_token **双双更换**、到期顺延 24 h，新令牌再取档案仍 200；日志出现「正版令牌已过期，正在刷新…」                                                                                                          |
| 正版启动真机（2026-11-01） | `--cli launch 1.20.1` 实机跑通：日志第 3 行 `Setting user: ShakableCat`，进单人存档 `ShakableCat[local:E:ee884371] logged in with entity id 104`，关窗口后 CLI 打印「退出码：0」。**但人物是默认皮肤（红上衣的 Kai）**——游戏日志里没有任何 skin / error 行，单机也照常进服，只有肉眼看得见；核对启动参数发现 `--uuid xxxxxxxx-xxxx-xxxx-xxxx-xxxx` 只有 28 位（§4-14）                                                 |
| 皮肤修复复核            | 修 `FormatUuid` 后重跑：`--uuid …-xxxxxxxxxxxx` 补齐到 36 位，同一存档第三人称截图里换成账号真实皮肤（灰白连帽衫 + 暗红领口 + 黑裤），与 `textures.minecraft.net/texture/ae252684…` 的贴图逐块一致；`--cli account` 顺手把 `accounts.json` 里存量的 28 位 UUID 自愈成 36 位。结论：**皮肤是否加载在日志里查不出来**，只能靠 UUID 断言 + 人眼核对截图（第三人称视角由用户手动切换） |

### 8.2 命令速查

```bash
# 版本与环境
SlopeX.exe --cli list                 # 已安装版本
SlopeX.exe --cli java                 # 检测到的 Java
SlopeX.exe --cli java-ensure 25       # 确保存在 Java 25
SlopeX.exe --cli manifest 20          # 可安装版本
SlopeX.exe --cli install 1.20.1       # 安装原版
SlopeX.exe --cli verify 1.20.1        # 完整性校验
SlopeX.exe --cli repair 1.20.1        # 补齐缺失的库 / 客户端 jar / 资源
SlopeX.exe --cli plan 1.20.1          # 打印启动命令行（不启动）
SlopeX.exe --cli launch 1.20.1        # 启动游戏
SlopeX.exe --cli diagnose 1.20.1      # 对最近一次启动失败做诊断

# 加载器：list 查可用版本，install 装（缺原版会先补原版）
SlopeX.exe --cli fabric-list 1.20.1
SlopeX.exe --cli install-fabric 1.20.1 0.20.1
SlopeX.exe --cli forge-list 1.20.1
SlopeX.exe --cli install-forge 1.20.1
SlopeX.exe --cli neoforge-list 1.21.1
SlopeX.exe --cli install-neoforge 1.21.1

# 模组：中文关键词自动走 MC百科 检索；末尾参数是来源（all|modrinth|curseforge，默认 all）
SlopeX.exe --cli mods-list 1.20.1-forge-47.4.28
SlopeX.exe --cli mods-search 1.20.1-forge-47.4.28 jei curseforge
SlopeX.exe --cli mods-search 1.20.1-forge-47.4.28 机械动力
SlopeX.exe --cli mods-install 1.20.1-forge-47.4.28 jei modrinth
SlopeX.exe --cli selftest            # 自检 6 条（5 条搜索 + 1 条账号档案），需联网，失败退出码 1

# 资源包：search 搜项目，versions 看全部版本，install 装进版本目录（第三个参数 shader|resourcepack）
SlopeX.exe --cli packs-search complementary shader
SlopeX.exe --cli packs-versions complementary-reimagined
SlopeX.exe --cli packs-install 1.20.1-forge-test shader complementary-reimagined

# 整合包 / 账号 / 排障
SlopeX.exe --cli pack-info 包.mrpack
SlopeX.exe --cli import-pack 包.mrpack
SlopeX.exe --cli login / account / logout
SlopeX.exe --cli legal [brief]        # 打印内置法律声明（brief = 首启简版）
SlopeX.exe --cli music-test / music-sim / msa-explain

# 所有命令都接受 --dir <游戏目录>，临时指向别的 .minecraft
build.cmd                             # 构建单文件到仓库根目录
```

### 8.3 关键路径

| 用途          | 位置                                                     |
| ----------- | ------------------------------------------------------ |
| 游戏根目录       | `<启动器目录>\.minecraft`                                   |
| 版本隔离目录      | `.minecraft\versions\<版本 ID>`                          |
| 配置与日志       | `<启动器目录>\SlopeXData\`（不可写时回落 `%APPDATA%\SlopeX\`）      |
| 托管 Java 运行时 | 默认 `%APPDATA%\SlopeX\runtime\java-<主版本>`，可在设置中改到启动器目录内 |

### 8.4 git 速查

```bash
git log --oneline              # 提交历史（当前 62 条，HEAD = 01ce89f）
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
