# SlopeX Launcher 进度报告与实施计划

> 首次生成：2026-10-29 02:32 ｜ 最近更新：2026-10-29 02:42
> 对应产物：`SlopeX.exe` 1.0.0（自包含单文件，138.5 MB，win-x64）
> 代码规模：26 个 C# / XAML 源文件，6,525 行
> 版本控制：git 已初始化，`main` 基线提交 `8cc0cd1`
> 本轮改动：仅工程基建（git 初始化 + `.gitignore` 补强），**未修改任何源码**

---

## 0. 一句话结论

**已经是一个能真正装、真正启动的启动器**：原版版本安装、Java 自动补齐、版本隔离、离线启动全链路跑通，两条不同世代的版本（1.20.1 / 26.3）都在本机实机进过游戏。
缺的是**账号体系**（微软正版登录）与**模组生态**（Forge/Fabric 安装、模组管理、整合包导入）。

---

## 1. 交付物清单

| 路径 | 说明 | 状态 |
|---|---|---|
| `SlopeX.exe` | 免安装单文件，双击即用，无需装 .NET | ✅ |
| `src/SlopeX/` | 完整源码（.NET 8 + WPF） | ✅ |
| `.minecraft/` | 便携游戏目录，1.3 GB，已装 26.3 与 1.20.1 | ✅ |
| `docs/screenshots/` | 7 张界面截图（首页 / 版本 / 下载 / 任务 / 设置 / Java 缺失 / 自动安装） | ✅ |
| `README.md` | 功能、架构、构建、限制说明 | ✅ |
| `build.cmd` | 一键构建（纯 ASCII，规避 cmd 编码问题） | ✅ |
| `tools/screenshot.ps1` | 内部工具：启动并截窗口图，用于视觉回归 | ✅ |
| `.git/` + `.gitignore` | 版本历史基线 `8cc0cd1`，只跟踪 40 个源文件（产物全排除） | ✅ |
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
| **版本隔离** | ✅ | 每版本独立 `versions\<id>`，首次启用继承旧存档 |
| 登录 | ⚠️ | 仅离线登录（自定义玩家名），无正版验证 |
| 任务与日志 | ✅ | 安装进度卡 + 游戏实时日志（增量缓冲刷新） |
| 设置持久化 | ✅ | `%APPDATA%\SlopeX\config.json`，路径存相对形式 |
| 便携模式 | ✅ | exe + `.minecraft` 整个目录搬走仍可用（实测过 `I:\sxtest`） |
| 无界面 CLI 排障 | ✅ | `list / java / java-ensure / manifest / install / verify / plan / launch` |

---

## 3. 架构与技术选型

### 3.1 为什么是 C# 而不是 VB.NET

PCL2 是 VB.NET + WPF（`Meloong-Git/PCL`，`Modules/Mod*` 分层 + `Controls`/`Pages`）。本工程用 **C# + WPF / net8.0-windows**：运行时、XAML、控件模型完全一致，只是宿主语言换成 C#（PCL2 自己的底层库 MeloongCore 本身就是 C#）。
**代码为独立实现，未复制 PCL2 源码**，只借鉴模块划分与启动器行为逻辑。

### 3.2 模块分层

```
ModBase      路径 / 日志 / 配置 / JSON 工具 / MVVM 基类        526 行
ModNet       并发下载 / 镜像改写 / 源回落 / 单文件超时 / SHA1    387 行
ModMinecraft 版本扫描 / inheritsFrom 合并 / 版本隔离环境        486 行
ModJava      运行时探测 / 版本匹配 / 自动下载安装               789 行
ModLaunch    规则引擎 / classpath / natives / 命令行 / 进程     562 行
ModDownload  版本安装 / 资源补齐 / 任务中心                     477 行
ModCli       无界面模式                                      379 行
Pages+Windows+Themes+App  UI 层（5 页 + 主窗口 + 样式字典）  2,919 行
```

（模块层 3,606 行 + UI 层 2,919 行 = 6,525 行）

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

---

## 5. 当前限制

