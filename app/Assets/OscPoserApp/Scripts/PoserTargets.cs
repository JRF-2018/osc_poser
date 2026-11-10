// 送り先(ポート)ごとに VRM・ポーズなどを持つ(0.4.0、panel.py からの取り込み)。
//   起動オプション: -targets 9000,9010,9020  [-target 番号(0 から)]。画面は今までどおりで、ポートの欄を変えると、
//   そのポートの VRM・ポーズなどに切り替わる(なければ、そのポートで前に開いたものを開く)。JRF の指示で、相手のボタンは出さない
//   (一般の人には見せない機能。panel.py の 0・1・2 と API で連動する)。-labels は読むが使わない。
// 相手ごとに VRM・アニメーション・時刻・重ねた姿勢・体の足す分・手の状態(☛)・送信(OscSender: 巡回もそれぞれ)・物を持つ・
// 視点を持つ。切り替えるときは今の相手の値を Slot にしまい、次の相手の値を出す(ほかのコードは今までどおり 1 体ぶんの欄を使う)。
// 隠した相手の VRM は SetActive(false) で見えなくするだけ(読み直さない)。巡回送信・持ち続けるのは隠れていても続く。
// 最後に開いたファイルなどは、ポートごとのキー(-port N と同じ)に覚える。
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UniVRM10;
using UnityEngine;

public partial class PoserApp
{
    class Slot
    {
        public int Port;
        public string Label;
        public Vrm10Instance Vrm;
        public HumanPoseHandler Handler;
        public HumanPose Pose;
        public string VrmPath;
        public float ModelHeight = 1.5f;
        public AnimClip Clip;
        public float Time;
        public bool Playing;
        public float[] BaseMuscles;
        public PoseParams.Body BaseBody;
        public string BaseName;
        public HandIK Ik = new HandIK();
        public bool IkLeft, IkSelected;
        public float[] IkRef;
        public PoseParams.Body Offset;
        public OscSender Sender;
        public HoldInput Hold;
        public bool PoseOn, AutoPaused = true;
        public Dictionary<int, float> LastValues, LastQueued;
        public float LastChange;
        public Vector3 CamTarget;
        public float Yaw = 180, Pitch = 5, Dist = 3;
        public bool ViewSet;
        public bool NeedLoad;   // 後から作った相手: 切り替えたとき、そのポートで前に開いたものを開く
    }

    List<Slot> slots;   // null = 1 体だけ(今までどおり)
    int cur;
    int switchTo = -1;  // 読み込み・計算の途中に頼まれた切り替え(終わってから)
    bool Multi => slots != null;

    // -targets を読む(Awake から)。ポートが 2 つ以上なら相手ごとの欄を作る
    void ParseTargets()
    {
        var t = Arg("targets");
        if (string.IsNullOrEmpty(t)) return;
        var ports = t.Split(',').Select(s => int.TryParse(s.Trim(), out var p) ? p : 0).Where(p => p > 0 && p < 65536).ToList();
        if (ports.Count == 0) return;
        var labels = (Arg("labels") ?? "").Split(',').Select(s => s.Trim()).ToList();
        slots = new List<Slot>();
        for (int i = 0; i < ports.Count; i++)
            slots.Add(NewSlot(ports[i], i < labels.Count && labels[i] != "" ? labels[i] : ports[i].ToString()));
    }

    Slot NewSlot(int port, string label)
    {
        var s = new Slot { Port = port, Label = label };
        s.Sender = new OscSender { Port = port };
        s.Hold = new HoldInput(s.Sender);
        return s;
    }

    // ポートの欄・API から: そのポートの相手に(なければ作る)
    void SwitchToPort(int port)
    {
        int i = SlotIndexOfPort(port);
        if (i < 0)
        {
            var s = NewSlot(port, port.ToString());
            s.NeedLoad = true;
            slots.Add(s);
            CopySenderSettings();
            i = slots.Count - 1;
        }
        SwitchTarget(i);
    }

    IEnumerator LoadNewSlot()
    {
        starting = true;
        yield return StartCoroutine(LoadRemembered(false));
        starting = false;
    }

    // 送る速さなどの設定は、全部の相手で同じ
    void CopySenderSettings()
    {
        if (!Multi) return;
        foreach (var s in slots)
        {
            if (s.Sender == sender) continue;
            s.Sender.ValWait = sender.ValWait; s.Sender.IdxWait = sender.IdxWait; s.Sender.Gap = sender.Gap;
            s.Sender.NoZero = sender.NoZero; s.Sender.CycleOn = sender.CycleOn; s.Sender.CyclePair = sender.CyclePair;
        }
    }

