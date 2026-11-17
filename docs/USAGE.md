# 参数与输出

`wvresp 1.0`。命令行风格对齐 `tshark` / `pktmon`，参数名尽量复用它们的心智，方便直接上手。

```text
wvresp --url <URL> [--url <URL> ...] [options]
```

## 采集

| 参数 | 对应 tshark / pktmon | 说明 |
| --- | --- | --- |
| `-u, --url <URL>` | tshark 输入是 `-r <pcap>` | 本工具的输入是 URL。可重复，按出现顺序导航，每次导航之间停 800 ms |
| `-w, --out <dir>` | `-w <file>` / pktmon `--file <etl>` | 输出目录，默认 `.\wvresp-YYYYmmdd-HHmmss` |
| `-a duration:<sec>` | `-a duration:N` | 到时自动停。也接受 `time:` 前缀、数字后带 `s` |
| `-c <n>` | `-c <n>` | 收够 N 个响应就停。**N 是过滤前的响应计数**（与 tshark 的 `-c` 同义）；用 `-c` 做「收够 N 个 body」会提前停 |
| `--stop-kept <n>` | 无 | 收够 N 个**真正落盘**的 body 才停（tshark 没有这个，但本工具需要） |
| `-i, --iface <webview2\|any>` | `-i <iface>` / pktmon `list` | 采集面。只接受 `webview2` / `any`，其他值直接报错并退出 2 |
| `-o http.user_agent=<s>` | `-o <pref>=<val>` | 覆盖 User-Agent |
| `-o wvresp.user_data=<dir>` | 无 | WebView2 用户数据目录。指到已有目录即可复用 cookie / 登录态 |
| `-o wvresp.max_save=<bytes>` | 无 | 单个 body 落盘上限，默认 8 MiB |
| `--hidden` | 无 | 窗口不可见运行（`ShowInTaskbar=false`、最小化、不占焦点），适合脚本 |

默认 user data 目录：`%LOCALAPPDATA%\wvresp\webdata`。不指定时不会污染浏览器配置。

## 过滤

过滤分两段：`-f` / `--type` 在**取 body 之前**判断（省 IO），`-Y` 也在前置判断里执行，配合 `-V` 可看到被丢弃的行。

| 参数 | 语义 |
| --- | --- |
| `-f, --host <glob[,glob]>` | host 过滤，逗号分隔、可重复。**无通配符 → 子串匹配**（忽略大小写）；含 `*` / `?` → glob 全匹配 |
| `--type <substr[,substr]>` | Content-Type 子串过滤（匹配整行 `Content-Type`，所以 `json` 能命中 `application/json; charset=utf-8`） |
| `-Y <expr>` | 显示期过滤。空格分隔的多个 token 取 **AND** |
| `--min-bytes <n>` | body ≥ n 才落盘（不够记 `reason=size`） |
| `--max-bytes <n>` | body ≤ n 才落盘 |

### `-Y` 语法

每个 token 形如 `key OP value`，键名区分大小写无关：

| key | 取值的字段 |
| --- | --- |
| `host` | 请求 host |
| `url` | 完整 URL |
| `ctype` / `content_type` | `Content-Type` 整行 |
| `status` | 响应状态码 |
| `method` | 不支持（用 `-e method` 输出，不做过滤） |

支持的运算符：`>=` `<=` `>` `<` `==` `!=` `=` `:`（后两者等价于 `==`）。
比较值的匹配规则同 `-f`：**无通配符 → 子串，有 `*` / `?` → glob**。

不带运算符的裸 token，直接在 URL 上做子串匹配。

示例：

```powershell
wvresp -u https://www.doubao.com/chat/ -Y "ctype=json status>=200"
wvresp -u https://www.doubao.com/chat/ -Y "host=*.doubao.com" -Y "api/chat"
```

## 输出

