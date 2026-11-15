# Chip-L Agent 调试接口

给自动化 agent 用的说明书。**不需要截图或点坐标**，一条 HTTP 请求就能读取结构化的芯片数据，
并且界面会同步切到对应位置——人机看到的是同一份事实。

---

## 1. 连接

程序启动后会在 `127.0.0.1` 上开一个**随机端口**，并把地址和令牌写进：

```
%LOCALAPPDATA%\ChipL\agent.json
```

```json
{
  "url": "http://127.0.0.1:45506/",
  "token": "3f8a…",
  "pid": 12345,
  "startedAt": "2026-11-16T07:12:14",
  "hint": "请求需带 &token=…；进程异常退出时本文件可能残留，以 pid 是否存活为准"
}
```

**每次启动端口和令牌都会变**，不要写死，始终以 `agent.json` 为准。
进程被强杀时该文件会残留 —— 先确认 `pid` 是否存活，再决定是否重新启动程序。

```bash
TOKEN=$(jq -r .token "$LOCALAPPDATA/ChipL/agent.json")
URL=$(jq -r .url   "$LOCALAPPDATA/ChipL/agent.json")
```

### 鉴权

所有请求都要带 `&token=<token>`，否则返回 `401`。

```bash
curl -s "${URL}ping?token=$TOKEN"
```

### 协议

- 极简 HTTP/1.1，`Connection: close`，**一次请求一个连接**（不要复用）。
- `GET` 用查询参数；也支持 `POST` + JSON 扁平对象（所有字段拍平成同名的字符串参数）。
- 中文参数请做 URL 编码（`Xeon 6+` → `Xeon%206%2B`）。

### 统一返回

```json
{ "ok": true, "ui": "给用户看的一句话", "data": { }, "error": null }
```

| 字段 | 说明 |
| --- | --- |
| `ok` | `true` 成功；`false` 时 HTTP 状态码为 **400** |
| `ui` | 人类可读的一句话，同时显示在窗口顶部的绿色横幅上 |
| `data` | 结构化结果，各指令见下文 |
| `error` | 失败原因，成功时为 `null` |

`/ping` 是最轻的连通性检查：

```json
{ "ok": true, "ui": "调试接口连通", "data": { "pid": 12345, "version": "1.0.0" } }
```

---

## 2. 核心概念

### 模块 `module`

| 值 | 含义 | 说明 |
| --- | --- | --- |
| `cpu` | 处理器 | 2 个品牌（AMD / Intel） |
| `gpu` | 显卡 | 3 个品牌（AMD / Intel / NVIDIA） |
| `soc` | 芯片 | 21 家厂商，无「类别」层 |

### 类别 `category`

类别用**两字母 key**，中文名按模块自动切换：

| key | CPU 中文名 | GPU 中文名 |
| --- | --- | --- |
| `dk` | 桌面处理器 | 桌面系列 |
| `mb` | 移动处理器 | 移动系列 |
| `dw` | 工作站处理器 | 工作站系列 |
| `mw` | 移动工作站处理器 | 移动工作站系列 |
| `sv` | 服务器处理器 | 服务器系列 |
| `sc` | 超级计算机处理器 | 超级计算机系列 |
| `ed` | 嵌入式处理器 | 嵌入式系列 |
| `cs` | 游戏机处理器 | 游戏机系列 |
| `cm` | *（CPU 无）* | 矿卡系列 |
| `__unconfirmed` | 未确认型号 | 未确认型号 |

`nav` / `search` 的 `category` 参数**接受 key 或中文名**（`dk` 与 `桌面处理器` 等价）。
返回的 `category` 字段一律是中文名。

> **层级有依赖**：`category` 只在 `brand` 命中后才生效，`product` 依赖 `category`，
> `serie` 依赖 `product`。所以下钻时要**一路带全**，单独传 `serie` 不会生效。
> 名称匹配是「先精确、再子串」，子串匹配可能命中多义项（如 `Xeon` 会匹配到多个产品线），
> 拿不准时先 `/tree` 看全貌再下钻。

