// OscPoser: VRM にアニメーション(.anim)のある時刻の姿勢を当てて見て、その姿勢を VRChat に OSC で送る。
// アバターには Assets/OscPoser の prefab(Modular Avatar)を入れておく。
// 姿勢は HumanPoseHandler.SetHumanPose で当てるので、Unity(VRChat)の筋肉の計算そのまま。
//
// 起動オプション(どれも省略可): -port N  -vrm FILE  -anim FILE  -time 秒  -lang ja|en  -advanced 0|1
//   -targets 9000,9010,9020 [-target i](ポートごとに VRM・ポーズなどを持つ窓)  -api [PORT](127.0.0.1 の HTTP・MCP で操作を受ける。既定 9330)  -help(/?)
//   (試し用) -shot FILE.png  -wait 秒(撮る前)  -quit  -nosave  -settings(設定を開く)  -send(読み込んだら送る)
//            -overlay FILE(-anim の上に重ねる .anim)  -grabtest(持つ → 5 秒 → 回す 1 回 → 離す)
//            -ikops "R m0,10,15; R h x45; L e15; R g; R r"(☛ の操作: m 手首を動かす cm、h 軸 度 で回す、e 肘、g 握る、r 戻す)  -run 秒(その秒数で終わる)
// -port N を付けて起動すると、その送り先のための窓になる(ショートカットで、別のアカウントの VRChat 用に開ける)。
//   最後に開いた VRM・アニメ・時刻はポートごとに覚え、ポートは設定に書き込まない(-port なしで開いたときに響かない)。
// -shot か -nosave があると、設定(ポート・最後に開いたファイルなど)を書き込まない。
//
// 0.4.0: 複数の相手を 1 つの窓で(-targets、PoserTargets.cs)、外から操作する口(-api、PoserApi.cs)、ポーズの .json(PoseFile.cs)。panel.py から使う。
// 0.3.0: 巡回送信(OscSender)、一部のポーズの .anim(手だけなど)の重ね、上級機能(物を持つ: HoldInput、手を動かす: HandIK)。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UniVRM10;
using UnityEngine;

public partial class PoserApp : MonoBehaviour
{
    public const string Version = "0.4.0";
    const float TopH = 58;

    Camera cam;
    Material lineMat;
    Font font;
    readonly Dictionary<string, string> args = new Dictionary<string, string>();
    bool noSave;   // 試しの起動では設定を書き込まない
    int portArg;   // -port N で起動したとき N(0 = なし)
    static bool english;   // 画面の言語(既定は Windows の言語。日本語でなければ英語)

    // 画面の文字: 日本語と英語
    public static string T(string ja, string en) => english ? en : ja;
    static float W(float ja, float en) => english ? en : ja;

    // VRM
    Vrm10Instance vrm;
    HumanPoseHandler handler;
    HumanPose pose;
    string vrmPath;
    float modelHeight = 1.5f;
    bool loading;
    bool starting;   // 起動して前のファイルを開いている間(API のコマンドを待たせる)

    // アニメーション
    AnimClip clip;
    float time;
    bool playing, loop = true;
    // 一部のポーズ(手だけの .anim など)を開いたときの下の姿勢(そのときの姿勢を固めたもの。体は足す分の前)。全身のクリップを開くと消す
    float[] baseMuscles;
    PoseParams.Body baseBody;
    string baseName;

    // 上級機能: 物を持つ・手を動かす
    bool advanced;
    HoldInput hold;
    IntPtr myWindow;
    HandIK ik = new HandIK();
    bool ikLeft, ikBusy;
    bool ikSelected;   // 手を選んでいるか(起動したときは選んでいない。手でない所をクリックすると外れる)
    Vector3 downPos; bool downEmpty;   // 手でない所のクリック(ドラッグでない)を見分ける
    IkOp ikPending;
    float[] ikRef;   // IK の下の姿勢(これが変わったら、その分の手の状態を消す)
    float ikStepCm = 3f, ikStepDeg = 15f;
    string ikStepCmText, ikStepDegText, cyclePairText;
    readonly HashSet<string> heldNow = new HashSet<string>();   // この OnGUI で押されている「押している間」のボタン

    class IkOp
    {
        public bool Left, Reset;
        public Vector3 MoveCm;
        public readonly List<(char ax, float deg)> Rot = new List<(char, float)>();
        public float Elbow;
        public int Grip;
        // 画面のハンドルから: 足元の向きで m、手の骨の向きでの回転(左右を写さない)、肘の回り具合(度)
        public Vector3 MoveAbs;
        public Quaternion RawRot = Quaternion.identity;
        public float SwivelRaw;
    }

    // 体: 元のポーズに足す分(位置は ±1 = ±1.5、角度は度)
    PoseParams.Body offset;
    bool inPlace = true;   // アニメーションの左右・前後の移動は使わない

    // 送信
    OscSender sender;
    // 動かしたら送る: autoSend は選んだ設定(覚える)。起動したときと .anim を開いたときは「休み」(autoPaused)にし、
    // 「送る」か「全部」で休みが終わる。(0.2.0 は開いただけで送っていた。0.2.1 はチェック自体を OFF にしていた)
    bool poseOn, autoSend, autoPaused = true, topmost, showSettings;
    string portText = "9000";
    string valWaitText, idxWaitText, gapText;
    Dictionary<int, float> lastValues, lastQueued;
    float lastChange;

    // カメラ
    Vector3 target;
    float yaw = 180, pitch = 5, dist = 3;
    bool dragging;
    Vector3 lastMouse;

    // 画面
    string status = "";
    bool styled;
    GUIStyle small, plain, tipStyle, icon;
    float bottomH = 250;

    // ---- 起動・終了 ----

