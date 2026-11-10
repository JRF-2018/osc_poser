// 画面のハンドルで手を動かす(上級機能、0.3.0)。☛ のボタンと同じく、動かせるのは手首の位置・手の向き・肘の回り具合だけ。
// Unity のエディターのような見た目: W 移動(足元の向きの 3 本の矢印 + 真ん中の玉で画面に沿って)、E 回転(手の骨の 3 つの軸の輪)、Q 隠す。
// 肘: 肩と手首を結ぶ線の回りの輪と、肘の玉。玉を輪に沿ってドラッグすると肘が回る(手首の位置と手の向きはそのまま)。どちらのモードでも出る。
// 手首の小さな白い輪をクリックすると、その手を選ぶ(起動したときはどちらも選んでいない。手でない所をクリックすると外れる)。
// 選んだ手の白い玉をクリック(動かさずに離す)すると、移動と回転が切り替わる。白い玉をつかんでドラッグしている間は、ホイールで見ている向きの奥・手前へも(1 目盛り 1 cm)。
// ドラッグした量は ☛ のボタンと同じ IK(IkPress)に足していく(解いている間の分はためて、まとめて解く)。
// 線は画面の座標で太さのある四角として描き(GL.LoadPixelMatrix)、当たり判定も同じ座標でする。
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class PoserApp
{
    enum GizmoMode { Off, Move, Rotate }
    GizmoMode gizmo = GizmoMode.Move;
    Material gizmoMat;

    enum HKind { Center, Axis, Ring, Elbow, Other }
    class Handle
    {
        public HKind Kind;
        public readonly List<Vector2> Pts = new List<Vector2>();   // 画面の座標(左下が原点)
        public bool Closed, Arrow;
        public float Width = 3f, Radius;                           // Radius > 0 は玉
        public Color Col;
        public bool Left;                                           // Other: どちらの手首の輪か
        public Vector3 Origin, Axis;                                // 世界の座標
        public List<Vector3> World;                                 // 輪の点(つかんだ所を覚えるため)
    }
    readonly List<Handle> handles = new List<Handle>();
    int hotHandle = -1, activeHandle = -1;
    Vector3 gizmoGrab;          // つかんだ所(世界)
    Vector3 gizmoLastMouse;
    Handle dragH;
    bool centerClick;   // 白い玉を押して、まだ動かしていない(このまま離せばクリック)
    Vector3 gizmoDown;   // つかんでいるハンドル(一覧は毎フレーム作り直すので、物を覚える)

    void InitGizmo()
    {
        gizmoMat = new Material(Shader.Find("Hidden/Internal-Colored"));
        gizmoMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        gizmoMat.SetInt("_ZWrite", 0);
        gizmoMat.SetInt("_Cull", 0);
        gizmoMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        gizmoMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        Camera.onPostRender += DrawGizmo;
    }

    bool GizmoShown => advanced && gizmo != GizmoMode.Off && vrm != null && handler != null;

    // 骨の位置から、ハンドルを作る(姿勢を当てたあと = LateUpdate で)
    void BuildGizmo()
    {
        handles.Clear();
        if (!GizmoShown) return;
        var an = vrm.GetComponent<Animator>();
        var root = vrm.transform;
        // 選んでいない手首に白い輪
        foreach (var lf in new[] { false, true })
        {
            if (ikSelected && lf == ikLeft) continue;
            var w = an.GetBoneTransform(lf ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (w != null && ScreenOk(w.position, out var wp))
                handles.Add(new Handle { Kind = HKind.Other, Pts = { wp }, Radius = 8f, Col = new Color(1, 1, 1, 0.9f), Origin = w.position, Width = 2f, Left = lf });
        }
        if (!ikSelected) return;
        bool left = ikLeft;
        var hd = an.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        var la = an.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        var ua = an.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        if (hd == null || la == null || ua == null) return;
        var o = hd.position;
        float size = GizmoSize(o);
        Color R = new Color(0.95f, 0.3f, 0.3f), G = new Color(0.4f, 0.9f, 0.35f), B = new Color(0.35f, 0.55f, 1f);


        // 肘: 肩と手首を結ぶ線の回りの輪と、肘の玉
        var s = ua.position; var e = la.position;
        var a = (o - s).normalized;
        var c = s + a * Vector3.Dot(e - s, a);
        float r = (e - c).magnitude;
        if (r > 1e-3f)
        {
            var u = (e - c) / r; var v = Vector3.Cross(a, u);
            var ring = Ring(c, u * r, v * r);
            if (ring != null) handles.Add(ring.Item1.Fill(new Handle { Kind = HKind.Elbow, Closed = true, Width = 2f, Col = new Color(1f, 0.85f, 0.3f, 0.6f), Origin = c, Axis = a, World = ring.Item2 }));
        }
        if (ScreenOk(e, out var ep))
            handles.Add(new Handle { Kind = HKind.Elbow, Pts = { ep }, Radius = 8f, Col = new Color(1f, 0.85f, 0.3f), Origin = c, Axis = a });

        if (gizmo == GizmoMode.Move)
        {
            foreach (var (ax, col) in new[] { (root.right, R), (root.up, G), (root.forward, B) })
                if (ScreenOk(o, out var p0) && ScreenOk(o + ax * size, out var p1))
                    handles.Add(new Handle { Kind = HKind.Axis, Pts = { p0, p1 }, Arrow = true, Col = col, Origin = o, Axis = ax, Width = 3f });
        }
        else
        {
            foreach (var (ax, col) in new[] { (hd.rotation * Vector3.right, R), (hd.rotation * Vector3.up, G), (hd.rotation * Vector3.forward, B) })
            {
                var u = Vector3.Cross(ax, Mathf.Abs(Vector3.Dot(ax, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
                var v = Vector3.Cross(ax, u);
                var ring = Ring(o, u * size * 0.8f, v * size * 0.8f);
                if (ring != null) handles.Add(ring.Item1.Fill(new Handle { Kind = HKind.Ring, Closed = true, Width = 3f, Col = col, Origin = o, Axis = ax, World = ring.Item2 }));
            }
        }
        // 白い玉: 移動ではドラッグで画面に沿って動かす。どちらのモードでも、クリックで移動と回転を切り替える
        if (ScreenOk(o, out var pc))
            handles.Add(new Handle { Kind = HKind.Center, Pts = { pc }, Radius = 7f, Col = new Color(0.95f, 0.95f, 0.95f), Origin = o });
    }

    // 輪(画面の点と世界の点)。カメラの後ろにかかるなら null
    Tuple<PtList, List<Vector3>> Ring(Vector3 c, Vector3 u, Vector3 v)
    {
        var pts = new PtList(); var world = new List<Vector3>();
        for (int i = 0; i < 48; i++)
        {
            float t = i * Mathf.PI * 2 / 48;
            var w = c + u * Mathf.Cos(t) + v * Mathf.Sin(t);
            if (!ScreenOk(w, out var p)) return null;
            pts.Add(p); world.Add(w);
        }
        return Tuple.Create(pts, world);
    }

    class PtList : List<Vector2>
    {
        public Handle Fill(Handle h) { h.Pts.AddRange(this); return h; }
    }

    // 画面でおよそ 70 画素になる長さ(m)。遠くから見ても近くから見ても同じ大きさに
    float GizmoSize(Vector3 at)
    {
        float d = Vector3.Dot(at - cam.transform.position, cam.transform.forward);
        return Mathf.Max(0.01f, d) * 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height * 70f;
    }

    bool ScreenOk(Vector3 w, out Vector2 p)
    {
        var s = cam.WorldToScreenPoint(w);
        p = new Vector2(s.x, s.y);
        return s.z > 0.01f;
    }

    // 画面の点 m からハンドルまでの距離(画素)
    static float Dist(Handle h, Vector2 m)
    {
        if (h.Radius > 0) return Mathf.Max(0, (h.Pts[0] - m).magnitude - h.Radius);
        float best = float.MaxValue;
        int n = h.Pts.Count;
        for (int i = 0; i + 1 < n || (h.Closed && i < n); i++)
        {
            var a = h.Pts[i]; var b = h.Pts[(i + 1) % n];
            var ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0 : Mathf.Clamp01(Vector2.Dot(m - a, ab) / ab.sqrMagnitude);
            best = Mathf.Min(best, (a + ab * t - m).magnitude);
        }
        return best;
    }

    int Pick(Vector2 m)
    {
        // 玉が先(肘・真ん中・もう一方の手)、次に線
        int best = -1; float bd = 6f;
        for (int pass = 0; pass < 2 && best < 0; pass++)
            for (int i = 0; i < handles.Count; i++)
            {
                if ((handles[i].Radius > 0) != (pass == 0)) continue;
                float d = Dist(handles[i], m);
                if (d < bd) { bd = d; best = i; }
            }
        return best;
    }

    // マウスを押したとき: ハンドルをつかんだら true(カメラは回さない)
    bool GizmoMouseDown(Vector3 mouse)
    {
        if (!GizmoShown) return false;
        int i = Pick(mouse);
        if (i < 0) return false;
        var h = handles[i];
        if (h.Kind == HKind.Other) { ikLeft = h.Left; ikSelected = true; return true; }
        activeHandle = i;
        centerClick = h.Kind == HKind.Center;
        gizmoDown = mouse;
        dragH = h;
        gizmoLastMouse = mouse;
        gizmoGrab = h.Origin;
        if (h.World != null)   // 輪: つかんだ所にいちばん近い点
        {
            int k = 0; float bd = float.MaxValue;
            for (int j = 0; j < h.Pts.Count; j++) { float d = (h.Pts[j] - (Vector2)mouse).sqrMagnitude; if (d < bd) { bd = d; k = j; } }
            gizmoGrab = h.World[k];
        }
        else if (h.Kind == HKind.Elbow)
            gizmoGrab = vrm.GetComponent<Animator>().GetBoneTransform(ikLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).position;
        return true;
    }

    // ドラッグ中(Update で)。離したら false
    bool GizmoDrag(Vector3 mouse, bool held)
    {
        if (dragH == null) return false;
        var h = dragH;
        if (!held || !GizmoShown)
        {
            if (centerClick && held == false) gizmo = gizmo == GizmoMode.Move ? GizmoMode.Rotate : GizmoMode.Move;   // 白い玉のクリック
            centerClick = false;
            activeHandle = -1; dragH = null;
            return false;
        }
        // 白い玉をつかんでいる間のホイール: 見ている向きの奥(+)・手前へ(視点は寄らない)
        float wheel = Input.mouseScrollDelta.y;
        if (h.Kind == HKind.Center && gizmo == GizmoMode.Move && wheel != 0)
        {
            centerClick = false;   // ホイールを回したら、クリックではない
            var dz = cam.transform.forward * (0.01f * wheel);
            var ri = Quaternion.Inverse(vrm.transform.rotation);
            IkPress(o => o.MoveAbs += ri * dz);
        }
        if (centerClick)
        {
            if ((mouse - gizmoDown).magnitude <= 3f) return true;   // まだクリックかもしれない
            centerClick = false;
            gizmoLastMouse = gizmoDown;   // 押した所から動かす
        }
        var md = (Vector2)(mouse - gizmoLastMouse);
        gizmoLastMouse = mouse;
        if (md.sqrMagnitude < 0.01f) return true;
        var rootInv = Quaternion.Inverse(vrm.transform.rotation);
        switch (h.Kind)
        {
            case HKind.Center:
            {
                if (gizmo != GizmoMode.Move) break;   // 回転のときはクリックだけ
                // 画面に沿って(手首の奥行きで)
                float z = cam.WorldToScreenPoint(h.Origin).z;
                var d = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, z)) - cam.ScreenToWorldPoint(new Vector3(mouse.x - md.x, mouse.y - md.y, z));
                IkPress(o => o.MoveAbs += rootInv * d);
                break;
            }
            case HKind.Axis:
            {
                var ds = h.Pts[1] - h.Pts[0];
                if (ds.sqrMagnitude < 4f) break;   // 矢印がこちらを向いていて短い
                float len = GizmoSize(h.Origin);
                float t = Vector2.Dot(md, ds) / ds.sqrMagnitude * len;
                IkPress(o => o.MoveAbs += rootInv * (h.Axis * t));
                break;
            }
            case HKind.Ring:
            case HKind.Elbow:
            {
                // つかんだ所の、回る向きの接線に沿った動きで回す
                float deg = TangentDeg(h.Origin, h.Axis, gizmoGrab, md);
                if (deg == 0) break;
                if (h.Kind == HKind.Ring)
                {
                    var hd = vrm.GetComponent<Animator>().GetBoneTransform(ikLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    var local = Quaternion.Inverse(hd.rotation) * h.Axis;
                    IkPress(o => o.RawRot *= Quaternion.AngleAxis(deg, local));
                }
                else
                {
                    // 肘を線の回りに deg 回す → 肘の回り具合(右腕で外へ +)の向きに直す
                    var an = vrm.GetComponent<Animator>();
                    var s = an.GetBoneTransform(ikLeft ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm).position;
                    var e = an.GetBoneTransform(ikLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).position;
                    var w = an.GetBoneTransform(ikLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position;
                    Current(out _, out var b);
                    var frame = vrm.transform.rotation * Quaternion.Euler(b.Pitch, b.Yaw, b.Roll);
                    float s0 = HandIK.SwivelOf(frame, s, e, w, ikLeft, out _);
                    float s1 = HandIK.SwivelOf(frame, s, s + Quaternion.AngleAxis(5f, h.Axis) * (e - s), w, ikLeft, out _);
                    float sign = Mathf.DeltaAngle(s0, s1) >= 0 ? 1f : -1f;
                    IkPress(o => o.SwivelRaw += deg * sign);
                }
                // つかんだ所も一緒に回す(次の接線のため)
                gizmoGrab = h.Origin + Quaternion.AngleAxis(deg, h.Axis) * (gizmoGrab - h.Origin);
                break;
            }
        }
        return true;
    }

    // 点 g を軸 axis(中心 c)の回りに回すとき、画面の動き md がどれだけの角度(度)に当たるか
    float TangentDeg(Vector3 c, Vector3 axis, Vector3 g, Vector2 md)
    {
        var rel = g - c;
        if (rel.sqrMagnitude < 1e-8f) return 0;
        var t = Quaternion.AngleAxis(5f, axis) * rel - rel;   // 5 度回したときの動き
        if (!ScreenOk(g, out var p0) || !ScreenOk(g + t, out var p1)) return 0;
        var ts = p1 - p0;
        if (ts.sqrMagnitude < 0.25f) return 0;   // 輪をほぼ真横から見ている所
        return Vector2.Dot(md, ts) / ts.sqrMagnitude * 5f;
    }

    // 試し用(-gizmotest): kind の handle を(axisDot の向きのものを)つかんで、画面で (dx, dy) 画素ずつ n フレーム動かす
    System.Collections.IEnumerator GizmoTest(HKind kind, Vector3 axis, bool ball, Vector2 step, int n)
    {
        yield return null;
        ikSelected = true;
        yield return null;
        var h = handles.Find(x => x.Kind == kind && (x.Radius > 0) == ball && (axis == Vector3.zero || Vector3.Dot(x.Axis, axis) > 0.9f));
        if (h == null) { Debug.Log($"gizmotest: {kind} が見つかりません"); yield break; }
        var m = (Vector3)(h.Radius > 0 ? h.Pts[0] : h.Pts[h.Pts.Count - 1]);
        if (h.Closed) m = h.Pts[0];
        GizmoMouseDown(m);
        for (int i = 0; i < n; i++)
        {
            m += (Vector3)step;
            GizmoDrag(m, true);
            yield return null;
        }
        GizmoDrag(m, false);
        while (ikBusy || ikPending != null) yield return null;
        Current(out var dm, out _);
        Debug.Log($"gizmotest {kind}: {status} | arm " + string.Join(" ", System.Linq.Enumerable.Select(ArmIdxs[ikLeft ? 1 : 0], i => dm[i].ToString("0.000"))));
    }

    // つかんでいるハンドルの一覧の中の番号(光らせるため)と、マウスの下のハンドル
    void UpdateHot()
    {
        activeHandle = -1;
        if (dragH != null)
            for (int i = 0; i < handles.Count; i++)
                if (handles[i].Kind == dragH.Kind && handles[i].Radius > 0 == dragH.Radius > 0 && Vector3.Dot(handles[i].Axis, dragH.Axis) > 0.99f) { activeHandle = i; break; }
        var m = Input.mousePosition;
        hotHandle = dragH == null && !OverUi(m) ? Pick(m) : -1;
    }

    void DrawGizmo(Camera c)
    {
        if (c != cam || handles.Count == 0) return;
        if (hotHandle >= handles.Count) hotHandle = -1;
        gizmoMat.SetPass(0);
        GL.PushMatrix();
        GL.LoadPixelMatrix();
        for (int i = 0; i < handles.Count; i++)
        {
            var h = handles[i];
            var col = i == activeHandle || (activeHandle < 0 && i == hotHandle) ? new Color(1f, 1f, 0.3f) : h.Col;
            if (h.Radius > 0) { Disc(h.Pts[0], h.Radius, col, h.Kind == HKind.Other); continue; }
            GL.Begin(GL.QUADS);
            GL.Color(col);
            int n = h.Pts.Count;
            for (int j = 0; j + 1 < n || (h.Closed && j < n); j++) Seg(h.Pts[j], h.Pts[(j + 1) % n], h.Width);
            GL.End();
            if (h.Arrow && n >= 2)
            {
                var a = h.Pts[n - 2]; var b = h.Pts[n - 1];
                var d = (b - a); if (d.sqrMagnitude < 1) continue;
                d.Normalize(); var nn = new Vector2(-d.y, d.x);
                GL.Begin(GL.TRIANGLES);
                GL.Color(col);
                GL.Vertex3(b.x + d.x * 12, b.y + d.y * 12, 0);
                GL.Vertex3(b.x + nn.x * 6, b.y + nn.y * 6, 0);
                GL.Vertex3(b.x - nn.x * 6, b.y - nn.y * 6, 0);
                GL.End();
            }
        }
        GL.PopMatrix();
    }

    static void Seg(Vector2 a, Vector2 b, float w)
    {
        var d = b - a; if (d.sqrMagnitude < 1e-6f) return;
        var n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
        GL.Vertex3(a.x - n.x, a.y - n.y, 0); GL.Vertex3(b.x - n.x, b.y - n.y, 0);
        GL.Vertex3(b.x + n.x, b.y + n.y, 0); GL.Vertex3(a.x + n.x, a.y + n.y, 0);
    }

    static void Disc(Vector2 c, float r, Color col, bool hollow)
    {
        if (hollow)
        {
            Disc(c, r, new Color(0.1f, 0.1f, 0.15f, 0.55f), false);   // 白い袖の上でも見えるよう、暗い下地
            GL.Begin(GL.QUADS); GL.Color(col);
            for (int i = 0; i < 24; i++)
            {
                float t0 = i * Mathf.PI * 2 / 24, t1 = (i + 1) * Mathf.PI * 2 / 24;
                Seg(c + new Vector2(Mathf.Cos(t0), Mathf.Sin(t0)) * r, c + new Vector2(Mathf.Cos(t1), Mathf.Sin(t1)) * r, 2.5f);
            }
            GL.End();
            return;
        }
        GL.Begin(GL.TRIANGLES); GL.Color(col);
        for (int i = 0; i < 24; i++)
        {
            float t0 = i * Mathf.PI * 2 / 24, t1 = (i + 1) * Mathf.PI * 2 / 24;
            GL.Vertex3(c.x, c.y, 0);
            GL.Vertex3(c.x + Mathf.Cos(t0) * r, c.y + Mathf.Sin(t0) * r, 0);
            GL.Vertex3(c.x + Mathf.Cos(t1) * r, c.y + Mathf.Sin(t1) * r, 0);
        }
        GL.End();
    }
}