    void Stash(Slot s)
    {
        s.Vrm = vrm; s.Handler = handler; s.Pose = pose; s.VrmPath = vrmPath; s.ModelHeight = modelHeight;
        s.Clip = clip; s.Time = time; s.Playing = playing;
        s.BaseMuscles = baseMuscles; s.BaseBody = baseBody; s.BaseName = baseName;
        s.Ik = ik; s.IkLeft = ikLeft; s.IkSelected = ikSelected; s.IkRef = ikRef;
        s.Offset = offset; s.Sender = sender; s.Hold = hold;
        s.PoseOn = poseOn; s.AutoPaused = autoPaused;
        s.LastValues = lastValues; s.LastQueued = lastQueued; s.LastChange = lastChange;
        s.CamTarget = target; s.Yaw = yaw; s.Pitch = pitch; s.Dist = dist; s.ViewSet = true;
        if (vrm != null) vrm.gameObject.SetActive(false);
    }

    void Unstash(Slot s)
    {
        vrm = s.Vrm; handler = s.Handler; pose = s.Pose; vrmPath = s.VrmPath; modelHeight = s.ModelHeight;
        clip = s.Clip; time = s.Time; playing = s.Playing;
        baseMuscles = s.BaseMuscles; baseBody = s.BaseBody; baseName = s.BaseName;
        ik = s.Ik; ikLeft = s.IkLeft; ikSelected = s.IkSelected; ikRef = s.IkRef;
        ikPending = null;
        offset = s.Offset; sender = s.Sender; hold = s.Hold;
        poseOn = s.PoseOn; autoPaused = s.AutoPaused;
        lastValues = s.LastValues; lastQueued = s.LastQueued; lastChange = s.LastChange;
        portArg = s.Port;
        portText = s.Port.ToString();
        if (vrm != null) vrm.gameObject.SetActive(true);
        if (s.ViewSet) { target = s.CamTarget; yaw = s.Yaw; pitch = s.Pitch; dist = s.Dist; }
        else ResetView("front");
    }

    // 相手を替える。読み込み・計算の途中なら、終わってから(Update で)
    void SwitchTarget(int i)
    {
        if (!Multi || i < 0 || i >= slots.Count) return;
        if (loading || ikBusy || starting) { switchTo = i; return; }
        switchTo = -1;
        if (i == cur) return;
        SavePrefs();   // 今の相手の最後のファイル・手の状態を覚える
        Stash(slots[cur]);
        cur = i;
        Unstash(slots[cur]);
        status = T("送り先: ", "Sending to: ") + $"127.0.0.1:{slots[cur].Port}";
        if (slots[cur].NeedLoad) { slots[cur].NeedLoad = false; StartCoroutine(LoadNewSlot()); }
    }

    // API の "port" のため: 設定を書かず、状態の行も変えずに切り替える
    void QuickSwitch(int i)
    {
        var st = status;
        Stash(slots[cur]);
        cur = i;
        Unstash(slots[cur]);
        status = st;
    }

    void UpdateSwitch()
    {
        if (switchTo >= 0 && !loading && !ikBusy && !starting) SwitchTarget(switchTo);
    }

    int SlotIndexOfPort(int port) => Multi ? slots.FindIndex(s => s.Port == port) : -1;

    // 起動したとき: 相手ごとに、前に開いた VRM・アニメ・時刻・重ねた姿勢・手の状態を戻す
    IEnumerator LoadAllSlots()
    {
        for (int i = 0; i < slots.Count; i++)
        {
            cur = i;
            Unstash(slots[i]);
            yield return StartCoroutine(LoadRemembered(false));
            Stash(slots[i]);
        }
        int first = 0;
        if (Arg("target") != null && int.TryParse(Arg("target"), out var ft) && ft >= 0 && ft < slots.Count) first = ft;
        cur = first;
        Unstash(slots[cur]);
        status = T("送り先: ", "Sending to: ") + $"127.0.0.1:{slots[cur].Port}";
    }

    // 前に開いたもの(ポートごとのキー)を開く。useArgs なら -vrm・-anim・-time を優先
    IEnumerator LoadRemembered(bool useArgs)
    {
        var path = (useArgs ? Arg("vrm") : null) ?? PrefString("vrm");
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) LoadVrm(path);
        else status = T("VRM(.vrm)をこの窓にドロップしてください", "Drop a VRM (.vrm) file onto this window");
        while (loading) yield return null;

        var animArg = useArgs ? Arg("anim") : null;
        var anim = animArg ?? PrefString("anim");
        if (!string.IsNullOrEmpty(anim) && File.Exists(anim) && OpenClipAny(anim))
        {
            var t = useArgs && Arg("time") != null ? float.Parse(Arg("time"), CultureInfo.InvariantCulture)
                : animArg == null ? PlayerPrefs.GetFloat(PortKey("time"), PlayerPrefs.GetFloat("time", clip.Length)) : clip.Length;
            time = Mathf.Clamp(t, 0, clip.Length);
            if (animArg == null) RestoreBase();   // 重ねがけの下の姿勢も戻す
        }
        yield return null;
        RestoreIk();
    }

    void DisposeSlots()
    {
        if (!Multi) return;
        foreach (var s in slots)
        {
            if (s.Sender != sender) s.Sender?.Dispose();
            if (s.Handler != handler) s.Handler?.Dispose();
        }
    }
}