    void Awake()
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] == "/?" || a[i] == "/h" || a[i] == "/help") { args["help"] = "1"; continue; }   // Windows の流儀の /?
            if (!a[i].StartsWith("-")) continue;
            var key = a[i].Substring(1).ToLowerInvariant();
            args[key] = i + 1 < a.Length && !a[i + 1].StartsWith("-") ? a[++i] : "1";
        }

        cam = new GameObject("Camera").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.20f, 0.22f, 0.26f);
        cam.nearClipPlane = 0.02f;
        cam.fieldOfView = 30;
        var light = new GameObject("Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.transform.rotation = Quaternion.Euler(30, 160, 0);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
        lineMat = new Material(Shader.Find("Hidden/Internal-Colored"));
        lineMat.SetInt("_ZWrite", 0);
        Camera.onPostRender += DrawGrid;
        InitGizmo();
        font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo UI", "Meiryo", "Yu Gothic UI", "MS UI Gothic", "Segoe UI Symbol" }, 13);
        Application.targetFrameRate = 30;   // VRChat と一緒に動かすので控えめに

        if (Arg("help") != null || Arg("h") != null || Arg("?") != null) { ShowHelp(); return; }
        noSave = Arg("shot") != null || Arg("nosave") != null;
        if (Arg("port") != null && int.TryParse(Arg("port"), out var pa) && pa > 0 && pa < 65536) portArg = pa;
        ParseTargets();
        if (Multi) { portArg = slots[0].Port; sender = slots[0].Sender; hold = slots[0].Hold; }
        else { sender = new OscSender(); hold = new HoldInput(sender); }
        LoadPrefs();
        CopySenderSettings();
        StartApi();
    }

    void OnDestroy()
    {
        Camera.onPostRender -= DrawGrid;
        Camera.onPostRender -= DrawGizmo;
        WinNative.UninstallDrop();
        StopApi();
        DisposeSlots();
        handler?.Dispose();
        sender?.Dispose();
    }

    void OnApplicationQuit() { if (!helpOnly) SavePrefs(); }

    IEnumerator Start()
    {
        if (helpOnly) yield break;
        WinNative.InstallDrop();
        WinNative.SetTopmost(topmost);
        myWindow = WinNative.MyWindow();
        ResetView("front");

        showSettings = Arg("settings") != null;
        // 前に開いた VRM・アニメ・時刻(と重ねた姿勢・動かした手)を戻す。-targets なら相手ごとに
        starting = true;
        if (Multi) yield return StartCoroutine(LoadAllSlots());
        else yield return StartCoroutine(LoadRemembered(true));
        starting = false;
        if (Arg("overlay") != null) { yield return null; while (loading) yield return null; OpenClip(Arg("overlay")); }
        if (Arg("grabtest") != null)
        {
            hold.Grab(sender.Port, myWindow);
            yield return new WaitForSecondsRealtime(5f);
            clickKey = HoldInput.Key("SpinHoldUD", 1);
            yield return new WaitForSecondsRealtime(1f);
            hold.Release(sender.Port);
        }
        if (Arg("send") != null || Arg("ikops") != null || Arg("run") != null || Arg("gizmotest") != null)
        {
            yield return null;
            while (loading) yield return null;
            if (Arg("ikops") != null)
                foreach (var part in Arg("ikops").Split(';'))
                {
                    var t = part.Trim();
                    if (t.Length < 3) continue;
                    ikLeft = t[0] == 'L'; ikSelected = true;
                    var rest = t.Substring(2).Trim();
                    var nums = rest.Substring(1).Trim();
                    switch (rest[0])
                    {
                        case 'm': var v = nums.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); IkPress(o => o.MoveCm += new Vector3(v[0], v[1], v[2])); break;
                        case 'h': IkPress(o => o.Rot.Add((nums[0], float.Parse(nums.Substring(1), CultureInfo.InvariantCulture)))); break;
                        case 'e': IkPress(o => o.Elbow += float.Parse(nums, CultureInfo.InvariantCulture)); break;
                        case 'g': IkPress(o => o.Grip++); break;
                        case 'r': IkPress(o => o.Reset = true); break;
                    }
                    yield return null;
                    while (ikBusy) yield return null;
                    Current(out var dm, out _);
                    var sdd = ik.Get(ikLeft);
                    Debug.Log($"ikops: {t} → {status} | arm " + string.Join(" ", ArmIdxs[ikLeft ? 1 : 0].Select(i => dm[i].ToString("0.000", CultureInfo.InvariantCulture)))
                              + $" | pos {sdd.Pos.ToString("F3")} sw {sdd.Swivel:0.0}");
                }
                if (Arg("gizmotest") != null)
            {
                yield return null;
                gizmo = GizmoMode.Move;
                yield return StartCoroutine(GizmoTest(HKind.Axis, vrm.transform.up, false, new Vector2(0, 6), 12));
                yield return StartCoroutine(GizmoTest(HKind.Center, Vector3.zero, true, new Vector2(4, 0), 10));
                yield return StartCoroutine(GizmoTest(HKind.Elbow, Vector3.zero, true, new Vector2(0, 4), 10));
                gizmo = GizmoMode.Rotate;
                yield return null;
                var hd = vrm.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightHand);
                yield return StartCoroutine(GizmoTest(HKind.Ring, hd.rotation * Vector3.right, false, new Vector2(5, 5), 10));
            }
            if (Arg("send") != null) SendNow(false);
        }
        if (Arg("run") != null && Arg("shot") == null)
        {
            yield return new WaitForSecondsRealtime(float.Parse(Arg("run"), CultureInfo.InvariantCulture));
            Application.Quit();
        }

        if (Arg("shot") != null)
        {
            yield return null;
            while (loading) yield return null;
            for (int i = 0; i < 5; i++) yield return null;
            if (Arg("wait") != null) yield return new WaitForSecondsRealtime(float.Parse(Arg("wait"), CultureInfo.InvariantCulture));
            ScreenCapture.CaptureScreenshot(Arg("shot"));
            for (int i = 0; i < 5; i++) yield return null;
            if (Arg("quit") != null) Application.Quit();
        }
    }

    string Arg(string key) => args.TryGetValue(key, out var v) ? v : null;

    // -help: 起動オプションを出して終わる(Unity の exe には標準出力がないので、メッセージの窓で)
    void ShowHelp()
    {
        var lang = Arg("lang") ?? PlayerPrefs.GetString("lang", "");
        english = lang == "" ? Application.systemLanguage != SystemLanguage.Japanese : lang == "en";
        WinNative.MessageBox($"OscPoser {Version}", T(
            "起動オプション(どれも省略できます)\n\n" +
            "  -port N              送り先のポート(VRChat の OSC の受信ポート)。そのポート用の窓になり、最後に開いたファイルをポートごとに覚える\n" +
            "  -targets 9000,9010   ポートごとに VRM・ポーズ・手・送信を持つ窓(ポートの欄を変えると切り替わる)\n" +
            "  -target i            -targets のうち、最初に出す番号(0 から)\n" +
            "  -api [PORT]          AI(MCP)・スクリプトから操作する口を開く(既定 9330。この PC の中からだけ)\n" +
            "                       MCP: http://127.0.0.1:9330/mcp、HTTP: GET /state・POST /cmd\n" +
            "  -vrm FILE  -anim FILE  -time 秒   開く VRM・アニメーション(.anim かポーズの .json)・時刻\n" +
            "  -lang ja|en          画面の言語\n" +
            "  -advanced 0|1        上級機能の行を出すか\n" +
            "  -nosave              設定を書き込まない\n" +
            "  -help(/? でも)      これを出す\n\n" +
            "説明: https://github.com/JRF-2018/osc_poser",
            "Command-line options (all optional)\n\n" +
            "  -port N              Destination port (VRChat's OSC input port). A window for that port; the last files are remembered per port\n" +
            "  -targets 9000,9010   Keep a VRM, pose, hands and sending per port (changing the Port field switches)\n" +
            "  -target i            Which of -targets to show first (from 0)\n" +
            "  -api [PORT]          Open the control port for AI (MCP) and scripts (default 9330, this PC only)\n" +
            "                       MCP: http://127.0.0.1:9330/mcp, HTTP: GET /state, POST /cmd\n" +
            "  -vrm FILE  -anim FILE  -time SEC   VRM, animation (.anim or pose .json) and time to open\n" +
            "  -lang ja|en          UI language\n" +
            "  -advanced 0|1        Show the advanced rows\n" +
            "  -nosave              Don't write settings\n" +
            "  -help (or /?)        Show this\n\n" +
            "Docs: https://github.com/JRF-2018/osc_poser"));
        helpOnly = true;
        Application.Quit();
    }
    bool helpOnly;

    // -port N で起動したときは、最後に開いたファイルなどをポートごとに覚える(なければ、ふだんのもの)
    string PortKey(string k) => portArg != 0 ? $"{k}_p{portArg}" : k;
    string PrefString(string k) => PlayerPrefs.GetString(PortKey(k), PlayerPrefs.GetString(k, ""));

    void LoadPrefs()
    {
        int port = portArg != 0 ? portArg : PlayerPrefs.GetInt("port", 9000);
        portText = port.ToString();
        sender.Port = port;
        sender.ValWait = PlayerPrefs.GetFloat("valWait", 0.025f);
        sender.IdxWait = PlayerPrefs.GetFloat("idxWait", 0.1f);
        sender.Gap = PlayerPrefs.GetFloat("gap", 0.1f);
        sender.NoZero = PlayerPrefs.GetInt("noZero", 1) != 0;
        inPlace = PlayerPrefs.GetInt("inPlace", 1) != 0;
        topmost = PlayerPrefs.GetInt("topmost", 0) != 0;
        autoSend = PlayerPrefs.GetInt("autoSend", 1) != 0;
        sender.CycleOn = PlayerPrefs.GetInt("cycle", 1) != 0;
        sender.CyclePair = PlayerPrefs.GetFloat("cyclePair", 0.3f);
        advanced = Arg("advanced") != null ? Arg("advanced") != "0" : PlayerPrefs.GetInt("advanced", 0) != 0;
        ikStepCm = PlayerPrefs.GetFloat("ikStepCm", 3f);
        ikStepDeg = PlayerPrefs.GetFloat("ikStepDeg", 15f);
        var lang = Arg("lang") ?? PlayerPrefs.GetString("lang", "");
        english = lang == "" ? Application.systemLanguage != SystemLanguage.Japanese : lang == "en";
        WaitTexts();
    }

    void WaitTexts()
    {
        valWaitText = sender.ValWait.ToString("0.###", CultureInfo.InvariantCulture);
        idxWaitText = sender.IdxWait.ToString("0.###", CultureInfo.InvariantCulture);
        gapText = sender.Gap.ToString("0.###", CultureInfo.InvariantCulture);
        cyclePairText = sender.CyclePair.ToString("0.###", CultureInfo.InvariantCulture);
        ikStepCmText = ikStepCm.ToString("0.##", CultureInfo.InvariantCulture);
        ikStepDegText = ikStepDeg.ToString("0.##", CultureInfo.InvariantCulture);
    }

    void SavePrefs()
    {
        if (noSave) return;
        if (portArg == 0) PlayerPrefs.SetInt("port", sender.Port);
        PlayerPrefs.SetFloat("valWait", sender.ValWait);
        PlayerPrefs.SetFloat("idxWait", sender.IdxWait);
        PlayerPrefs.SetFloat("gap", sender.Gap);
        PlayerPrefs.SetInt("noZero", sender.NoZero ? 1 : 0);
        PlayerPrefs.SetInt("inPlace", inPlace ? 1 : 0);
        PlayerPrefs.SetInt("topmost", topmost ? 1 : 0);
        PlayerPrefs.SetInt("autoSend", autoSend ? 1 : 0);
        PlayerPrefs.SetString("lang", english ? "en" : "ja");
        PlayerPrefs.SetInt("cycle", sender.CycleOn ? 1 : 0);
        PlayerPrefs.SetFloat("cyclePair", sender.CyclePair);
        PlayerPrefs.SetInt("advanced", advanced ? 1 : 0);
        PlayerPrefs.SetFloat("ikStepCm", ikStepCm);
        PlayerPrefs.SetFloat("ikStepDeg", ikStepDeg);
        if (clip != null) { PlayerPrefs.SetString(PortKey("anim"), clip.Path); PlayerPrefs.SetFloat(PortKey("time"), time); }
        SaveBase();
        SaveIk();
        PlayerPrefs.Save();
    }

    // ---- 読み込み ----

    async void LoadVrm(string path)
    {
        if (loading) return;
        loading = true;
        status = T("読み込み中: ", "Loading: ") + Path.GetFileName(path);
        try
        {
            var inst = await Vrm10.LoadPathAsync(path, canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.None, showMeshes: true);
            if (inst == null) throw new Exception(T("VRM として読めませんでした", "not a readable VRM"));
            if (vrm != null) Destroy(vrm.gameObject);
            handler?.Dispose();
            vrm = inst;
            var animator = inst.GetComponent<Animator>();
            handler = new HumanPoseHandler(animator.avatar, inst.transform);
            handler.GetHumanPose(ref pose);
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            modelHeight = head != null ? head.position.y + 0.12f : 1.5f;
            vrmPath = path;
            if (!noSave) { PlayerPrefs.SetString(PortKey("vrm"), path); PlayerPrefs.Save(); }
            ResetIk();   // 別のアバター
            status = $"VRM: {Path.GetFileName(path)}";
            ResetView("front");
        }
        catch (Exception e)
        {
            status = T("VRM を読めません: ", "Cannot read the VRM: ") + e.Message;
            Debug.LogException(e);
        }
        loading = false;
    }

    bool OpenClipAny(string path) => OpenClip(path);

    bool OpenClip(string path)
    {
        AnimClip c0;
        try { c0 = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? PoseFile.Load(path) : AnimClip.Load(path); }
        catch (Exception e)
        {
            status = T("アニメーションを読めません: ", "Cannot read the animation: ") + e.Message;
            return false;
        }
        return UseClip(c0);
    }

    // 読んだクリップ(.anim・ポーズの .json・API の値)を今の姿勢にする
    bool UseClip(AnimClip c)
    {
        var path = c.Path;
        try
        {
            if (c.Partial)
            {
                // 一部のポーズ: 今の姿勢(体は足す分の前)を下に固めて、その上に重ねる
                Underlying(out baseMuscles, out baseBody);
                baseName = clip != null ? $"{(baseName != null ? baseName + " + " : "")}{Path.GetFileNameWithoutExtension(clip.Path)}@{time:0.##}"
                                        : baseName ?? T("立った姿勢", "standing");
            }
            else { baseMuscles = null; baseName = null; }
            clip = c;
            autoPaused = true;   // 開いただけでは送らない(「送る」で再開)
            time = clip.Length;
            playing = false;
            status = T($"アニメーション: {Path.GetFileName(path)}({clip.Length:0.###} 秒)", $"Animation: {Path.GetFileName(path)} ({clip.Length:0.###} s)");
            if (c.Partial)
                status = T($"一部のポーズ(体の主な筋肉 {c.CoreHave}/{c.CoreTotal})なので、今の姿勢に重ねます: {Path.GetFileName(path)}",
                           $"Partial pose ({c.CoreHave}/{c.CoreTotal} main muscles), layered on the current pose: {Path.GetFileName(path)}");
            if (!noSave && File.Exists(path)) { PlayerPrefs.SetString(PortKey("anim"), path); PlayerPrefs.Save(); }
            return true;
        }
        catch (Exception e)
        {
            status = T("アニメーションを読めません: ", "Cannot read the animation: ") + e.Message;
            return false;
        }
    }

    void OpenDropped(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".vrm") LoadVrm(path);
        else if (ext == ".anim" || ext == ".json") OpenClipAny(path);
        else status = T($"開けない種類のファイルです: {Path.GetFileName(path)}(.vrm・.anim・ポーズの .json をドロップできます)", $"Unsupported file: {Path.GetFileName(path)} (drop .vrm, .anim or a pose .json)");
    }

    // ---- 姿勢 ----

    // 下の姿勢: 一部のポーズの下に固めた姿勢 + アニメーション(手の IK と、体に足す分の前)
    void Underlying(out float[] muscles, out PoseParams.Body b)
    {
        bool partial = clip != null && baseMuscles != null;
        muscles = partial ? (float[])baseMuscles.Clone() : new float[HumanTrait.MuscleCount];
        b = partial ? baseBody : new PoseParams.Body();
        if (clip == null) return;
        var p = clip.Sample(time);
        foreach (var kv in p.Muscles)
            muscles[Muscles.Index[kv.Key]] = Mathf.Clamp(kv.Value, -PoseParams.MuscleRange, PoseParams.MuscleRange);
        if (!partial || p.HasY) b.Y = (p.RootT.y - PoseParams.BaseY) / PoseParams.PosRange;
        if ((!inPlace || clip.KeepXZ) && (!partial || p.HasXZ)) { b.X = p.RootT.x / PoseParams.PosRange; b.Z = p.RootT.z / PoseParams.PosRange; }
        if (!partial || p.HasQ)
        {
            var e = PoseParams.EulerYXZ(p.RootQ);
            b.Yaw = e.x; b.Pitch = e.y; b.Roll = e.z;
        }
    }

    // 今の姿勢: 筋肉(HumanTrait の順、±4 に収める)と体(下の姿勢 + 手の IK + 足す分)
    void Current(out float[] muscles, out PoseParams.Body body)
    {
        Underlying(out muscles, out var b);
        ik.Apply(muscles);
        b.Y = Mathf.Clamp(b.Y + offset.Y, -1, 1);
        b.X = Mathf.Clamp(b.X + offset.X, -1, 1);
        b.Z = Mathf.Clamp(b.Z + offset.Z, -1, 1);
        b.Yaw = PoseParams.Wrap180(b.Yaw + offset.Yaw);
        b.Pitch = PoseParams.Wrap180(b.Pitch + offset.Pitch);
        b.Roll = Mathf.Clamp(PoseParams.Wrap180(b.Roll + offset.Roll), -PoseParams.RollRange, PoseParams.RollRange);
        body = b;
    }

    void ApplyPose(float[] muscles, PoseParams.Body b)
    {
        if (handler == null) return;
        pose.bodyPosition = new Vector3(b.X * PoseParams.PosRange, PoseParams.BaseY + b.Y * PoseParams.PosRange, b.Z * PoseParams.PosRange);
        pose.bodyRotation = Quaternion.Euler(b.Pitch, b.Yaw, b.Roll);   // Unity の Euler は YXZ の順
        pose.muscles = muscles;
        handler.SetHumanPose(ref pose);
    }

    static bool Same(Dictionary<int, float> a, Dictionary<int, float> b)
    {
        if (a == null || b == null) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || Mathf.Abs(v - kv.Value) > 1e-4f) return false;
        return true;
    }

    void Update()
    {
        if (helpOnly) return;
        foreach (var f in WinNative.TakeDropped()) OpenDropped(f);
        RunApiJobs();
        UpdateSwitch();
        Keys();
        Mouse();

        if (clip != null && playing)
        {
            time += Time.deltaTime;
            if (time > clip.Length)
            {
                if (loop && clip.Length > 0) time %= clip.Length;
                else { time = clip.Length; playing = false; }
            }
        }
        CheckIkBase();
        UpdateHeld();
        Current(out var muscles, out var body);
        ApplyPose(muscles, body);
        var values = PoseParams.Values(muscles, body);
        if (!Same(values, lastValues)) { lastValues = values; lastChange = Time.unscaledTime; }
        // 動かしたら送る: 止まって 0.35 秒たったら、変わった値だけ
        if (autoSend && !autoPaused && !playing && !Same(values, lastQueued) && Time.unscaledTime - lastChange > 0.35f)
        {
            sender.Send(values, true, false);
            lastQueued = values;
        }
        UpdateCamera();
    }

    // ---- 手を動かす(上級機能) ----

    static readonly int[][] ArmIdxs = { HandIK.ArmIdx(false), HandIK.ArmIdx(true) };
    static readonly int[][] FingerIdxs = { HandIK.FingerIdx(false), HandIK.FingerIdx(true) };
    int ikGen;

    void ResetIk()
    {
        ik.R.Reset(); ik.L.Reset();
        ikRef = null; ikPending = null;
        ikGen++;
    }

    static bool Differs(float[] a, float[] b, IEnumerable<int> idx)
    {
        foreach (var i in idx) if (Mathf.Abs(a[i] - b[i]) > 1e-3f) return true;
        return false;
    }

    // 下の姿勢(アニメーション・時刻・重ねたポーズ)が変わったら: 腕・指以外が変われば手の状態をすべて消す(ほかのポーズ)。
    // 腕だけ変われば、その手は今の腕から(状態を消す)。指だけ変われば、その手の「握る」をやめる(新しい指を使う)。panel.py の ☛ と同じ
    void CheckIkBase()
    {
        if (!ik.Active) { if (!ikBusy) ikRef = null; return; }
        Underlying(out var m, out _);
        if (ikRef == null) { ikRef = m; return; }
        var hands = new HashSet<int>(ArmIdxs[0].Concat(ArmIdxs[1]).Concat(FingerIdxs[0]).Concat(FingerIdxs[1]));
        bool other = false;
        for (int i = 0; i < m.Length && !other; i++)
            if (!hands.Contains(i) && Mathf.Abs(m[i] - ikRef[i]) > 1e-3f) other = true;
        if (other)
        {
            ResetIk();
            status = T("姿勢が変わったので、手の操作(☛)を元に戻しました", "The pose changed, so the hand edits (☛) were cleared");
            return;
        }
        for (int k = 0; k < 2; k++)
        {
            var sd = ik.Get(k == 1);
            if (Differs(m, ikRef, ArmIdxs[k])) { sd.HasState = false; sd.Arm = null; sd.ElbowBtn = sd.ElbowDir = 0; }
            if (Differs(m, ikRef, FingerIdxs[k])) sd.Grip = false;
        }
        ikRef = m;
    }

    // ---- 動かした手を覚える(次に同じ VRM・アニメ・時刻で開いたとき戻す) ----

    // 重ねがけ(一部のポーズ)の下の姿勢を覚える。次に同じ .anim(重ねたもの)で開いたとき戻す
    void SaveBase()
    {
        if (clip == null || baseMuscles == null) { PlayerPrefs.DeleteKey(PortKey("baseState")); return; }
        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        var b = baseBody;
        PlayerPrefs.SetString(PortKey("baseState"), string.Join("\n", clip.Path, baseName ?? "",
            string.Join(",", baseMuscles.Select(F)), string.Join(",", new[] { b.Y, b.X, b.Z, b.Yaw, b.Pitch, b.Roll }.Select(F))));
    }

    void RestoreBase()
    {
        var data = PlayerPrefs.GetString(PortKey("baseState"), "");
        if (data == "" || clip == null || !clip.Partial) return;
        try
        {
            var p = data.Split('\n');
            if (p.Length < 4 || p[0] != clip.Path) return;
            var m = p[2].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var b = p[3].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (m.Length != HumanTrait.MuscleCount || b.Length != 6) return;
            baseMuscles = m;
            baseBody = new PoseParams.Body { Y = b[0], X = b[1], Z = b[2], Yaw = b[3], Pitch = b[4], Roll = b[5] };
            baseName = p[1];
        }
        catch (Exception) { }
    }

    string IkSig() => $"{vrmPath}|{clip?.Path}|{time.ToString("0.####", CultureInfo.InvariantCulture)}|{(baseMuscles != null ? baseName : "")}";

    void SaveIk()
    {
        if (!ik.Active || vrmPath == null) { PlayerPrefs.DeleteKey(PortKey("ikState")); return; }
        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        foreach (var left in new[] { false, true })
        {
            var s = ik.Get(left);
            var nums = new List<float> { s.HasState && s.Arm != null ? 1 : 0, s.Grip ? 1 : 0, s.Pos.x, s.Pos.y, s.Pos.z, s.Rot.x, s.Rot.y, s.Rot.z, s.Rot.w, s.Swivel };
            nums.AddRange(s.Arm ?? new float[HandIK.ArmMuscles.Length]);
            parts.Add(string.Join(",", nums.Select(F)));
        }
        PlayerPrefs.SetString(PortKey("ikSig"), IkSig());
        PlayerPrefs.SetString(PortKey("ikState"), string.Join(";", parts));
    }

    void RestoreIk()
    {
        if (handler == null || PlayerPrefs.GetString(PortKey("ikSig"), "") != IkSig()) return;
        var data = PlayerPrefs.GetString(PortKey("ikState"), "");
        if (data == "") return;
        try
        {
            var parts = data.Split(';');
            for (int k = 0; k < 2 && k < parts.Length; k++)
            {
                var v = parts[k].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var s = ik.Get(k == 1);
                s.Reset();
                s.HasState = v[0] != 0;
                s.Grip = v[1] != 0;
                s.Pos = new Vector3(v[2], v[3], v[4]);
                s.Rot = new Quaternion(v[5], v[6], v[7], v[8]);
                s.Swivel = v[9];
                if (s.HasState) s.Arm = v.Skip(10).Take(HandIK.ArmMuscles.Length).ToArray();
            }
            ikRef = null;
            if (ik.Active) status = T("前に動かした手(☛)を戻しました", "Restored the hands you moved last time (☛)");
        }
        catch (Exception) { ResetIk(); }
    }

    void IkPress(Action<IkOp> f)
    {
        if (handler == null) { status = T("先に VRM を開いてください", "Open a VRM first"); return; }
        if (!ikSelected) { status = T("動かす手を選んでください(右/左のボタンか、3D の画面で手首の白い輪をクリック)", "Choose a hand first (the R/L button, or click the white circle on a wrist in the 3D view)"); return; }
        if (ikPending != null && ikPending.Left != ikLeft)
        {
            status = T("いまの手の操作が終わってから、もう一方の手を動かしてください", "Wait for the current hand to finish before moving the other hand");
            return;
        }
        if (ikPending == null) ikPending = new IkOp { Left = ikLeft };
        f(ikPending);
        if (!ikBusy) StartCoroutine(IkRun());
    }

    IEnumerator IkRun()
    {
        ikBusy = true;
        while (ikPending != null)
        {
            var op = ikPending;
            ikPending = null;
            int gen = ikGen;
            var sd = ik.Get(op.Left);
            string hand = op.Left ? T("左手", "Left hand") : T("右手", "Right hand");
            if (op.Reset) { sd.Reset(); status = T($"{hand}を元のポーズに戻しました", $"{hand}: back to the pose"); }
            if (op.Grip % 2 == 1) { sd.Grip = !sd.Grip; status = T($"{hand}の指: {(sd.Grip ? "軽く握る" : "元のポーズ")}", $"{hand} fingers: {(sd.Grip ? "light grip" : "from the pose")}"); }
            if (op.MoveCm == Vector3.zero && op.Rot.Count == 0 && op.Elbow == 0 && op.MoveAbs == Vector3.zero
                && Quaternion.Angle(op.RawRot, Quaternion.identity) < 1e-3f && op.SwivelRaw == 0) continue;

            // 肘のボタン: 向き(胸から遠く/近く)を決めるのは続けて押す最初の 1 回だけ。同じボタンを続けて押す間は同じ向きに
            // 回し続け(いちばん遠い所も越える)、反対のボタンなら逆向きに。ほかの操作をはさむと決め直す
            float swDelta = 0, away = 0;
            bool keep = op.Elbow != 0 && sd.ElbowBtn != 0;
            if (op.Elbow != 0)
            {
                if (keep) swDelta = ((op.Elbow > 0) == (sd.ElbowBtn > 0) ? sd.ElbowDir : -sd.ElbowDir) * Mathf.Abs(op.Elbow);
                else away = op.Elbow;
            }
            var lq = Quaternion.identity;
            foreach (var (ax, deg0) in op.Rot)
            {
                float deg = op.Left && ax != 'z' ? -deg0 : deg0;   // 左手は写したように
                lq *= Quaternion.AngleAxis(deg, ax == 'x' ? Vector3.right : ax == 'y' ? Vector3.up : Vector3.forward);
            }
            lq *= op.RawRot;
            swDelta += op.SwivelRaw;
            Underlying(out var snap, out _);
            Current(out var m, out var b);
            var bodyPos = new Vector3(b.X * PoseParams.PosRange, PoseParams.BaseY + b.Y * PoseParams.PosRange, b.Z * PoseParams.PosRange);
            var bodyRot = Quaternion.Euler(b.Pitch, b.Yaw, b.Roll);
            var req = new HandIK.Request { Left = op.Left, Move = op.MoveCm / 100f, MoveAbs = op.MoveAbs, LocalRot = lq, SwivelDelta = swDelta, Away = away };
            status = T("手の形を計算しています…", "Solving the arm…");
            HandIK.Result r = null;
            yield return StartCoroutine(ik.Solve(vrm.GetComponent<Animator>(), handler, m, bodyPos, bodyRot, req, x => r = x));
            string note = null;
            if (op.Elbow != 0)
            {
                if (keep) { sd.ElbowBtn = op.Elbow > 0 ? 1 : -1; sd.ElbowDir = swDelta > 0 ? 1 : -1; }
                else
                {
                    int edir = r.ElbowDir < 0 ? -1 : 1;
                    if (r.ElbowMoved < 0.5f)
                    {
                        // 最初の 1 回がいちばん遠い(近い)所だった: 真後ろから遠ざかる向き(回り具合の絶対値が大きくなる向き)に越える
                        edir = r.Swivel >= 0 ? 1 : -1;
                        req = new HandIK.Request { Left = op.Left, SwivelDelta = edir * Mathf.Abs(op.Elbow) };
                        yield return StartCoroutine(ik.Solve(vrm.GetComponent<Animator>(), handler, m, bodyPos, bodyRot, req, x => r = x));
                    }
                    else if (r.ElbowMoved < Mathf.Abs(op.Elbow) - 0.5f)
                        note = T($"肘はいちばん{(op.Elbow > 0 ? "遠い" : "近い")}所で止めました(続けて押すと越えて回します)",
                                 $"The elbow stopped at its {(op.Elbow > 0 ? "farthest" : "closest")} point (press again to go past it)");
                    sd.ElbowBtn = op.Elbow > 0 ? 1 : -1; sd.ElbowDir = edir;
                }
            }
            else { sd.ElbowBtn = sd.ElbowDir = 0; }
            Underlying(out var now, out _);
            if (gen != ikGen || Differs(now, snap, Enumerable.Range(0, now.Length)))
            {
                status = T("計算の間に姿勢が変わったので、この手の操作はやめました", "The pose changed while solving, so this hand edit was dropped");
                continue;
            }
            sd.HasState = true; sd.Pos = r.Pos; sd.Rot = r.Rot; sd.Swivel = r.Swivel; sd.Arm = r.Arm;
            ikRef = snap;
            status = note ?? (r.Reach > 0.01f ? T($"{hand}: 届かない分 {r.Reach * 100:0} cm(届く所まで)", $"{hand}: {r.Reach * 100:0} cm out of reach (moved as far as it goes)")
                     : r.RotError > 3f ? T($"{hand}: 回しきれない分 {r.RotError:0} 度", $"{hand}: {r.RotError:0}° could not be turned")
                     : T($"{hand}: 動かしました", $"{hand}: done"));
        }
        ikBusy = false;
    }

    // 「押している間」のボタン(物を回す・MoveHoldFB)。RepeatButton は押している間の Repaint で true を返す
    string heldAxis, heldSeen, clickKey;
    bool HoldButton(GUIContent c, string axis, int sign, float w)
    {
        bool down = GUILayout.RepeatButton(c, icon, GUILayout.Width(w), GUILayout.Height(22));
        if (down)
        {
            var key = HoldInput.Key(axis, sign);
            if (Event.current.type == EventType.Repaint) heldSeen = key;
            else clickKey = key;   // すぐ離したとき(Repaint をはさまない)も 1 回は回す
        }
        return down;
    }

    void UpdateHeld()
    {
        hold.HeldAxis = heldAxis;
        var key = heldAxis ?? clickKey;
        clickKey = null;
        if (key == null) return;
        var axis = key.Substring(0, key.Length - 1);
        hold.Pulse(sender.Port, axis, key.EndsWith("+") ? 1 : -1);
    }

    // 解いている途中の姿勢(コルーチンは Update のあとに動く)を描かないよう、描く前に今の姿勢を当て直し、ハンドルを作る
    void LateUpdate()
    {
        if (helpOnly) return;
        if (ikBusy) { Current(out var m, out var b); ApplyPose(m, b); }
        BuildGizmo();
        UpdateHot();
    }

    void SendNow(bool all)
    {
        Current(out var muscles, out var body);
        var values = PoseParams.Values(muscles, body);
        if (all) sender.Forget();
        int n = sender.Send(values, true, true);
        lastQueued = values;
        autoPaused = false;
        poseOn = true;
        status = n > 0 ? T($"{n} 個を送ります(ポート {sender.Port})", $"Sending {n} values (port {sender.Port})") : T("変わった値はありません(任意ポーズを ON にしました)", "No values changed (pose turned ON)");
    }

    void ApplyPort()
    {
        if (int.TryParse(portText, out var p) && p > 0 && p < 65536 && p != sender.Port)
        {
            if (Multi) { SwitchToPort(p); return; }   // ポートごとに VRM・ポーズなどを持つ(-targets のとき)
            sender.Cancel();
            sender.Port = p;
            sender.Forget();   // 別のアバターなので、次は全部送る
            SavePrefs();
            status = T("送り先: ", "Sending to: ") + $"127.0.0.1:{p}";
        }
    }

    void ApplyWaits()
    {
        float F(string s, float old) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 5 ? v : old;
        sender.ValWait = F(valWaitText, sender.ValWait);
        sender.IdxWait = F(idxWaitText, sender.IdxWait);
        sender.Gap = F(gapText, sender.Gap);
        sender.CyclePair = Mathf.Max(0.05f, F(cyclePairText, sender.CyclePair));
        float G(string s, float old, float max) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && v <= max ? v : old;
        ikStepCm = G(ikStepCmText, ikStepCm, 50);
        ikStepDeg = G(ikStepDegText, ikStepDeg, 180);
        WaitTexts();
        CopySenderSettings();
        SavePrefs();
    }

    // ---- キー・マウス・カメラ ----

    bool Typing => GUIUtility.keyboardControl != 0;

    void Keys()
    {
        if (Typing) return;
        if (Input.GetKeyDown(KeyCode.F)) ResetView("front");
        if (advanced)
        {
            if (Input.GetKeyDown(KeyCode.W)) gizmo = GizmoMode.Move;
            if (Input.GetKeyDown(KeyCode.E)) gizmo = GizmoMode.Rotate;
            if (Input.GetKeyDown(KeyCode.Q)) gizmo = GizmoMode.Off;
        }
        if (clip == null) return;
        float frame = 1f / clip.SampleRate;
        if (Input.GetKeyDown(KeyCode.Space)) playing = !playing;
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { playing = false; time = Mathf.Max(0, time - frame); }
        if (Input.GetKeyDown(KeyCode.RightArrow)) { playing = false; time = Mathf.Min(clip.Length, time + frame); }
        if (Input.GetKeyDown(KeyCode.Home)) { playing = false; time = 0; }
        if (Input.GetKeyDown(KeyCode.End)) { playing = false; time = clip.Length; }
    }

    bool OverUi(Vector3 mouse)
    {
        float y = Screen.height - mouse.y;
        return y < TopH || y > Screen.height - bottomH;
    }

    void Mouse()
    {
        var m = Input.mousePosition;
        bool any = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
        if (GizmoDrag(m, Input.GetMouseButton(0))) return;
        if (Input.GetMouseButtonDown(0) && !OverUi(m) && GizmoMouseDown(m)) { GUIUtility.keyboardControl = 0; downEmpty = false; return; }
        // 手でない所をクリック(動かさずに離す)したら、手の選択を外す
        if (Input.GetMouseButtonDown(0) && !OverUi(m)) { downPos = m; downEmpty = true; }
        if (Input.GetMouseButton(0) && downEmpty && (m - downPos).magnitude > 3f) downEmpty = false;
        if (Input.GetMouseButtonUp(0) && downEmpty) { downEmpty = false; if (advanced) ikSelected = false; }
        if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)) && !OverUi(m))
        {
            dragging = true;
            lastMouse = m;
            GUIUtility.keyboardControl = 0;
        }
        if (!any) dragging = false;
        if (dragging)
        {
            var d = m - lastMouse;
            lastMouse = m;
            if (Input.GetMouseButton(0))
            {
                yaw += d.x * 0.3f;
                pitch = Mathf.Clamp(pitch - d.y * 0.3f, -89, 89);
            }
            else
            {
                float k = dist * 0.0015f;
                target -= cam.transform.right * d.x * k + cam.transform.up * d.y * k;
            }
        }
        if (!OverUi(m) && Input.mouseScrollDelta.y != 0)
            dist = Mathf.Clamp(dist * (1 - Input.mouseScrollDelta.y * 0.1f), 0.2f, 30f);
    }

    void ResetView(string view)
    {
        target = new Vector3(0, modelHeight * 0.5f, 0);
        float visible = Mathf.Max(0.2f, 1 - (TopH + bottomH) / Screen.height);
        dist = modelHeight * 0.6f / (Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * visible);
        pitch = 5;
        switch (view)
        {
            case "right": yaw = -90; break;
            case "left": yaw = 90; break;
            case "back": yaw = 0; break;
            default: yaw = 180; break;
        }
    }

    void UpdateCamera()
    {
        var rot = Quaternion.Euler(pitch, yaw, 0);
        cam.transform.position = target + rot * new Vector3(0, 0, -dist);
        cam.transform.rotation = rot;
        // 上下の帯の分、モデルを見える所の真ん中に寄せる
        float t = TopH / Screen.height, b = bottomH / Screen.height;
        cam.ResetProjectionMatrix();
        var p = cam.projectionMatrix;
        p.m12 = t - b;
        cam.projectionMatrix = p;
    }

    void DrawGrid(Camera c)
    {
        if (c != cam) return;
        lineMat.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);
        for (int i = -10; i <= 10; i++)
        {
            float f = i * 0.25f;
            GL.Color(i == 0 ? new Color(1, 1, 1, 0.35f) : new Color(1, 1, 1, 0.12f));
            GL.Vertex3(f, 0, -2.5f); GL.Vertex3(f, 0, 2.5f);
            GL.Vertex3(-2.5f, 0, f); GL.Vertex3(2.5f, 0, f);
        }
        GL.Color(new Color(0.4f, 0.8f, 1f, 0.8f));   // 正面(+Z)の印
        GL.Vertex3(0, 0.001f, 0); GL.Vertex3(0, 0.001f, 0.6f);
        GL.Vertex3(-0.08f, 0.001f, 0.5f); GL.Vertex3(0, 0.001f, 0.6f);
        GL.Vertex3(0.08f, 0.001f, 0.5f); GL.Vertex3(0, 0.001f, 0.6f);
        GL.End();
        GL.PopMatrix();
    }

    // ---- 画面 ----

    void Style()
    {
        if (styled) return;
        styled = true;
        GUI.skin.font = font;
        foreach (var s in new[] { GUI.skin.label, GUI.skin.button, GUI.skin.toggle, GUI.skin.textField, GUI.skin.box })
        {
            s.fontSize = 13;
            s.clipping = TextClipping.Overflow;
        }
        plain = new GUIStyle(GUI.skin.label) { wordWrap = false };
        small = new GUIStyle(plain) { fontSize = 11 };
        small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
        icon = new GUIStyle(GUI.skin.button) { fontSize = 17, padding = new RectOffset(0, 0, 0, 1) };   // 記号のボタン(13 では小さくて読めない)
        // ツールチップ: 下の文字が透けて読みにくかったので、ほぼ不透明な背景に(0.3.0)
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.10f, 0.11f, 0.13f, 0.97f));
        bg.Apply();
        tipStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, padding = new RectOffset(6, 6, 4, 4) };
        tipStyle.normal.background = bg;
        tipStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
    }

    void OnGUI()
    {
        if (helpOnly) return;
        Style();
        var evType = Event.current.type;
        bottomH = 252 + (showSettings ? 56 : 0) + (advanced ? 84 : 0);
        float w = Screen.width, h = Screen.height;
        GUI.Box(new Rect(0, 0, w, TopH), GUIContent.none);
        GUI.Box(new Rect(0, h - bottomH, w, bottomH), GUIContent.none);
        TopBar(new Rect(6, 4, w - 12, TopH - 6));
        BottomBar(new Rect(6, h - bottomH + 4, w - 12, bottomH - 8));
        if (!string.IsNullOrEmpty(GUI.tooltip))
        {
            var tip = new GUIContent(GUI.tooltip);
            var size = tipStyle.CalcSize(tip);
            size.x = Mathf.Min(size.x, Mathf.Min(w - 8, 460));
            size.y = tipStyle.CalcHeight(tip, size.x);
            var m = Event.current.mousePosition;
            var rect = new Rect(Mathf.Clamp(m.x + 12, 4, w - size.x - 4), m.y + 18 + size.y > h ? Mathf.Max(0, m.y - size.y - 6) : m.y + 18, size.x, size.y);
            GUI.Label(rect, tip, tipStyle);
        }
        if (evType == EventType.Repaint) { heldAxis = heldSeen; heldSeen = null; }
    }

    void TopBar(Rect r)
    {
        GUILayout.BeginArea(r);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("VRM…", T("VRM(0.x・1.x)を開く。ドロップでもよい", "Open a VRM (0.x or 1.x). You can also drop it")), GUILayout.Width(56)))
        {
            var dir = vrmPath != null ? Path.GetDirectoryName(vrmPath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var f = WinNative.OpenFile(T("VRM を開く", "Open VRM"), "VRM", "*.vrm", dir);
            if (f != null) LoadVrm(f);
        }
        if (GUILayout.Button(new GUIContent(T("アニメ…", "Anim…"), T("人型のアニメーション(.anim、テキスト形式)を開く。ドロップでもよい", "Open a humanoid animation (.anim, text format). You can also drop it")), GUILayout.Width(64)))
        {
            var dir = clip != null ? Path.GetDirectoryName(clip.Path) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var f = WinNative.OpenFile(T("アニメーションを開く", "Open animation"), T("アニメーション", "Animation"), "*.anim", dir);
            if (f != null) OpenClip(f);
        }
        GUILayout.Space(6);
        foreach (var (label, view) in new[] { (T("前", "F"), "front"), (T("右", "R"), "right"), (T("左", "L"), "left"), (T("後", "B"), "back") })
            if (GUILayout.Button(label, GUILayout.Width(30))) ResetView(view);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(new GUIContent(english ? "日本語" : "English", english ? "画面を日本語に" : "Switch to English"), GUILayout.Width(60)))
        {
            english = !english;
            SavePrefs();
        }
        var tm = GUILayout.Toggle(topmost, new GUIContent(T("手前", "On top"), T("この窓をいつも手前に", "Keep this window on top")));
        if (tm != topmost) { topmost = tm; WinNative.SetTopmost(topmost); SavePrefs(); }
        GUILayout.EndHorizontal();
        var files = $"{(vrmPath != null ? Path.GetFileName(vrmPath) : T("(VRM なし)", "(no VRM)"))}  ／  {(clip != null ? (baseMuscles != null ? $"{baseName} + " : "") + Path.GetFileName(clip.Path) : T("(アニメーションなし: 立った姿勢)", "(no animation: standing)"))}";
        GUILayout.Label(new GUIContent(files, baseMuscles != null ? T("一部のポーズ(手だけなど)を、下の姿勢に重ねています。全身のアニメーションを開くと、重ねたものは消えます",
                "A partial pose (hands only, etc.) is layered on the pose underneath. Opening a full-body animation clears the layers") : null), plain);
        GUILayout.EndArea();
    }

    void BottomBar(Rect r)
    {
        GUILayout.BeginArea(r);

        // 時間
        GUILayout.BeginHorizontal();
        GUI.enabled = clip != null;
        if (GUILayout.Button(new GUIContent(playing ? "❚❚" : "▶", T("再生・止める(Space)", "Play / pause (Space)")), GUILayout.Width(34))) playing = !playing;
        if (GUILayout.Button(new GUIContent("<", T("1 コマ戻す(←)", "Previous frame (←)")), GUILayout.Width(26)) && clip != null) { playing = false; time = Mathf.Max(0, time - 1f / clip.SampleRate); }
        if (GUILayout.Button(new GUIContent(">", T("1 コマ進める(→)", "Next frame (→)")), GUILayout.Width(26)) && clip != null) { playing = false; time = Mathf.Min(clip.Length, time + 1f / clip.SampleRate); }
        float len = clip != null ? clip.Length : 1;
        var nt = GUILayout.HorizontalSlider(time, 0, len, GUILayout.ExpandWidth(true), GUILayout.Height(20));
        if (clip != null && !Mathf.Approximately(nt, time)) { time = nt; playing = false; }
        GUILayout.Label(clip != null ? $"{time:0.00}/{clip.Length:0.00} {T("秒", "s")} {time * clip.SampleRate:0}f" : "", plain, GUILayout.Width(118));
        loop = GUILayout.Toggle(loop, new GUIContent("↻", T("くり返す", "Loop")), GUILayout.Width(32));
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // 送信
        GUILayout.Space(2);
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(T("ポート", "Port"), T("VRChat の OSC の受信ポート(普通は 9000)", "VRChat's OSC input port (usually 9000)")), plain, GUILayout.Width(40));
        GUI.SetNextControlName("port");
        portText = GUILayout.TextField(portText, GUILayout.Width(48));
        if (GUI.GetNameOfFocusedControl() != "port") ApplyPort();
        var old = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.55f, 0.85f, 1f);
        if (GUILayout.Button(new GUIContent(T("送る", "Send"), T("今の姿勢を送って、任意ポーズを ON に(変わった値だけ送る)", "Send this pose and turn the pose ON (only changed values are sent)")), GUILayout.Width(W(56, 56)), GUILayout.Height(24))) SendNow(false);
        GUI.backgroundColor = poseOn ? new Color(1f, 0.75f, 0.4f) : old;
        if (GUILayout.Button(new GUIContent(poseOn ? T("ポーズ中", "Pose ON") : T("ポーズ OFF", "Pose OFF"), T("任意ポーズの ON・OFF(OFF で普通に戻り、歩ける)", "Turn the pose on or off (OFF returns to normal so you can walk)")), GUILayout.Width(84), GUILayout.Height(24)))
        {
            poseOn = !poseOn;
            sender.SetOn(poseOn);
        }
        GUI.backgroundColor = old;
        if (GUILayout.Button(new GUIContent(T("全部", "All"), T("全部の値を送り直す(VRChat でアバターを読み直したあとや、後から入った人に見せるとき)", "Resend all values (after reloading the avatar in VRChat, or for people who joined later)")), GUILayout.Width(44), GUILayout.Height(24))) SendNow(true);
        var autoLabel = autoSend && autoPaused ? T("動かしたら送る(休止中)", "Auto send (paused)") : T("動かしたら送る", "Auto send");
        var auto = GUILayout.Toggle(autoSend, new GUIContent(autoLabel, T("時刻や体を変えて止めたら、変わった値を送る。起動したときと .anim を開いたときは、「送る」を押すまで休止する", "When you change the time or body and stop, send the changed values. Paused at startup and after opening an .anim, until you press Send")));
        if (auto != autoSend)
        {
            if (auto) { autoPaused = false; lastQueued = lastValues; }   // 付け直したらすぐ再開。その時点の姿勢は送らない
            autoSend = auto;
            SavePrefs();
        }
        GUILayout.FlexibleSpace();
        showSettings = GUILayout.Toggle(showSettings, new GUIContent(T("設定", "Settings"), T("送る速さ", "Sending speed")), GUI.skin.button, GUILayout.Width(W(44, 64)));
        GUILayout.EndHorizontal();
        if (showSettings)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(T("待ち(秒): 値", "Wait (s): value"), T("値を送ってから番号を送るまで", "After sending a value, before sending its index")), plain, GUILayout.Width(W(78, 96)));
            valWaitText = GUILayout.TextField(valWaitText, GUILayout.Width(44));
            GUILayout.Label(new GUIContent(T("番号", "index"), T("番号を送ったあと。カメラ役などほかの人の側で取りこぼすなら長く", "After sending an index. Make it longer if other people miss values")), plain, GUILayout.Width(W(30, 38)));
            idxWaitText = GUILayout.TextField(idxWaitText, GUILayout.Width(44));
            GUILayout.Label(new GUIContent("0", T("番号を 0 に戻したあと", "After setting the index back to 0")), plain, GUILayout.Width(14));
            gapText = GUILayout.TextField(gapText, GUILayout.Width(44));
            sender.NoZero = GUILayout.Toggle(sender.NoZero, new GUIContent(T("0 を挟まない", "No 0"), T("番号を直接次へ(速い)", "Go straight to the next index (faster)")));
            if (GUILayout.Button(T("適用", "Apply"), GUILayout.Width(W(44, 52)))) ApplyWaits();
            if (GUILayout.Button(T("既定", "Default"), GUILayout.Width(W(44, 60))))
            {
                sender.ValWait = 0.025f; sender.IdxWait = 0.1f; sender.Gap = 0.1f; sender.NoZero = true;
                sender.CycleOn = true; sender.CyclePair = 0.3f; ikStepCm = 3f; ikStepDeg = 15f;
                WaitTexts();
                CopySenderSettings();
                SavePrefs();
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            var cy = GUILayout.Toggle(sender.CycleOn, new GUIContent(T("巡回送信", "Cycle send"),
                T("送り終えたあとも、送った値を 1 組ずつ回して送り直し続ける。取りこぼした値や、後から入った人の側のポーズが自然に直る。\n" +
                  "起動してから最初に送るまでは回さない。任意ポーズが OFF の間も回す(ON にした瞬間から正しいポーズに)。\n" +
                  "回すのは送り終えた値で、送る前の編集中の値ではない",
                  "After sending, keep resending the sent values one by one. Values that were missed, and the pose seen by people who join later, fix themselves.\n" +
                  "Starts after your first send. Keeps going while the pose is OFF (so it is right the moment you turn it ON).\n" +
                  "It resends what was sent, not values you are still editing")), GUILayout.Width(W(84, 92)));
            if (cy != sender.CycleOn) { sender.CycleOn = cy; CopySenderSettings(); SavePrefs(); }
            GUILayout.Label(new GUIContent(T("間隔", "every"), T("巡回の 1 組の間隔(秒)。0.3 秒で 100 個がおよそ 30 秒で 1 周", "Seconds per value when cycling. At 0.3 s, 100 values take about 30 s")), plain, GUILayout.Width(W(28, 36)));
            cyclePairText = GUILayout.TextField(cyclePairText, GUILayout.Width(40));
            GUILayout.Space(10);
            var adv = GUILayout.Toggle(advanced, new GUIContent(T("上級機能", "Advanced"),
                T("物を持つ・持った物を回す・手を自由に動かす(☛)の行を出す", "Show the rows for holding objects, turning held objects and moving hands (☛)")), GUILayout.Width(W(80, 84)));
            if (adv != advanced) { advanced = adv; SavePrefs(); }
            GUI.enabled = advanced;
            GUILayout.Label(new GUIContent(T("☛ cm", "☛ cm"), T("手を動かす 1 回の量(cm。アバターの大きさに合わせて伸び縮みする)", "How far one hand move goes (cm, scaled to the avatar size)")), plain, GUILayout.Width(36));
            ikStepCmText = GUILayout.TextField(ikStepCmText, GUILayout.Width(32));
            GUILayout.Label(new GUIContent(T("度", "deg"), T("手首・肘を回す 1 回の角度", "How far one wrist or elbow turn goes (degrees)")), plain, GUILayout.Width(W(16, 26)));
            ikStepDegText = GUILayout.TextField(ikStepDegText, GUILayout.Width(32));
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // 体
        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label(T("体(元のポーズに足す)", "Body (added to the pose)"), plain);
        var ip = GUILayout.Toggle(inPlace, new GUIContent(T("その場で", "In place"), T("アニメーションの左右・前後の移動を使わない", "Ignore the animation's sideways and forward movement")));
        if (ip != inPlace) { inPlace = ip; SavePrefs(); }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(new GUIContent(T("体を戻す", "Reset body"), T("足した分を 0 に", "Set all the added amounts to 0")), GUILayout.Width(W(72, 84)))) offset = new PoseParams.Body();
        GUILayout.EndHorizontal();
        Current(out _, out var body);
        GUILayout.BeginHorizontal();
        offset.Y = BodySlider(T("上下", "Up"), offset.Y, 1, $"{body.Y * PoseParams.PosRange:+0.00;-0.00}");
        offset.Yaw = BodySlider(T("向き", "Turn"), offset.Yaw, PoseParams.YawRange, $"{body.Yaw:+0;-0}°");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        offset.X = BodySlider(T("左右", "Side"), offset.X, 1, $"{body.X * PoseParams.PosRange:+0.00;-0.00}");
        offset.Pitch = BodySlider(T("前傾", "Pitch"), offset.Pitch, PoseParams.PitchRange, $"{body.Pitch:+0;-0}°");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        offset.Z = BodySlider(T("前後", "Fwd"), offset.Z, 1, $"{body.Z * PoseParams.PosRange:+0.00;-0.00}");
        offset.Roll = BodySlider(T("横傾", "Roll"), offset.Roll, PoseParams.RollRange, $"{body.Roll:+0;-0}°");
        GUILayout.EndHorizontal();

        if (advanced) AdvancedRows();

        // 状態
        GUILayout.Space(4);
        var (done, total) = sender.Progress;
        if (hold.Message != null) { status = hold.Message; hold.Message = null; }
        var line = sender.Busy ? T("送信中 ", "Sending ") + $"{done}/{total}" : status;
        if (sender.LastError != null) line = T("送れません: ", "Cannot send: ") + sender.LastError;
        GUILayout.Label(line, plain);
        GUILayout.Label($"OscPoser {Version}   " + (sender.Cycling && !sender.Busy ? T("巡回中  ", "Cycling  ") : "") + T("左ドラッグ: 回す  右: 動かす  ホイール: 寄る  Space: 再生  ←→: 1 コマ", "Left drag: orbit  Right: pan  Wheel: zoom  Space: play  ←→: frame"), small);
        GUILayout.EndArea();
    }

    // 上級機能の行: 物を持つ・回す、手を動かす(☛)
    void AdvancedRows()
    {
        const float B = 26;
        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(T("物", "Obj"), T("持った物(ワールドのピックアップ)の操作。VRChat の OSC の入力を送る。デスクトップで持てるのは右手だけ",
            "Held object (world pickup) controls. Sends VRChat OSC inputs. On desktop only the right hand can hold")), plain, GUILayout.Width(W(20, 28)));
        var old = GUI.backgroundColor;
        if (hold.Holding) GUI.backgroundColor = new Color(1f, 0.75f, 0.4f);
        if (GUILayout.Button(new GUIContent(hold.Holding ? T("持っている", "Holding") : T("持つ", "Grab"),
            T("VRChat の照準を物に合わせておいて押す。VRChat の窓を前面に出して持ち、持ち続ける(1 秒ごとに押し直す)。\n" +
              "持ったらこの窓に戻る。窓が見つからなければ 3 秒待つので、その間に VRChat の窓をクリック。離すのは「離す」",
              "Aim VRChat's crosshair at the object, then press. Brings VRChat's window to the front, grabs and keeps holding (re-pressed every second).\n" +
              "Then comes back to this window. If VRChat's window is not found, it waits 3 s for you to click it. Press Release to let go")),
            GUILayout.Width(W(70, 64)), GUILayout.Height(22)))
            hold.Grab(sender.Port, myWindow);
        GUI.backgroundColor = old;
        if (GUILayout.Button(new GUIContent(T("離す", "Release"), T("持っている物を離す(GrabRight を 0 にして DropRight)", "Let go of the held object (GrabRight 0, then DropRight)")),
            GUILayout.Width(W(44, 60)), GUILayout.Height(22)))
            hold.Release(sender.Port);
        GUILayout.Space(8);
        var fb = T("OSC の MoveHoldFB で、持った物を{0}へ少し動かす(押している間)。\n" +
                   "2026-11 時点のデスクトップ版 VRChat では、なぜか効かない(デスクトップでは VRChat の窓でマウスのホイール)。\n" +
                   "VRChat の更新で使えるようになるかもしれないので置いている",
                   "Move the held object a little {0} with OSC MoveHoldFB (while pressed).\n" +
                   "As of 2026-11 this does not work in desktop VRChat, for unknown reasons (on desktop, use the mouse wheel in VRChat's window).\n" +
                   "It is here in case a VRChat update makes it work");
        HoldButton(new GUIContent("⇈", string.Format(fb, T("奥", "away"))), "MoveHoldFB", +1, B);
        HoldButton(new GUIContent("⇊", string.Format(fb, T("手前", "closer"))), "MoveHoldFB", -1, B);
        GUILayout.Space(8);
        var sp = T("持った物を回す: {0}(押している間。1 回 約 4.6 度ずつ。VRChat の窓が後ろにあっても効く)",
                   "Turn the held object: {0} (while pressed, about 4.6° per step; works with VRChat in the background)");
        HoldButton(new GUIContent("↻", string.Format(sp, T("時計回り", "clockwise"))), "SpinHoldCwCcw", +1, B);
        HoldButton(new GUIContent("↺", string.Format(sp, T("反時計回り", "counterclockwise"))), "SpinHoldCwCcw", -1, B);
        HoldButton(new GUIContent("⤴", string.Format(sp, T("上へ", "up"))), "SpinHoldUD", +1, B);
        HoldButton(new GUIContent("⤵", string.Format(sp, T("下へ", "down"))), "SpinHoldUD", -1, B);
        HoldButton(new GUIContent("⇜", string.Format(sp, T("左へ", "left"))), "SpinHoldLR", -1, B);
        HoldButton(new GUIContent("⇝", string.Format(sp, T("右へ", "right"))), "SpinHoldLR", +1, B);
        GUILayout.EndHorizontal();

        // ☛ 手を自由に動かす
        var ikTip = T("手を自由に動かす(☛)。腕は「手首の位置・手の向き・肘の回り具合」で 1 つに決まる(IK で計算。数秒かかる)。\n" +
                      "上の段: 手首を回す(位置はそのまま)・肘を回す(手首の位置と手の向きはそのまま)。下の段: 手首を動かす(向きと肘はそのまま)。\n" +
                      "逆の操作で元に戻る。届かなければ届く所まで。時刻やアニメーションを変えると元に戻る。体の位置・傾きを変えても手は体についていく。\n" +
                      "VRM は VRChat のアバターと同じものを。結果は「動かしたら送る」か「送る」で送る。1 回の量は設定で",
                      "Move a hand freely (☛). The arm is set by wrist position, hand rotation and elbow swing (solved by IK; takes a few seconds).\n" +
                      "Top row: turn the wrist (position stays), swing the elbow (wrist position and rotation stay). Bottom row: move the wrist (rotation and elbow stay).\n" +
                      "The opposite button undoes. Out of reach: goes as far as it can. Changing the time or animation clears it. The hand follows body offsets.\n" +
                      "Use the same VRM as the VRChat avatar. Sent by Auto send or Send. Step sizes are in Settings");
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent("☛", ikTip), plain, GUILayout.Width(W(20, 28)));
        if (GUILayout.Button(new GUIContent(!ikSelected ? "−" : ikLeft ? T("左", "L") : T("右", "R"),
                T("動かす手(右 / 左 を切り替え。「−」は選んでいない)。3D の画面で手首の白い輪をクリックしても選べる。手でない所をクリックすると外れる",
                  "Hand to move (toggle right / left; \"−\" = none). You can also click the white circle on a wrist in the 3D view. Clicking elsewhere deselects")), GUILayout.Width(B), GUILayout.Height(22)))
        {
            if (!ikSelected) { ikSelected = true; ikLeft = false; }
            else ikLeft = !ikLeft;
        }
        GUILayout.Space(6);
        foreach (var (icon, tip, ax, sg) in new[] {
            ("⟲", T("手首をひねる(手の長さの向きの軸の回り)。手のひらの向きを変える", "Twist the wrist (around the hand's length)"), 'x', 1),
            ("⟳", T("手首をひねる(逆向き)", "Twist the wrist (the other way)"), 'x', -1),
            ("⤹", T("手首を曲げる(手のひらの側 / 甲の側)", "Bend the wrist (palm / back)"), 'z', 1),
            ("⤸", T("手首を曲げる(逆向き)", "Bend the wrist (the other way)"), 'z', -1),
            ("↶", T("手首を横に曲げる(親指の側 / 小指の側)", "Bend the wrist sideways (thumb / little finger)"), 'y', 1),
            ("↷", T("手首を横に曲げる(逆向き)", "Bend the wrist sideways (the other way)"), 'y', -1) })
            if (GUILayout.Button(new GUIContent(icon, tip), this.icon, GUILayout.Width(B), GUILayout.Height(22)))
                IkPress(o => o.Rot.Add((ax, sg * ikStepDeg)));
        GUILayout.Space(6);
        if (GUILayout.Button(new GUIContent("⥁", T("肘を体から離す(手首の位置と手の向きはそのまま。続けて押すと同じ向きに回し続ける)", "Swing the elbow away from the body (wrist stays; keep pressing to keep swinging)")), icon, GUILayout.Width(B), GUILayout.Height(22)))
            IkPress(o => o.Elbow += ikStepDeg);
        if (GUILayout.Button(new GUIContent("⥀", T("肘を体に寄せる(手首の位置と手の向きはそのまま)", "Swing the elbow toward the body (wrist stays)")), icon, GUILayout.Width(B), GUILayout.Height(22)))
            IkPress(o => o.Elbow -= ikStepDeg);
        GUILayout.Space(6);
        var sd = ik.Get(ikLeft);
        GUI.backgroundColor = sd.Grip ? new Color(1f, 0.75f, 0.4f) : old;
        if (GUILayout.Button(new GUIContent("✊", T("指: 軽く握る / 元のポーズの指(切り替え)", "Fingers: light grip / from the pose (toggle)")), icon, GUILayout.Width(B), GUILayout.Height(22)))
            IkPress(o => o.Grip++);
        GUI.backgroundColor = old;
        if (GUILayout.Button(new GUIContent("⌂", T("この手を元のポーズに戻す", "Put this hand back to the pose")), icon, GUILayout.Width(B), GUILayout.Height(22)))
            IkPress(o => { o.Reset = true; o.MoveCm = Vector3.zero; o.Rot.Clear(); o.Elbow = 0; o.Grip = 0; });
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label(GUIContent.none, plain, GUILayout.Width(W(20, 28)));
        GUILayout.Space(B + 6 + 4);
        foreach (var (icon, tip, mv) in new[] {
            ("◂", T("手首を左へ", "Wrist left"), Vector3.left), ("▸", T("手首を右へ", "Wrist right"), Vector3.right),
            ("▴", T("手首を上へ", "Wrist up"), Vector3.up), ("▾", T("手首を下へ", "Wrist down"), Vector3.down),
            ("⊙", T("手首を前へ", "Wrist forward"), Vector3.forward), ("⊗", T("手首を後ろへ", "Wrist back"), Vector3.back) })
            if (GUILayout.Button(new GUIContent(icon, tip), this.icon, GUILayout.Width(B), GUILayout.Height(22)))
                IkPress(o => o.MoveCm += mv * ikStepCm);
        GUILayout.Space(8);
        var gTip = T("画面のハンドル(Unity のように): 移動(W)= 矢印で足元の向きに・白い玉で画面に沿って、回転(E)= 手の骨の 3 つの軸の輪、隠す(Q)。\n" +
                     "黄色い玉と輪は肘: 玉を輪に沿ってドラッグすると肘が回る(手首はそのまま)。もう一方の手首の小さな輪をクリックすると手が替わる",
                     "On-screen handles (like Unity): Move (W) = arrows along the feet's axes, white ball along the screen; Rotate (E) = rings around the hand bone's axes; Hide (Q).\n" +
                     "The yellow ball and ring are the elbow: drag the ball along the ring to swing the elbow (wrist stays). Click the small circle on the other wrist to switch hands");
        foreach (var (mode, label) in new[] { (GizmoMode.Move, T("移動", "Move")), (GizmoMode.Rotate, T("回転", "Rotate")), (GizmoMode.Off, T("隠す", "Hide")) })
        {
            GUI.backgroundColor = gizmo == mode ? new Color(1f, 0.75f, 0.4f) : old;
            if (GUILayout.Button(new GUIContent(label, gTip), GUILayout.Width(W(40, 52)), GUILayout.Height(22))) gizmo = mode;
        }
        GUI.backgroundColor = old;
        GUILayout.Space(6);
        GUILayout.Label(ikBusy ? T("計算中…", "Solving…") : (ik.Active ? T("☛ 使用中", "☛ in use") : ""), small);
        GUILayout.EndHorizontal();
    }

    // 体のスライダー: ラベル、スライダー、今の値(元のポーズ + 足した分)。ダブルクリックの代わりに「0」ボタンで足した分を消す
    float BodySlider(string label, float v, float range, string shown)
    {
        GUILayout.Label(label, plain, GUILayout.Width(W(30, 36)));
        v = GUILayout.HorizontalSlider(v, -range, range, GUILayout.ExpandWidth(true), GUILayout.Height(20));
        GUILayout.Label(shown, plain, GUILayout.Width(44));
        if (GUILayout.Button(new GUIContent("0", T("足した分を消す", "Clear the added amount")), GUILayout.Width(22))) v = 0;
        GUILayout.Space(6);
        return v;
    }
}
