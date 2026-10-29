# Mojang AppID 白名单申请表 — 填写答案

> 表单地址：https://aka.ms/mce-reviewappid
> 申请对象：SlopeX Launcher
> Client ID：`5fd00f1e-021f-494e-90ad-7a3c3a58989d`
> Tenant ID：`67dfcc35-bf92-4eee-bf90-04d7709c43a0`
> 整理时间：2026-10-29 20:11 ｜ 最近更新：2026-10-29 20:14

---

## 填写前先取得两个值

**Tenant ID（第 6 项必填）**

Azure Portal → 应用注册 → SlopeX Launcher → **概述** → 「**目录(租户) ID**」。

> **本项目实际值（已从门户抄录）**：`67dfcc35-bf92-4eee-bf90-04d7709c43a0`
>
> 概述页会出现三个 GUID，别拿错：
> | 顺序 | 字段 | 值 | 用途 |
> |---|---|---|---|
> | 1 | 应用程序(客户端) ID | `5fd00f1e-021f-494e-90ad-7a3c3a58989d` | **表单第 5 项** |
> | 2 | 对象 ID | `f67fb832-ef8f-450a-ac93-549ed6e60593` | 不填（应用在本目录内的内部标识） |
> | 3 | 目录(租户) ID | `67dfcc35-bf92-4eee-bf90-04d7709c43a0` | **表单第 6 项** |

**联系邮箱（第 2 项）**

必须填**拥有这个 Azure 应用的那个微软账号的邮箱**。表单开头明确写了会用它与
Azure Portal 里的联系信息交叉核对；换成别的邮箱大概率核不上。

---

## 逐项答案

### 1. I verify that I have read and understood the EULA and all Usage Guidelines

选 **Yes**。

填写前请真的读一遍 http://aka.ms/mcusageguidelines ——里面禁止的行为包括
分发游戏本体、绕过正版验证、售卖账号等。本项目不做这些。

---

### 2. Contact Information (Valid email address)

```
<拥有 Azure 应用的那个微软账号邮箱>
```

---

### 3. What type of request is this?

选 **New AppID for Approval**。

---

### 4. Application Name

```
SlopeX Launcher
```

---

### 5. Application ID

```
5fd00f1e-021f-494e-90ad-7a3c3a58989d
```

---

### 6. Tenant ID

```
67dfcc35-bf92-4eee-bf90-04d7709c43a0
```

---

### 7. Associated website or domain

✅ **已采用 A 方案，直接填**：

```
https://github.com/TwistzzzzSudo/SlopeX-Launcher
```

仓库状态（2026-10-29 20:38 核实）：

| 项 | 值 |
|---|---|
| 可见性 | Public（匿名抓取可读，已用无痕方式验证） |
| 默认分支 | `main` |
| 跟踪文件 | 57 个，约 1.05 MB（产物与 `.minecraft` 均已被 `.gitignore` 排除） |
| 许可 | MIT（`LICENSE`） |
| README | 顶部有英文摘要，说明定位、设备码流程、API 使用范围与"不做什么" |

> 提审前建议再自查一次：在**未登录 GitHub 的无痕窗口**打开该地址，
> 能看到代码与 README 即说明确实是 Public。

**以下为当初的备选方案，现不再需要：**

**B. 用你的个人主页 / 博客里介绍这个项目的一页**

```
https://<你的域名>/slopex
```

**C. 都没有时，如实填写（可能被要求补充材料）**

```
No public website yet. Local/open-source personal project on Windows;
source code available on request.
```

> **不要填与项目无关的网址**（网盘、社交主页、随便一个域名）。
> 表单原文是「where we can find more information about **your application**」，
> 填了不相干的东西会被判定为无效提交，反而拖慢审核。

---

### 8. Justification

直接复制下面这段（英文，约 120 词）：

