# 要抓某个应用程序，怎么判断、怎么选定

`kpcap` 只能抓 **WebView2 一侧**。所以面对一个程序，先判定它有没有这一侧，再决定抓不抓。按四步走，别跳过第 1 步。

## 第 1 步：它是不是网页壳

看进程树里有没有 `msedgewebview2.exe`，以及它的父进程是谁：

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -match 'msedgewebview2|chrome|electron' } |
  Select-Object ProcessId, ParentProcessId, Name, CommandLine |
  Format-Table -AutoSize -Wrap
```

再对照目标程序的 PID：

```powershell
$target = (Get-Process -Name '你的程序名' -ErrorAction Stop).Id
Get-CimInstance Win32_Process |
  Where-Object { $_.ParentProcessId -eq $target } |
  Select-Object ProcessId, Name
```

判定：

| 观察到的现象 | 结论 |
| --- | --- |
| 目标进程下挂 `msedgewebview2.exe` | 内嵌 **WebView2** → 本工具适用 |
| 目标进程下挂多个 `chrome.exe` / `electron` | Electron / CEF 壳 → 同源 URL 一般可复刻，但不是同一条采集面（见第 4 步） |
| 只有目标进程自己，没有浏览器子进程，网络走 `svchost` / 自身 socket | 原生 TLS（Schannel / WinHTTP / 自研）→ **不可用** |
| 目标是个普通浏览器（Chrome/Edge） | 用它的页面地址在本工具里复刻即可（不读它的 profile） |

## 第 2 步：能不能复刻

「复刻」= 用它页面里**同源**的 URL，在 `kpcap` 里导航一次，看响应是否同口径。

- 页面型目标（网页壳程序）：直接 `-u <页面地址>`。
- 接口型目标：先用浏览器 DevTools 或本工具裸抓一次，找出真正返回数据的 URL（常见是 `*.json`、`/api/`）。
- 需要按特定路径才能触发的接口：`-u` 只做导航，触发交互仍需你在页面里点；工具不注入点击。

## 第 3 步：要不要复用登录态

有数据只有在登录后才出。做法是把 WebView2 的用户数据目录固定下来：

```powershell
kpcap --hidden -u https://host/inbox -o kp.user_data=D:\kp\cap-profile -Y "ctype=json"
```

同一个 `user_data` 目录再次使用，cookie 就在。工具本身**不驱动登录流程**：登录由宿主正常完成，工具只复用会话（等价于 tshark 抓已经在跑的流量）。

## 第 4 步：给结论

只给三种，不要含糊：

| 结论 | 判据 | 下一步 |
| --- | --- | --- |
| **可复刻** | 进程树里有 `msedgewebview2`；同源 URL 能拿到同口径响应 | 直接抓，写进脚本 |
| **需登录态** | 未登录抓到 401/跳登录页，复用 `user_data` 后正常 | 固定 `kp.user_data` 目录 |
| **不可用（非网页发起）** | 进程树里没有浏览器子进程；抓到的响应与目标无关 | 换工具：原生 TLS 只能走 tshark/pktmon/friTap 那一侧 |

## 常见坑

- **只看到 `msedgewebview2.exe` 却抓不到目标数据**：目标数据可能由原生辅助进程（独立 exe）发起，不在 WebView2 采集面内。
- **`--type json` 全落空**：`Content-Type` 可能带参数（`application/json; charset=utf-8`）。本工具按子串匹配，`--type json` 能命中；若写成 `--type application/json` 也可以，但 `-Y ctype=json` 才是按子串的写法。
- **`-c 6` 很早就停**：`-c` 是过滤前计数，前 6 个可能是 JS/CSS。要按真正落盘计数用 `--stop-kept`。
- **302 响应体缺失**：`BODY-ERR ... 管道正在被关闭 (0x800700E8)`，当前未规避。

## 边界提醒

判定与抓取都只在自己部署/自有的宿主与测试页上做。工具不内置登录、不注入、不读凭据，也不负责绕过目标程序的任何鉴权。
