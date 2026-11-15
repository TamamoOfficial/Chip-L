# Chip-L 桌面版

**把三款安卓芯片库应用（CPU-L / GPU-L / SoC-L）合成为一个 Windows 桌面程序**，并完整保留原作的「服务端加密数据库热更新」能力。

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows&logoColor=white)
![UI](https://img.shields.io/badge/WPF-desktop-0078D6)
![License](https://img.shields.io/badge/License-MIT-2F855A)
![Data](https://img.shields.io/badge/%E5%86%85%E7%BD%AE%E5%BF%AB%E7%85%A7-CPU%205%2C991%20%2F%20GPU%202%2C205%20%2F%20SoC%201%2C034-2F855A)

---

## 目录

- [这是什么](#这是什么)
- [下载与运行](#下载与运行)
- [功能](#功能)
- [界面结构](#界面结构)
- [数据库热更新原理](#数据库热更新原理)
- [从源码构建](#从源码构建)
- [命令行自检](#命令行自检)
- [本地调试接口（Agent API）](#本地调试接口agent-api)
- [数据完整性](#数据完整性)
- [项目结构](#项目结构)
- [数据来源与版权](#数据来源与版权)

---

## 这是什么

Android 端的 iJR Software 把芯片资料拆成了三个独立 App：

| 应用 | 版本 | 内容 |
| --- | --- | --- |
| CPU-L | 2.9.3 | 处理器规格（AMD / Intel） |
| GPU-L | 2.8.5 | 显卡规格（AMD / Intel / NVIDIA） |
| SoC-L | 2.8.4 | 移动与嵌入式芯片（21 家厂商） |

装机时得来回切换三个 App，桌面端又没有替代品。Chip-L 把三库**合并进一个窗口**，一次搜索同时覆盖处理器、显卡、SoC，
保留原作的层级导航（品牌 → 类别 → 产品线 → 系列）与全部规格字段，并额外补上了桌面端才做得到的功能：
**跨模块收藏、多款并排对比、数据库热更新、命令行与本地 Agent 接口**。

程序为单文件绿色版（.NET 8 self-contained），**无需安装运行时，也不需要管理员权限**。

---

## 下载与运行

到 [Releases](../../releases) 下载 `ChipL.exe`，双击即可。首次启动约 1 秒载入内置数据库。

- 缓存与配置写在 `%LOCALAPPDATA%\ChipL\`（收藏、热更新缓存、调试接口令牌）。
- 程序只监听回环地址，不联网上传任何数据；唯一的出站请求是检查数据库更新。
- 删除 `%LOCALAPPDATA%\ChipL\db\` 下的文件即可回到 exe 内置的快照。

---

## 功能

### 三库合一 + 五层定位

左侧切换 **CPU 处理器 / GPU 显卡 / SoC 芯片**，顶部分级导航逐级下钻：

- **CPU**：AMD / Intel → 桌面 / 移动 / 工作站 / 移动工作站 / 服务器 / 嵌入式 / 游戏机处理器
- **GPU**：AMD / Intel / NVIDIA → 桌面 / 移动 / 工作站 / 移动工作站 / 服务器 / 超算 / 嵌入式 / 游戏机 / 矿卡系列
- **SoC**：21 家厂商（字母序）→ 系列分组

面包屑实时显示当前路径，右上「✕ 清除筛选」一键回到全库。

### 搜索

支持型号、系列、代号、料号、S-Spec 等多个字段；空格分隔可组合多关键词。**搜索与导航可以叠加**。

### 完整规格

点选任意芯片，右侧按分组展示：综合 / 核心 / 缓存 / 内存 / 图形 / 基准测试……数值与日期（`202602` → 2026 年 2 月）已做人类可读格式化。

### 对比（⚖）

列表每行右侧点 **⚖** 加入对比清单，顶栏「⇄ 对比 (N)」凑够 2 款后打开对比窗。
左侧只列已选项目（可逐个 ✕ 移除），右侧按项数**等分列宽**、长内容自动换行，**差异项橙色高亮**。CPU / GPU / SoC 三份清单彼此独立。

### 收藏（☆）

每行右侧 **☆** 收进收藏夹（★ 表示已收藏），三个模块共用一个收藏表，持久化到 `%LOCALAPPDATA%\ChipL\db\favorites.json`。

### 随机（🎲）

不知道该看哪颗？点「随机」在当前类别里抽一款，并自动定位到它在导航树里的位置。

### 排序

按名称 / 按发布时间（新→旧）/ 按发布时间（旧→新）。

### 未确认型号

官方把传闻产品的 `id_serie` 标为 `-1`、名称带 `*`。桌面版把它们归入**「未确认型号」**类别（排在最后），
列表项带橙色「未确认」角标——**既不混进正式系列，也不被丢弃**。

---

## 界面结构

```
┌───────────────────────────────────────────────────────────────┐
│  CPU 处理器  GPU 显卡  SoC 芯片        [检查数据库更新] 数据库状态 │
├──────────┬────────────────────────────────────────────────────┤
│          │  品牌 → 类别 → 产品线 → 系列   (面包屑 / 清除筛选)   │
│  收藏    ├────────────────────────────────────────────────────┤
│  ★ 列表  │  搜索框                    [排序] [🎲] [⇄ 对比 (N)] │
│          ├──────────────────────┬─────────────────────────────┤
│          │  芯片列表             │  规格详情                    │
│          │  · Ryzen 7 9800X3D ☆⚖ │  综合 / 核心 / 缓存 / …      │
│          │  · Core Ultra 9 285K   │                             │
└──────────┴──────────────────────┴─────────────────────────────┘
```

品牌/类别/产品线/系列四层均使用 `WrapPanel` **自动换行**：SoC 的 21 家厂商在窄窗口下会折成两三行，
不会被窗口边缘裁掉（早期误用 `StackPanel` 导致"看起来只有十几家"，已修正）。

---

## 数据库热更新原理

安卓版的数据库不是明文，而是「服务端下发一份加密 ZIP」。完整还原后的链路：

```
GET https://www.ijrsoftware.com/json/db_cpus.json
    → {release, version, size, url}
GET {url}                                # 通常是 https://www.ijrsoftware.com/db/cpus
    → AES-256-CFB8 密文
AES-256-CFB8 解密                          # key = lower(MD5("com.ijsoft.cpul")) 的 32 字节 ASCII
    → ZIP（内含单个 JSON：cpus_db）
解析 JSON → 10 张表 → 写入 %LOCALAPPDATA%\ChipL\db\cpus.json.gz
```

三个模块的差异只在包名与 JSON 文件名：

| 模块 | 包名 | key = MD5(包名) | JSON 文件名 |
| --- | --- | --- | --- |
| CPU | `com.ijsoft.cpul` | `4819cb0ccc29370ab10c29c5ed289b90` | `cpus_db` |
| GPU | `com.ijsoft.gpul` | `28276d00282bb90ec1c965f9b87bc7c6` | `gpus_db` |
| SoC | `com.ijsoft.socl` | `c82d94dd1742e740a0d3c28b2c1acc28` | `socs_db` |

IV 的构造方式与安卓版（`a6.h.g`）一致：取 key 的十六进制串**从末位往前**每两个字符，组成 16 字节 IV。
实现见 [`ChipL/Core/DbCrypto.cs`](ChipL/Core/DbCrypto.cs)。

### 三道安全闸门

热更新不是「下载完直接覆盖」，中间有三道闸门（见 [`ChipL/Core/DatabaseStore.cs`](ChipL/Core/DatabaseStore.cs)）：

1. **解密校验** —— 解密结果必须是 ZIP（`PK\x03\x04`）。
2. **解析校验** —— JSON 必须能解析成预期结构，且记录数 ≥ 10。
3. **回读校验 + 原子替换** —— 先写 `.tmp`，重新解压比对长度一致后，再用 `File.Replace` 原子换入。

任一步失败都只提示错误，**旧数据库保持可用**。
实测线上 GPU/SoC 包确实是被截断的（ZIP 中央目录缺失），程序正确拒绝了这两个包并保留原数据——这正是该机制的价值所在。

### 缓存目录

```
%LOCALAPPDATA%\ChipL\db\
├── cpus.json.gz / gpus.json.gz / socs.json.gz   当前使用的数据库
└── *.meta.json                                  版本号、release、更新时间
```

### 更新说明弹窗

热更新成功后（手动或启动时静默检查）弹出「数据库已更新」窗口：

- 顶部统计条：各库 **旧条数 → 新条数**、新增型号数、新增系列数、移除条目数
- 按 `品牌 · 类别 · 系列` 分组列出新增型号，每条附 **上代产品** 与发布时间
- 每行右侧「查看 ›」直接跳转到该型号

差异算法见 [`ChipL/Core/DbDiffBuilder.cs`](ChipL/Core/DbDiffBuilder.cs)：以 `id` 为键比对更新前后的快照，
上代产品取同系列中发布时间早于新品、且最接近的一条。

---

## 从源码构建

需要 **.NET 8 SDK**：

```bash
git clone https://github.com/TamamoOfficial/Chip-L.git
cd Chip-L

dotnet publish ChipL -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o dist
```

产物为 `dist/ChipL.exe`（约 65 MB，内含三库 gzip 快照）。

数据库以 `EmbeddedResource` 方式编进程序，源文件是 [`db/gz/`](db/gz) 下的三个 `.gz`。
替换该目录下的文件后重新构建，即可换掉内置快照：

```bash
# db/CPU/cpus_db 等为从 APK 解出的原始 JSON（无扩展名）
gzip -c db/CPU/cpus_db > db/gz/cpus_db.gz
```

> **注意**：`db/CPU/cpus_db` 是 APK 内的旧库（5,771 条），
> 而仓库里的 `db/gz/cpus_db.gz` 是线上 v14（5,991 条）。
> 直接执行上面的命令会用旧库覆盖新快照——如需保持新型号，请先把线上包
> 解出来的 `cpus_db` 放到该路径，或用 `ChipL.exe --selftest --update` 更新后
> 从 `%LOCALAPPDATA%\ChipL\db\cpus.json.gz` 取出。

---

## 命令行自检

```bash
ChipL.exe --selftest                                   # 校验内置库 + 远程版本接口
ChipL.exe --selftest --update                          # 连带执行一次真实热更新
ChipL.exe --selftest --clear                           # 清除本地热更新缓存
ChipL.exe --selftest --server http://127.0.0.1:8099    # 指向镜像站测试
```

退出码 `0` 表示全部通过；非零时会打印具体失败项。

---

## 本地调试接口（Agent API）

程序启动后在 `127.0.0.1` 上开一个只读优先的调试端口，并把访问令牌写入 `%LOCALAPPDATA%\ChipL\agent.json`：

```json
{ "url": "http://127.0.0.1:45506/", "token": "…", "pid": 12345 }
```

设计要点：

1. **只监听回环地址** —— 外网无法访问。
2. **强制 token 鉴权** —— 所有请求都要带 `&token=…`，否则 `401`。
3. **端口随机** —— 避免冲突，以 `agent.json` 为准，不要写死。
4. **UI 同步可见** —— 每次调用都会在窗口顶部弹出绿色横幅「◉ 调试接口　详情：Intel Xeon 6960E+」，
   并真实切换模块/筛选/选中项。用户能实时看到 agent 在查什么，而不是背地里翻数据。

### 指令一览

| 指令 | 说明 |
| --- | --- |
| `GET /state` | 当前模块、品牌/类别/产品线/系列、搜索词、筛选条数、选中项 |
| `GET /modules` | 三个库的条数与品牌列表 |
| `GET /nav?module=gpu&brand=NVIDIA&category=dk&product=GeForce&serie=…` | 逐级下钻（参数都可省略） |
| `GET /search?q=9966` | 搜索 |
| `GET /select?id=3922` 或 `?name=EPYC 9966` | 选中（找不到会自动回到全库再试） |
| `GET /detail?id=3922` | 选中并返回完整规格 |
| `GET /compare?a=3922&b=9966` | 返回逐项差异，同时弹出对比窗 |
| `GET /basket?action=list\|add\|remove\|clear\|open` | 对比清单操作 |
| `GET /favorite?action=list\|toggle\|clear` | 收藏操作 |
| `GET /filter?search=xxx` | 设置搜索词并返回筛选状态 |
| `GET /sort?by=name\|newest\|oldest` | 切换排序 |
| `GET /module?module=gpu` | 切换芯片类别 |
| `GET /pick[?module=gpu]` | 随机抽一款并定位导航 |
| `GET /tree?brand=HiSilicon` | 导出某品牌的 产品线 → 系列 → 条数 结构 |
| `GET /update[&interactive=1]` | 触发热更新检查 |
| `GET /clearcache[?module=cpu]` | 清空热更新缓存回内置库 |
| `GET /status` | 当前模块/清单/收藏/数据库来源 |
| `GET /selftest` | 运行内置自检 |
| `GET /history` | 本次运行的操作记录 |
| `GET /help` | 指令清单 |

返回值统一为：

```json
{ "ok": true, "ui": "给用户看的一句话", "data": { }, "error": null }
```

### 典型用法

```bash
TOKEN=$(jq -r .token "$LOCALAPPDATA/ChipL/agent.json")
URL=$(jq -r .url   "$LOCALAPPDATA/ChipL/agent.json")

# 一条命令定位到 Xeon 6+ 全系列（原来要点十几下）
curl -s "${URL}nav?module=cpu&brand=Intel&category=服务器处理器&product=Xeon&serie=Xeon%206%2B&token=$TOKEN"

# 取某一颗的完整规格
curl -s "${URL}detail?id=3922&token=$TOKEN"
```

这样检查功能不再需要「截图 → 找坐标 → 点击 → 再截图」，一条命令拿到结构化结果，且界面同步反映，
**人机看到的是同一份事实**。

---

## 数据完整性

按 `id` 逐条比对，原始记录与程序解析结果**数量完全一致，无任何型号丢失**：

```
CPU  2,057 + 3,934 = 5,991  ✓
GPU  1,064 + 173 + 968 = 2,205  ✓
SoC  1,034  ✓
```

详情表存在「一个型号多条详情」的情况（CPU 278 组、GPU 394 组、SoC 56 组），
这些是同一型号的不同变体（如 K6-2 300 的 66MHz / 100MHz 总线版本）。
桌面版取发布定义中的其中一条作为主数据——**变体细节有取舍，型号本身不缺**。

### 内置快照为什么不是 APK 里那份

**CPU 内置快照取的是服务端线上 v14（5,991 条），而不是 APK 打包时的旧库（5,771 条）。**

APK 打包时数据库落后于线上，缺 23 个系列，其中包括：

- Intel `Xeon 6+ Series`（6960E+ / 6970E+ / 6980E+ / 6990E+，2026-02）
- AMD `EPYC 9006 Series`（9016 / 9116 / 9176F … 9996，共 31 款，2026-03）

若只搬 APK 数据，用户打开就会找不到这些新型号，因此内置快照统一取线上最新包。
GPU / SoC 的线上包目前是**被截断的**，所以这两库仍用 APK 内置版；等官方修复后热更新会自动跟进。

---

## 项目结构

```
Chip-L/
├── ChipL/                        WPF 源码（.NET 8）
│   ├── Core/
│   │   ├── ChipDatabase.cs       十张表 → 内存模型、导航树、搜索
│   │   ├── DatabaseStore.cs      热更新：下载 / 解密 / 校验 / 原子落盘
│   │   ├── DbCrypto.cs           AES-256-CFB8 + MD5 key + 逆序 IV
│   │   ├── DbDiffBuilder.cs      新旧快照差异与「上代产品」
│   │   ├── SpecSchema.cs         各模块规格字段与分组
│   │   └── ValueFormat.cs        数值 / 大小 / 日期格式化
│   ├── Agent/
│   │   ├── AgentServer.cs        裸 TcpListener 的极简 HTTP/1.1
│   │   └── MainWindow.Agent.cs   指令实现 + 绿色横幅反馈
│   ├── MainWindow.xaml(.cs)      主窗口
│   ├── MainWindow.Compare.cs     对比逻辑
│   ├── CompareWindow.xaml(.cs)   对比窗口
│   ├── NavigationBar.xaml(.cs)   四层导航
│   ├── UpdateSummaryWindow.*     更新说明弹窗
│   ├── SelfTest.cs               命令行自检
│   ├── Theme.xaml                深色主题
│   └── ChipL.csproj
├── db/
│   ├── CPU/cpus_db               从 APK 解出的原始 JSON（无扩展名）
│   ├── GPU/gpus_db
│   ├── SoC/socs_db
│   └── gz/                       内嵌进 exe 的 gzip 快照（构建时使用）
├── LICENSE                       MIT
└── README.md
```

`ChipL.exe` 不打进仓库，请从 [Releases](../../releases) 下载。

---

## 数据来源与版权

芯片规格数据、缩略图均来自 iJR Software 的公开接口（[www.ijrsoftware.com](https://www.ijrsoftware.com)），
**版权归原作者所有**。本项目只是桌面端浏览与同步工具，未修改、未重新分发数据库的商业用途，
请在使用时遵守原站条款。若权利方有异议，请联系后立即处理。

源代码以 MIT 协议开源，详见 [LICENSE](LICENSE)。