| 参数 | 说明 |
| --- | --- |
| `-T table` | 默认。每个**落盘**的响应打印一行摘要：`i status method bytesB md5 url(≤180字)` |
| `-T json` | 每个响应打印一个 JSON 对象（含未落盘的，`kept` / `reason` 标明） |
| `-T jsonl` | 同 `-T json`，一行一个对象（后续处理按行读） |
| `-T fields` | 结束时对**已落盘**的响应输出制表符分隔的字段；字段由 `-e` 指定 |
| `-e <f[,f]>` | 指定字段；用了 `-e` 会自动把默认的 `table` 切成 `fields` |
| `--manifest <name>` | `<out>` 下的清单文件名，默认 `capture.jsonl` |
| `--export-objects http,<dir>` | 每个 body 都落盘到 `<dir>`（相对路径按 `<out>` 解析），忽略 `wvresp.max_save` |
| `-z` | 结束时打印 host（按次数倒序前 25，带已存字节数）与 content-type（前 15）统计块 |
| `-q` | 静默：只在 `wvresp.log` 写，不往控制台打 |
| `-V` | 逐条打印每个响应（含被过滤掉的） |

### `-e` 可用字段

| 短名 | 兼容长名 | 值 |
| --- | --- | --- |
| `i` | `no` / `frame.number` | 响应序号（从 1 开始，过滤前计数） |
| `t` | `time` | 距启动的毫秒数 |
| `status` | `http.response.code` | 状态码 |
| `method` | `http.request.method` | 请求方法 |
| `url` | `http.request.full_uri` | 完整 URL |
| `host` | `http.host` | host |
| `ctype` | `content_type` | `Content-Type` 整行 |
| `bytes` | `length` | body 字节数（未取到 body 时为 0） |
| `md5` | — | body 的 MD5（小写） |
| `file` | — | 落盘文件相对 `<out>` 的路径 |
| `kept` | — | `1` / `0` |
| `reason` | — | 未落盘的原因 |

未知字段输出 `?`。

## 输出目录

```
<out>/
  bodies/            0001_<host>_<basename>.<ext>
  capture.jsonl      每响应一行 JSON
  wvresp.log          nav ok=/err=、BODY-ERR、hosts 与 content-types 统计
```

`bodies/` 文件名 = `序号:D4` + `_` + `host_路径基名`（非法字符替换为 `_`、截断 60 字）+ 按 Content-Type 推的扩展名（json / html / css / js / svg / png / jpg / gif / webp / pdf / txt / bin / dat）。

`capture.jsonl` 一行：

```json
{"i":37,"t":9123,"status":200,"method":"GET","url":"https://mcs.doubao.com/list","host":"mcs.doubao.com","ctype":"application/json; charset=utf-8","bytes":21,"md5":"...","file":"bodies\\0037_mcs.doubao.com_list.json","kept":true,"reason":null}
```

`reason` 枚举：

| reason | 含义 |
| --- | --- |
| `host` | 被 `-f` 挡掉 |
| `type` | 被 `--type` 挡掉 |
| `filter` | 被 `-Y` 挡掉 |
| `status` | 被 `-Y status…` 挡掉 |
| `size` | 被 `--min-bytes` / `--max-bytes` 挡掉 |
| `toobig` | 超过 `wvresp.max_save` |
| `bodyerr` | `GetContentAsync()` 抛异常（典型是 302 重定向：`管道正在被关闭 (0x800700E8)`） |
| `badfilter:<key>` | `-Y` 里出现了不认识的键 |

## 退出码

| 码 | 含义 |
| --- | --- |
| `0` | 正常结束（含 `-h` / `--version`） |
| `2` | 用法错误（缺 `--url`、未知选项、`-o` 未知键、`-i` 非法值） |
| `4` | 一个响应都没收到（导航失败、URL 不可达等） |

## 典型组合

```powershell
# 只看某个接口的 JSON，按字段取
wvresp --hidden -u https://host/page -Y "ctype=json url=/api/" -e i,status,bytes,md5,url

# 抓完做统计，看流量集中在哪些域
wvresp -u https://host/page -a duration:20 -z -q

# 收满 5 个 body 就停，并把原文全量导出到 obj\
wvresp --hidden -u https://host/page --stop-kept 5 --export-objects http,obj

# 复用已登录的会话目录
wvresp --hidden -u https://host/inbox -o wvresp.user_data=D:\kp\cap-profile -Y "ctype=json"
```
