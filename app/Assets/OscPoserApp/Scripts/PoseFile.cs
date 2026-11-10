// ポーズの値(pose_send.py の poses/*.json・pose_state*.json と同じ形)を、動かないクリップにする(0.4.0)。
//   { "Chest Front-Back": 0.1, ...(筋肉の値。Unity の筋肉の単位),
//     "(RootT.y)": 上下, "(RootT.x)": 左右, "(RootT.z)": 前後(±1 = ±1.5),
//     "(RootQ.yaw)": 向き(±1 = ±90 度), "(RootQ.pitch)": 前傾(±1 = ±180 度), "(RootQ.roll)": 横傾(±1 = ±90 度) }
// 便利セット 2026 の単位。体の主な筋肉の 8 割に満たなければ、一部のポーズとして今の姿勢に重ねる(.anim と同じ)。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class PoseFile
{
    public const float YawUnit = 90f, PitchUnit = 180f, RollUnit = 90f;

    public static AnimClip Load(string path)
    {
        var d = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
        if (d == null) throw new Exception(PoserApp.T("ポーズの .json ではありません", "not a pose .json"));
        var vals = new Dictionary<string, float>();
        foreach (var kv in d)
            if (kv.Value is double v) vals[kv.Key] = (float)v;
        return FromValues(path, vals);
    }

    public static AnimClip FromValues(string path, IDictionary<string, float> vals)
    {
        var c = new AnimClip { Path = path, Length = 0, KeepXZ = true };
        void K(string name, float v) => c.Curves[name] = AnimationCurve.Constant(0, 0, v);
        foreach (var kv in vals)
            if (Muscles.Index.ContainsKey(kv.Key)) K(kv.Key, kv.Value);
        if (vals.TryGetValue("(RootT.y)", out var y)) K("RootT.y", PoseParams.BaseY + y * PoseParams.PosRange);
        if (vals.TryGetValue("(RootT.x)", out var x)) K("RootT.x", x * PoseParams.PosRange);
        if (vals.TryGetValue("(RootT.z)", out var z)) K("RootT.z", z * PoseParams.PosRange);
        bool hasQ = vals.ContainsKey("(RootQ.yaw)") || vals.ContainsKey("(RootQ.pitch)") || vals.ContainsKey("(RootQ.roll)");
        if (hasQ)
        {
            vals.TryGetValue("(RootQ.yaw)", out var yaw);
            vals.TryGetValue("(RootQ.pitch)", out var pitch);
            vals.TryGetValue("(RootQ.roll)", out var roll);
            var q = Quaternion.Euler(pitch * PitchUnit, yaw * YawUnit, roll * RollUnit);   // Unity の Euler は YXZ の順
            K("RootQ.x", q.x); K("RootQ.y", q.y); K("RootQ.z", q.z); K("RootQ.w", q.w);
        }
        if (c.Curves.Count == 0) throw new Exception(PoserApp.T("ポーズの値がありません", "no pose values"));
        return c;
    }

    // 今の姿勢を同じ形に(筋肉は HumanTrait の順の配列、体は送る値の単位)
    public static Dictionary<string, float> ToValues(float[] muscles, PoseParams.Body b)
    {
        var d = new Dictionary<string, float>();
        foreach (var n in PoseParams.MuscleNames)
            d[n] = Muscles.Index.TryGetValue(n, out var h) ? muscles[h] : 0f;
        d["(RootT.y)"] = b.Y; d["(RootT.x)"] = b.X; d["(RootT.z)"] = b.Z;
        d["(RootQ.yaw)"] = b.Yaw / YawUnit; d["(RootQ.pitch)"] = b.Pitch / PitchUnit; d["(RootQ.roll)"] = b.Roll / RollUnit;
        return d;
    }
}
