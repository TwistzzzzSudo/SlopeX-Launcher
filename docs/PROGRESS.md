# SlopeX Launcher 进度报告与实施计划

> 首次生成：2026-10-29 02:32 ｜ 最近更新：2026-11-05 15:58
> 对应产物：`SlopeX.exe` 1.0.0（自包含单文件，win-x64）
> 代码规模：88 个 C# / XAML 源文件，≈23,553 行
> 版本控制：分支 `main`，远端公开 `github.com/TwistzzzzSudo/SlopeX-Launcher`
> 当前状态：**S3 视觉定案**（7 个组件里补完 4 个，剩 `ComboBox` / `ContextMenu` / `Steps` 待有真实场景再补）；四条加载器线 + 整合包导入 + 模组增删与中英文搜索（Modrinth / CurseForge / 双源三选一）+ 光影 / 材质包下载 + 法律声明（首启确认）全部可用；**微软登录已通过 Mojang 白名单，实测登录 / 静默刷新 / 档案接口全部正常（2026-11-01）**
> 下一个执行项 = **继续完善启动器**（集中补既有链路，不铺新功能面），候选待办见 §6.2
> 本轮改动：⓪设置页两处体验修正——(a) 滚轮改为平滑滚动（新增 `Controls/ScrollAssist`）：步长从系统默认的 16 px/行提到不低于 88 px/格并加缓动，同时接管内层 ListBox 吞掉滚轮的问题，"鼠标停在 Java 列表上整页滚不动"就是它；(b) 选 `32` / `64` 线程时先弹风险确认框（新增通用 `Controls/ConfirmDialog`，取消 / Esc 退回原档，确认后才点亮并提示保存）；①新增分段下载引擎（`Modules/ModNet/Downloader.cs`）——单文件按 256 KB–8 MB 切片、默认 16 线程并发（可调 8/16/32/64），失败断点续传、整体失败回退单流；接入模组 / 光影 / 材质三条下载链路，主因是 `cdn.modrinth.com` 单连接实测只有 35 KB/s（详见 §6「分段下载与下载进度」）；②模组安装与资源包安装加进度条：百分比 + 已下载/总大小 + 实时速度，装完自动收起；③模组搜索加来源筛选——`全部` / `Modrinth` / `CurseForge` 三选一；CurseForge 元数据经社区镜像 MCIM（官方接口要 API Key），文件仍直连 forgecdn；`Modrinth` 与 `全部` 两种选择下 Modrinth 侧都走官方接口（镜像数据滞后，见 §6.3 的修正说明）；中文搜索按词条里登记的链接同时支持两个平台（详见 §6.3）；④版本内「资源包」上线——光影 / 材质包两个页签，每行一个「安装」、点中版本即下载，搜索不过滤 MC 版本（规格与实测见 §6.1）；⑤模组搜索支持中文（MC百科 检索 → 词条链接 → 项目核对版本与加载器）；⑥修掉跨线程写 UI 导致模组安装必定失败的问题；⑦主题背景音乐默认音量 20% → 15%；⑧法律声明内置进 exe（设置页底部可查看），源文件留在 `docs/LegalNotice.md` 作为备份；⑨首次启动的简版确认框（勾选后才能点「确认」，同意后写入配置不再弹出）；⑩文档整改：已完成项与待办分家、命令速查补全；⑪新增命令行自检 `selftest`——用 5 条固定样本 + 1 条账号档案断言，覆盖「中文 → MC百科 → 平台」链路（机械动力 → `LNytGWDc`、苹果皮 → `EsAfCjCV`、钠在 forge 下必须被滤掉、「全部」落到 CurseForge、英文直搜两源存活），任一失败返回退出码 1（见 §6「命令行自检」）；⑫**微软登录通过 Mojang 白名单**（2026-11-01）——`/minecraft/profile` 返回 200、皮肤 ACTIVE、2 个披风；人为过期后静默刷新成功、双令牌轮换；诊断文案里「还在等审核」的说法同步改掉；⑬**真机启动后发现皮肤退回默认，根因是本地存的 UUID 只有 28 位**——`Msa.FormatUuid` 把最后一段写成 `Substring(20, 4)`（应为 12），游戏拿到残缺 UUID 查不到档案就套默认皮肤，**日志里没有任何报错**；修好格式化并加一次性自愈（启动读账号时比对长度，不足 36 位就用现成令牌重拉档案写回），`selftest` 补第 6 条断言盯住它（见 §4-14、§6「命令行自检」、§8.1「正版启动真机」）；⑭设置页「关于」卡片右下角加署名 `Program By ShakableCat · DeepSeek`（与「打开配置目录」同一行、右对齐，用 `TCaption` 随主题走）；⑮`--cli account` 的令牌文案从「有效（到期 …）」改为「… 到期，自动续期，无需重新登录」——原句没有说清过期后会怎样，容易被读成账号要失效（见 §7.2）；⑯模组依赖改为**按声明锁定版本**——Modrinth 依赖项带 `version_id` 时直接装那一版，不再一律装「该项目最新适配版」；新增 `--cli mods-deps` 打印依赖解析结果（见 §6「依赖版本锁定」）；⑰新增模组元数据读取器 `Modules/ModMods/ModJar.cs`——直接读 jar 里的 `fabric.mod.json` / `mods.toml` / `neoforge.mods.toml`，拿到 modid、版本、依赖区间、不兼容声明，并带一套 maven + semver 区间判断；新增 `--cli mods-scan <版本|目录> [--check]`（见 §6「模组元数据读取器」）；⑱新增模组体检 `Modules/ModMods/ModHealth.cs`——缺前置 / 版本不符 / 冲突 / 重复 / 坏包五类问题一次查全，装完模组自动跑一次并把结果带回界面，模组面板加「检查依赖」按钮（见 §6「模组体检」）；⑲新增**更新检查**——按文件 SHA1 批量问 Modrinth，面板加「检查更新」可确认后一键下载替换（见 §6「模组更新检查」）；⑳Toast 的底色 / 文字改用各主题自带的 `ToastBg` / `ToastFg`——原来借 `Ink` 反色，深色与插画主题下会变成一整块白（见 §6「美术与主题」的 Toast 取色表）；㉑**前置去重改为按 modid 判断**——装模组前先扫 `mods\` 读每个 jar 的 modid，已装的前置不再下载（原来只按文件名判重，`flywheel-0.6.10` 与 `0.6.13` 会被当成两个模组，见 §6「依赖去重」）；㉒**Modrinth 侧不再走 MCIM 镜像**——镜像的版本数据比官方滞后八个多月，「全部」来源下会装到旧版本（Iris 1.7.2 而不是 1.7.6，连带 Sodium 退到 0.5.11），现在只让镜像服务 CurseForge（见 §6.3 的修正说明）；㉓**四个列表加上作者图标**——本地模组读 jar 自带的图（`fabric.mod.json` 的 `icon` / `mods.toml` 的 `logoFile`），搜索结果与资源包用平台的 `icon_url`，无图标时显示首字母占位（见 §6「模组 / 资源包图标」）；㉔**账号独立成页并加皮肤预览**——左侧导航从 5 项扩到 6 项，账号页支持正版皮肤与上传的离线皮肤（**没导入就留白**，不放仿制的默认史蒂夫），3D 预览可拖动旋转、双击复位，第二层（外套 / 帽子）一并渲染；新增 `skin-render` / `skin-premium` / `skin-set` / `skin-clear` 四条排障命令（见 §6「账号页与皮肤预览」）。㉕**界面文案瘦身**——按用户逐屏标注删掉 6 处非必要提示与整块设置：启动页的「存档隔离：强制开启 → …」与「小提示」卡片、版本页的「双击版本可立即启动」、账号页的「正版账号：皮肤与正版验证服务器可用」与皮肤格式说明、设置页的「存档隔离」整块（含打开存档目录按钮）、「数据与运行时」整卡、主题说明与动态说明、Java 版本对照表；机制本身不变，只是不再在界面上解释（见 §6「界面文案瘦身」）。㉖**首页点版本即切换**——「全部版本」里点一行就地换掉顶部「准备启动」的版本，顺带把 `LastVersion` 改成点击即落盘（原来只在启动时才写，重启会退回旧版本），Toast 给确认；根因是 `RowItem` 把 `ListBoxItem.Focusable` 关掉导致 `SelectionChanged` 从不触发，新增 `RowItemPick` 只给这个列表用（见 §6「首页点版本即切换」）。㉗**首页加皮肤预览并抽公共取数逻辑**——右栏新增 3D 皮肤预览（与账号页同一个控件，可拖动旋转），「账号」与「皮肤」合成一张卡、「游戏目录」挪到其下方；取数逻辑抽成 `SkinProvider`（正版按 UUID 缓存、离线读本地、登出清缓存），账号页删掉约 60 行重复；顺带修掉登录正版后卡片仍显示离线玩家名与「离线登录」的自相矛盾（见 §6「首页皮肤卡」）。㉘**CurseForge 侧更新检查**——指纹算法定案为**整个 jar 的字节流**做 MurmurHash2（seed 1）、剔除空白字节 `0x09/0x0A/0x0D/0x20`，且必须提交**无符号**值（有符号负值 0 命中）；`ModUpdater` 改为「Modrinth 按 SHA1、CF 按指纹」两段式，只对 Modrinth 认不出的 jar 走 CF；顺带修掉「1.0.2 → 1.0.2」这类同版本误报（见 §6「CurseForge 侧更新检查」）。㉙**披风选择**——账号页皮肤卡片加披风一行（Chip 单选，含「无」），`state=ACTIVE` 判定当前佩戴、`PUT/DELETE /minecraft/profile/capes/active` 切换；3D 预览加独立的披风薄板与贴图；账号没有披风时只显示「无」；踩到 WPF 3D 的 `ViewportUnits` 必须用 `Absolute`（用相对模式会把整张图压进 UV 包围盒，背面同时看到正反两面的图案）。㉚**声明与文档去关联化**——法律声明删掉「与第三方启动器的关系」整节并重排节号、弹窗按钮「关闭」改「已明白」，设置页删「架构参考」行与法律卡片里的相关说明，README、`docs` 与 11 处代码注释里的对照表述一并清理，对外只留「全部代码为独立实现」（见 §6「声明与文档去关联化」）。㉛**首页统计条下线、版本号收口**——首页底部四格统计（已安装版本 / Java 版本 / 内存上限 / 下载源）整块删除，右下角状态栏改为常驻版本号 `v1.0.0`；新增 `Modules/ModBase/AppInfo`，版本只从 `csproj` 的 `<Version>` 读（编译进程序集），设置页、CLI 与启动参数 `launcher_version` 全部改为引用它，不再各自写死；顺带修掉 `--cli` 不带命令时的崩溃（见 §6「首页统计条下线与版本号收口」）。㉜**整合包下载**——左侧「下载」下新增悬停滑出的子项「整合包」；新页面支持 Modrinth / CurseForge 双源搜索，中文经 MC百科 全站检索（<c>filter=2</c>）映射回平台项目，选中包版本后下载 <c>.mrpack</c> / CF zip 并交给既有 <c>ModpackInstaller</c> 安装；新增 <c>--cli modpacks-search</c> / <c>modpacks-versions</c> 排障命令（见 §6「整合包下载」）。以上改动 `dotnet build -c Release` 通过（0 警告 0 错误）
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

### 3.1 技术栈选择

本工程用 **C# + WPF / net8.0-windows**，不依赖任何第三方 NuGet 包。选它的理由都很具体：XAML 的布局与 `Viewport3D` 能直接把皮肤 / 披风渲染出来，`System.Text.Json` 处理版本 JSON 不需要额外依赖，自包含发布能压成一个零依赖的 exe。
**全部代码为独立实现**，未使用任何第三方启动器的源代码。

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
| 15  | **mods.toml 表头带行尾注释时整段被跳过**：Forge 官方模板就写成 `[[mods]] #mandatory`，解析时没先剥掉 `#` 之后，`TrimStart('[')` / `TrimEnd(']')` 得到 `mods]] #mandatory`，被当成未知表 | modId / version / displayName 全读不到，模组列表只显示文件名，体检也判不出它的依赖 | 解析表头前先截掉 `#` 后半个行；`ModManager` 里那套重复的 TOML 正则实现删除、统一走 `ModJar`，并补上 `${file.jarVersion}` 的 MANIFEST 兜底 |
| 16  | **WPF 的 3D 纹理坐标原点在左上角**（与 OpenGL 相反）：按 OpenGL 习惯把 V 轴做 `1 - v` 翻转后，头和腿的贴图整个对调、躯干落在空白区 | 皮肤预览显示成几块散乱的色块，完全看不出人形 | 去掉 V 轴翻转，`v = ty / uvH` 直接用（见 `Controls/SkinPreview.cs` 的 `Face()`）；改错不会报错，只能看图发现 |
| 17  | **`PerspectiveCamera.FieldOfView` 是水平视场角**：按垂直角算相机距离，在窄高的预览框里角色会缩成一小团 | 角色只占预览框的一半，四周大片留白 | 垂直半角 = `atan(tan(水平半角) / 宽高比)`，再取横纵两个方向所需距离的较大值 |
| 18  | **WPF 3D 的纹理采样固定是双线性插值**，`ImageBrush` 上没有"最近邻"开关 | 64×64 像素画铺到每像素约 10 个屏幕像素时，每条边界被摊成 10 px 宽的渐变带，整体发糊（实测一条横线 99 像素出现 44 种颜色） | 交付 3D 前先按最近邻把贴图预放大 16 倍，插值只落在 1/16 个皮肤像素内（同样一行只剩 3 种颜色）；alpha 同时二值化，避免外衣层边缘混白 |
---

