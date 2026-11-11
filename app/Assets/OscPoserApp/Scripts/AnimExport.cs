// 今の姿勢を、Unity の人型のアニメーション(.anim、テキスト形式、1 コマ)に書き出す(0.4.2)。
// 書き方は make_benri2026_pose.py の POSE_ANIM と同じ(Unity・VRChat で動いたもの)。m_ClipBindingConstant は空で、
// Unity が読み込むときに作り直す。曲線は m_FloatCurves と m_EditorCurves の両方に(classID 95 = Animator、path は空)。
// 筋肉は HumanTrait の名前(アニメーションの曲線の名前)、体は RootT(位置、人型の単位)・RootQ(向き)。
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class AnimExport
{
    const string Curve =
        "  - curve:\n" +
        "      serializedVersion: 2\n" +
        "      m_Curve:\n" +
        "      - serializedVersion: 3\n" +
        "        time: 0\n" +
        "        value: {0}\n" +
        "        inSlope: 0\n" +
        "        outSlope: 0\n" +
        "        tangentMode: 136\n" +
        "        weightedMode: 0\n" +
        "        inWeight: 0.33333334\n" +
        "        outWeight: 0.33333334\n" +
        "      m_PreInfinity: 2\n" +
        "      m_PostInfinity: 2\n" +
        "      m_RotationOrder: 4\n" +
        "    attribute: {1}\n" +
        "    path: \n" +
        "    classID: 95\n" +
        "    script: {{fileID: 0}}\n";

    const string Head =
        "%YAML 1.1\n" +
        "%TAG !u! tag:unity3d.com,2011:\n" +
        "--- !u!74 &7400000\n" +
        "AnimationClip:\n" +
        "  m_ObjectHideFlags: 0\n" +
        "  m_CorrespondingSourceObject: {fileID: 0}\n" +
        "  m_PrefabInstance: {fileID: 0}\n" +
        "  m_PrefabAsset: {fileID: 0}\n" +
        "  m_Name: ";

    const string Mid1 =
        "  serializedVersion: 6\n" +
        "  m_Legacy: 0\n" +
        "  m_Compressed: 0\n" +
        "  m_UseHighQualityCurve: 1\n" +
        "  m_RotationCurves: []\n" +
        "  m_CompressedRotationCurves: []\n" +
        "  m_EulerCurves: []\n" +
        "  m_PositionCurves: []\n" +
        "  m_ScaleCurves: []\n" +
        "  m_FloatCurves:\n";

    const string Mid2 =
        "  m_PPtrCurves: []\n" +
        "  m_SampleRate: 60\n" +
        "  m_WrapMode: 0\n" +
        "  m_Bounds:\n" +
        "    m_Center: {x: 0, y: 0, z: 0}\n" +
        "    m_Extent: {x: 0, y: 0, z: 0}\n" +
        "  m_ClipBindingConstant:\n" +
        "    genericBindings: []\n" +
        "    pptrCurveMapping: []\n" +
        "  m_AnimationClipSettings:\n" +
        "    serializedVersion: 2\n" +
        "    m_AdditiveReferencePoseClip: {fileID: 0}\n" +
        "    m_AdditiveReferencePoseTime: 0\n" +
        "    m_StartTime: 0\n" +
        "    m_StopTime: 0\n" +
        "    m_OrientationOffsetY: 0\n" +
        "    m_Level: 0\n" +
        "    m_CycleOffset: 0\n" +
        "    m_HasAdditiveReferencePose: 0\n" +
        "    m_LoopTime: 1\n" +
        "    m_LoopBlend: 0\n" +
        "    m_LoopBlendOrientation: 1\n" +
        "    m_LoopBlendPositionY: 1\n" +
        "    m_LoopBlendPositionXZ: 1\n" +
        "    m_KeepOriginalOrientation: 1\n" +
        "    m_KeepOriginalPositionY: 1\n" +
        "    m_KeepOriginalPositionXZ: 1\n" +
        "    m_HeightFromFeet: 0\n" +
        "    m_Mirror: 0\n" +
        "  m_EditorCurves:\n";

    const string Tail =
        "  m_EulerEditorCurves: []\n" +
        "  m_HasGenericRootTransform: 0\n" +
        "  m_HasMotionFloatCurves: 0\n" +
        "  m_Events: []\n";

    // muscles: HumanTrait の順の配列。body: 送る値の単位(位置 ±1 = ±1.5、角度は度)
    public static int Write(string path, float[] muscles, PoseParams.Body b)
    {
        string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);
        var attrs = new List<(string, float)>();
        // 曲線の名前: HumanTrait の名前。指だけはアニメーションでの書き方(「Left Index 1 Stretched」→「LeftHand.Index.1 Stretched」)
        var finger = new Regex(@"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (1|2|3|Spread)(?: (.*))?$");
        for (int i = 0; i < HumanTrait.MuscleCount && i < muscles.Length; i++)
        {
            var n = HumanTrait.MuscleName[i];
            var m = finger.Match(n);
            if (m.Success) n = $"{m.Groups[1].Value}Hand.{m.Groups[2].Value}.{m.Groups[3].Value}" + (m.Groups[4].Success ? " " + m.Groups[4].Value : "");
            attrs.Add((n, muscles[i]));
        }
        var q = Quaternion.Euler(b.Pitch, b.Yaw, b.Roll);   // Unity の Euler は YXZ の順(ApplyPose と同じ)
        attrs.Add(("RootT.x", b.X * PoseParams.PosRange));
        attrs.Add(("RootT.y", PoseParams.BaseY + b.Y * PoseParams.PosRange));
        attrs.Add(("RootT.z", b.Z * PoseParams.PosRange));
        attrs.Add(("RootQ.x", q.x)); attrs.Add(("RootQ.y", q.y)); attrs.Add(("RootQ.z", q.z)); attrs.Add(("RootQ.w", q.w));
        var curves = new StringBuilder();
        foreach (var (a, v) in attrs) curves.AppendFormat(CultureInfo.InvariantCulture, Curve, F(v), a);
        var name = Path.GetFileNameWithoutExtension(path);
        var text = Head + name + "\n" + Mid1 + curves + Mid2 + curves + Tail;
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return attrs.Count;
    }
}
