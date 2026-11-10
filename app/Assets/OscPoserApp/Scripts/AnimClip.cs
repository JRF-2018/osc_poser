// Unity の人型のアニメーション(.anim、テキスト形式)を実行時に読み、ある時刻の HumanPose を作る。(VrmPoseViewer のものに、エラーの英語を足した)
// エディターの外では .anim を AnimationClip として読み込めないので、m_FloatCurves の曲線(classID 95)を
// AnimationCurve に組み立て直して Evaluate する(Unity の補間そのまま)。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

public class AnimClip
{
    public string Path;
    public float Length;
    public float SampleRate = 60f;
    public readonly Dictionary<string, AnimationCurve> Curves = new Dictionary<string, AnimationCurve>();

    public string PackageFile;    // .unitypackage から読んだときの、そのファイル(Path はパッケージの中の Assets/... のパス)
    public string Text;           // .unitypackage から読んだときの中身(保存に使う)
    public bool KeepXZ;           // ポーズの .json から作ったもの: 「その場で」でも左右・前後を使う(0.4.0)

    public static AnimClip Load(string path) => Parse(File.ReadAllLines(path), path);

    public static AnimClip FromPackage(string packageFile, string pathName, string text)
    {
        var clip = Parse(text.Replace("\r\n", "\n").Split('\n'), pathName);
        clip.PackageFile = packageFile;
        clip.Text = text;
        return clip;
    }

    static AnimClip Parse(string[] lines, string path)
    {
        var clip = new AnimClip { Path = path };
        // m_FloatCurves を優先し、人型の曲線がなければ m_EditorCurves を読む
        if (!clip.ReadSection(lines, "  m_FloatCurves:"))
            clip.ReadSection(lines, "  m_EditorCurves:");
        if (clip.Curves.Count == 0)
            throw new Exception(PoserApp.T("人型のアニメーションの曲線がありません(テキスト形式の人型の .anim か確かめてください)", "no humanoid curves (is it a humanoid .anim in text format?)"));
        foreach (var line in lines)
        {
            if (line.StartsWith("  m_SampleRate: "))
                clip.SampleRate = F(line.Substring(16));
        }
        return clip;
    }

    static float F(string s)
    {
        s = s.Trim();
        if (s == "Infinity") return float.PositiveInfinity;
        if (s == "-Infinity") return float.NegativeInfinity;
        return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    bool ReadSection(string[] lines, string header)
    {
        int i = Array.IndexOf(lines, header);
        if (i < 0) return false;
        var keys = new List<Keyframe>();
        Keyframe k = default;
        bool inKey = false;
        string attr = null;
        bool humanoid = false;
        void FlushKey()
        {
            if (inKey) keys.Add(k);
            inKey = false;
        }
        void FlushCurve()
        {
            FlushKey();
            if (attr != null && humanoid && keys.Count > 0)
            {
                Curves[attr] = new AnimationCurve(keys.ToArray());
                Length = Mathf.Max(Length, keys[keys.Count - 1].time);
            }
            keys.Clear();
            attr = null;
            humanoid = false;
        }
        for (i++; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith("  m_") || (l.Length > 0 && l[0] != ' ')) break;   // 次の項目
            // 曲線の始まり。古い書き方は "  - curve:"、新しい Unity は "  - serializedVersion: 2" の次に "    curve:"
            if (l.StartsWith("  - ")) { FlushCurve(); continue; }
            if (l.StartsWith("      - serializedVersion:")) { FlushKey(); k = new Keyframe(); k.weightedMode = WeightedMode.None; k.inWeight = k.outWeight = 1f / 3f; inKey = true; continue; }
            if (inKey && l.StartsWith("        "))
            {
                var t = l.Trim();
                int c = t.IndexOf(':');
                if (c < 0) continue;
                var name = t.Substring(0, c);
                var val = t.Substring(c + 1);
                switch (name)
                {
                    case "time": k.time = F(val); break;
                    case "value": k.value = F(val); break;
                    case "inSlope": k.inTangent = F(val); break;
                    case "outSlope": k.outTangent = F(val); break;
                    case "weightedMode": k.weightedMode = (WeightedMode)int.Parse(val.Trim()); break;
                    case "inWeight": k.inWeight = F(val); break;
                    case "outWeight": k.outWeight = F(val); break;
                }
                continue;
            }
            if (l.StartsWith("    attribute: ")) { FlushKey(); attr = l.Substring(15).Trim(); }
            else if (l.StartsWith("    classID: ")) humanoid = l.Substring(13).Trim() == "95";
        }
        FlushCurve();
        return Curves.Count > 0;
    }