## 5. 当前限制

### 功能缺口

- 仅 Windows（WPF + 注册表探测）
- 模组：增删启停与搜索安装（含中文）都有；依赖会**按声明锁定版本**（见 §6「依赖版本锁定」）。已装模组能做**体检**（缺前置 / 版本不符 / 冲突 / 重复 / 坏包，见 §6「模组体检」）和**更新检查**（见 §6「模组更新检查」与「CurseForge 侧更新检查」，Modrinth 按 SHA1、CurseForge 按文件指纹），两者都能从面板触发；体检只看 jar 里作者写的声明——没声明的实际冲突仍以游戏日志为准
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

- Java 运行时（180 MB）固定在 `%APPDATA%\SlopeX\runtime`；设置页已不再暴露「运行时目录」入口，要改只能直接编辑 `config.json` 的 `JavaRuntimeDir`
- 根目录 `saves` / `resourcepacks`（16 MB）是旧版共用模式遗留，现已不被任何版本使用；界面上不再提示，需要时手动整理

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

> **当前主线 = 继续打磨启动器**（2026-11-01 02:42 定）。1–4 是打磨项，按需取用；第 5 项是被主动搁置的验证项，不占主线。

1. **启动器自更新**：单文件 exe 没有升级通道，换了新版只能手动替换文件
2. **完整性修复升级**：≥ 8 MB 的大文件现在只比大小、不校验 SHA1（见 §5 工程风险）
3. **多任务队列**：并发安装已可用，缺「暂停 / 继续」与失败重试队列
4. **界面细节**：高 DPI 适配、键盘导航、日志搜索与导出
5. **真实服务器联机验证（⏸ 已搁置）**：目前只验到令牌 / 档案 / 皮肤三层，`Setting user: ShakableCat` 只在单机日志里出现过，**没有连过一台强制正版验证的服务器**。真正能验的是「服务器接受这个会话」——`--uuid` 与 `session.minecraft.net/join` 都走通才算数。需要一台开 online-mode 的服务器（用户自开或给地址）；不用改代码，属于验收项而非开发项，所以排到主线之外

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
| `全部`（默认） | 官方 `api.modrinth.com` | 镜像 |

> **2026-11-01 修正**：`全部` 原先让 Modrinth 也走镜像（与 CF 共用一个出口）。后来发现**镜像的版本数据严重滞后**——Iris 官方已到 1.7.6（2025-03-03），镜像最新只到 1.7.2（2024-06-29），差八个多月。后果是「全部」来源下点安装会装到旧版本，依赖解析也跟着退化（Sodium 只到 0.5.11 而不是 0.5.12-beta.2）。现已改为 Modrinth 一律走官方，镜像只服务 CurseForge。

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

结论：瓶颈不在源站，在**每条连接 35 KB/s** 这一层，换镜像、加缓存都没用，只能把并发连接开出来。所以引擎直接连源站，靠分段把带宽叠起来：小于 1 MB 的文件不拆、单片 256 KB 起步（最大 8 MB，按剩余量动态领取）、连接数由设置项决定。

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

### 模组元数据读取器（✅ 2026-11-01 落地，B1）

