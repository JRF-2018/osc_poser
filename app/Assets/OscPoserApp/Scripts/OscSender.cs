// VRChat への OSC の送信と、任意ポーズのシリアル送信。
//
// 1 組 = OscPoser/Val = 値 → (ValWait 待つ) → OscPoser/Idx = 番号 → (IdxWait 待つ) → [NoZero でなければ Idx = 0 → (Gap 待つ)]。
// 待つのは、ほかの人(カメラ役)の側に同期で届くのを待つため。送るのは別のスレッドで、
// 送っている途中に値が変われば、まだ送っていない分は最後の値に置き換わる(同じ番号を何度も送らない)。
// 送った値は控え(Sent)に残し、次からは変わった値だけを送る(SendChanged)。
//
// 巡回送信(0.3.0、panel.py の ♙ 巡回から): 送るものがない間も、控え(送り終えた値。編集中の値ではない)を 1 組ずつ
// 順番をばらして送り直し続ける。取りこぼした値や、後から入った人の側の値が自然に直る。控えは起動したときは空なので、
// 最初の送信のあとから始まる。任意ポーズが OFF の間も回す(OFF の間に入った人も、ON にした瞬間から正しいポーズを見られる)。
// 新しい送信が優先(巡回は送るものがないときだけ)。1 組ごとに Idx を 0 に戻す。
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class OscSender : IDisposable
{
    public const string Prefix = "/avatar/parameters/OscPoser/";

    public string Host = "127.0.0.1";
    public int Port = 9000;
    public float ValWait = 0.025f, IdxWait = 0.1f, Gap = 0.1f;
    public bool NoZero = true;
    public bool CycleOn = true;
    public float CyclePair = 0.3f;   // 巡回の 1 組の間隔(秒。少しばらつかせる)

    readonly UdpClient udp = new UdpClient();
    readonly object gate = new object();
    readonly SortedDictionary<int, float> pending = new SortedDictionary<int, float>();
    readonly Dictionary<int, float> sent = new Dictionary<int, float>();
    readonly Thread worker;
    volatile bool quit;
    bool turnOnAfter;
    int total, done;
    public string LastError;
    readonly List<int> cycleOrder = new List<int>();
    readonly Random rnd = new Random();
    float cycleWait;
    volatile bool cycling;
    public bool Cycling => cycling;

    public OscSender()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "OscPoser send" };
        worker.Start();
    }

    public void Dispose()
    {
        quit = true;
        lock (gate) Monitor.PulseAll(gate);
        udp.Dispose();
    }

    // 送っている途中か、と進み具合(送った数 / 全体)
    public bool Busy { get { lock (gate) return pending.Count > 0 || inFlight; } }
    public (int done, int total) Progress { get { lock (gate) return (done, total); } }
    bool inFlight;

    // 値(-1〜1 に収めたもの)を送る。changedOnly なら、控えと同じ値(同期の細かさ 1/127 の範囲)は送らない。
    // onAfter なら、送り終わってから On を送る(任意ポーズが一度に完成した姿で始まるように)。
    public int Send(IDictionary<int, float> values, bool changedOnly, bool onAfter)
    {
        int n = 0;
        lock (gate)
        {
            if (pending.Count == 0 && !inFlight) { total = 0; done = 0; }
            foreach (var kv in values)
            {
                var v = Math.Max(-1f, Math.Min(1f, kv.Value));
                if (changedOnly && !pending.ContainsKey(kv.Key) && sent.TryGetValue(kv.Key, out var old) && Math.Abs(old - v) < 0.5f / 127f)
                    continue;
                if (!pending.ContainsKey(kv.Key)) total++;
                pending[kv.Key] = v;
                n++;
            }
            if (onAfter) turnOnAfter = true;
            if (n == 0 && turnOnAfter && !inFlight) { turnOnAfter = false; SendBool("On", true); }
            Monitor.PulseAll(gate);
        }
        return n;
    }

    // 控えを消す(アバターを着替えた・読み直したときなど。次の送信は全部送る)
    public void Forget()
    {
        lock (gate) { sent.Clear(); cycleOrder.Clear(); }
    }

    // まだ送っていない分をやめる
    public void Cancel()
    {
        lock (gate) { pending.Clear(); turnOnAfter = false; }
    }

    public void SetOn(bool on)
    {
        if (!on) lock (gate) turnOnAfter = false;
        SendBool("On", on);
    }

    void Run()
    {
        while (!quit)
        {
            int idx = 0; float v = 0; bool cyc = false;
            lock (gate)
            {
                while (pending.Count == 0 && !quit)
                {
                    if (CycleOn && sent.Count > 0)
                    {
                        cycling = true;
                        if (!Monitor.Wait(gate, (int)(cycleWait * 1000)) && pending.Count == 0 && CycleOn && NextCycle(out idx, out v))
                        {
                            cyc = true;
                            break;
                        }
                    }
                    else
                    {
                        cycling = false;
                        Monitor.Wait(gate, 500);
                    }
                }
                if (quit) return;
            }
            if (cyc)
            {
                float pair = Math.Max(0.05f, CyclePair);
                SendFloat("Val", v);
                Sleep(pair * 0.2f);
                SendInt("Idx", idx);
                Sleep(pair * 0.4f);
                SendInt("Idx", 0);
                lock (gate) cycleWait = pair * (0.3f + 0.3f * (float)rnd.NextDouble());   // 0 のあと。ばらつかせる
                continue;
            }
            lock (gate)
            {
                cycleOrder.Clear();   // 新しい値を送ったら、巡回は新しい周から
                using (var e = pending.GetEnumerator()) { e.MoveNext(); idx = e.Current.Key; v = e.Current.Value; }
                pending.Remove(idx);
                inFlight = true;
            }
            try
            {
                SendFloat("Val", v);
                Sleep(ValWait);
                SendInt("Idx", idx);
                Sleep(IdxWait);
                bool last;
                // 最後か、送っている間に同じ番号の値が変わったとき(番号が同じままだと写し直されない)は 0 を挟む
                lock (gate) last = pending.Count == 0 || pending.ContainsKey(idx);
                if (!NoZero || last)
                {
                    SendInt("Idx", 0);
                    Sleep(Gap);
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            lock (gate)
            {
                sent[idx] = v;
                done++;
                inFlight = false;
                cycleWait = CyclePair;   // 送り終えたら、少し置いてから巡回
                if (pending.Count == 0 && turnOnAfter)
                {
                    turnOnAfter = false;
                    SendBool("On", true);
                }
            }
        }
    }

    // 巡回の次の 1 組(gate の中で呼ぶ)。1 周ごとに順番をばらす
    bool NextCycle(out int idx, out float v)
    {
        idx = 0; v = 0;
        for (int guard = 0; guard < 2; guard++)
        {
            if (cycleOrder.Count == 0)
            {
                cycleOrder.AddRange(sent.Keys);
                for (int i = cycleOrder.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); (cycleOrder[i], cycleOrder[j]) = (cycleOrder[j], cycleOrder[i]); }
            }
            while (cycleOrder.Count > 0)
            {
                idx = cycleOrder[cycleOrder.Count - 1];
                cycleOrder.RemoveAt(cycleOrder.Count - 1);
                if (sent.TryGetValue(idx, out v)) return true;
            }
        }
        return false;
    }

    static void Sleep(float s)
    {
        if (s > 0) Thread.Sleep((int)(s * 1000));
    }

    // ---- OSC のメッセージ(アドレス、型、値。どれも 4 バイト区切り、数は big endian)----

    void SendFloat(string name, float v) => Raw(Prefix + name, ",f", BitConverter.GetBytes(v), Port);
    void SendInt(string name, int v) => Raw(Prefix + name, ",i", BitConverter.GetBytes(v), Port);
    void SendBool(string name, bool v) => Raw(Prefix + name, v ? ",T" : ",F", null, Port);

    // VRChat の入力(/input/名前。持つ・離す・物を回すなど)。port を決めて送る(送っている途中にポートを変えても、始めた相手に)
    public void Input(int port, string name, int v) => Raw("/input/" + name, ",i", BitConverter.GetBytes(v), port);
    public void Input(int port, string name, float v) => Raw("/input/" + name, ",f", BitConverter.GetBytes(v), port);

    void Raw(string address, string tag, byte[] arg, int port)
    {
        var buf = new List<byte>();
        Pad(buf, Encoding.ASCII.GetBytes(address));
        Pad(buf, Encoding.ASCII.GetBytes(tag));
        if (arg != null)
        {
            if (BitConverter.IsLittleEndian) Array.Reverse(arg);
            buf.AddRange(arg);
        }
        var data = buf.ToArray();
        try { udp.Send(data, data.Length, Host, port); }
        catch (Exception ex) { LastError = ex.Message; }
    }

    static void Pad(List<byte> buf, byte[] s)
    {
        buf.AddRange(s);
        int n = 4 - s.Length % 4;   // 終わりの 0 を少なくとも 1 つ
        for (int i = 0; i < n; i++) buf.Add(0);
    }
}
