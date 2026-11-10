// 3D の視点を API・MCP から決める(0.4.1)。ポーズを作るとき、AI が手元に寄ったり斜めから見たりできるように。
//   focus: whole(全身)・head・chest・hips・right_hand・left_hand・hands・feet。見る中心をその骨に(今の姿勢で)
//   azimuth: アバターの体の正面から回った角度(0 = 正面、90 = アバターの右、180 = 後ろ、-90 = 左。度)。体を回したポーズでも体から測る
//   elevation: 見下ろす角度(+ で上から、- で下から。度、±89 まで)
//   distance: 中心からの距離(m)。zoom: 今の距離に掛ける(0.5 で 2 倍に寄る)
//   pan: [右, 上](m、画面の向きで中心をずらす)
//   relative: true なら azimuth・elevation を今の値に足す
// 何も書かなければ今のまま。focus だけ書くと、その部分が画面に収まる距離にする。画面のマウス操作と同じ視点を動かす(窓にも見える)。
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class PoserApp
{
    // 撮影のときに画面のハンドル(☛ の矢印・輪)を隠す
    bool hideGizmoForShot;

    // 部分が画面に収まる距離: 見せたい半分の高さ / (tan(画角/2) × 上下の帯を除いた割合)。ResetView と同じ考え方
    float FitDistance(float halfSize)
    {
        float visible = Mathf.Max(0.2f, 1 - (TopH + bottomH) / Screen.height);
        return Mathf.Clamp(halfSize / (Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * visible), 0.1f, 30f);
    }

    // 体の向き(元のポーズ + 足す分、度)。azimuth はこれから測る(体を回したポーズでも 0 = 正面)
    float BodyYaw()
    {
        Current(out _, out var b);
        return b.Yaw;
    }

    Vector3? FocusPoint(string focus, out float fitDist)
    {
        fitDist = 0;
        var an = vrm != null ? vrm.GetComponent<Animator>() : null;
        if (an == null) return null;
        Vector3 B(HumanBodyBones b) { var t = an.GetBoneTransform(b); return t != null ? t.position : Vector3.zero; }
        float h = modelHeight;
        switch (focus)
        {
            case "whole":
            {
                var hips = B(HumanBodyBones.Hips);
                fitDist = FitDistance(h * 0.6f);
                return new Vector3(hips.x, h * 0.5f + (hips.y - h * 0.55f), hips.z);   // 体の位置(上下・左右・前後)についていく
            }
            case "head": fitDist = FitDistance(0.12f * h); return B(HumanBodyBones.Head) + Vector3.up * 0.05f * h;
            case "chest": fitDist = FitDistance(0.22f * h); return B(HumanBodyBones.Chest);
            case "hips": fitDist = FitDistance(0.3f * h); return B(HumanBodyBones.Hips);
            case "right_hand": fitDist = FitDistance(0.1f * h); return B(HumanBodyBones.RightHand);
            case "left_hand": fitDist = FitDistance(0.1f * h); return B(HumanBodyBones.LeftHand);
            case "hands":
            {
                var r = B(HumanBodyBones.RightHand); var l = B(HumanBodyBones.LeftHand);
                fitDist = FitDistance(Mathf.Max(0.12f * h, (r - l).magnitude * 0.6f));
                return (r + l) * 0.5f;
            }
            case "feet": fitDist = FitDistance(0.18f * h); return (B(HumanBodyBones.RightFoot) + B(HumanBodyBones.LeftFoot)) * 0.5f;
            default: throw new Exception("focus は whole・head・chest・hips・right_hand・left_hand・hands・feet");
        }
    }

    // q の視点の指定を当てる(書いていないものは今のまま)。何か変えたら true
    bool ApplyView(Dictionary<string, object> q)
    {
        bool any = false;
        float by = BodyYaw();
        if (S(q, "view") is string v)   // 前からの指定(front・right・left・back): 全身。向きは体から
        {
            ResetView(v);
            yaw += by;
            any = true;
        }
        var focus = S(q, "focus");
        if (focus != null)
        {
            var p = FocusPoint(focus, out var fit);
            if (p == null) throw new Exception(T("VRM がありません", "no VRM"));
            target = p.Value;
            dist = fit;
            any = true;
        }
        bool rel = Bq(q, "relative");
        if (Fq(q, "azimuth") is float az) { yaw = rel ? yaw + az : 180f + az + by; any = true; }
        if (Fq(q, "elevation") is float el) { pitch = Mathf.Clamp(rel ? pitch + el : el, -89, 89); any = true; }
        if (Fq(q, "distance") is float d) { dist = Mathf.Clamp(d, 0.1f, 30f); any = true; }
        if (Fq(q, "zoom") is float z && z > 0) { dist = Mathf.Clamp(dist * z, 0.1f, 30f); any = true; }
        if (q.TryGetValue("pan", out var po) && po is List<object> pl && pl.Count >= 2)
        {
            var rot = Quaternion.Euler(pitch, yaw, 0);
            target += rot * Vector3.right * Convert.ToSingle(pl[0]) + rot * Vector3.up * Convert.ToSingle(pl[1]);
            any = true;
        }
        if (any) UpdateCamera();
        return any;
    }

    Dictionary<string, object> ViewJson()
    {
        float az = Mathf.DeltaAngle(0, yaw - 180f - BodyYaw());   // 体の向きから
        return new Dictionary<string, object>
        {
            ["azimuth"] = az, ["elevation"] = pitch, ["distance"] = dist,
            ["target"] = new List<object> { target.x, target.y, target.z },
        };
    }
}