**要解决什么**：在此之前启动器只知道 `mods\` 里有几个文件、叫什么名字，**不知道 jar 里面是什么**——于是依赖检查、冲突检查、更新检查三件事都无从谈起，只能等游戏崩了再从日志猜（`Diagnosis` 那三条规则）。

**做法**：新增 `Modules/ModMods/ModJar.cs`（503 行）

| 格式 | 读哪里 | 拿什么 |
| --- | --- | --- |
| Fabric | `fabric.mod.json` | `id`、`version`、`depends{}`、`breaks{}` |
| Forge | `META-INF/mods.toml` | `modId`、`version`、`[[dependencies.X]]` 的 `modId` / `versionRange` / `mandatory` |
| NeoForge | `META-INF/neoforge.mods.toml` | 同上（优先于 `mods.toml` 匹配） |

- 只读元数据这一个条目，不解压其它内容；读失败收敛成 `JarMod.Error` 继续往下扫，**一个坏包不会中断整次扫描**（用户目录里躺着旧版 jar 是常态）
- TOML 是手写的最小解析器（项目至今零外部依赖）：只认 `[[mods]]`、`[[dependencies.X]]` 两种表和 `key = value` 标量，跳过 mixins / accessTransformers 等其它表
- 区间判断两套语法：Forge 的 maven（`[1.20.1,1.21)`、`[1.0,)`、`[1.0]`）、Fabric 的 semver（`>=1.20.1`、`^1.2`、`~1.2`、`1.20.x`、多条件空格分隔）
- 版本比较按 semver 处理预发布：`0.5.12-beta.2` 低于 `0.5.12`，构建元数据 `+mc1.20.1` 不参与比较
- **拿不准就返回 true**（语法写坏了、版本为空）：宁可漏报，也不要天天误报让人把这个功能关掉

**工具**：`SlopeX.exe --cli mods-scan <版本|目录> [--check]`。给目录时按绝对路径扫，便于对着任意模组包核对；`--check` 顺带做一次清单校验（缺前置 / 版本不符），平台自带 id（`minecraft`、`forge`、`fabricloader`、`fabric-api` 等）不计入。

**实测（三组对照，同一批样本只改依赖版本）**

```
testmods-none  × testmod 需要 sodium >=0.5.12，未安装
               × createlike 需要 flywheel [0.6.10,)，未安装

testmods-low   × testmod 需要 sodium >=0.5.12，实际装的是 0.5.11     ← semver 下限
               × createlike 需要 flywheel [0.6.10,)，实际装的是 0.5.0   ← maven 下限

testmods-ok    没有发现问题                                          ← 0.5.13 / 0.7.0
```

样本用 Python 直接写 zip 造（`fabric.mod.json` / `mods.toml`），另含一个损坏 zip 和一个只有 `readme.txt` 的合法 zip：前者报 `End of Central Directory record could not be found`，后者报「不是模组 jar」，都没有让扫描中断。六个 jar 里还放了两个同 modid 的，重复检测正确地把它们列了出来。

**工作量口径**：新增 `ModJar.cs`（503 行）；`Cli.cs` 加 `mods-scan` 子命令与 1 行 help。

### 依赖去重（✅ 2026-11-01 落地）

**问题**：装模组时会自动补前置，但「已存在」的判断只在**下载阶段按文件名**做。`flywheel-0.6.10.jar` 躺在 `mods\` 里时，再去下 `flywheel-0.6.13.jar` 照样会装——两个文件名不同、却是同一个模组，结果就是两份并存，游戏侧报 `DuplicateModsFound`。同一个前置被多个模组需要时（Create 系尤其明显）很容易撞上。

**做法**：把判重提前到**依赖入队之前**

- 安装开始时扫一次 `mods\`，读每个 jar 的 modid（`ModJar.ScanDir`，只读元数据，几十毫秒）
- 依赖侧拿 modid：Modrinth 的 `dependencies` 里只有 `projectId`，用 `/v2/projects?ids=[...]` **一次批量**换 slug；CF 的 `/v1/mods` 响应里本来就带 `slug`，之前丢掉了，现在一并返回
- 命中已装清单就记进 `Skipped` 并 `continue`，**根本不发起下载**（不是下完再丢）
- 依赖项的元组从 `(Id, VersionId, Name)` 扩成 `(Id, VersionId, Slug, Name)`

**兜底**：slug 与 modid 不保证 100% 相同（历史项目有过不一致的）。查不到 slug 时退回原行为——正常下载，宁可多下一次，也不因为一次查询失败就漏装前置。

**实测（Iris 依赖 Sodium，决定性对照）**

```
mods 里有 sodium-fabric-0.5.13+mc1.20.1.jar
  → 装 Iris：下载 1/1 iris-1.7.2+mc1.20.1.jar（2.6 MB）
  → 结果：安装 1 个，跳过 1 个（已存在）
      + iris-1.7.2+mc1.20.1.jar
      = 已存在 Sodium（已装 0.5.13+mc1.20.1）

清空 mods 后装 Iris
  → 下载 1/2 iris…、2/2 sodium-fabric-0.5.11+mc1.20.1.jar
  → 结果：安装 2 个
