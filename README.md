# Chip-L 桌面版

把三款安卓芯片资料应用 **CPU-L / GPU-L / SoC-L**（iJR Software）合成为一个 Windows 程序，并保留原作的在线数据库热更新。

单文件绿色版，双击即用，不需要安装 .NET 运行时。

## 功能

- **三库合一** —— 左侧切换 CPU / GPU / SoC，顶部分级导航「品牌 → 类别 → 产品线 → 系列」逐级下钻，面包屑实时显示路径
- **搜索** —— 型号 / 代号 / 料号 / S-Spec 多字段，可与导航筛选叠加
- **对比** —— 列表行内 ⚖ 加入清单，2 款以上并排显示，差异项橙色高亮（三库清单独立）
- **收藏** —— ★ 收进收藏夹，跨模块共用，持久化到本地
- **随机** —— 当前类别里抽一款并自动定位到导航树
- **排序** —— 按名称 / 发布时间新旧
- **数据库热更新** —— 完整还原官方的加密包下发（AES-256-CFB8），三道校验后才原子替换，失败保留旧库
- **未确认型号** —— `id_serie = -1` 的传闻产品单独归类，不混进正式系列

## 内置数据

| 模块 | 条数 | 来源 |
| --- | --- | --- |
| CPU | 5,991 | 线上 v14（比 APK 内置版多 23 个系列） |
| GPU | 2,205 | APK 2.8.5 内置 |
| SoC | 1,034 | APK 2.8.4 内置 |

缓存与配置在 `%LOCALAPPDATA%\ChipL\`；删除其中的 `db\` 即可回到 exe 内置快照。

## 使用

到 [Releases](../../releases) 下载 `ChipL.exe`，双击运行。

## 构建

需要 .NET 8 SDK：

```bash
dotnet publish ChipL -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

数据库以 `EmbeddedResource` 编进程序，源文件为 [`db/gz/`](db/gz) 下的 gzip 快照（构建时使用）。

## Agent 调试接口

程序启动后在 `127.0.0.1` 上开一个随机端口的调试接口（仅回环 + token 鉴权），
地址与令牌写入 `%LOCALAPPDATA%\ChipL\agent.json`。所有调用都会在窗口顶部显示绿色提示条，
并在界面上真实切换模块 / 筛选 / 选中项。

完整指令、参数与返回字段见 **[docs/agent-api.md](docs/agent-api.md)**。

```bash
TOKEN=$(jq -r .token "$LOCALAPPDATA/ChipL/agent.json")
URL=$(jq -r .url   "$LOCALAPPDATA/ChipL/agent.json")
curl -s "${URL}detail?id=3922&token=$TOKEN"
```

## 数据来源与版权

芯片规格数据、缩略图来自 iJR Software 的公开接口（[www.ijrsoftware.com](https://www.ijrsoftware.com)），**版权归原作者所有**。
本项目仅为桌面端浏览与同步工具，请遵守原站条款。

程序源码以 [MIT](LICENSE) 协议开源。