> **`nav` 不会重置已有层级**：只传一部分参数时，未传的层级**保持原值**。
> 例如当前在「Intel › 服务器处理器 › Xeon › Xeon 6+ Series」，调用 `/nav?serie=别的系列`
> 会失败（新系列不在 Xeon 下）；而 `/nav?serie=Xeon 6+ Series` 在搜索词非空时可能筛出 0 条。
> 想回到全库，最简单的办法是**切走再切回**同一模块：
>
> ```bash
> curl -s "${URL}module?module=gpu&token=$TOKEN"   # 切走
> curl -s "${URL}module?module=cpu&token=$TOKEN"   # 切回 → 导航与搜索全部清空
> ```
>
> 注意 `search` 的搜索词同样与导航**叠加**，`/search?q=EPYC` 在导航被限定在某个系列时可能返回 0 条，
> 这不是搜索坏了，而是筛选条件叠加的结果。用 `/state` 先看清楚当前 `brand/category/product/serie`。

### 四个导航层级

```
品牌 brand  →  类别 category  →  产品线 product  →  系列 serie  →  型号
```

- 产品线与系列均按 `order_family` **降序**（新一代在前）。
- SoC 没有「类别」层，`category` 恒为 `null`。
- 未确认型号（`id_serie = -1`，名称带 `*`）单独归入 `__unconfirmed` 类别，排在最后。

### 型号 ID

`id` 是字符串（如 `"3922"`），**跨模块不保证唯一**，需要精确匹配时请带上前一步从
`/nav`、`/search` 拿到的上下文。查找支持 `id` 精确匹配，也支持 `name` 子串匹配（不区分大小写）。

---

## 3. 指令速查

所有路径都是 `GET ${URL}<path>?token=$TOKEN&…`。

### 读取

| 指令 | 关键参数 | 用途 |
| --- | --- | --- |
| `/ping` | — | 连通性检查（返回 pid、version） |
| `/state` | — | 当前模块 / 导航位置 / 搜索词 / 结果条数 / 选中项 |
| `/modules` | — | 三个库的条数与品牌列表 |
| `/status` | — | 运行时概览：pid、端口、对比清单、收藏、各库版本与来源 |
| `/tree` | `brand`（可选） | 某品牌完整结构：产品线 → 系列 → 条数 |
| `/help` | — | 指令清单 |
| `/history` | — | 本次运行的横幅记录（最多 50 条） |
### 导航与查找

| 指令 | 关键参数 | 用途 |
| --- | --- | --- |
| `/nav` | `module` `brand` `category` `product` `serie`（均可省略） | 逐级下钻 |
| `/search` | `q` | 搜索（可与已有导航筛选叠加） |
| `/select` | `id` 或 `name` | 按 id / 名称选中，自动滚动到列表 |
| `/detail` | `id` 或 `name` | 选中并返回**完整规格** |
| `/opendetail` | `id` 或 `name`，`module`（可选） | 打开详情（跨模块自动搜索） |
| `/compare` | `a` `b` | 两款逐项差异，同时弹出对比窗 |
| `/pick` | `module`（可选） | 随机抽一款并定位导航 |

### 修改状态

| 指令 | 关键参数 | 用途 |
| --- | --- | --- |
| `/module` | `module` | 切换芯片类别 |
| `/sort` | `by` = `name` \| `newest` \| `oldest` | 切换排序 |
| `/filter` | `search` | 设置搜索词并返回筛选状态 |

### 对比清单 `basket`

作用于**当前模块**的清单（三库清单彼此独立）：`action` = `list`（默认）\| `add` \| `remove` \| `clear` \| `open`

| 调用 | 说明 |
| --- | --- |
| `/basket?action=list` | 当前模块清单 |
| `/basket?action=add&id=1418` | 加入（重复加入自动忽略） |
| `/basket?action=remove&id=1418` | 移出 |
| `/basket?action=clear` | 清空当前模块 |
| `/basket?action=open` | 打开对比窗（需 ≥2 款，可跨模块） |

### 收藏 `favorite`

跨模块共用一个收藏表。