```

第二条是关键：证明不是「恒跳过」，而是真的看了盘上有没有。

**工作量口径**：`ModInstaller` 加 `ScanInstalled` 与入队前的判断；`ModrinthSource` 加 `SlugsAsync`（批量）；`CurseForgeSource.RequiredDepsAsync` 多返回一个 slug。

**遗留**：这次只解决「重复装」，没解决「版本太低」——如果盘上的 Sodium 是 0.5.0、Iris 要 0.5.12，仍然会跳过不动。这正是下面体检里 `version` 那类问题，装完会报出来，但安装流程本身不会自动升级。

### 模组体检（✅ 2026-11-01 落地，B2）

**要解决什么**：读取器只解决"能读到"，还得有人拿它做判断，并把结论送到用户眼前——否则只是给开发者多了一个 CLI。

**做法**：新增 `Modules/ModMods/ModHealth.cs`（201 行），一次扫出五类问题：

| 类别 | 判定 |
| --- | --- |
| `broken` | jar 打不开 / 元数据看不懂 |
| `duplicate` | 同一 modid 出现多份（游戏侧会报 `DuplicateModsFound`） |
| `missing` | 必需依赖的 modid 不在清单里 |
| `version` | 在清单里，但版本不满足声明的区间 |
| `conflict` | A 的 `breaks` 点名 B，且 B 的当前版本落在 B 被点的区间内 |

- 平台 / 加载器自带的 id（`minecraft`、`forge`、`neoforge`、`fabricloader`、`fabric-api`、`javafml` 等）不计入缺前置——否则每个模组都会报一串"缺 minecraft"
- 一个 modid 有多份时，按**版本最高**的那份参与后续判断（用户多半是想升级，只是忘了删旧的）
- 冲突双向声明（A 说与 B 不兼容、B 也说与 A 不兼容）只报一条，用排序后的 key 去重

**两个接入点**

1. **装完自动跑**：`ModInstaller.InstallAsync` 下载循环结束后调一次 `ModHealth.Check(modsDir)`，结果挂在 `ModInstallResult.Health` 上；`Summary()` 末尾追加「体检：缺前置 2」这种，`HealthLines()` 给界面展开前几条。体检本身失败只记日志，**不影响安装结果**
2. **面板手动跑**：模组管理页头加「检查依赖」按钮（打开目录 / 刷新 / **检查依赖** / 添加模组）。点一次：副标题换成体检摘要，Toast 显示"摘要 + 第一条问题 + 详见日志"，完整列表逐条写 `Log.Warn`

**实测**

CLI 侧沿用三组对照，另加一组冲突样本（在 `testmods-ok` 里塞一个 `optifine`）：

```
testmods-ok         没有发现问题
testmods-conflict   × testmod 声明与 optifine 不兼容，当前装的是 1.20.1_HD_U_I6
```

界面侧用同一批样本塞进 `.minecraft\versions\1.20.1\mods`，点「检查依赖」：

- 副标题：`1.20.1  ·  5 个模组，冲突 1`
- Toast：`5 个模组，冲突 1；例如：testmod 声明与 optifine 不兼容，当前装的是 1.20.1_HD_U_I6（详见日志）`

验证完把 5 个测试 jar 从真实目录删掉了，`mods\` 恢复为空。

**已知局限**

- 只看**声明**，不看游戏实际行为：作者写错 `versionRange`、或者两个模组实际冲突但都没声明，这里都发现不了。真正的事后证据仍以游戏日志为准（`Diagnosis` 那三条规则保留）
- 元数据读不出来时无法判断任何事，只报 `broken`——所以"体检干净"不等于"一定能跑"
- 不联网：不核对"这个版本在平台上存不存在"，也不做更新检查

**工作量口径**：新增 `ModHealth.cs`（201 行）；`ModInstaller` 安装结果加 `Health` + `HealthLines`、下载后调一次体检；`ModsPanel` 加「检查依赖」按钮与 `CheckHealth_Click`；`Cli.cs` 的 `--check` 改为复用 `ModHealth`（删掉原来那份重复逻辑）。

### 模组更新检查（✅ 2026-11-01 落地，CLI + 面板）

**要解决什么**：体检能查「装的这堆东西彼此配不配」，但查不出「它们是不是旧了」——用户只能自己盯着版本号去平台翻。

**做法**：新增 `Modules/ModMods/ModUpdater.cs`（138 行）

- 扫 `mods\`，每个 jar 算**文件 SHA1**（`ModJar.Sha1Of`，流式读，大包也不吃内存）
- `POST /v2/version_files/update`，50 个一批；单批失败只记日志，剩下批次继续
- 只返回**确实有更新**的：已是最新、不在 Modrinth 上架、读不出元数据的都不出现

**一个必须踩过才知道的 API 坑**：`version_files/update` **连「已是最新」的 hash 也会返回**（返回该版本自身），不是「只回有更新的」。所以判断不能看「有没有返回」，得比对返回版本的主文件 `sha1` 与请求的 hash 是否相同——相同即已最新。

**更新动作**（`ApplyAsync`）：先下到 `<新文件名>.new`（完整性与大小由下载引擎按 SHA1 校验），成功后才「旧文件改名 `.old` → 新文件就位 → 删 `.old`」；任何一步失败都回退原状，不会出现「旧的删了、新的没下完」。

**两个入口**

1. 面板页头「检查更新」：检查走线程池（算 SHA1 是同步 IO，别卡界面），结果用 `ConfirmDialog` 列出前 5 条差异，确认后逐个替换，进度写在副标题
2. CLI `mods-update <版本>`：只列可更新的，不动文件

**实测**

CLI 两组对照：

```
旧版 2.5.1 + 最新版 2.5.2 都在目录 → 只报 2.5.1（2.5.2 不出现）
只留 2.5.2                       → 没有可更新的
```

界面端到端：放 2.5.1 → 点「检查更新」→ 弹窗「发现 1 个可更新的模组：2.5.1+mc1.20 → 2.5.2+mc1.20.1」→ 点「更新 1 个」→ 弹窗关闭、副标题「已更新 1 个模组」、列表版本变 `2.5.2+mc1.20.1`；文件系统里只剩新文件，没有 `.new` / `.old` 残留。

**踩到的坑**：`ConfirmDialog` 只回结果、**不关模态层**（设计如此，关不关由调用方决定），第一版漏了 `HideModal()`，更新跑完弹窗还挂在屏幕上，用户会以为没反应。

**局限**

- **这一版只覆盖 Modrinth**（CurseForge 侧 2026-11-04 补上，见上一节「CurseForge 侧更新检查」）
- 只换文件，不处理「新版改了前置要求」这类连带问题；更新后建议再点一次「检查依赖」
- 按当前版本的 MC 版本 / 加载器过滤，换版本要重新检查

**工作量口径**：新增 `ModUpdater.cs`（138 行）；`ModJar` 加 `Sha1Of`；`ModrinthSource` 加 `CheckUpdatesAsync`；`ModsPanel` 加「检查更新」按钮与 `CheckUpdates_Click` / `ApplyAllAsync`；`Cli.cs` 加 `mods-update` 子命令与 1 行 help。

### CurseForge 侧更新检查（✅ 2026-11-04 落地）

**要解决什么**：上一版更新检查只认 Modrinth，从 CF 装的模组一条都查不到。CF 没有「按 SHA1 批量查更新」的接口，只有**指纹反查**：先算文件指纹认出「本地这个 jar 是哪个项目的哪个文件」，再去看该项目有没有更新的文件。

**指纹算法（关键，容易做错）**：对**整个 jar 的字节流**做 MurmurHash2（seed = 1），但要先剔除空白字节 `0x09 / 0x0A / 0x0D / 0x20`，长度也按剔除后算。

- 第一版按「排序后的条目名拼接」去算（社区里流传的另一种说法），拿 95 个本地 jar 试 → **0/95 命中**，四种变体（丢 / 留 `META-INF/MANIFEST.MF` × 有 / 无符号）全灭
- 改成整文件字节流后，先用一个直接从 CF 下载、SHA1 可核对的文件验证：指纹 `1871968345` → 精确命中 `fileId 9394360`（`jei-1.20.1-forge-15.74.0.234.jar`）
- **必须提交无符号值**：同一个 jar 用有符号（负值）提交后 0 命中，改无符号（0–4294967295）就命中。95 个本地 jar 全量对照：有符号 42/95，负值改无符号后 89/95（剩下 6 个是自己编译或改过的 jar）

**实现**：`Modules/ModMods/JarFingerprint.cs`

- 长度要先知道才能初始化哈希（`h = seed ^ len`），所以**扫两遍文件**；两遍都是 64 KB 分块流式读，几十 MB 的 jar 也不整份进内存
- 边读边过滤空白，用 4 字节小缓冲拼块——过滤会打乱 4 字节对齐，不能直接按原始偏移切块
- 返回 `long` 承载 uint，保持无符号

**两段式检查**（`ModUpdater`）

```
Modrinth：SHA1 批量问 /version_files/update（50 个一批）
   ↓ 认得的（含「已是最新」那些）不再问 CF
CurseForge：指纹批量反查 /v1/fingerprints（100 个一批）
   ↓ 逐个项目 GET /mods/{id}/files?pageSize=1&gameVersion=&modLoaderType=
   ↓ 4 个并发，单个项目失败只丢它一个
```

- 只对 Modrinth 认不出的 jar 走 CF：CF 那一步每个项目要单独发一次请求，本地 95 个模组里 Modrinth 认得大部分，跳过它们把请求数从约 89 降到约 10
- 实测这个取舍不丢更新：**对全部 89 个 CF 认得的 jar 都查一遍，结果同样是 39 条**，与「只查 Modrinth 认不出的」完全一致
- 判「有没有新版本」用两条硬条件：最新文件的 `fileName` 与命中的不同，且 `fileDate` **晚于**本地这份。只比 fileId 不够——作者补传旧分支时列表顺序会变
- `ModUpdate` 加 `Source`（Modrinth / CurseForge），CLI 输出带来源前缀

**顺带修掉一处误报**：`DungeonsAriseSevenSeas` 原来报「1.0.2 → 1.0.2」，指向的是同一版本号为 1.19.2 编译的 jar。原因是 Modrinth 的 `version_files/update` 会把「同一版本号为不同 MC 版本各传一份」的条目当成更新。现在版本号完全相同就不算更新（`SameVersion`）——只比 `sha1` 不够。

**实测**（`Voxy_1.20.1_Forge-1.0.0`，95 个 jar / 93 个可读）

```
CLI   mods-update  → 12 秒，可更新 38 个：Modrinth 32 + CurseForge 6
                       CF 六条：framework / ftb-library / sophisticatedcore /
                                sophisticatedbackpacks / ftb-ultimine / jecharacters