```
SlopeX Launcher is a non-commercial, open-source third-party launcher for
Minecraft: Java Edition on Windows, written in C#/.NET (WPF).

It authenticates users exclusively through the standard Microsoft OAuth 2.0
device code flow: the user signs in on Microsoft's own page, and the launcher
never sees the password. The resulting Microsoft token is exchanged via Xbox
Live and XSTS, then submitted to
https://api.minecraftservices.com/authentication/login_with_xbox to obtain a
Minecraft access token. That token is used for two purposes only: (1) launching
a game copy the user already owns, and (2) proving ownership when joining
online servers that require it. We also read /minecraft/profile to display the
correct player name, UUID and skin.

We request access solely so that legitimate owners of the game can sign in with
their own account — the same functionality already provided by other approved
launchers. We do not sell or redistribute the game, do not operate any
authentication bypass, and do not offer "offline/cracked" access through this
integration.
```

如果还想强调范围最小化，可在末尾追加一句：

```
Requested scopes are limited to XboxLive.signin and offline_access.
```

---

### 9. Any other information that we should be aware of?

> **实际提交版（用户改写，2026-10-29 20:46）** —— 与下方原稿内容一致，措辞更口语。
> 提交时采纳了「redirect URI」一句的修正（Azure 里配过 `nativeclient`，
> 原文写 "no redirect URI" 与配置对不上，改为 "no redirect handling"）。

```
A few technical details that might help with the review:

The Azure app is registered as a public client, so there's no client secret.
The launcher binary only contains the client ID — there's nothing sensitive
that could leak.

Sign-in uses the device code flow. No redirect handling or embedded browser is
involved; the user finishes signing in in their own browser.

Access and refresh tokens stay on the user's machine, in a single JSON file
under the launcher's data directory. We operate no backend, so tokens are never
transmitted to us.

We don't collect telemetry or analytics.

Game files come from Mojang's official endpoints (piston-meta.mojang.com /
piston-data.mojang.com). The official CDN is often unreachable from mainland
China, so we also offer BMCLAPI as an optional mirror — it's a public mirror
already used by several Chinese launchers. Authentication itself always goes
straight to Microsoft and Mojang, never through a third party.

Requested scopes are limited to XboxLive.signin and offline_access; no other
Microsoft Graph or Xbox permissions are used.

The launcher doesn't modify, patch, or redistribute Minecraft assets.
```

<details>
<summary>原始草稿（留档对照）</summary>

```
Technical details that may help the review:

- The Azure application is registered as a public client (no client secret).
  Only the client ID is embedded in the launcher binary; there are no
  credentials to leak.
- Authentication uses the device code flow, so no redirect URI or embedded
  browser is involved. The user completes sign-in in their own browser.
- Access tokens and refresh tokens are stored locally on the user's machine
  only (a single JSON file in the launcher's data directory). Nothing is sent
  to any server operated by us; we operate no backend at all.
- No telemetry or analytics of any kind.
- Game files are downloaded from Mojang's official endpoints
  (piston-meta.mojang.com / piston-data.mojang.com). A public mirror
  (BMCLAPI, used by several existing Chinese launchers) is offered as an
  optional download source because the official CDN is frequently unreachable
  from mainland China. Authentication always goes directly to Microsoft and
  Mojang — never through a third party.
- The launcher does not modify, patch or redistribute Minecraft assets.
```

</details>

---

## 提交前自查

- [ ] 第 1 项选了 Yes，且确实读过 Usage Guidelines
- [ ] 第 2 项邮箱 = Azure 应用所属账号的邮箱
- [ ] 第 3 项 = New AppID for Approval
- [ ] 第 5 项是 **Application (Client) ID**，不是租户 ID（两者都是 GUID，最容易填反）
- [ ] 第 6 项是 **Directory (Tenant) ID**
- [ ] 第 7 项不是无关网址
- [ ] 第 8 项没有留空、没有写 "test" 之类

---

## 提交之后

审核周期以天到周计，且不保证通过。期间不需要改任何代码——
`client_id` 已在 `Modules/ModAccount/Msa.cs` 里固定，审批通过后直接重试登录即可。

若被拒或要求补充材料，把邮件原文贴过来，按它的要求补。
