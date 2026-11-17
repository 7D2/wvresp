# wvresp

**把网页的 HTTP(S) 响应正文原样存下来——不需要管理员权限。**

wvresp 是一个 Windows 命令行小工具。你给它一个网址，它打开一个窗口把网页跑一遍，把每个响应（HTML、JSON、JS、图片……）的**明文正文**存成文件，再附一份 JSON 清单和日志。

它不装驱动，不改证书，不注入进程，不读凭据。跑它不需要管理员，退出后也不留系统级残留。

**名字**：`wvresp` = **W**e**V**iew **resp**onse，直白到不用解释——它拿的就是 WebView 的响应。

**谁做的**：个人独立开发、MIT 开源，不隶属任何公司；不收集、不回传任何数据，代码能在这里全部读完。

---

## 它是怎么做到的

一句话：**它站在"数据已经解开"的那一层。**

网页壳程序（比如用微软 WebView2 做的客户端、Edge 的网页视图）打开网页时，TLS 由 WebView2 自己完成。宿主程序调一个官方接口拿到的，本来就是解密好的响应：

```csharp
wv.CoreWebView2.WebResourceResponseReceived += (s, e) => {
    // e.Response.StatusCode / Headers
    // e.Response.GetContentAsync()  →  明文 body
};
```

wvresp 就是这样一个"宿主"：它自己开窗口、自己访问你给的网址、自己通过微软官方 API 取明文。所以：

- 不需要网卡抓包权限（不走网卡）；
- 不需要证书（不伪造、不替换、不写系统证书库）；
- 不需要注入（数据本来就在自己进程里）；
- 不需要 SSLKEYLOGFILE（没有要解密的密文）。

原理上它取的是**自己这次访问的**内容，不是别人的流量。

---

## 它能做什么

| 能力 | 说明 |
| --- | --- |
| 顺序访问多个网址 | `-u` 可重复，按顺序打开，每次间隔 800 ms |
| 过滤 | 按域名（`-f`，支持 `*` / `?` 通配）、按内容类型（`--type`）、按状态码/地址/大小（`-Y`）、按 body 字节数（`--min-bytes/--max-bytes`） |
| 落盘 | `<输出目录>\bodies\` 下按 `序号_域名_文件名.扩展名` 存正文，扩展名按内容类型推 |
| 清单 | `capture.jsonl`，每个响应一行，含未保存的（带 `reason` 说明为什么没存） |
| 日志 | `wvresp.log`：导航成功/失败、取正文失败的报错、域名与内容类型统计 |
| 统计 | `-z` 打印访问最多的域名（带已存字节数）与内容类型分布 |
| 字段输出 | `-e i,status,url,bytes,md5,...` 逐行输出，方便管道处理；`-T table/json/fields/jsonl` 四种格式 |
| 复用会话 | `-o wvresp.user_data=<目录>` 指到已有的用户数据目录，带上 cookie 再抓 |
| 全量导出 | `--export-objects http,<目录>` 把每个正文都导出（不受单文件大小上限约束） |
| 脚本化 | `--hidden` 不弹窗抢焦点；退出码 `0` 正常 / `2` 用法错误 / `4` 一个响应都没收到 |

参数名刻意照着 `tshark` / `pktmon` 起（`-w/-c/-a/-i/-f/-Y/-o/-T/-e/-z/-q/-V`），如果你用过那两个，基本不用重新学。

---

## 它不能做什么

这些都是**能力边界**，不是待修的 bug：

1. **抓不到别的程序的流量。** 它只覆盖"自己窗口里发起的请求"。第三方程序的原生 TLS（Windows 自带的 Schannel、WinHTTP、自己写的 socket）它没有任何入口。
2. **不做全机抓包，不产 pcap。** 那是 Wireshark / tshark / dumpcap / pktmon 的工作，请用它们。
3. **不解密任意流量。** 没有密钥导出、没有证书替换、没有 TLS hook；它压根不解密，因为它拿到的已经是明文。
4. **不注入、不提权、不读凭据、不写系统证书库。** 工具没有这些能力，也不打算加。
5. **不绕过鉴权。** 登录态只能靠复用你自己的会话目录（等同于你已经登录过的窗口），工具不驱动登录流程、不尝试绕过任何登录或证书校验。
6. **302 重定向的正文目前取不到**（报 `BODY-ERR ... 管道正在被关闭 (0x800700E8)`）；流式（SSE）响应的完整性未验证。
7. **固定版 WebView2 运行时暂不支持**，只支持微软的常青版（Evergreen）。

---

## 对标产品

同一个目的（"把通过网络传输的数据拿到手"），市面上的成熟工具走的是另外几条路：

| 工具 | 它怎么拿数据 | 前提/权限 | wvresp 的差别 |
| --- | --- | --- | --- |
| Wireshark / tshark / dumpcap | 抓网卡，离线解析 pcap | 需装 Npcap 驱动，通常要管理员 | 不抓网卡，不产 pcap |
| pktmon（Windows 自带） | 内核层抓包 | **必须管理员** | 不需要管理员 |
| tshark + `SSLKEYLOGFILE` | 抓网卡 + 用浏览器导出的 TLS 密钥解密 | 需要抓包驱动配合 | 不解密，无密钥环节 |
| mitmproxy / Fiddler | 中间人代理，签发证书解密 | 要装根证书，系统级要管理员 | 不碰证书，程序感知不到 |
| friTap / Frida | 注入进程，hook TLS 库捞密钥 | 附加进程要权限，会被反调试检测 | 不注入 |

它们的定位是"**能抓这台机器上的一切**"。wvresp 的定位是"**只拿自己这一份、并且不提权**"。这两件事不冲突：需要全机流量就用上面那些，需要"某个网页壳程序里到底返回了什么"就用 wvresp。

这一点是有意为之：这几条路都有成熟工具、也不缺这一份实现，而且全都要抬权限；再做一个只是重复造轮子。

---

## 快速开始

```powershell
# 构建（需要 .NET Core SDK 3.1）
.\build.ps1
# 产物：src\bin\Release\netcoreapp3.1\wvresp.exe

# 抓一个页面，8 秒后停，只留这个域名
wvresp -u https://example.com/ -w .\cap1 -a duration:8 -f example.com

# 只要 JSON 接口，按字段逐行看
wvresp --hidden -u https://example.com/api -Y "ctype=json status>=200" -e i,status,bytes,md5,url

# 收满 5 个正文就停，并把正文全量导出
wvresp --hidden -u https://example.com/api --stop-kept 5 --export-objects http,obj -q -z
```

完整参数、字段名与输出格式见 [`docs/USAGE.md`](docs/USAGE.md)；"要抓某个程序该怎么判断"见 [`docs/TARGET.md`](docs/TARGET.md)；设计取舍见 [`docs/DESIGN.md`](docs/DESIGN.md)。

---

## 关于隐私

- 工具不联网回传任何东西；抓到的内容只写在你指定的输出目录里。
- 只访问你在命令行里给出的网址，不内置任何站点、不内置登录、不内置上传。
- 没有后台服务、没有开机自启、没有系统级安装；删掉目录就等于卸载干净。

---

## 许可

MIT，见 [`LICENSE`](LICENSE)。