面板  检查更新      → 弹窗「发现 38 个可更新的模组」，与 CLI 一致
指纹  89/95 被 CF 认出（未认出的 6 个是自己编译或改过的）
SHA1  CF 声明的 sha1 与实际下载逐字节一致（jei 9394360）
```

**局限**

- 依赖 MCIM 镜像的 `/v1/fingerprints`；官方 `api.curseforge.com` 要 API Key。镜像停服或加 UA 白名单就退化回「只查 Modrinth」——不报错，只是少一部分结果
- 更新检查只换文件，不处理「新版改了前置要求」这类连带问题
- 指纹只对**未被改动**的 jar 有效：重新打包、重新签名都会改变字节，CF 认不出来

**工作量口径**：新增 `JarFingerprint.cs`（约 140 行）；`CurseForge.cs` 加 `MatchFingerprintsAsync` / `LatestFileAsync` / `CfMatch`，并把文件对象解析抽成 `ToFileRefAsync`；`ModUpdate` 加 `Source`、`ModFileRef` 加 `FileDate`；`ModUpdater` 加 `CheckCurseForgeAsync` / `CheckOneAsync`；`Cli.cs` 输出带来源；两处文案改口。


### 模组 / 资源包图标（✅ 2026-11-01 落地）

**要解决什么**：四个列表（本地模组、模组搜索、光影、材质包）此前只有文字，一屏几十行全靠读名字区分。现在改为显示作者随包上传的图标。

**两个来源**

| 列表 | 图标从哪来 | 说明 |
| --- | --- | --- |
| 本地模组 | **jar 里自带的图** | Fabric 读 `fabric.mod.json` 的 `icon`（字符串或 `{"64": "...", "128": "..."}`，取最大尺寸）；Forge / NeoForge 读 `mods.toml` 的 `logoFile` |
| 模组搜索 / 光影 / 材质 | 平台项目的 `icon_url` | 后台并发下载（限 8 路），不阻塞列表渲染 |

本地优先读 jar 而不是查平台，一是准确（作者上传的就是这张），二是不用联网——整合包里 95 个模组也不用发 95 个请求。

**统一约定**

- 模型上挂 `byte[] IconData`，界面用 `IconConverter` 转 `BitmapImage`（`DecodePixelWidth = 64`，高分屏不糊）
- 没有图标的显示**标题首字母**占位，列表不会出现空洞
- 搜索结果与资源包用 `INotifyPropertyChanged` **只通知 `IconData` 这一个属性**：列表先出、图标陆续补，不用整表重绑，滚动位置也不会跳

**实测（四个页面逐个截图核对）**

```
本地 Voxy 整合包 95 个模组  → 91 个显示作者图标，4 个显示首字母占位
搜索 jei                    → 5 条全有图标
光影搜 bsl                  → 6 条全有图标
材质搜 32x                  → 7 条全有图标
```

另作抽样统计：95 个真实模组里 33/39（前 40 个样本）的 jar 自带图标，路径形态五花八门（`logo.png`、`icon.png`、`fpsreducer_logo.png`），只要字段写对就能取到。

**工作量口径**：新增 `Controls/IconLoader.cs`（图标下载 + 字节转图 + 并发补齐）；`Net` 加 `GetBytesAsync`；`ModJar` 多解一个图标条目；`ModEntry` / `ModSearchHit` 加 `IconData` 与 `Initial`；四个 XAML 加 32–36 px 的图标位（`Border` + `ImageBrush`，圆角裁切，无图时露出底层首字母）。


### 首页皮肤卡（✅ 2026-11-04 落地）

**要解决什么**：首页右栏只有账号名与游戏目录，看不到自己长什么样；而账号页的 3D 预览又要多跳一次才能看到。用户要求在右栏补一块皮肤预览，随后又要求把「账号」与「皮肤」合成一张卡，并把「游戏目录」挪到合并后的卡片下面。

**右栏最终只有两张卡**

```
┌─────────────────────────┐
│ 头像  ShakableCat   编辑 │
│       正版登录 · 皮肤自动同步
│ ─────────────────────── │
│ ┌─────────────────────┐ │
│ │   3D 预览（300 高）  │ │  ← 可拖动旋转、双击复位
│ └─────────────────────┘ │
│ 正版皮肤 · 细手臂        │
└─────────────────────────┘
┌─────────────────────────┐
│ 游戏目录                 │
│ …\.minecraft  [打开][设置]│
└─────────────────────────┘
```

**取数逻辑抽成共用的 `Modules/ModAccount/SkinProvider.cs`**：账号页原来自己写了一遍"读正版 → 缓存 → 读离线 → 清坏配置"，首页再抄一遍就会有两份缓存、两次下载。现在两边都调同一个：

| 方法 | 作用 |
| --- | --- |
| `PremiumAsync(session)` | 正版皮肤，**按 UUID 缓存**在内存里；没上传皮肤时返回 `Ok=false` 的结果（`Error` 是中性的说明） |
| `Offline()` | 离线皮肤；没导入返回 `null`（页面据此留白），文件坏了顺手清配置 |
| `InvalidatePremium()` | 退出登录时调用，避免还拿上一个账号的贴图 |
| `ModelText(model)` | 「细手臂 / 粗手臂」 |

账号页因此删掉约 60 行重复代码（`LoadSkinsAsync` 里的下载与异常处理、`LoadOfflineSkin` 的整段、`ModelText`），行为不变——只是数据来源换成共用缓存。

**顺带修掉一处自相矛盾**：这张卡原来在登录正版后仍显示配置里的离线玩家名（`Steve`）和「离线登录 · 无需验证」，而左下角状态栏写的是「ShakableCat · 正版登录」。同一个窗口里两个身份，很怪。现在卡片跟着 `Msa.Current` 走——正版显示账号名 + 「正版登录 · 皮肤自动同步」，离线才用配置里的玩家名。

**显示规则**：登录正版 → 正版皮肤；没登录或账号没上传皮肤 → 退回离线皮肤；都没有 → 留白 + 提示「点「编辑」去账号页挑一张 PNG」。和账号页同一套优先级。

**实测**

```
首页        → ShakableCat / 正版登录 · 皮肤自动同步 / 白发黑红衣服的细手臂角色
              说明行「正版皮肤 · 细手臂」，拖动后转到侧前方，双击复位
              下方独立卡片显示游戏目录与「打开文件夹 / 设置」
账号页      → 重构后仍正常：正版皮肤 Chip 选中、说明「正版皮肤 · 细手臂」、预览可拖动
```


### 披风选择（✅ 2026-11-04 落地）

**要解决什么**：账号页能看皮肤，但正版账号还能戴披风，启动器里既看不到也换不了——想换披风得开浏览器去 minecraft.net。用户要求在皮肤板块里补一个披风选择，**账号没有任何披风时显示「无」**。

**接口事实**（拿真实账号验的，不是照文档猜）

| 项 | 事实 |
| --- | --- |
| 读 | `GET /minecraft/profile` 的 `capes` 数组，每项 `id / state / url / alias` |
| `state` 语义 | **`ACTIVE` = 当前戴着**，同一时间只会有一条；`INACTIVE` = 拥有但没戴（实测账号：Pan = INACTIVE、15th Anniversary = ACTIVE） |
| 戴 | `PUT /minecraft/profile/capes/active`，body `{"capeId": "<id>"}`，**返回更新后的完整档案**（可直接从中取新列表） |
| 摘 | `DELETE /minecraft/profile/capes/active`，同样回完整档案 |
| 错误 | 400 = `profile does not own cape`（没这款披风）；401 = 令牌失效 |

**界面**：披风一行放在皮肤卡片里、皮肤来源切换（正版 / 离线）下方，一行 Chip：

```
皮肤
[正版皮肤] [离线皮肤]
正版皮肤 · 细手臂

披风
[无] [Pan] [15th Anniversary]
```

- **只在「登录了正版 + 当前看的是正版外观」时出现**：披风属于账号外观，离线皮肤是本地 PNG，摆在离线预览下点它不会有任何视觉反馈
- 账号一款披风都没有时，只渲染一个「无」Chip（选中、不可点）——这就是需求里那句「没有任何披风则显示无」
- 有披风时「无」也可点，走 `DELETE` 把披风摘掉——否则戴上就摘不下来了
- 切换期间所有 Chip 置灰，避免连点；失败弹 Toast 并从 `_capes` 重建，不会留下假的选中状态
- 点中当前那款直接返回，不白跑一次请求

**3D 预览**：`SkinPreview` 加 `SetCape(BitmapSource)`，在角色背后挂一片 10×16×1 的薄板——上沿齐肩（y=8）、下沿到大腿（y=-8）、z 取 -3.0（本体背面 -2、外衣层 -2.25，留一条缝防闪烁）。贴图是独立的一张，所以是**第二个 `GeometryModel3D` + 独立材质**，不能并进皮肤那个 mesh。

披风贴图布局（64×32）：顶 (1,0)、底 (11,0)、右 (0,1)、前 (1,1)、左 (11,1)、后 (12,1)。**图案画在 (1,1) 那块**（实测两块 10×16 的颜色数：图案区 22 色 / 内衬区 3 色），也就是朝外的那一面——所以前后两张要**对调**，不能照搬皮肤的顺序。

**踩到的坑：`ViewportUnits` 必须是 `Absolute`**

披风的 UV 只占贴图左上角 22×17 的一小块。用皮肤那套 `RelativeToBoundingBox` 时，WPF 会把**整张图拉伸进这块 UV 的包围盒**——表现是背面同时能看到正反两面的图案（creeper 脸 + "15" 并排）。用 10 色竖条测试披风验证：改成 `Absolute` 前彩条只占披风宽度的 ~40%（右侧采样到透明区），改后十条铺满整个宽度。

**取数**：`SkinProvider` 新增 `CapesAsync`（按 UUID 缓存，读失败**不缓存**，免得一次网络抖动一直显示「无」）、`UpdateCapes`（切换后用平台返回的列表替换缓存）、`ActiveCape`、`CapeTextureAsync`（按 URL 缓存贴图）。首页预览也调同一套，两个页面共用一次下载。

**实测**（真实账号，2 款披风）

```
账号页      → 披风一行「无 / Pan / 15th Anniversary」，15th Anniversary 深色选中
             拖动预览转到背面：绿色 creeper 图案完整铺在背上，宽度比身体略宽
