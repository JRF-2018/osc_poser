// 物を持つ(上級機能、0.3.0。panel.py の ✊ ✋ と ✥ の回すボタンから)。どれも VRChat の OSC の入力(/input/...)。
// - 持つ: GrabRight を押しっぱなしにし、1 秒ごとに 1 を送り直す(持ち続ける)。VRChat の窓が前面でないと持てないので、
//   OSC の受信ポートから VRChat の窓を探して前面に出し、持ったら(1 秒後)この窓に戻る。窓が見つからなければ
//   3 秒待つので、その間に VRChat の窓をクリックする。照準は物に合わせておく。デスクトップで持てるのは右手だけ。
// - 離す: GrabRight を 0 にして DropRight を押す。
// - 回す・MoveHoldFB: 1 回 = 値 0.3 を 0.1 秒 → 0 を 0.1 秒(約 4.6 度)。押している間くり返す(すぐ離しても 1 回)。
//   SpinHold は VRChat の窓が後ろにあっても効く。MoveHoldFB は 2026-11 のデスクトップ版では効かない(VRChat の更新で効く日のため)。
using System;
using System.Threading;

public class HoldInput
{
    readonly OscSender osc;
    public HoldInput(OscSender osc) { this.osc = osc; }

    public const float PulseValue = 0.3f, PulseOn = 0.1f, PulseOff = 0.1f;
    public static readonly string[] Axes = { "SpinHoldCwCcw", "SpinHoldUD", "SpinHoldLR", "MoveHoldFB" };

    volatile bool keeping;
    Thread keepThread;
    int keepPort;
    public bool Holding => keeping;
    public volatile string Message;   // 画面の状態の行に出す(別のスレッドから)

    // 持つ。me = この窓(戻るため。メインスレッドで取っておく)
    public void Grab(int port, IntPtr me)
    {
        if (keeping) return;
        keeping = true;
        keepPort = port;
        keepThread = new Thread(() => KeepLoop(port, me)) { IsBackground = true, Name = "OscPoser grab" };
        keepThread.Start();
    }

    void KeepLoop(int port, IntPtr me)
    {
        try
        {
            var vrc = WinNative.FindWindowByUdpPort(port);
            bool front = vrc != IntPtr.Zero && WinNative.BringToFront(vrc);
            if (front) Thread.Sleep(300);
            else
            {
                for (int i = 3; i > 0 && keeping; i--)
                {
                    Message = PoserApp.T($"VRChat の窓が見つかりません。{i} 秒後に持ちます(VRChat の窓をクリックして、照準を物に)",
                                         $"VRChat's window not found. Grabbing in {i} s (click the VRChat window, aim at the object)");
                    Thread.Sleep(1000);
                }
            }
            if (!keeping) return;
            Message = PoserApp.T("持って、持ち続けます(離すのは「離す」)", "Grabbing and holding (press Release to let go)");
            bool first = true;
            while (keeping)
            {
                osc.Input(port, "GrabRight", 1);
                for (int i = 0; i < 10 && keeping; i++) Thread.Sleep(100);
                if (first && front && me != IntPtr.Zero) WinNative.BringToFront(me);   // 持つ(最初の押し)が済んでから戻る
                first = false;
            }
        }
        finally
        {
            osc.Input(port, "GrabRight", 0);
        }
    }

    public void Release(int port)
    {
        bool was = keeping;
        keeping = false;
        int p = was ? keepPort : port;
        new Thread(() =>
        {
            keepThread?.Join(1500);
            osc.Input(p, "GrabRight", 0);
            osc.Input(p, "DropRight", 1);
            Thread.Sleep(200);
            osc.Input(p, "DropRight", 0);
            Message = PoserApp.T("離しました", "Released");
        }) { IsBackground = true }.Start();
    }

    // 回す・MoveHoldFB(押している間)。held は押しているかどうか(メインスレッドが書き換える)
    volatile bool pulsing;
    public volatile string HeldAxis;   // 今押しているボタンの軸(+/− 付き)。null で離した
    public void Pulse(int port, string axis, int sign)
    {
        if (pulsing) return;
        pulsing = true;
        var key = axis + (sign > 0 ? "+" : "-");
        new Thread(() =>
        {
            try
            {
                float v = PulseValue * sign;
                do
                {
                    osc.Input(port, axis, v);
                    Thread.Sleep((int)(PulseOn * 1000));
                    osc.Input(port, axis, 0f);
                    Thread.Sleep((int)(PulseOff * 1000));
                } while (HeldAxis == key);
                osc.Input(port, axis, 0f);
            }
            finally { pulsing = false; }
        }) { IsBackground = true, Name = "OscPoser spin" }.Start();
    }

    public static string Key(string axis, int sign) => axis + (sign > 0 ? "+" : "-");
}