### 功能缺口
- 仅 Windows（WPF + 注册表探测）
- 无微软正版登录，只能离线账号
- 无 Forge / NeoForge / Fabric 一键安装（能启动别家装好的模组版本，但自己不装）
- 无模组管理（批量启停、依赖检查）
- 无整合包导入（CurseForge / Modrinth）
- 版本隔离只有“全隔离 / 全共用”两态，没有“材质包共享 + 存档隔离”的混合模式

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
- `%APPDATA%\SlopeX\`（config、logs、Java 运行时 180 MB）仍不在便携目录内
- 版本隔离后根目录的 `saves` / `resourcepacks`（16 MB）作为“公共源”保留，属冗余

---

## 6. 后续计划

### P0 — 补齐“能玩主流整合包”的最小闭环

**1. Fabric / Quilt 安装**
- 做法：拉 `fabric-meta` 的 loader 列表 → 生成 `inheritsFrom` 版本 JSON → 补 loader 依赖库
- 验收：`--cli install-fabric 1.20.1` 后能直接启动，且隔离目录里有 `mods`
- 风险：低（无 processors，纯 JSON 合并）

**2. Forge / NeoForge 安装**
- 做法：下载 installer → 解析 `install_profile.json` → 跑 processors（含 `client-extra`、`srg` 映射）
- 验收：装完 `--cli verify` 通过、能进游戏、崩溃日志无缺失类
- 风险：高（processors 依赖 Java 环境与临时目录，失败模式多），建议放最后

**3. 微软正版登录**
- 做法：OAuth 2.0 设备码流程（`live.com/oauth20_connect.srf`）+ XBL / XSTS 换 token + `/minecraft/profile`
- 需要你提供：**自建 Azure 应用（公共客户端）的 client_id**，否则只能继续离线
- 验收：登录后 skin 正常显示、服务器可进（非正版服仍拒绝属正常）
- 风险：中（token 刷新、设备码轮询超时处理）

### P1 — 从“能启动”到“好用”

**4. 模组管理**：列表 / 启停（改名 `.disabled`）/ 依赖缺失提示
**5. 整合包导入**：解析 `manifest.json`（CurseForge）与 `.mrpack`（Modrinth），映射到隔离目录
**6. 完整性修复**：全量 SHA1 + 一键补齐（复用现有 `Downloader`，只补缺失与损坏）
**7. 多任务队列**：并行安装多个版本、暂停/继续、失败重试队列

### P2 — 打磨

**8. 完全便携**：`config / logs / runtime` 迁到 `SlopeXData\`，实现“一个文件夹拷走即用”
**9. 启动器自更新**：检查 GitHub Release / 自建清单，下载新 exe 并替换
**10. 崩溃诊断**：解析 `crash-reports`，把 `UnsupportedClassVersionError`、缺模组、OOM 翻成人话
**11. 界面细节**：高 DPI 适配、键盘导航、日志搜索与导出

### 建议里程碑

| 里程碑 | 内容 | 完成标志 |
|---|---|---|
| M2 | Fabric + 模组管理 | 主流 Fabric 整合包能装能玩 |
| M3 | 微软登录 | 正版账号可登录、皮肤正常 |
| M4 | Forge / NeoForge + 整合包导入 | 覆盖 90% 玩家实际使用场景 |

---

## 7. 待决策事项

> 2026-10-29 02:42：第 4 项已决并落地，其余 3 项仍待定。

1. **Forge 要不要做**：工作量最大的单项（预估是 Fabric 的 3–4 倍），但对整合包覆盖率影响最大。可选 A 自研 processors / B 调官方 installer `--installClient` / C 暂不做。
2. **微软登录是否接受自建 Azure 应用**：需要你去 portal 建一个“公共客户端”应用并给我 client_id；不建就只能保持离线。
3. **是否要完全便携**：把 config / logs / Java 运行时也搬进启动器目录。
4. ~~**是否初始化 git 仓库**~~ ✅ **已决（2026-10-29 02:42）**
   - 已 `git init -b main`，基线提交 `8cc0cd1`；身份仅本仓库生效（`SlopeX Dev <dev@slopex.local>`），未改全局配置
   - `.gitignore` 补强：`/SlopeXData/`、`/runtime/`、`*.sxpart`（为第 3 项预埋）、`*.pdb`、`.idea/`、`*.log`、`*.tmp`、`Thumbs.db`、`.DS_Store`
   - 验证：`git status` 干净 0 行，跟踪 40 个文件，`SlopeX.exe` / `.minecraft` / `bin` / `obj` 全部被拦

---

## 8. 附录

### 8.1 实测记录

| 项目 | 数据 |
|---|---|
| 1.20.1 安装 | 701.7 MB / 3,598 资源 / 190 秒 |
| 1.20.1 启动 | 进游戏，`Setting user: Steve`，OpenAL 初始化，退出码 143（主动结束） |
| 26.3 启动 | Java 25、LWJGL 3.4.3、SDL 建窗、RTX 3060 OpenGL 3.3 |
| Java 25 自动安装 | 清华 TUNA，55.8 MB，3.9 秒，解压到 `runtime\java-25` |
| 版本隔离继承 | 存档「新的世界」16 MB + `options.txt`(137 行) 复制到 `versions\1.20.1` |
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
build.cmd                             # 构建单文件到仓库根目录
```

### 8.3 关键路径

| 用途 | 位置 |
|---|---|
| 游戏根目录 | `<启动器目录>\.minecraft` |
| 版本隔离目录 | `.minecraft\versions\<版本 ID>` |
| 配置与日志 | `%APPDATA%\SlopeX\` |
| 托管 Java 运行时 | `%APPDATA%\SlopeX\runtime\java-<主版本>` |

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
