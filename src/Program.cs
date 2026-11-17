// kpcap - WebView2 会话抓取工具（tshark/pktmon 参数风格的子集）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace kpcap
{
    sealed class Opt
    {
        public List<string> Urls = new List<string>();
        public string OutDir = null;
        public int Count = 0;
        public int StopKept = 0;
        public int DurationSec = 0;
        public List<string> Hosts = new List<string>();
        public List<string> Types = new List<string>();
        public string DisplayFilter = null;
        public long MinBytes = 0;
        public long MaxBytes = long.MaxValue;
        public long MaxSave = 8L * 1024 * 1024;
        public string Format = "table";
        public List<string> Fields = new List<string>();
        public string Manifest = "capture.jsonl";
        public string UserAgent = null;
        public string UserData = null;
        public bool Hidden = false;
        public bool Quiet = false;
        public bool Verbose = false;
        public bool Stats = false;
        public bool ExportAll = false;
        public string ExportDir = null;
    }

    static class Cli
    {
        public static bool Parse(string[] a, Opt o, out string err)
        {
            err = null;
            for (int i = 0; i < a.Length; i++)
            {
                string t = a[i];
                string val = null;
                if (t.StartsWith("--") && t.Contains("=")) { int p = t.IndexOf('='); val = t.Substring(p + 1); t = t.Substring(0, p); }
                Func<string> need = delegate {
                    if (val != null) return val;
                    if (i + 1 >= a.Length) throw new Exception("missing value for " + t);
                    return a[++i];
                };
                switch (t)
                {
                    case "-h": case "--help": err = null; throw new UsageException();
                    case "--version": throw new VersionException();
                    case "--url": case "-u": o.Urls.Add(need()); break;
                    case "-w": case "--out": o.OutDir = need(); break;
                    case "-c": o.Count = int.Parse(need(), CultureInfo.InvariantCulture); break;
                    case "--stop-kept": o.StopKept = int.Parse(need(), CultureInfo.InvariantCulture); break;
                    case "-a":
                        {
                            string v = need();
                            if (v.StartsWith("duration:") || v.StartsWith("time:")) v = v.Substring(v.IndexOf(':') + 1);
                            o.DurationSec = (int)Math.Ceiling(double.Parse(v.TrimEnd('s'), CultureInfo.InvariantCulture));
                            break;
                        }
                    case "--host": case "-f": o.Hosts.AddRange(need().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)); break;
                    case "--type": o.Types.AddRange(need().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)); break;
                    case "-Y": case "--display-filter": o.DisplayFilter = need(); break;
                    case "--min-bytes": o.MinBytes = long.Parse(need(), CultureInfo.InvariantCulture); break;
                    case "--max-bytes": o.MaxBytes = long.Parse(need(), CultureInfo.InvariantCulture); break;
                    case "--max-save": o.MaxSave = long.Parse(need(), CultureInfo.InvariantCulture); break;
                    case "-T": case "--format": o.Format = need().ToLowerInvariant(); break;
                    case "-e":
                        {
                            string v = need();
                            if (v.StartsWith("http.") || v.StartsWith("tcp.")) v = v.Substring(v.IndexOf('.') + 1);
                            o.Fields.AddRange(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                            if (o.Format == "table") o.Format = "fields";
                            break;
                        }
                    case "--manifest": o.Manifest = need(); break;
                    case "-o":
                        {
                            string v = need();
                            int p = v.IndexOf('=');
                            if (p < 0) throw new Exception("-o expects key=value");
                            string k = v.Substring(0, p), x = v.Substring(p + 1);
                            switch (k.ToLowerInvariant())
                            {
                                case "http.user_agent": o.UserAgent = x; break;
                                case "kp.user_data": o.UserData = x; break;
                                case "kp.max_save": o.MaxSave = long.Parse(x, CultureInfo.InvariantCulture); break;
                                default: throw new Exception("unknown -o key: " + k);
                            }
                            break;
                        }
                    case "--export-objects":
                        {
                            string v = need();
                            string[] pp = v.Split(',');
                            o.ExportAll = true;
                            o.ExportDir = pp.Length > 1 ? pp[1] : "objects";
                            break;
                        }
                    case "-i": case "--iface":
                        {
                            string v = need();
                            if (v != "webview2" && v != "any") throw new Exception("only -i webview2 is supported (this tool captures its own WebView2 session)");
                            break;
                        }
                    case "-q": o.Quiet = true; break;
                    case "-V": o.Verbose = true; break;
                    case "-z": o.Stats = true; break;
                    case "--hidden": o.Hidden = true; break;
                    default: err = "unknown option: " + t; return false;
                }
            }
            if (o.Format == "jsonl") o.Format = "json";
            if (o.Urls.Count == 0) { err = "at least one --url is required"; return false; }
            return true;
        }
    }

    sealed class UsageException : Exception { }
    sealed class VersionException : Exception { }

    sealed class Rec
    {
        public int I; public string Method; public int Status; public string Url; public string Host;
        public string CType; public long Bytes; public string Md5; public string File; public bool Kept; public string Reason;
        public long T;
    }

    sealed class CapForm : Form
    {
        readonly Opt o;
        WebView2 wv;
        readonly List<Rec> recs = new List<Rec>();
        StreamWriter man, log;
        int respCount, keptCount, bodyErr;
        long bytesSaved, bytesSeen;
        readonly Dictionary<string, int> hostCount = new Dictionary<string, int>();
        readonly Dictionary<string, long> hostBytes = new Dictionary<string, long>();
        readonly Dictionary<string, int> ctypeCount = new Dictionary<string, int>();
        System.Windows.Forms.Timer stopTimer, navTimer;
        DateTime t0 = DateTime.Now;
        int navIndex;
        public int ExitCode = 0;
        string outDir, bodyDir;
        readonly object gate = new object();

        public CapForm(Opt opt)
        {
            o = opt;
            outDir = o.OutDir ?? Path.Combine(Directory.GetCurrentDirectory(), "kpcap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(outDir);
            bodyDir = Path.Combine(outDir, "bodies");
            Directory.CreateDirectory(bodyDir);
            man = new StreamWriter(Path.Combine(outDir, o.Manifest), false, new UTF8Encoding(false)) { AutoFlush = true };
            log = new StreamWriter(Path.Combine(outDir, "kpcap.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            Text = "kpcap";
            Width = 1000; Height = 700;
            StartPosition = FormStartPosition.CenterScreen;
            if (o.Hidden) { ShowInTaskbar = false; Opacity = 0; WindowState = FormWindowState.Minimized; }
            wv = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(wv);
            Load += async (s, e) => await InitAsync();
            FormClosing += (s, e) => Cleanup();
        }

        void Say(string s)
        {
            lock (gate) { log.WriteLine(s); if (!o.Quiet) Console.WriteLine(s); }
        }

        async Task InitAsync()
        {
            string ud = o.UserData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kpcap", "webdata");
            Directory.CreateDirectory(ud);
            var env = await CoreWebView2Environment.CreateAsync(null, ud, null);
            await wv.EnsureCoreWebView2Async(env);
            var st = wv.CoreWebView2.Settings;
            if (o.UserAgent != null) st.UserAgent = o.UserAgent;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            Say("kpcap start version=" + typeof(CoreWebView2).Assembly.GetName().Version + " core=" + wv.CoreWebView2.Environment.GetType().Name);
            Say("out=" + outDir);
            wv.CoreWebView2.WebResourceResponseReceived += OnResponse;
            wv.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                Say("nav ok=" + e.IsSuccess + " err=" + e.WebErrorStatus + " url=" + wv.CoreWebView2.Source);
                if (navIndex + 1 < o.Urls.Count) { navIndex++; navTimer.Interval = 800; navTimer.Start(); }
            };
            navTimer = new System.Windows.Forms.Timer { Interval = 800 };
            navTimer.Tick += (s, e) => { navTimer.Stop(); NavigateCurrent(); };
            stopTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, o.DurationSec) * 1000 };
            stopTimer.Tick += (s, e) => { stopTimer.Stop(); Finish("duration reached"); };
            navIndex = 0;
            NavigateCurrent();
            if (o.DurationSec > 0) stopTimer.Start();
            if (o.Count > 0) Say("autostop after count=" + o.Count);
            if (o.StopKept > 0) Say("autostop after kept=" + o.StopKept);
        }

        void NavigateCurrent()
        {
            string u = o.Urls[Math.Min(navIndex, o.Urls.Count - 1)];
            Say("nav -> " + u);
            try { wv.CoreWebView2.Navigate(u); } catch (Exception ex) { Say("NAV-ERR " + ex.Message); }
        }

        static bool Glob(string pat, string s)
        {
            if (pat == null) return true;
            if (pat.IndexOf('*') < 0 && pat.IndexOf('?') < 0) return (s ?? "").IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0;
            var rx = "^" + Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(s ?? "", rx, RegexOptions.IgnoreCase);
        }

        bool PreFilter(string url, string host, string ctype, int status, out string reason)
        {
            reason = null;
            if (o.Hosts.Count > 0)
            {
                bool hit = o.Hosts.Any(h => Glob(h, host));
                if (!hit) { reason = "host"; return false; }
            }
            if (o.Types.Count > 0)
            {
                bool hit = o.Types.Any(t => (ctype ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) { reason = "type"; return false; }
            }
            if (!string.IsNullOrEmpty(o.DisplayFilter))
            {
                foreach (string rawTok in o.DisplayFilter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string tok = rawTok.Trim();
                    if (tok.Length == 0) continue;
                    var m = Regex.Match(tok, @"^([A-Za-z\-]+)(>=|<=|==|!=|>|<|=|:)(.+)$");
                    if (m.Success)
                    {
                        string k = m.Groups[1].Value.ToLowerInvariant();
                        string op = m.Groups[2].Value;
                        if (op == ":" || op == "=") op = "==";
                        string v = m.Groups[3].Value;
                        string fv;
                        switch (k)
                        {
                            case "host": fv = host; break;
                            case "url": fv = url; break;
                            case "ctype": case "contenttype": fv = ctype; break;
                            case "method": fv = null; break;
                            case "status":
                                {
                                    int want = int.Parse(v, CultureInfo.InvariantCulture);
                                    bool ok = op == ">=" ? status >= want : op == "<=" ? status <= want
                                            : op == ">" ? status > want : op == "<" ? status < want
                                            : op == "!=" ? status != want : status == want;
                                    if (!ok) { reason = "status"; return false; }
                                    continue;
                                }
                            default: reason = "badfilter:" + k; return false;
                        }
                        if (fv == null) { reason = "badfilter:" + k; return false; }
                        if (!Glob(v, fv)) { reason = "filter"; return false; }
                    }
                    else if ((url ?? "").IndexOf(tok, StringComparison.OrdinalIgnoreCase) < 0)
                    { reason = "filter"; return false; }
                }
            }
            return true;
        }

        string ExtFor(string ctype)
        {
            string c = (ctype ?? "").ToLowerInvariant();
            if (c.Contains("json")) return ".json";
            if (c.Contains("html")) return ".html";
            if (c.Contains("css")) return ".css";
            if (c.Contains("javascript") || c.Contains("ecmascript")) return ".js";
            if (c.Contains("svg")) return ".svg";
            if (c.Contains("png")) return ".png";
            if (c.Contains("jpeg") || c.Contains("jpg")) return ".jpg";
            if (c.Contains("gif")) return ".gif";
            if (c.Contains("webp")) return ".webp";
            if (c.Contains("pdf")) return ".pdf";
            if (c.Contains("text/")) return ".txt";
            if (c.Contains("protobuf") || c.Contains("octet-stream")) return ".bin";
            return ".dat";
        }

        static string Trunc(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        static string Safe(string s, int max)
        {
            if (s == null) s = "";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') sb.Append(ch); else sb.Append('_');
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim('_');
        }

        async void OnResponse(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            var url = e.Request.Uri;
            var method = e.Request.Method;
            int status = e.Response.StatusCode;
            string ctype = "";
            try { ctype = e.Response.Headers.GetHeader("Content-Type"); } catch { }
            string host = "";
            try { host = new Uri(url).Host; } catch { }
            int n = Interlocked.Increment(ref respCount);
            Interlocked.Add(ref bytesSeen, 0);
            lock (gate)
            {
                if (!hostCount.ContainsKey(host)) hostCount[host] = 0;
                hostCount[host]++;
                string ct = ctype.Split(';')[0].Trim().ToLowerInvariant();
                if (ct.Length == 0) ct = "(none)";
                if (!ctypeCount.ContainsKey(ct)) ctypeCount[ct] = 0;
                ctypeCount[ct]++;
            }

            string reason;
            bool pre = PreFilter(url, host, ctype, status, out reason);
            byte[] buf = null;
            if (pre)
            {
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var stm = await e.Response.GetContentAsync()) await stm.CopyToAsync(ms);
                        buf = ms.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref bodyErr);
                    lock (gate) log.WriteLine("BODY-ERR " + url + " :: " + ex.Message);
                    reason = "bodyerr";
                    pre = false;
                }
            }

            var r = new Rec { I = n, Method = method, Status = status, Url = url, Host = host, CType = ctype, T = (long)(DateTime.Now - t0).TotalMilliseconds };
            if (pre && buf != null)
            {
                r.Bytes = buf.Length;
                bool inRange = buf.Length >= o.MinBytes && buf.Length <= o.MaxBytes;
                if (!inRange) { r.Reason = "size"; }
                else if (o.ExportAll || buf.Length <= o.MaxSave)
                {
                    string fn = string.Format("{0:D4}_{1}{2}", n, Safe(host + "_" + Path.GetFileName(new Uri(url).AbsolutePath), 60), ExtFor(ctype));
                    string full = Path.Combine(o.ExportDir != null ? (Path.IsPathRooted(o.ExportDir) ? o.ExportDir : Path.Combine(outDir, o.ExportDir)) : bodyDir, fn);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllBytes(full, buf);
                    r.File = full.Substring(outDir.Length).TrimStart('\\', '/');
                    using (var md5 = MD5.Create()) r.Md5 = BitConverter.ToString(md5.ComputeHash(buf)).Replace("-", "").ToLowerInvariant();
                    r.Kept = true;
                    Interlocked.Increment(ref keptCount);
                    Interlocked.Add(ref bytesSaved, buf.Length);
                    lock (gate)
                    {
                        if (!hostBytes.ContainsKey(host)) hostBytes[host] = 0;
                        hostBytes[host] += buf.Length;
                    }
                }
                else r.Reason = "toobig";
            }
            else r.Reason = reason ?? "filter";

            bool stopNow = false;
            lock (gate)
            {
                recs.Add(r);
                WriteManifest(r);
                if (o.Verbose || (r.Kept && o.Format != "json") || o.Format == "json")
                {
                    string line = o.Format == "json" ? Json(r) : string.Format("{0} {1} {2} {3}B {4} {5}", r.I, r.Status, r.Method, r.Bytes, r.Md5 ?? "-", Trunc(r.Url, 180));
                    log.WriteLine(line);
                    if (!o.Quiet) Console.WriteLine(line);
                }
                if (o.Count > 0 && respCount >= o.Count) stopNow = true;
                if (o.StopKept > 0 && keptCount >= o.StopKept) stopNow = true;
            }
            if (stopNow) BeginInvoke((Action)(() => Finish("count reached")));
        }

        string Json(Rec r)
        {
            Func<string, string> Q = delegate(string s) { return s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; };
            return "{" + string.Join(",", new[] {
                "\"i\":" + r.I, "\"t\":" + r.T, "\"status\":" + r.Status, "\"method\":" + Q(r.Method),
                "\"url\":" + Q(r.Url), "\"host\":" + Q(r.Host), "\"ctype\":" + Q(r.CType),
                "\"bytes\":" + r.Bytes, "\"md5\":" + Q(r.Md5), "\"file\":" + Q(r.File),
                "\"kept\":" + (r.Kept ? "true" : "false"), "\"reason\":" + Q(r.Reason) }) + "}";
        }

        void WriteManifest(Rec r)
        {
            man.WriteLine(Json(r));
        }

        string FieldOf(Rec r, string f)
        {
            switch (f.ToLowerInvariant())
            {
                case "i": case "no": case "frame.number": return r.I.ToString();
                case "t": case "time": return r.T.ToString();
                case "status": case "http.response.code": return r.Status.ToString();
                case "method": case "http.request.method": return r.Method;
                case "url": case "http.request.full_uri": return r.Url;
                case "host": case "http.host": return r.Host;
                case "ctype": case "content_type": return r.CType;
                case "bytes": case "length": return r.Bytes.ToString();
                case "md5": return r.Md5 ?? "";
                case "file": return r.File ?? "";
                case "kept": return r.Kept ? "1" : "0";
                case "reason": return r.Reason ?? "";
                default: return "?";
            }
        }

        void Finish(string why)
        {
            if (IsDisposed) return;
            Say("stop: " + why);
            Say(string.Format("stats responses={0} kept={1} bodyErr={2} savedBytes={3} seenHosts={4}", respCount, keptCount, bodyErr, bytesSaved, hostCount.Count));
            if (o.Stats || !o.Quiet)
            {
                Say("--- hosts ---");
                foreach (var kv in hostCount.OrderByDescending(k => k.Value).Take(25))
                    Say(string.Format("{0,6}  {1}  {2}B", kv.Value, kv.Key, hostBytes.ContainsKey(kv.Key) ? hostBytes[kv.Key] : 0));
                Say("--- content-types ---");
                foreach (var kv in ctypeCount.OrderByDescending(k => k.Value).Take(15))
                    Say(string.Format("{0,6}  {1}", kv.Value, kv.Key));
            }
            var kept = recs.Count(x => x.Kept);
            if (o.Fields.Count > 0)
            {
                foreach (var r in recs.Where(x => x.Kept))
                    Console.WriteLine(string.Join("\t", o.Fields.Select(f => FieldOf(r, f))));
            }
            ExitCode = respCount == 0 ? 4 : 0;
            Close();
        }

        void Cleanup()
        {
            try { man.Flush(); man.Dispose(); } catch { }
            try { log.Flush(); log.Dispose(); } catch { }
        }
    }

    static class Program
    {
        static void Usage()
        {
            Console.WriteLine(@"kpcap 1.0 - WebView2 session capture (tshark/pktmon style)

USAGE
  kpcap --url <URL> [--url <URL> ...] [options]

CAPTURE
  -i, --iface <webview2|any>   capture surface (only its own WebView2 session)
  --url, -u <URL>              start URL (repeatable, navigated in order)
  -w, --out <dir>              output dir (default .\kpcap-YYYYmmdd-HHmmss)
  -a duration:<sec>            autostop after N seconds
  -c <n>                       autostop after N responses (tshark -c: before filters)
  --stop-kept <n>              autostop after N bodies actually kept
  -o http.user_agent=<s>       override User-Agent
  -o kp.user_data=<dir>        WebView2 user data dir (keeps cookies/session)
  -o kp.max_save=<bytes>       max body size saved per response (default 8 MiB)
  --hidden                     run minimized/invisible

FILTER
  -f, --host <glob[,glob]>     keep hosts matching glob (comma separated, repeatable)
  --type <substr[,substr]>     keep content-types containing substring
  -Y <expr>                    display filter tokens (AND), e.g.:
                                 host=*.doubao.com  status>=400  ctype=json  api/chat
  --min-bytes <n>              keep bodies >= n bytes
  --max-bytes <n>              keep bodies <= n bytes

OUTPUT
  -T table|json|fields|jsonl    per-response output format (default table)
  -e <field[,field]>            fields: i,status,method,url,host,ctype,bytes,md5,file,kept,reason
  --manifest <name>            manifest file name inside out dir (default capture.jsonl)
  --export-objects http,<dir>   save every body to <dir> (ignores max-save)
  -z                           print host/content-type statistics
  -q                           quiet (log file only)
  -V                           verbose (print every response)

EXIT
  0 ok | 2 usage | 4 no response captured
");
        }

        [STAThread]
        static int Main(string[] args)
        {
            var o = new Opt();
            string err;
            try
            {
                if (!Cli.Parse(args, o, out err)) { Console.Error.WriteLine("ERR " + err); Usage(); return 2; }
            }
            catch (UsageException) { Usage(); return 0; }
            catch (VersionException) { Console.WriteLine("kpcap 1.0 (netcoreapp3.1 + Microsoft.Web.WebView2)"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("ERR " + ex.Message); Usage(); return 2; }

            Application.EnableVisualStyles();
            var f = new CapForm(o);
            Application.Run(f);
            return f.ExitCode;
        }
    }
}
