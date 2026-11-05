// OscPoser: VRM にアニメーション(.anim)のある時刻の姿勢を当てて見て、その姿勢を VRChat に OSC で送る。
// アバターには Assets/OscPoser の prefab(Modular Avatar)を入れておく。
// 姿勢は HumanPoseHandler.SetHumanPose で当てるので、Unity(VRChat)の筋肉の計算そのまま。
//
// 起動オプション(試し用。どれも省略可): -vrm FILE  -anim FILE  -time 秒  -port N  -shot FILE.png  -wait 秒(撮る前)  -quit  -nosave  -lang ja|en
// -shot か -nosave があると、設定(ポート・最後に開いたファイルなど)を書き込まない。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UniVRM10;
using UnityEngine;

public class PoserApp : MonoBehaviour
{
    public const string Version = "0.2.2";
    const float TopH = 58;

    Camera cam;
    Material lineMat;
    Font font;
    readonly Dictionary<string, string> args = new Dictionary<string, string>();
    bool noSave;   // 試しの起動では設定を書き込まない
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

    // アニメーション
    AnimClip clip;
    float time;
    bool playing, loop = true;

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
    GUIStyle small, plain;
    float bottomH = 250;

    // ---- 起動・終了 ----

    void Awake()
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 1; i < a.Length; i++)
        {
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
        font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo UI", "Meiryo", "Yu Gothic UI", "MS UI Gothic" }, 13);
        Application.targetFrameRate = 30;   // VRChat と一緒に動かすので控えめに

        noSave = Arg("shot") != null || Arg("nosave") != null;
        sender = new OscSender();
        LoadPrefs();
    }

    void OnDestroy()
    {
        Camera.onPostRender -= DrawGrid;
        WinNative.UninstallDrop();
        handler?.Dispose();
        sender?.Dispose();
    }

    void OnApplicationQuit() => SavePrefs();

    IEnumerator Start()
    {
        WinNative.InstallDrop();
        WinNative.SetTopmost(topmost);
        ResetView("front");
        if (Arg("port") != null) { portText = Arg("port"); ApplyPort(); }

        var path = Arg("vrm") ?? PlayerPrefs.GetString("vrm", "");
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) LoadVrm(path);
        else status = T("VRM(.vrm)をこの窓にドロップしてください", "Drop a VRM (.vrm) file onto this window");

        var anim = Arg("anim") ?? PlayerPrefs.GetString("anim", "");
        if (!string.IsNullOrEmpty(anim) && File.Exists(anim) && OpenClip(anim))
        {
            var t = Arg("time") != null ? float.Parse(Arg("time"), CultureInfo.InvariantCulture)
                : Arg("anim") == null ? PlayerPrefs.GetFloat("time", clip.Length) : clip.Length;
            time = Mathf.Clamp(t, 0, clip.Length);
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

    void LoadPrefs()
    {
        portText = PlayerPrefs.GetInt("port", 9000).ToString();
        sender.Port = PlayerPrefs.GetInt("port", 9000);
        sender.ValWait = PlayerPrefs.GetFloat("valWait", 0.025f);
        sender.IdxWait = PlayerPrefs.GetFloat("idxWait", 0.1f);
        sender.Gap = PlayerPrefs.GetFloat("gap", 0.1f);
        sender.NoZero = PlayerPrefs.GetInt("noZero", 1) != 0;
        inPlace = PlayerPrefs.GetInt("inPlace", 1) != 0;
        topmost = PlayerPrefs.GetInt("topmost", 0) != 0;
        autoSend = PlayerPrefs.GetInt("autoSend", 1) != 0;
        var lang = Arg("lang") ?? PlayerPrefs.GetString("lang", "");
        english = lang == "" ? Application.systemLanguage != SystemLanguage.Japanese : lang == "en";
        WaitTexts();
    }

    void WaitTexts()
    {
        valWaitText = sender.ValWait.ToString("0.###", CultureInfo.InvariantCulture);
        idxWaitText = sender.IdxWait.ToString("0.###", CultureInfo.InvariantCulture);
        gapText = sender.Gap.ToString("0.###", CultureInfo.InvariantCulture);
    }

    void SavePrefs()
    {
        if (noSave) return;
        PlayerPrefs.SetInt("port", sender.Port);
        PlayerPrefs.SetFloat("valWait", sender.ValWait);
        PlayerPrefs.SetFloat("idxWait", sender.IdxWait);
        PlayerPrefs.SetFloat("gap", sender.Gap);
        PlayerPrefs.SetInt("noZero", sender.NoZero ? 1 : 0);
        PlayerPrefs.SetInt("inPlace", inPlace ? 1 : 0);
        PlayerPrefs.SetInt("topmost", topmost ? 1 : 0);
        PlayerPrefs.SetInt("autoSend", autoSend ? 1 : 0);
        PlayerPrefs.SetString("lang", english ? "en" : "ja");
        if (clip != null) { PlayerPrefs.SetString("anim", clip.Path); PlayerPrefs.SetFloat("time", time); }
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
            if (!noSave) { PlayerPrefs.SetString("vrm", path); PlayerPrefs.Save(); }
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

    bool OpenClip(string path)
    {
        try
        {
            clip = AnimClip.Load(path);
            autoPaused = true;   // 開いただけでは送らない(「送る」で再開)
            time = clip.Length;
            playing = false;
            status = T($"アニメーション: {Path.GetFileName(path)}({clip.Length:0.###} 秒)", $"Animation: {Path.GetFileName(path)} ({clip.Length:0.###} s)");
            if (!noSave) { PlayerPrefs.SetString("anim", path); PlayerPrefs.Save(); }
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
        else if (ext == ".anim") OpenClip(path);
        else status = T($"開けない種類のファイルです: {Path.GetFileName(path)}(.vrm と .anim をドロップできます)", $"Unsupported file: {Path.GetFileName(path)} (drop .vrm or .anim)");
    }

    // ---- 姿勢 ----

    // 今の姿勢: 筋肉(HumanTrait の順、±4 に収める)と体(アニメーションの体 + 足す分)
    void Current(out float[] muscles, out PoseParams.Body body)
    {
        muscles = new float[HumanTrait.MuscleCount];
        var b = new PoseParams.Body();
        if (clip != null)
        {
            var p = clip.Sample(time);
            foreach (var kv in p.Muscles)
                muscles[Muscles.Index[kv.Key]] = Mathf.Clamp(kv.Value, -PoseParams.MuscleRange, PoseParams.MuscleRange);
            b.Y = (p.RootT.y - PoseParams.BaseY) / PoseParams.PosRange;
            if (!inPlace) { b.X = p.RootT.x / PoseParams.PosRange; b.Z = p.RootT.z / PoseParams.PosRange; }
            var e = PoseParams.EulerYXZ(p.RootQ);
            b.Yaw = e.x; b.Pitch = e.y; b.Roll = e.z;
        }
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
        foreach (var f in WinNative.TakeDropped()) OpenDropped(f);
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
        SavePrefs();
    }

    // ---- キー・マウス・カメラ ----

    bool Typing => GUIUtility.keyboardControl != 0;

    void Keys()
    {
        if (Typing) return;
        if (Input.GetKeyDown(KeyCode.F)) ResetView("front");
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
    }

    void OnGUI()
    {
        Style();
        bottomH = showSettings ? 282 : 252;
        float w = Screen.width, h = Screen.height;
        GUI.Box(new Rect(0, 0, w, TopH), GUIContent.none);
        GUI.Box(new Rect(0, h - bottomH, w, bottomH), GUIContent.none);
        TopBar(new Rect(6, 4, w - 12, TopH - 6));
        BottomBar(new Rect(6, h - bottomH + 4, w - 12, bottomH - 8));
        if (!string.IsNullOrEmpty(GUI.tooltip))
        {
            var tip = new GUIContent(GUI.tooltip);
            var size = GUI.skin.box.CalcSize(tip);
            size.x = Mathf.Min(size.x + 8, w - 8);
            var m = Event.current.mousePosition;
            var rect = new Rect(Mathf.Clamp(m.x + 12, 4, w - size.x - 4), m.y + 18 + size.y > h ? m.y - size.y - 6 : m.y + 18, size.x, size.y);
            GUI.Box(rect, tip);
        }
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
        GUILayout.Label($"{(vrmPath != null ? Path.GetFileName(vrmPath) : T("(VRM なし)", "(no VRM)"))}  ／  {(clip != null ? Path.GetFileName(clip.Path) : T("(アニメーションなし: 立った姿勢)", "(no animation: standing)"))}", plain);
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
        var autoLabel = autoSend && autoPaused ? T("動かしたら送る(送るまで休み)", "Auto send (paused)") : T("動かしたら送る", "Auto send");
        var auto = GUILayout.Toggle(autoSend, new GUIContent(autoLabel, T("時刻や体を変えて止めたら、変わった値を送る。起動したときと .anim を開いたときは、「送る」を押すまで休む", "When you change the time or body and stop, send the changed values. Paused at startup and after opening an .anim, until you press Send")));
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
                WaitTexts();
                SavePrefs();
            }
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

        // 状態
        GUILayout.Space(4);
        var (done, total) = sender.Progress;
        var line = sender.Busy ? T("送信中 ", "Sending ") + $"{done}/{total}" : status;
        if (sender.LastError != null) line = T("送れません: ", "Cannot send: ") + sender.LastError;
        GUILayout.Label(line, plain);
        GUILayout.Label($"OscPoser {Version}   " + T("左ドラッグ: 回す  右: 動かす  ホイール: 寄る  Space: 再生  ←→: 1 コマ", "Left drag: orbit  Right: pan  Wheel: zoom  Space: play  ←→: frame"), small);
        GUILayout.EndArea();
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
