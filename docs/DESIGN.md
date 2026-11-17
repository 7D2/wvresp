# 设计取舍

## 一句话

一个**不提权**的 HTTPS 取数工具：放进本进程，拿 WebView2 已经解密好的响应；其余抓包路径交给已经成熟的工具。

## 抓取面：宿主侧取数

WebView2 是宿主进程里的一个控件，浏览器引擎跑在 `msedgewebview2.exe` 子进程里，TLS 由引擎自己完成并已协商好密钥。宿主通过 COM/托管 API 拿到的，是**已经解密、已经解压**的响应体：

```csharp
env = await CoreWebView2Environment.CreateAsync(null, userDataDir, browserExecutableFolder);
await wv.EnsureCoreWebView2Async(env);
wv.CoreWebView2.WebResourceResponseReceived += (s, e) => {
    // e.Request.Uri / Method, e.Response.StatusCode / Headers
    // e.Response.GetContentAsync() → 明文 body
};
```

这条路径没有中间环节：不经过网卡、不经过代理、不写证书、不需要密钥。所以「不需要管理员」不是妥协出来的，而是这条路本来就在用户态、就在本进程权限之内。

## 为什么不做另外五条

判断标准只有两条：**有没有成熟工具**、**要不要提权**。两条一凑，结论就很清楚。

| 路径 | 现成工具 | 权限 | 结论 |
| --- | --- | --- | --- |
| 网卡全量抓包 + pcap | Wireshark / tshark / dumpcap | 需装 Npcap 驱动，通常要管理员 | 已有，不做 |
| 内核层抓包 | pktmon（Windows 自带） | `pktmon start` 必须管理员（实测无提权报「无法与 PktMon 驱动程序通信。拒绝访问。」，exit 5） | 已有，不做 |
| TLS 密钥导出 → 解密 | tshark + `SSLKEYLOGFILE`（另有 `CLIENT_TRAFFIC_SECRET_0` 判据） | 需配套抓包驱动 | 已有，不做 |
| 注入进程 hook 密钥导出 | friTap / Frida | 附加进程要权限，且会被反调试检测 | 已有，不做 |
| 中间人证书解密 | mitmproxy / Fiddler | 系统根证书要管理员，且程序能感知 | 已有，不做 |
| 读凭据 / 密钥材料 | mimikatz 一类 | 管理员 + 常见杀软拦截 | 不做 |

再写一遍同样的东西，收益只是「多一个实现」，成本是维护面变大、还要处理提权失败、驱动冲突、被杀软误杀这些与取数无关的问题。所以这里只把缝隙占住。

## 参数为什么要照 tshark / pktmon（但不对标它们）

**先纠正一个对外说法**：本工具的对标是 `curl`——同样输入一个 URL，差别只在"跑不跑 JS"和"要不要会话"。`tshark` / `pktmon` 是"另一条路"（全机流量、需要提权），我们把它们的参数名借来，只是因为用户已经形成的那套肌肉记忆好用，不代表能力面对等：`-w` 在这里是目录不是 pcap，`-i` 只认 `webview2`，`-Y` 只有 4 个键。

用户拿到一个新工具，第一件事是找自己熟悉的选项。所以参数名、语义、默认输出习惯尽量沿用：

| 心智 | 沿用 | 差异 |
| --- | --- | --- |
| `-w` 输出 | ✅ | 这里是目录而不是 pcap 文件 |
| `-c` 计数停止 | ✅ | 保持「过滤前计数」的 tshark 语义；另加 `--stop-kept` 补「按落盘计数」 |
| `-a duration:` | ✅ | 同时接受 `time:` 与 `N s` |
| `-i` 接口 | ✅ | 只接受 `webview2` / `any`，非法值直接报错——把能力边界说在参数层 |
| `-f` 采集期过滤 | ✅ | 用 host glob + 子串，而不是 BPF |
| `-Y` 显示期过滤 | ✅ | 是 `key OP value` 的 token 集合，不是完整 Wireshark 语法 |
| `-o key=value` | ✅ | 只认 3 个键 |
| `-T` / `-e` / `-z` / `-q` / `-V` | ✅ | `-e` 兼容 `http.response.code` 这类长名 |

不假装支持没有实现的东西：`-i` 传别的直接报错，`-Y` 遇到不认识的键记为 `badfilter:<key>` 并丢弃该行，而不是静默通过。

## 实现上的几个决定

- **控制台输出类型**：`OutputType=Exe`（不是 `WinExe`），这样 stdout 可以被管道和脚本消费。
- **窗口**：抓取器本身是一个 WinForms 窗体（WebView2 控件必须挂窗口）。`--hidden` 让它在脚本里不抢焦点。
- **单文件**：`Program.cs` 一个文件装完参数解析、宿主、事件、清单、日志、统计；不引第三方 CLI 框架，保持可读、易改。
- **每响应立刻写清单**：`capture.jsonl` 用 `AutoFlush`，进程被中断也留得下已抓到的记录。
- **过滤顺序**：`-f` / `--type` / `-Y` 在取 body 之前判断，省掉无谓的 `GetContentAsync`；`--min/--max-bytes` 只能在拿到 body 之后判断。
- **文件名安全化**：host + 路径基名做白名单替换，避免 Windows 非法字符与超长路径。

## 已知限制（都在 README 里同步）

1. 只覆盖本进程 WebView2 一侧；第三方原生 TLS 无入口。
2. 302 重定向响应体 `BODY-ERR 管道正在被关闭 (0x800700E8)`。
3. SSE / 流式响应完整性未验证。
4. 固定版 WebView2（`browserExecutableFolder`）未暴露成参数。

第 4 条是唯一一个「能力上做得通、只是还没接线」的缺口；前三条是平台或协议带来的边界。