    public bool Has(string name) => Curves.ContainsKey(name);

    // 一部のポーズか(0.3.0、pose_send と同じ判定): 体の主な筋肉(指・目・顎・UpperChest を除く)の 8 割に満たないクリップ
    // (手だけ、上半身だけ など)は、入っている値だけを今のポーズに重ねる。全身のクリップは、入っていない筋肉を 0 にする。
    public int CoreHave, CoreTotal;
    public bool Partial
    {
        get
        {
            CoreTotal = 0; CoreHave = 0;
            foreach (var n in PoseParams.MuscleNames)
            {
                if (n.Contains("Hand.") || n.Contains("Eye") || n.Contains("Jaw") || n.Contains("UpperChest")) continue;
                CoreTotal++;
                if (Curves.ContainsKey(n)) CoreHave++;
            }
            return CoreHave < 0.8f * CoreTotal;
        }
    }
    public float Value(string name, float t, float fallback = 0f) =>
        Curves.TryGetValue(name, out var c) ? c.Evaluate(t) : fallback;

    // 時刻 t の値を、筋肉の名前(アニメーションの曲線の名前)→ 値 と、体の位置・向きにする
    public PoseData Sample(float t)
    {
        var p = new PoseData();
        foreach (var kv in Curves)
            if (Muscles.Index.ContainsKey(kv.Key))
                p.Muscles[kv.Key] = kv.Value.Evaluate(t);
        p.HasY = Has("RootT.y");
        p.HasXZ = Has("RootT.x") || Has("RootT.z");
        p.HasQ = Has("RootQ.x") || Has("RootQ.y") || Has("RootQ.z") || Has("RootQ.w");
        if (p.HasY || p.HasXZ)
            p.RootT = new Vector3(Value("RootT.x", t), Value("RootT.y", t, 1f), Value("RootT.z", t));
        if (p.HasQ)
        {
            // 一部の成分だけのクリップもある(例: x・w だけ)。ない成分は単位の向きの値
            var q = new Quaternion(Value("RootQ.x", t), Value("RootQ.y", t), Value("RootQ.z", t), Value("RootQ.w", t, 1f));
            p.RootQ = Quaternion.Normalize(q);
        }
        return p;
    }
}

// 一つの姿勢: 筋肉の値(曲線の名前で)と、体の位置(RootT、人型の単位)・向き(RootQ)
public class PoseData
{
    public readonly Dictionary<string, float> Muscles = new Dictionary<string, float>();
    public Vector3 RootT = new Vector3(0, 1, 0);
    public Quaternion RootQ = Quaternion.identity;
    public bool HasY, HasXZ, HasQ;
}

// 筋肉の名前の表。HumanTrait.MuscleName は指を "Left Thumb 1 Stretched" と呼ぶが、
// アニメーションの曲線では "LeftHand.Thumb.1 Stretched" なので、曲線の名前で引けるようにする。
public static class Muscles
{
    public static readonly Dictionary<string, int> Index = Build();

    static Dictionary<string, int> Build()
    {
        var d = new Dictionary<string, int>();
        var finger = new Regex(@"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (1|2|3|Spread)(?: (.*))?$");
        for (int i = 0; i < HumanTrait.MuscleCount; i++)
        {
            var n = HumanTrait.MuscleName[i];
            d[n] = i;
            var m = finger.Match(n);
            if (m.Success)
            {
                var alt = $"{m.Groups[1].Value}Hand.{m.Groups[2].Value}.{m.Groups[3].Value}";
                if (m.Groups[4].Success) alt += " " + m.Groups[4].Value;
                d[alt] = i;
            }
        }
        return d;
    }
}
