// VRChat への OSC の送信と、任意ポーズのシリアル送信。
//
// 1 組 = OscPoser/Val = 値 → (ValWait 待つ) → OscPoser/Idx = 番号 → (IdxWait 待つ) → [NoZero でなければ Idx = 0 → (Gap 待つ)]。
// 待つのは、ほかの人(カメラ役)の側に同期で届くのを待つため。送るのは別のスレッドで、
// 送っている途中に値が変われば、まだ送っていない分は最後の値に置き換わる(同じ番号を何度も送らない)。
// 送った値は控え(Sent)に残し、次からは変わった値だけを送る(SendChanged)。
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

    readonly UdpClient udp = new UdpClient();
    readonly object gate = new object();
    readonly SortedDictionary<int, float> pending = new SortedDictionary<int, float>();
    readonly Dictionary<int, float> sent = new Dictionary<int, float>();
    readonly Thread worker;
    volatile bool quit;
    bool turnOnAfter;
    int total, done;
    public string LastError;

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
        lock (gate) sent.Clear();
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
            int idx; float v;
            lock (gate)
            {
                while (pending.Count == 0 && !quit) Monitor.Wait(gate);
                if (quit) return;
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
                if (pending.Count == 0 && turnOnAfter)
                {
                    turnOnAfter = false;
                    SendBool("On", true);
                }
            }
        }
    }

    static void Sleep(float s)
    {
        if (s > 0) Thread.Sleep((int)(s * 1000));
    }

    // ---- OSC のメッセージ(アドレス、型、値。どれも 4 バイト区切り、数は big endian)----

    void SendFloat(string name, float v) => Raw(name, ",f", BitConverter.GetBytes(v));
    void SendInt(string name, int v) => Raw(name, ",i", BitConverter.GetBytes(v));
    void SendBool(string name, bool v) => Raw(name, v ? ",T" : ",F", null);

    void Raw(string name, string tag, byte[] arg)
    {
        var buf = new List<byte>();
        Pad(buf, Encoding.ASCII.GetBytes(Prefix + name));
        Pad(buf, Encoding.ASCII.GetBytes(tag));
        if (arg != null)
        {
            if (BitConverter.IsLittleEndian) Array.Reverse(arg);
            buf.AddRange(arg);
        }
        var data = buf.ToArray();
        try { udp.Send(data, data.Length, Host, Port); }
        catch (Exception ex) { LastError = ex.Message; }
    }

    static void Pad(List<byte> buf, byte[] s)
    {
        buf.AddRange(s);
        int n = 4 - s.Length % 4;   // 終わりの 0 を少なくとも 1 つ
        for (int i = 0; i < n; i++) buf.Add(0);
    }
}
