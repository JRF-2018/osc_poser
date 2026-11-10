// 外から OscPoser を操作する口(0.4.0)。-api [PORT](既定 9330)で起動したときだけ、127.0.0.1 の HTTP で受ける(ほかの PC からは届かない)。
//   GET  /state                      今の状態(JSON)
//   POST /cmd   {"cmd": "...", ...}  コマンド(下の HandleCmd)。答えは JSON {"ok": true/false, "status": "...", ...}
// panel.py(と、将来の MCP のサーバーなど)が使う。コマンドはメインスレッド(Update)で順に実行し、
// VRM の読み込み・手の計算など時間のかかるものは、終わるまで答えを待たせる(最大 120 秒)。
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public partial class PoserApp
{
    class ApiJob
    {
        public Dictionary<string, object> Req;
        public Dictionary<string, object> Res;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    public const int DefaultApiPort = 9330;
    TcpListener apiListener;
    Thread apiThread;
    int apiPort;
    readonly ConcurrentQueue<ApiJob> apiJobs = new ConcurrentQueue<ApiJob>();

    void StartApi()
    {
        if (Arg("api") == null) return;
        if (Arg("api") == "1") apiPort = DefaultApiPort;   // 数字なしの -api(引数の読み方で "1" になる)は 9330
        else if (!int.TryParse(Arg("api"), out apiPort) || apiPort <= 1 || apiPort >= 65536) return;
        try
        {
            apiListener = new TcpListener(IPAddress.Loopback, apiPort);
            apiListener.Start();
            apiThread = new Thread(ApiLoop) { IsBackground = true, Name = "OscPoser api" };
            apiThread.Start();
        }
        catch (Exception e)
        {
            status = T($"操作の口(ポート {apiPort})を開けません: ", $"Cannot open the API port {apiPort}: ") + e.Message;
            apiListener = null;
        }
    }

    void StopApi()
    {
        try { apiListener?.Stop(); } catch (Exception) { }
        apiListener = null;
    }

    void ApiLoop()
    {
        while (apiListener != null)
        {
            TcpClient c;
            try { c = apiListener.AcceptTcpClient(); }
            catch (Exception) { return; }
            var t = new Thread(() => ApiServe(c)) { IsBackground = true };
            t.Start();
        }
    }

    void ApiServe(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.ReceiveTimeout = 10000;
                var ns = c.GetStream();
                string method, path, host = null, origin = null; int len = 0;
                var head = ReadLine(ns).Split(' ');
                if (head.Length < 2) return;
                method = head[0]; path = head[1];
                for (string l; (l = ReadLine(ns)) != "";)
                {
                    int k = l.IndexOf(':');
                    if (k <= 0) continue;
                    var name = l.Substring(0, k).Trim().ToLowerInvariant();
                    var val = l.Substring(k + 1).Trim();
                    if (name == "content-length") int.TryParse(val, out len);
                    else if (name == "host") host = val;
                    else if (name == "origin") origin = val;
                }
                if (len < 0 || len > 4 << 20) { ReplyRaw(ns, 413, "{}"); return; }
                var body = new byte[len];
                for (int got = 0; got < len;)
                {
                    int n = ns.Read(body, got, len - got);
                    if (n <= 0) break;
                    got += n;
                }
                // ブラウザの Web ページからの要求は断る(悪いページが 127.0.0.1 に命令を送れないように。DNS rebinding も)。
                // ブラウザは Origin を付けて送る。付いていれば、この口そのもの(http://127.0.0.1:ポート など)のときだけ通す。
                // (本文を読んでから断る。読まずに閉じると、相手には接続のリセットに見える)
                if (!LocalName(host, false) || (origin != null && !LocalName(origin, true)))
                {
                    ReplyRaw(ns, 403, Json(new Dictionary<string, object> { ["ok"] = false, ["error"] = "forbidden (browser / non-local)" }));
                    return;
                }
                if (path.StartsWith("/mcp"))
                {
                    if (method != "POST") { ReplyRaw(ns, 405, ""); return; }
                    var (code, text) = McpHandle(Encoding.UTF8.GetString(body));
                    ReplyRaw(ns, code, text);
                    return;
                }
                Dictionary<string, object> req;
                if (method == "GET" && path.StartsWith("/state")) req = new Dictionary<string, object> { ["cmd"] = "state" };
                else if (method == "POST" && path.StartsWith("/cmd"))
                    req = MiniJson.Parse(Encoding.UTF8.GetString(body)) as Dictionary<string, object> ?? new Dictionary<string, object>();
                else { Reply(ns, 404, new Dictionary<string, object> { ["ok"] = false, ["error"] = "GET /state, POST /cmd, POST /mcp" }); return; }
                Reply(ns, 200, RunOnMain(req));
            }
            catch (Exception e)
            {
                try { Reply(c.GetStream(), 400, new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message }); } catch (Exception) { }
            }
        }
    }

    // メインスレッドで実行して、答えを待つ(ネットワークのスレッドから)
    Dictionary<string, object> RunOnMain(Dictionary<string, object> req)
    {
        var job = new ApiJob { Req = req };
        apiJobs.Enqueue(job);
        if (!job.Done.Wait(120000)) job.Res = new Dictionary<string, object> { ["ok"] = false, ["error"] = "timeout" };
        return job.Res;
    }

    // Host・Origin が、この口そのもの(127.0.0.1・localhost・[::1] とこのポート)か
    bool LocalName(string v, bool origin)
    {
        if (v == null) return !origin;   // Host のない古い要求は通す(Origin は null なら呼ばない)
        if (origin) { if (!v.StartsWith("http://")) return false; v = v.Substring(7); }
        foreach (var h in new[] { "127.0.0.1", "localhost", "[::1]" })
            if (v.Equals($"{h}:{apiPort}", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ReadLine(NetworkStream ns)
    {
        var b = new List<byte>();
        while (true)
        {
            int x = ns.ReadByte();
            if (x < 0 || x == '\n') break;
            if (x != '\r') b.Add((byte)x);
        }
        return Encoding.UTF8.GetString(b.ToArray());
    }

    static void Reply(NetworkStream ns, int code, Dictionary<string, object> res) => ReplyRaw(ns, code, Json(res));

    static void ReplyRaw(NetworkStream ns, int code, string text)
    {
        var body = Encoding.UTF8.GetBytes(text ?? "");
        var reason = code == 200 ? "OK" : code == 202 ? "Accepted" : "Error";
        var type = body.Length > 0 ? "Content-Type: application/json; charset=utf-8\r\n" : "";
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\n{type}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        ns.Write(head, 0, head.Length);
        ns.Write(body, 0, body.Length);
    }

    // メインスレッド(Update)で、届いたコマンドを順に
    bool apiRunning;
    readonly Queue<ApiJob> apiWaiting = new Queue<ApiJob>();
    void RunApiJobs()
    {
        while (apiJobs.TryDequeue(out var j)) apiWaiting.Enqueue(j);
        if (!apiRunning && apiWaiting.Count > 0) StartCoroutine(RunJob(apiWaiting.Dequeue()));
    }

    IEnumerator RunJob(ApiJob j)
    {
        apiRunning = true;
        var res = new Dictionary<string, object> { ["ok"] = true };
        IEnumerator co = null;
        // 読み込み・手の計算・相手の切り替えの途中なら、終わるまで待つ(起動した直後など)。state だけはすぐ答える
        if ((S(j.Req, "cmd") ?? "") != "state")
        {
            float t0 = Time.unscaledTime;
            while ((loading || ikBusy || switchTo >= 0 || starting) && Time.unscaledTime - t0 < 100) yield return null;
        }
        // "port" があれば、その相手で実行する(画面の相手は、すぐ済むコマンドなら同じフレームのうちに戻すので見えない)
        int back = -1, tmp = -1;
        var cmdName = S(j.Req, "cmd") ?? "";
        if (!Multi && Fq(j.Req, "port") is float p1 && (int)p1 != sender.Port && cmdName != "target" && cmdName != "state")
        {
            // ふつうの起動では、窓のポートとしか話せない(黙って窓のポートで動かないように)
            res["ok"] = false;
            res["error"] = T($"ポート {(int)p1} は窓のポート({sender.Port})と違います。先に set_port で切り替えてください(-targets で起動すると、ポートごとに VRM・ポーズなどを持ちます)",
                             $"Port {(int)p1} differs from the window's port ({sender.Port}). Switch with set_port first (start with -targets to keep a VRM, pose, etc. per port)");
            tmp = -2;
        }
        if (Multi && Fq(j.Req, "port") is float pp && cmdName != "target" && cmdName != "state")
        {
            tmp = SlotIndexOfPort((int)pp);
            if (tmp < 0) { res["ok"] = false; res["error"] = $"ポート {(int)pp} の相手はありません"; tmp = -2; }
            else if (tmp != cur) { back = cur; QuickSwitch(tmp); }
        }
        if (tmp != -2)
        try { co = HandleCmd(j.Req, res); }
        catch (Exception e) { res["ok"] = false; res["error"] = e.Message; }
        if (co != null)
        {
            while (true)
            {
                object step;
                try { if (!co.MoveNext()) break; step = co.Current; }
                catch (Exception e) { res["ok"] = false; res["error"] = e.Message; break; }
                yield return step;
            }
        }
        if (!res.ContainsKey("status")) res["status"] = status;
        if (back >= 0 && cur == tmp && !loading && !ikBusy) QuickSwitch(back);
        j.Res = res;
        j.Done.Set();
        apiRunning = false;
    }

    static string S(Dictionary<string, object> q, string k) => q.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    static float? Fq(Dictionary<string, object> q, string k) => q.TryGetValue(k, out var v) && v is double d ? (float?)d : null;
    static bool Bq(Dictionary<string, object> q, string k) => q.TryGetValue(k, out var v) && (v is bool b ? b : v is double d && d != 0);

    // コマンド。すぐ済むものは null を返し、待つものは IEnumerator(コルーチン)を返す
    IEnumerator HandleCmd(Dictionary<string, object> q, Dictionary<string, object> res)
    {
        var cmd = S(q, "cmd") ?? "";
        switch (cmd)
        {
            case "state":
                res["state"] = StateJson();
                return null;

            case "target":   // {"index": n} か {"port": p}
            {
                if (!Multi)   // ふつうの起動: 画面のポートの欄を書き換えたのと同じ(ポートを変え、控えを消す)
                {
                    int port1 = (int)Fq(q, "port").GetValueOrDefault(0);
                    if (port1 <= 0 || port1 >= 65536) throw new Exception(T("port がありません", "port is missing"));
                    portText = port1.ToString();
                    ApplyPort();
                    return null;
                }
                if (!q.ContainsKey("index"))   // ポートで(なければ作る。画面のポートの欄を変えたのと同じ)
                {
                    int port = (int)Fq(q, "port").GetValueOrDefault(0);
                    if (port <= 0 || port >= 65536) throw new Exception("port がありません");
                    return Wait(() => SwitchToPort(port), () => switchTo < 0 && !starting && sender.Port == port, res);
                }
                int i = (int)Fq(q, "index").GetValueOrDefault(-1);
                if (i < 0 || i >= slots.Count) throw new Exception("その相手はありません");
                return Wait(() => { SwitchTarget(i); }, () => switchTo < 0 && cur == i, res);
            }

            case "vrm":   // {"path": "..."}
            {
                var p = S(q, "path");
                if (p == null || !File.Exists(p)) throw new Exception("VRM がありません: " + p);
                return Wait(() => LoadVrm(p), () => !loading, res);
            }

            case "anim":   // {"path": ".anim か .json", "time": 秒 | "frame": n | "end": true, "send": true}
            case "pose":   // {"path": ".json"} か {"values": {"名前": 値, ...}}(一部なら重ねる)、"send"
            {
                bool okc;
                if (q.TryGetValue("values", out var vo) && vo is Dictionary<string, object> vd)
                {
                    var vals = vd.Where(kv => kv.Value is double).ToDictionary(kv => kv.Key, kv => (float)(double)kv.Value);
                    okc = UseClip(PoseFile.FromValues(S(q, "name") ?? "(values)", vals));
                }
                else
                {
                    var p = S(q, "path");
                    if (p == null || !File.Exists(p)) throw new Exception("ファイルがありません: " + p);
                    okc = OpenClip(p);
                }
                if (!okc) throw new Exception(status);
                if (Fq(q, "frame") is float fr) time = Mathf.Clamp(fr / clip.SampleRate, 0, clip.Length);
                else if (Fq(q, "time") is float tt) time = Mathf.Clamp(tt, 0, clip.Length);
                else time = clip.Length;
                if (Bq(q, "send")) SendNow(Bq(q, "all"));
                res["length"] = clip.Length; res["time"] = time; res["partial"] = baseMuscles != null;
                return null;
            }

            case "time":   // {"time": 秒} か {"frame": n}
                if (clip == null) throw new Exception("アニメーションがありません");
                playing = false;
                if (Fq(q, "frame") is float f2) time = Mathf.Clamp(f2 / clip.SampleRate, 0, clip.Length);
                else time = Mathf.Clamp(Fq(q, "time") ?? clip.Length, 0, clip.Length);
                return null;

            case "send":   // {"all": true} で全部送り直す
                SendNow(Bq(q, "all"));
                return null;

            case "on":
                poseOn = true; sender.SetOn(true); status = T("任意ポーズ ON", "Pose ON");
                return null;
            case "off":
                poseOn = false; sender.SetOn(false); status = T("任意ポーズ OFF", "Pose OFF");
                return null;

            case "body":
            {
                // 足す分: {"y":..,"x":..,"z":..(±1 = ±1.5),"yaw":..,"pitch":..,"roll":..(度)}。
                // "mode": "offset"(既定。足す分をその値に)・"add"(足す分に足す)・"abs"(元のポーズ + 足す分 = その値になるように)。"reset": true で 0
                if (Bq(q, "reset")) offset = new PoseParams.Body();
                var mode = S(q, "mode") ?? "offset";
                Underlying(out _, out var ub);
                float Set(string key, float old, float under)
                {
                    if (!(Fq(q, key) is float v)) return old;
                    return mode == "add" ? old + v : mode == "abs" ? v - under : v;
                }
                offset.Y = Set("y", offset.Y, ub.Y); offset.X = Set("x", offset.X, ub.X); offset.Z = Set("z", offset.Z, ub.Z);
                offset.Yaw = Set("yaw", offset.Yaw, ub.Yaw); offset.Pitch = Set("pitch", offset.Pitch, ub.Pitch); offset.Roll = Set("roll", offset.Roll, ub.Roll);
                if (Bq(q, "send")) SendNow(false);
                res["body"] = BodyJson();
                return null;
            }

            case "grab":
                hold.Grab(sender.Port, myWindow);
                return null;
            case "release":
                hold.Release(sender.Port);
                return null;
            case "spin":   // {"axis": "SpinHoldCwCcw"|"SpinHoldUD"|"SpinHoldLR"|"MoveHoldFB", "sign": 1|-1, "count": n}
            {
                var axis = S(q, "axis");
                if (!HoldInput.Axes.Contains(axis)) throw new Exception("axis は " + string.Join("・", HoldInput.Axes));
                int sign = Fq(q, "sign") is float sg && sg < 0 ? -1 : 1;
                int count = Mathf.Max(1, (int)(Fq(q, "count") ?? 1));
                return Spin(axis, sign, count);
            }

            case "ik":   // {"side": "R"|"L", "move": [右, 上, 前](cm), "rot": [["x", 度], ...], "elbow": 度, "grip": true, "reset": true}
            {
                if (handler == null) throw new Exception("VRM がありません");
                if (ikBusy) throw new Exception("手の計算の途中です");
                ikSelected = true;
                ikLeft = (S(q, "side") ?? "R").ToUpperInvariant().StartsWith("L");
                IkPress(o =>
                {
                    if (Bq(q, "reset")) { o.Reset = true; return; }
                    if (q.TryGetValue("move", out var mv) && mv is List<object> ml && ml.Count == 3)
                        o.MoveCm += new Vector3(Convert.ToSingle(ml[0]), Convert.ToSingle(ml[1]), Convert.ToSingle(ml[2]));
                    if (q.TryGetValue("rot", out var ro) && ro is List<object> rl)
                        foreach (var r in rl.OfType<List<object>>())
                            if (r.Count == 2) o.Rot.Add((Convert.ToString(r[0])[0], Convert.ToSingle(r[1])));
                    if (Fq(q, "elbow") is float el) o.Elbow += el;
                    if (Bq(q, "grip")) o.Grip++;
                });
                return Wait(null, () => !ikBusy, res);
            }

            case "values":   // 今の姿勢(pose_send の .json と同じ形)と、送り終えた値
            {
                Current(out var m, out var b);
                res["values"] = PoseFile.ToValues(m, b).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                return null;
            }

            case "save":   // {"path": "....json"} 今の姿勢をポーズの .json に
            {
                var p = S(q, "path") ?? throw new Exception("path がありません");
                if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new Exception("保存できるのは .json だけです");
                Current(out var m, out var b);
                File.WriteAllText(p, Json(PoseFile.ToValues(m, b).ToDictionary(kv => kv.Key, kv => (object)kv.Value), true));
                return null;
            }

            case "settings":   // cycle・cyclePair・valWait・idxWait・gap・noZero・autoSend・advanced・inPlace・topmost
            {
                if (q.ContainsKey("cycle")) sender.CycleOn = Bq(q, "cycle");
                if (Fq(q, "cyclePair") is float cp) sender.CyclePair = Mathf.Max(0.05f, cp);
                if (Fq(q, "valWait") is float vw) sender.ValWait = vw;
                if (Fq(q, "idxWait") is float iw) sender.IdxWait = iw;
                if (Fq(q, "gap") is float g) sender.Gap = g;
                if (q.ContainsKey("noZero")) sender.NoZero = Bq(q, "noZero");
                if (q.ContainsKey("autoSend")) { autoSend = Bq(q, "autoSend"); if (autoSend) { autoPaused = false; lastQueued = lastValues; } }
                if (q.ContainsKey("advanced")) advanced = Bq(q, "advanced");
                if (q.ContainsKey("inPlace")) inPlace = Bq(q, "inPlace");
                if (q.ContainsKey("topmost")) { topmost = Bq(q, "topmost"); WinNative.SetTopmost(topmost); }
                WaitTexts();
                CopySenderSettings();
                SavePrefs();
                return null;
            }

            case "front":   // この窓を前面に
                WinNative.BringToFront(myWindow);
                return null;

            case "shot":   // {"path": "....png"} この窓を撮る。path がなければ PNG を base64 で返す("png")。"view": front|right|left|back
            {
                var p = S(q, "path");
                if (p != null && !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new Exception("撮った画像は .png に");
                if (S(q, "view") is string v) ResetView(v);
                return Shot(p, res);
            }

            case "stop":   // 全部の相手の、まだ送っていない分をやめる(巡回は続く)
                if (Multi) foreach (var sl in slots) (sl.Sender == sender ? sender : sl.Sender).Cancel();
                else sender.Cancel();
                return null;

            case "quit":
                Application.Quit();
                return null;

            default:
                throw new Exception("知らないコマンドです: " + cmd);
        }
    }

    IEnumerator Wait(Action start, Func<bool> done, Dictionary<string, object> res)
    {
        start?.Invoke();
        yield return null;
        while (!done()) yield return null;
        res["status"] = status;
    }

    IEnumerator Spin(string axis, int sign, int count)
    {
        for (int i = 0; i < count; i++)
        {
            while (hold.Pulsing) yield return null;
            hold.Pulse(sender.Port, axis, sign);
            yield return null;
            while (hold.Pulsing) yield return null;   // 1 回 0.2 秒ほど
        }
    }

    IEnumerator Shot(string path, Dictionary<string, object> res)
    {
        yield return null;   // 姿勢・視点を当て直してから
        yield return new WaitForEndOfFrame();
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            var png = tex.EncodeToPNG();
            if (path != null) File.WriteAllBytes(path, png);
            else res["png"] = Convert.ToBase64String(png);
            res["width"] = tex.width; res["height"] = tex.height;
        }
        finally { Destroy(tex); }
    }

    Dictionary<string, object> BodyJson()
    {
        Current(out _, out var b);
        return new Dictionary<string, object>
        {
            ["offset"] = new Dictionary<string, object> { ["y"] = offset.Y, ["x"] = offset.X, ["z"] = offset.Z, ["yaw"] = offset.Yaw, ["pitch"] = offset.Pitch, ["roll"] = offset.Roll },
            ["total"] = new Dictionary<string, object> { ["y"] = b.Y, ["x"] = b.X, ["z"] = b.Z, ["yaw"] = b.Yaw, ["pitch"] = b.Pitch, ["roll"] = b.Roll },
        };
    }

    Dictionary<string, object> SlotJson(int port, string label, string vrmP, AnimClip c, float t, bool on, OscSender snd, HoldInput hd, HandIK k, bool current)
    {
        var (done, total) = snd.Progress;
        return new Dictionary<string, object>
        {
            ["port"] = port, ["label"] = label, ["current"] = current,
            ["vrm"] = vrmP, ["anim"] = c?.Path, ["time"] = t, ["length"] = c?.Length ?? 0f,
            ["poseOn"] = on, ["sending"] = snd.Busy, ["done"] = done, ["total"] = total, ["cycling"] = snd.Cycling,
            ["holding"] = hd.Holding, ["ikActive"] = k.Active, ["error"] = snd.LastError,
            ["grabSeq"] = hd.GrabSeq, ["spinLog"] = hd.SpinLog.Select(e => (object)new List<object> { e.axis, e.n }).ToList(),
        };
    }

    Dictionary<string, object> StateJson()
    {
        var list = new List<object>();
        if (Multi)
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                list.Add(i == cur ? SlotJson(sender.Port, s.Label, vrmPath, clip, time, poseOn, sender, hold, ik, true)
                                  : SlotJson(s.Port, s.Label, s.VrmPath, s.Clip, s.Time, s.PoseOn, s.Sender, s.Hold, s.Ik, false));
            }
        else list.Add(SlotJson(sender.Port, sender.Port.ToString(), vrmPath, clip, time, poseOn, sender, hold, ik, true));
        return new Dictionary<string, object>
        {
            ["version"] = Version, ["target"] = Multi ? cur : 0, ["targets"] = list,
            ["loading"] = loading || starting, ["ikBusy"] = ikBusy, ["partial"] = baseMuscles != null, ["base"] = baseName,
            ["body"] = BodyJson(), ["autoSend"] = autoSend, ["autoPaused"] = autoPaused, ["advanced"] = advanced,
            ["status"] = status,
        };
    }

    // 小さな JSON の書き出し
    static string Json(object o, bool pretty = false)
    {
        var sb = new StringBuilder();
        Write(sb, o, pretty, 0);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object o, bool pretty, int depth)
    {
        switch (o)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s:
                sb.Append('"');
                foreach (var ch in s)
                {
                    if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                    else if (ch == '\n') sb.Append("\\n");
                    else if (ch == '\r') sb.Append("\\r");
                    else if (ch == '\t') sb.Append("\\t");
                    else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                }
                sb.Append('"');
                break;
            case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : Math.Round(f, 5).ToString("R", CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture)); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case IDictionary<string, object> dict:
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) sb.Append('\n').Append(' ', depth + 1);
                    Write(sb, kv.Key, pretty, depth + 1);
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, pretty, depth + 1);
                }
                if (pretty && !first) sb.Append('\n').Append(' ', depth);
                sb.Append('}');
                break;
            }
            case System.Collections.IEnumerable list:
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, x, pretty, depth + 1);
                }
                sb.Append(']');
                break;
            }
            default: sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
        }
    }
}