| 调用 | 说明 |
| --- | --- |
| `/favorite?action=list[&module=gpu]` | 列出收藏（可按模块过滤） |
| `/favorite?action=toggle&id=1418[&module=cpu]` | 切换收藏，返回切换后的状态 |
| `/favorite?action=clear` | 清空收藏 |
| `/favtab?module=gpu` | 切换左侧收藏面板的页签 |

> **toggle 的 `module` 参数可以指定记录所在模块**；省略时按当前模块查找。

### 维护

| 指令 | 关键参数 | 用途 |
| --- | --- | --- |
| `/update` | `interactive` = `1`（可选） | 触发热更新（异步，立即返回 `started: true`） |
| `/clearcache` | `module` = `cpu` \| `gpu` \| `soc` \| `all` | 丢弃热更新缓存，回到 exe 内置库 |
| `/selftest` | — | 三库结构自检 |

---

## 4. 主要返回结构

### `/detail` 与 `/opendetail` 的 `data`

`/detail` 直接返回记录；`/opendetail` 返回一层包装（`requested` / `moduleAfter` / `detailShown`）。

```json
{
  "id": "3922",
  "name": "EPYC 9966",
  "module": "Cpu",
  "brand": "AMD",
  "category": "服务器处理器",
  "product": "EPYC",
  "serie": "EPYC 9006 Series",
  "unconfirmed": false,
  "fields": [
    { "key": "name",     "label": "名称",     "value": "EPYC 9966",        "section": "综合" },
    { "key": "launch",   "label": "发布时间", "value": "2026 年 3 月",     "section": "综合" },
    { "key": "cores",    "label": "核心数",   "value": "96",               "section": "核心" },
    { "key": "cache_l3", "label": "L3 缓存",  "value": "384 MB",           "section": "缓存" }
  ]
}
```

- `fields` 已按界面顺序排列，`section` 用于分组展示（综合 / 核心 / 缓存 / 内存 / 图形 / 基准测试）。
- 数值与日期**已格式化**（`202602` → `2026 年 2 月`，`98304` → `96 MB`），可直接展示给用户。
- 空值字段已被剔除，不要假设某个 key 一定存在。
- `module` 值形如 `Cpu` / `Gpu` / `Soc`（注意是**首字母大写**，与入参 `cpu` 不同）。

### `/nav`

```json
{
  "module": "Gpu",
  "brand": "NVIDIA",
  "category": "桌面系列",
  "product": "GeForce",
  "serie": "GeForce RTX 50 Series",
  "matched": 24,
  "items": [ { "id": "8123", "name": "GeForce RTX 5090" } ]
}
```

`items` 只返回**前 30 条**，用于展示；要完整条数看 `matched`，要全量请收窄筛选或遍历 `/tree`。

### `/search`

```json
{ "query": "9966", "module": "Cpu", "matched": 3,
  "items": [ { "id": "3922", "name": "EPYC 9966" } ] }
```

### `/tree`

```json
[{
  "brand": "HiSilicon",
  "products": [{
    "product": "Kirin 麒麟",
    "count": 34,
    "series": [ { "serie": "Kirin 9xxx", "count": 6 } ]
  }]
}]
```

不传 `brand` 时导出当前模块**全部品牌**。

> `/tree` **只作用于当前模块**，没有 `module` 参数。查 SoC 品牌前先切模块：
> `/nav?module=soc&token=…`，否则会返回 `未找到品牌 HiSilicon`。

### `/compare`

```json
{
  "left":  { "name": "EPYC 9966", "module": "Cpu" },
  "right": { "name": "Xeon 6980P", "module": "Cpu" },
  "differences": [
    { "label": "核心数", "left": "96", "right": "128", "different": true },
    { "label": "插槽",   "left": "SP5", "right": "SP5", "different": false }
  ]
}
```

`a` / `b` 可传 id 或名称子串，**跨模块也能比**——此时字段集按**左侧 `a` 的模块**取，
右侧只显示同名 / 同义字段，可能大面积为空。实际用起来还是同模块对比更有意义。
两边都为空的行已被剔除。调用后两款会自动加入 `a` 所在模块的对比清单并弹出对比窗。