点 Pan      → 日志「披风已切换为 Pan」，Chip 与 3D 预览同时变（浅蓝 + 金色图案）
点 无       → 日志「已摘掉披风」，Chip 落到「无」，背上干净
点回        → 恢复 15th Anniversary，与账号原状态一致
离线皮肤    → 披风整块隐藏，预览里也没有披风
空账号      → 临时让 `ParseCapes` 返回空列表重发布：只显示一个深色「无」，无其他选项；验证后已撤掉补丁
CLI         → `--cli capes` 列出 2 款（● 标当前）；`skin-premium <png> 180` 从背后渲染，含披风
```

**局限**

- 只对正版账号有效；离线账号没有披风这个概念
- 换披风改的是账号设置，**已经开着的游戏不会实时变**，要重启游戏（和网页上换披风一致）
- 只列出平台返回的披风；已过期 / 被回收的不会出现
- 预览里的披风是静态垂下的，没有游戏里的摆动

**工作量口径**：`Msa` 加 `CapeInfo` + `FetchCapesAsync` / `SetCapeAsync` / `ClearCapeAsync` / `SendCapeAsync` + 解析；`SkinTexture` 加通用 `BitmapFromBytes` / `BitmapFromUrlAsync`（披风不走皮肤尺寸校验）；`SkinProvider` 加四个披风方法；`SkinPreview` 加 `SetCape` + `BuildCape` + 贴图缓存通用化 + `RenderToFile` 的 cape / yaw 参数；账号页加披风区（XAML + 约 130 行逻辑）；首页预览带上披风；CLI 加 `capes`，`skin-premium` 支持角度。


### 首页点版本即切换（✅ 2026-11-04 落地）

**要解决什么**：「全部版本」里点一行只是高亮，顶部「准备起飞」卡片还停在原来那个版本，想换版本得点右上角「切换版本」跳到版本管理页再回来。用户要求点一下就地切换。

**根因不是缺逻辑，是点击根本到不了**：`RecentList_SelectionChanged` 里早就有"改 `_selected` + 写 `LastVersion`"的代码，但 `PlainList` 用的 `RowItem` 把 `ListBoxItem.Focusable` 设成 `False`——WPF 里没有焦点就不会选中，`SelectionChanged` 永远不触发。这个坑 2026-10-31 在资源包浮层踩过一次（见 §4-13），当时靠改成 `Button` 绕过；这次是纯选取列表，所以换成正路：新增 `RowItemPick`（`BasedOn RowItem`，只把 `Focusable` 改回 `True`、光标改手形），只挂在首页这个列表上——别的列表（模组 / Java）行内要点按钮，不能动 `RowItem` 本身。

**行为**

| 动作 | 结果 |
| --- | --- |
| 点某一行 | 顶部卡片换版本名 / 加载器标签 / 基础版本 / 版本目录，Java 检测重跑，列表高亮跟着走 |
| 同时 | `Config.LastVersion` 立即写盘（原来只在"启动游戏"时才落盘，重启启动器会退回旧版本） |
| 反馈 | 右下角 Toast「已切换到 <版本 ID>」——顶部卡片可能滚出视野，得给个确认 |
| 点当前已选中的行 | 直接返回，不重复写盘、不弹 Toast |

**与程序同步选中项的区分**：`ApplySelection()` 里把 `SelectedItem` 拉回 `_selected` 时会走一遍 `SelectionChanged`，用 `_pickSync` 标志 + `ReferenceEquals(v, _selected)` 双重挡掉，避免刷新页面被当成用户点击（否则每次进页面都会弹一次"已切换"）。

**实测**（`--cli` 同版本 exe，测试副本指向真实 `.minecraft`）

```
点「1.20.1」行 → 顶部：1.20.1 · 原版 · 基础版本 1.20.1 · versions\1.20.1
                 列表第 3 行高亮，Toast「已切换到 1.20.1」
config.json     → LastVersion = "1.20.1"
日志            → 切换当前版本：1.20.1
```


### 界面文案瘦身（✅ 2026-11-04 落地）

**要解决什么**：启动器每个页面都在"解释自己"——机制说明、格式要求、操作提示各占一行，信息密度压过了功能。用户逐屏标注了要删的位置，按图清理。

**删掉的东西**

| 位置 | 删掉的内容 | 说明 |
| --- | --- | --- |
| 启动页 · 游戏目录卡 | `存档隔离：强制开启 → <版本目录>` | 版本目录卡里已经显示了完整路径，这行是重复 |
| 启动页 · 右侧 | 「小提示」整卡 | 首次使用引导，装过版本后没有信息量 |
| 版本管理页 · 右上 | `双击版本可立即启动` | 每行都有「启动」按钮，双击只是加速方式 |
| 账号页 · 登录状态 | `正版账号：皮肤与正版验证服务器可用。` | 状态行已经写了「已登录：xxx」 |
| 账号页 · 皮肤卡 | `皮肤文件用 64×64 或 64×32 的 PNG；…` | 格式错了会直接提示，不需要提前讲 |
| 设置页 · 游戏目录卡 | 「存档隔离 强制」整块 + 存档路径 + 旧存档提示 + 「打开该版本存档目录」 | 机制强制生效，没有开关可讲；要看存档进版本目录即可 |
| 设置页 · 数据与运行时 | **整张卡片**（数据目录 + 运行时目录 + 「打开」/「浏览」/「恢复默认」） | 数据目录随 `SlopeX.exe` 走、运行时固定在 `%APPDATA%\SlopeX\runtime`，日常不需要改 |
| 设置页 · 主题 | `默认「极简白纸」，可跟随 Windows 深浅色…` 与 Chip 下的动态说明 | 五个 Chip 的名字已经说明了一切 |
| 设置页 · Java | `留空则按版本自动选择（…）。1.17 需 16+ …` | 自动检测已经在跑，版本对照表只在出问题时有用 |

**代码侧同步清理**（不是只删 XAML，避免留下悬空引用）

- `PageSettings.xaml.cs`：删 `UpdateIsolationInfo`、`DismissLegacySaves_Click`、`OpenSaves_Click`、`OpenDataDir_Click`、`BrowseRuntimeDir_Click`、`ResetRuntimeDir_Click` 六个方法，以及 `LoadFromConfig` / `Save_Click` 里对 `DataDirText` / `InputRuntimeDir` / `RuntimeDirHint` / `ThemeHint` 的读写
- `ThemeHint` 的整段 `switch` 一并删除，`UpdateThemeChips` 只做 Chip 互斥高亮
- Java 自动下载那条说明原本写「装到下方『运行时目录』」，因为卡片没了会指向不存在的控件，改成只讲镜像顺序与体积
- 主界面、版本页、账号页里对应的 `GameDirMode` / `AccountHint` 控件与赋值一并删除

**机制没有变**：版本仍然强制用 `versions\<id>` 作为游戏目录，`VersionEnv.SavesDir` / `HasLegacySharedSaves` 这套底层能力保留（将来做存档管理时直接用），只是不再有 UI 入口。`Config.DismissLegacySavesHint` 字段同样保留，老配置文件读进来不会报错。

**代价**：`JavaRuntimeDir` 现在只能改 `config.json`（README 与 §5「环境」已同步说明）。运行时目录本身日常不需要调整，这个取舍可以接受。

**实测**（`SlopeX.exe` 逐屏截图核对）

```
启动页      → 游戏目录卡只剩路径与「打开文件夹 / 设置」，右侧只剩账号与游戏目录两张卡
版本管理    → 右上角无提示文字，列表行距不变
账号页      → 登录状态只剩「已登录：ShakableCat」，皮肤卡只剩 Chip + 一行状态 + 选择文件
设置页      → 主题 / 游戏目录 / Java 运行时 / 性能 / 启动 / 下载源 / 下载线程 / 关于 / 法律声明，逐张检查无残缺卡片
设置页底部   → 「关于」里的「打开配置目录」仍在，配置路径可见
```


### 账号页与皮肤预览（✅ 2026-11-04 落地）

**要解决什么**：账号原来挤在设置页里，只有一行「已登录：xxx」，皮肤完全看不到——想知道「我现在长什么样」只能进游戏。

**页面结构**：账号从设置页独立成左侧第 5 项（启动 / 版本管理 / 下载 / 任务与日志 / **账号** / 设置），页内两张卡片：登录状态（含离线玩家名）与皮肤。

**皮肤预览**：用 WPF 自带的 `Viewport3D` 拼六个长方体，每个面按皮肤 UV 取样，不引任何 3D 库。

| 部位 | 尺寸 | 说明 |
| --- | --- | --- |
| 头 | 8×8×8 | 含头发与眼睛 |
| 躯干 | 8×12×4 | — |
| 双臂 | 4×12×4（slim 3×12×4） | 细手臂按皮肤变体收窄 1 像素 |
| 双腿 | 4×12×4 | — |

第二层（帽子 / 外衣 / 袖子 / 裤腿）一并画了，各方向外扩 0.5，透明像素漏出第一层。不带这层的话，很多皮肤（含测试用的那张）会缺一半细节。

**交互**：左键拖动改水平角与俯仰角（俯仰限 ±65°），双击回正面；相机距离按控件尺寸实时算，竖长框里上下留白一致。

**清晰度**：WPF 3D 的纹理采样固定是双线性插值，没有"最近邻"开关。64×64 的像素画铺到每像素约 10 个屏幕像素时，每条边界都被摊成一条 10 px 的渐变带——实测一条横线上 99 个像素出现 **44 种颜色**，看着就是糊。做法是先把贴图按最近邻**预放大 16 倍**（64→1024），插值只发生在放大后的相邻纹素之间（1/16 个皮肤像素），同样一行只剩 **3 种颜色**，视觉上就是硬边。顺带把 alpha 二值化，避免外衣层半透明边缘混出白边。

**三个皮肤来源**

| 来源 | 取法 |
| --- | --- |
| 正版账号 | `api.minecraftservices.com/minecraft/profile` 的 `skins[0]`（自带 `state` / `variant`），没有就退回 `properties` 里的 base64 贴图清单；地址统一换 https 再下 |
| 离线自定义 | 用户选的 PNG 复制到 `SlopeXData\skins\`，配置里**只存文件名**（整个目录搬走仍有效） |
| 都没有 | **留白**。预览框空着，提示「还没有选择皮肤。点「选择皮肤文件」挑一张…」 |

> 这里最初放的是代码画的一张"Steve 风格"贴图，后来撤掉了：它只是"像"史蒂夫，五官和发型都对不上，反而让人以为启动器自带了一张坏掉的默认皮肤。**没导入就不画**。

模型类型：正版用平台给的 `variant`，本地文件靠「slim 布局特有的那一列透明像素」判断。

**预览切换**：两个 Chip（正版皮肤 / 离线皮肤），正版未登录时该 Chip 禁用；用户手动选过之后页面刷新不再自动切回去（`_sourceChosenByUser`）——否则每次进页面都被拉回正版。

皮肤区只有一个按钮「选择皮肤文件」（离线状态下可用）。原来的「恢复默认」已随默认皮肤一起删掉——没有"默认"可回了；想换一张就直接再选一张，CLI 侧保留 `skin-clear` 供排障时清空配置。

**排障命令**（皮肤出问题在日志里完全沉默，只能看图）

```
SlopeX.exe --cli skin-render  [输出png] [皮肤文件]   渲染默认或指定皮肤
SlopeX.exe --cli skin-premium [输出png]              渲染当前正版账号的皮肤
SlopeX.exe --cli skin-set     <皮肤png>              设为离线皮肤
SlopeX.exe --cli skin-clear                          恢复内置默认
```

`skin-render` 在 STA 线程里用 `RenderTargetBitmap` 把预览渲染成 PNG——3D 参数不对时能在命令行直接看图，不用开窗口对坐标，迭代快得多。

**实测**

```
--cli skin-premium             → ShakableCat，SLIM，64×64，贴图 ae252684…
                                 渲染出白发 + 黑红衣服的细手臂角色
