# kpcap

Windows 上抓 **WebView2 的 HTTP(S) 明文响应**，命令行风格照 `tshark` / `pktmon`。

**不需要管理员**，不装驱动，不注入进程，不读凭据，不装证书，不用 `SSLKEYLOGFILE`。

## 它抓的是什么

宿主程序用 WebView2 打开网页时，TLS 在**宿主进程内**由 WebView2 自己完成。宿主可以直接拿到已经解密好的响应体：

```csharp
wv.CoreWebView2.WebResourceResponseReceived += ...
// 回调里：
e.Response.GetContentAsync()   // 已经是明文 body
```

`kpcap` 把这个窗口做成一条命令行：给 URL → 抓 → 过滤 → 落盘 → 清单 + 统计。

## 为什么不把另外几条路也做进来

两条理由：**别人已经做了**，而且**那些路都要抬权限**。

| 能力 | 现成工具 | 权限要求 | kpcap |
| --- | --- | --- | --- |
| 全机流量抓包 / pcap 解析 | Wireshark · tshark · dumpcap | 装 Npcap 驱动，通常要管理员 | 不做 |
| 内核层抓包 | pktmon（Windows 内置） | 必须管理员 | 不做 |
| 导出 TLS 密钥再解密 | tshark + `SSLKEYLOGFILE` | 要配合抓包驱动 | 不做 |
| 注入进程捞密钥 | friTap · Frida | 附加进程要权限，且会被反调试检测 | 不做 |
| 中间人证书解密 | mitmproxy · Fiddler | 装根证书（系统级要管理员） | 不做 |
| 读凭据 / 密钥材料 | mimikatz 一类 | 管理员，且常被杀软拦 | 不做 |

这些都有成熟工具、也不缺这一份，而且全都要提权；再做一个只是重复造轮子。
`kpcap` 只占那条没人占、又不用抬权的缝：**在本进程里，拿已经解密好的响应**。

## 构建

```powershell
git clone <repo-url> kpcap
cd kpcap
.\build.ps1
# 产物：src\bin\Release\netcoreapp3.1\kpcap.exe
```

依赖：.NET Core SDK 3.1（已验证 3.1.426）、NuGet 包 `Microsoft.Web.WebView2 1.0.1774.30`、本机 WebView2 运行时（Evergreen，已验证 157.0.4341.46）。

## 快速开始

```powershell
# 抓一个页面，8 秒后自动停
kpcap -u https://example.com/ -w .\cap1 -a duration:8 -f example.com

# 只留 JSON 接口，按字段逐行打印
kpcap --hidden -u https://www.doubao.com/chat/ -Y ctype=json -e i,status,ctype,bytes,md5,url

# 收够 5 个真正落盘的 body 就停，并把 body 原样全量导出
kpcap --hidden -u https://www.doubao.com/chat/ --stop-kept 5 --export-objects http,obj -q -z
```

## 参数（速查）

| 参数 | 作用 |
| --- | --- |
| `-u, --url <URL>` | 起始 URL，可重复，按顺序导航（间隔 800 ms） |
| `-w, --out <dir>` | 输出目录（默认 `.\kpcap-YYYYmmdd-HHmmss`） |
| `-a duration:<sec>` | 到时自动停 |
| `-c <n>` | 收够 N 个响应停（**过滤前计数**，同 tshark 语义） |
| `--stop-kept <n>` | 收够 N 个**真正落盘**的 body 停 |
| `-i, --iface <webview2\|any>` | 采集面，只接受 `webview2` / `any` |
| `-f, --host <glob[,glob]>` | 按 host 采集期过滤（无通配符 = 子串） |
| `--type <substr[,substr]>` | 按 Content-Type 子串过滤 |
| `-Y <expr>` | 显示期过滤，空格分隔取 AND，支持 `host= url= ctype= status>=` |
| `--min-bytes` / `--max-bytes` | body 长度门限 |
| `-o http.user_agent=<s>` | 覆盖 UA |
| `-o kp.user_data=<dir>` | 复用已有会话目录（cookie / 登录态） |
| `-o kp.max_save=<bytes>` | 单个 body 落盘上限（默认 8 MiB） |
| `-T table\|json\|fields\|jsonl` | 每响应输出格式 |
| `-e <field[,field]>` | 选择字段（`i,status,url,ctype,bytes,md5,file,...`） |
| `--manifest <name>` | 清单文件名（默认 `capture.jsonl`） |
| `--export-objects http,<dir>` | 全量导出 body（忽略 `kp.max_save`） |
| `-z` | 打印 host / content-type 统计 |
| `-q` / `-V` | 静默 / 逐条打印 |
| `--hidden` | 窗口不可见运行 |
| `-h` / `--version` | 用法 / 版本 |

完整语义、字段名与长名兼容见 [`docs/USAGE.md`](docs/USAGE.md)；设计取舍见 [`docs/DESIGN.md`](docs/DESIGN.md)。

## 输出

```
<out>/
  bodies/            0001_<host>_<basename>.<ext>   响应体，扩展名按 Content-Type 推
  capture.jsonl      每个响应一行（kept=false 也记）
  kpcap.log          nav 记录 / BODY-ERR / hosts / content-types 统计
```

`capture.jsonl` 每行字段：`i t status method url host ctype bytes md5 file kept reason`。

`reason` 取值 `host` / `type` / `filter` / `status` / `size` / `toobig` / `bodyerr` —— 排查「为什么没抓到」先看这一列。

## 要抓某个应用程序，怎么判断

四步判定链，见 [`docs/TARGET.md`](docs/TARGET.md)：查进程树有没有 `msedgewebview2` → 能不能用同源 URL 复刻 → 要不要复用登录态（`-o kp.user_data=`）→ 结论只给三种（可复刻 / 需登录态 / 不可用）。

## 已知限制

- 只覆盖**本进程 WebView2 发起**的那一侧。第三方程序的原生 TLS（Schannel / WinHTTP / 自研 socket）没有入口，这是采集面的物理边界，不是参数没开。
- 302 重定向的响应体仍报 `BODY-ERR ... 管道正在被关闭 (0x800700E8)`，未规避。
- SSE / 流式响应完整性未验证。
- 固定版 WebView2 运行时（`browserExecutableFolder`）尚未暴露为参数，当前只支持 Evergreen。

## License

MIT，见 [`LICENSE`](LICENSE)。