### `/status`

```json
{
  "pid": 12345,
  "agentPort": 45506,
  "currentModule": "Cpu",
  "selected": "EPYC 9966",
  "filtered": 24,
  "compareWindowOpen": false,
  "basket": { "cpu": 2, "gpu": 0, "soc": 0, "total": 2 },
  "favorites": 5,
  "databases": [
    { "module": "Cpu", "count": 5991, "version": "14", "source": "…" }
  ]
}
```

### `/selftest`

```json
{
  "passed": true,
  "problems": [],
  "report": [{
    "module": "Cpu", "records": 5991, "inTree": 5991,
    "brands": 2, "categories": 8, "products": 42, "series": 268,
    "unconfirmed": 27, "version": "14", "source": "…"
  }]
}
```

`problems` 非空时说明导航树与平铺条数不一致、或存在无名记录。

---

## 5. 典型工作流

### 定位到某个系列再读规格

```bash
# 1. 确认有哪些品牌
curl -s "${URL}modules?token=$TOKEN"

# 2. 逐级下钻（参数可省略；中文需编码）
curl -s "${URL}nav?module=cpu&brand=Intel&category=服务器处理器&product=Xeon&token=$TOKEN"

# 3. 从返回的 items 里拿 id，读完整规格
curl -s "${URL}detail?id=3922&token=$TOKEN"
```

### 挑两款对比

```bash
curl -s "${URL}basket?action=clear&token=$TOKEN"
curl -s "${URL}basket?action=add&id=3922&token=$TOKEN"
curl -s "${URL}basket?action=add&id=4012&token=$TOKEN"
curl -s "${URL}basket?action=open&token=$TOKEN"
curl -s "${URL}compare?a=3922&b=4012&token=$TOKEN"
```

### 核对数据是否需要更新

```bash
curl -s "${URL}selftest?token=$TOKEN"        # 结构是否完整
curl -s "${URL}status?token=$TOKEN"          # 当前各库版本与来源
curl -s "${URL}update&interactive=1&token=$TOKEN"   # 触发检查（异步）
curl -s "${URL}history?token=$TOKEN"         # 看它做了什么
```

---

## 6. 注意事项

1. **单线程串行** —— 所有请求都在 UI 线程执行，一次只处理一个。批量操作请顺序发，不要并发。
2. **会真实改动界面** —— `nav` / `select` / `module` / `filter` 都会改用户正在看的界面。
   这是设计目标（用户能实时看到 agent 在查什么），但用完记得把界面带回用户关心的位置。
3. **`/update` 是异步的** —— 立刻返回 `started: true`，实际结果要看 `/history` 或界面弹窗。
4. **`items` 有截断** —— `nav` / `search` 只给前 30 条，别把它当作全量。
5. **500 字以内的 `ui` 会显示在窗口上** —— 写给用户看，别塞机器字段。
6. **进程退出后 `agent.json` 会被删除**；异常退出时残留，以 `pid` 是否存活为准。
7. **接口只监听回环地址**，外部机器无法访问；如需远程，请自行做端口转发（本程序不支持）。
8. `/compare` 与 `/basket?action=open` 都会**弹出对比窗口**，自动化里记得最后用
   `/cmpwin?action=close` 收尾（`/cmpwin?action=` 不带值则查询窗口状态）。

---

## 7. 排错

| 现象 | 原因与处理 |
| --- | --- |
| `401 token 无效` | token 不对或没带；重新读 `agent.json` |
| 连接被拒绝 | 程序没运行，或 `agent.json` 是上次异常退出残留 |
| `404` / 空响应 | 路径拼错；记住是 `${URL}detail?…`，`url` 已带尾斜杠 |
| 返回 `ok: false` | 看 `error`；常见是 id 不存在、清单不足 2 款、`/tree` 品牌不属于当前模块 |
| 中文参数乱码 | 未 URL 编码，用 `--data-urlencode`（curl）或 `Uri.EscapeDataString` |
| 一直超时 | 上一条请求卡在 UI 线程；CAD 式的模态弹窗（如更新提示）会阻塞，先让用户关掉窗口 |