--cli skin-render              → 内置默认：棕发 / 蓝眼 / 青上衣 / 蓝裤 / 灰鞋
--cli skin-set preppy-kitty.png → 存入 SlopeXData\skins\，配置写入 OfflineSkin
界面 正版 Chip                 → 显示正版皮肤，说明「正版皮肤 · 细手臂」
界面 离线 Chip                 → 显示 preppy-kitty，说明「离线皮肤 · preppy-kitty.png（细手臂）」
界面 拖动预览                  → 正面转到侧后方，双击复位
```

**未验证**：「选择皮肤文件」的系统文件对话框没法自动化——Win11 的新式对话框不响应合成的键鼠事件（`keybd_event`、直接点击、Alt 快捷键都不生效）。对话框之后的保存逻辑用 `skin-set` 走同一段代码验证过：两者都调 `SkinStore.Save` + 写配置，差别只在入口。

**工作量口径**：新增 `Modules/ModAccount/SkinTexture.cs`（401 行：加载 / 校验 / 默认皮肤 / 头像裁切 / SkinStore）、`Controls/SkinPreview.cs`（473 行：3D 模型 + 相机 + 拖拽 + 离屏渲染）、`Pages/PageAccount.xaml(.cs)`（460 行）；`Msa` 加 `FetchSkinAsync` 与 `GetProfileJsonAsync`；`Config` 加 `OfflineSkin`；`MainWindow` 导航 5 项 → 6 项；`PageSettings` 移除账号卡片；`Cli` 加 4 个 `skin-*` 子命令。


### 声明与文档去关联化（✅ 2026-11-04 落地）

**要解决什么**：项目早期的 README、法律声明、设置页与代码注释里都带着「参考某某启动器」的说明。多轮迭代后，启动流程、下载链路、账号体系与界面结构都已经和它无关，这些表述只剩两种坏处——让人误以为代码有继承关系；法律声明里也要多解释一节本不必存在的内容。用户要求全部删掉，对外只保留「全部代码为独立实现」。

**改动清单**

| 位置 | 改动 |
| --- | --- |
| 法律声明弹窗 | 右上角「关闭」→「已明白」——读完正文给的是确认，不是「关掉」 |
| `docs/LegalNotice.md` | 删掉「与第三方启动器的关系」整节（原第一节），余下三节重排编号；项目性质一条补「全部源代码均为独立实现」 |
| `docs/LegalNotice-Brief.md` | 同上，四节 → 三节（项目性质 / 权利通知 / 其他） |
| 设置页「关于」 | 删掉「架构参考」整行——它唯一的用途就是点名对照对象 |
| 设置页「法律声明」 | 说明改为「非商业使用限制、权利通知与联系方式。使用本启动器即视为同意全部条款。」 |
| `README.md` | 首段改为「面向 Windows 的 Minecraft: Java Edition 第三方启动器，C# + WPF 独立实现」；技术栈一节删掉对照对象的语言与仓库；小节标题「架构（对照 …）」→「架构」；许可一节只留「全部代码为独立实现，未使用任何第三方启动器的源代码」 |
| 代码注释 11 处 | `Install` / `Fabric` / `Core` / `Net` / `Downloader` / `JavaFinder` / `JavaProvisioner` / `Launch` 里的「（对应 XX 的某模块）」全部删掉，只留功能描述 |
| 设置页提示 | 64 线程确认框里「也是 XX 的默认值」删掉 |
| `docs/PROGRESS.md` §3.1 | 「为什么是 C# 而不是 VB.NET」→「技术栈选择」，只讲自己的选型理由 |
| 分段下载一节 | 删掉对另一实现参数表的转述，改为直接写本工程的分片参数 |

**验证**

- 仓库全量文本检索（排除 `.git`、`.minecraft` 与测试副本）：相关关键词 **0 命中**
- 新 exe 做二进制字符串检索：同样 **0 命中**（法律声明是内嵌资源，旧正文会留在 exe 里，必须重新发布才干净）
- 实机截图：弹窗右上角为「已明白」，正文首节为「一、项目性质与非商业使用」；设置页「关于」卡片只剩「版本 / 配置文件」两行，法律卡片说明已改
- `--cli legal`：1042 字符 / 33 行，渲染 14 个块，与正文一致

**历史清理的边界**：116 个提交的**提交信息**已重写干净，19 个已合并的本地分支同步改写后仍落在新 main 的祖先上；本地备份引用删除、reflog 过期并 `gc --prune=now`，旧对象已读不出来；远端用 `--force-with-lease` 推送（远端原提交是本地旧历史的祖先，没有覆盖别人的提交）。**仍留有痕迹的是历史快照**——116 个提交里有 115 个的树中仍带着旧版 README / docs / 注释文本；要连快照一起清，等于把整条历史重写一遍（每个提交的树都会变），影响面远超改文案，需单独确认后再动。


### 首页统计条下线与版本号收口（✅ 2026-11-04 落地）

**要解决什么**

首页底部那排四格统计（已安装版本 / Java 版本 / 内存上限 / 下载源）拼的都是别处已有的信息：版本数在「全部版本」里数得出来，Java 与内存上限在「准备启动」卡里写着，下载源在设置页。而且「Java 版本」取的是机器上最高的那个主版本，与当前版本实际要用的 Java 不是一回事（这台机器上显示 25，选中的 1.20.1-forge 用的是 Java 17），留着只会读错。
右下角状态栏原本是「内存 N GB · BMCLAPI 镜像」，同样是设置页的重复；这个位置更适合常驻版本号。

**改动清单**

| 位置 | 改动 |
| --- | --- |
| `Pages/PageHome.xaml` | 删掉统计条整块 `<Border>`（约 49 行） |
| `Pages/PageHome.xaml.cs` | 删掉 `StatVersions` / `StatJava` / `StatMemory` / `StatSource` 的赋值；Java 检测的返回值从三元组收成二元组——`Item2` 原本只喂统计条，没有别的消费者 |
| `Windows/MainWindow.xaml.cs` | 构造时写一次 `StatusRight.Text`，`RefreshStatusAsync` 只更新左侧，不再覆盖右侧 |
| `Modules/ModBase/AppInfo.cs`（新增） | 版本号唯一来源：读程序集 `Version`，输出 `1.0.0` 与展示形 `v1.0.0`；不用 `InformationalVersion`（它带 `+commit` 后缀） |
| `Pages/PageSettings.xaml` / `.xaml.cs` | 「关于 → 版本」改为 `SlopeX Launcher + AppInfo.Version`，不再写死 |
| `Modules/ModCli/Cli.cs` | 启动横幅用 `AppInfo.Version`；同时修掉 `--cli` 不带命令时 `args.RemoveAt(0)` 对空列表抛 `ArgumentOutOfRangeException` —— 现在走 help |
| `Modules/ModLaunch/Launch.cs` | JVM 参数 `launcher_version` 改用 `AppInfo.Version` |

**为什么定在 1.0.0**

功能面已经覆盖完整生命周期（装版本 / 启动 / 正版与离线账号 / 模组 / 光影材质 / Java 托管 / 更新检查），`csproj`、设置页与发给游戏的 `launcher_version` 一直是 1.0.0，没必要另起一个号。后续按 `1.0.x` 修问题、`1.1.0` 加功能、`2.0.0` 动配置格式或存档结构。

**验证**

- `dotnet build -c Release`：0 警告 0 错误
- 实机截图：首页版本列表下方已无统计条，右下角为 `v1.0.0`
- 设置页「关于」：版本仍显示 `SlopeX Launcher 1.0.0`（走绑定，不再是字面量）
- `--cli list`：横幅 `SlopeX Launcher 1.0.0  |  gameDir = …`
- `--cli`（不带命令）：打印帮助，不再抛异常

**踩到的行尾问题**：这批源文件在仓库里是 **LF**，改写时若不显式处理，`git diff` 会显示整文件重写（`MainWindow.xaml.cs` 928 行变动）。提交前用 `git diff --stat --ignore-cr-at-eol` 对比真实改动量，再把 CRLF 统一回 LF。`docs/*.md` 相反，仓库里是 CRLF。


### 整合包下载（✅ 2026-11-05 落地）

**要解决什么**

以前只能把整合包下载到本地再导入（CLI 的 `import-pack`），界面里没有入口。这次补的是「找包 → 下载 → 安装」的前半段：搜索走 Modrinth 与 CurseForge，中文经 MC百科 映射；安装仍然复用 `ModpackInstaller`，两条入口（本地导入 / 在线下载）行为完全一致。

**导航**

「下载」下方新增子项「整合包」，鼠标停在「下载」上时滑出：动画改的是 `ListBoxItem.Height`（0 → 36，180 ms，CubicEase），下面的导航项被顶下去，视觉上就是从「下载」下方长出来；不需要另铺浮层。

- 鼠标离开侧栏后等 260 ms 再收（期间移回来就撤销），提示条式的闪烁被避免
- 子项被选中时**不**收——否则选中态跟着高度一起没了
- 点「下载」时鼠标还停在它上面，这时也不收，否则刚滑出来就消失，得挪开再挪回来

**搜索**

| 来源 | 实现 |
| --- | --- |
| CurseForge | `classId=4471`（整合包分类；模组是 6），走 MCIM 镜像 |
| Modrinth | `project_type:modpack` 的官方 v2 search |
| MC百科（中文关键词） | `search.mcmod.cn/s?key=<q>&filter=2`；**不能用 `modpack.html?key=`**——那个页面只支持元素 / 版本筛选，`key` 会被忽略，永远返回默认列表（实测「RLCraft」「无尽贪婪」拿到的都是同一批 32 条） |

中文链路：全站检索 → 结果卡片（`result-item`）→ 读取「/modpack/&lt;id&gt;.html」词条页 → 解码 `link.mcmod.cn/target/{base64}` → 取 `curseforge.com/minecraft/modpacks/<slug>` 或 `modrinth.com/modpack/<slug>`。整合包在平台上的路径与模组不同（`/modpack/` vs `/mod/`），正则必须分开。词条没登记链接时，用名字里括号中的英文名去 CF 精确搜一次兜底，只认标题或 slug 完全一致的。

**合并顺序与模组相反**：CF 在前、Modrinth 在后。实测搜 `all the mods`，Modrinth 前 8 条全是 "All the MrCrayfish's Mods" 这类小包，ATM 系列根本不在前排；CF 的相关度排序才符合预期。

**安装**

点「安装」先拉包版本列表（CF 侧剔除 `ServerFiles-*.zip` 这类服务端包），选中后把归档下到 `cache\packs\`，再交给 `TaskCenter.StartModpackInstall`——补原版、装加载器、按清单下模组、写 `slopexPack` 标记全部由既有实现处理，进度在「任务与日志」页。

**踩到的三个坑**

1. `UserControl` 只能有一个子级：页面里同时要 Grid 和 Popup，必须再包一层根 Grid，否则 `MC3089`。
2. **子项插在列表中间后 `SelectedIndex ≠ Tag`**：导航按 Tag 认页面编号，`Navigate(4)`（账号）如果直接写 SelectedIndex 会落到「任务与日志」。改成按 Tag 找项再取位置。
3. **塌着的 `Visibility.Collapsed` 项选不中**：`Navigate(6)` 必须先滑出子项再设置选中，否则选了个不存在的位置（表现出来就是跳到了「设置」）。

**验证**

- `--cli modpacks-search "all the mods"`：24 条，CF 的 ATM10 / ATM10 Sky / ATM11 在前
- `--cli modpacks-search 宝可梦`：MC百科 命中 9 条，核对 9 条，7 条映射到平台（Cobblemon 系列、Pixelmon、ATMons 等），2 条只挂了网盘
- `--cli modpacks-versions curseforge 925200`：49 个包版本，服务端包已剔除；`modrinth 1KVo5zza`：321 个 `.mrpack`
- 截图（用临时调试入口渲染，验证后已撤除）：导航「下载」下的「整合包」为选中态，页面标题、来源筛选、搜索框、结果表（图标 / 来源 / 作者 / 下载量 / 支持版本 / 更新 / 安装）全部正常

**没验到的**：鼠标悬停的真实触发。当时前台是全屏游戏（SCUM），鼠标被游戏捕获，`SetCursorPos` 与 `PostMessage(WM_MOUSEMOVE)` 都到不了启动器窗口——所以滑出后的布局用临时入口渲染验证，事件接线只到「XAML 编译期校验 + 代码审阅」这一层。


### 美术与主题（结论，2026-10-31 精简）

**进度**：S1 地基（双主题 token + 间距 / 圆角 / 动效刻度 + `ThemeService`，2026-10-29 完成）→ S2 边做边补 → 当前 **S3 视觉定案**。`docs/design-language.md` §5 还差 3 个组件（`ComboBox`、`ContextMenu`、`Steps`），都缺真实使用场景，等有地方用再补——提前设计等于按想象中的用法做一版没人用的东西。

**命名**：浅色「极简白纸」（默认）、深色「极简墨纸」。沿用纸媒隐喻——白纸以墨写字，墨纸以浅色显字，正好是 `Ink` token 的反相；深色底是暖调近黑，刻意不叫「极简黑」，避免"纯黑底"的误导。

**切换模型**：主题 × 模式二维。主题卡片选白纸 / 墨纸；「外观模式」三选一（跟随系统 / 仅浅色 / 仅深色）。跟随系统读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`，监听 `WM_SETTINGCHANGE` 实时切换、不重启；选了固定模式就忽略系统变化。

**Toast 的取色（2026-11-01 修正）**：原来用 `Ink` 当底色、`OnInk` 当文字——浅色主题下是黑底白字很醒目，但深色 / 插画主题的 `Ink` 本身是浅色，Toast 就成了一整块白，压在粉紫插画上很扎眼。现在各主题自带 `ToastBg` / `ToastFg`：

| 主题 | 底色 | 理由 |
| --- | --- | --- |
| 极简白纸 | `Ink` 近黑 + 白字 | 背景本来就浅，浅色面板会被淹没 |
| 极简墨纸 | `Paper3` 深灰 + `Ink` 亮字 | 比卡片亮一档，靠边框浮起 |
| 复古未来 / ViceCity | `Paper3` 的紫 + 暖白字 | 和卡片同族，不破坏插画氛围 |

`ToastCard` 顺带加了 1px `LineStrong` 边框——深色主题靠它和背景分开，浅色主题下这圈边落在黑底上基本看不见。

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
  - 仍未验证：连一台**强制正版验证**的服务器（需要对方的 session 校验，单机只走到档案与皮肤）。此项已搁置，见 §6.2 第 5 条。
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
