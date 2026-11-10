// 手を自由に動かす(上級機能、0.3.0。panel.py の ☛ と VrmPoseViewer の HandIK.cs から)。
// 腕は 7 つの筋肉(上腕の上下・前後・ひねり、肘の曲げ、前腕のひねり、手首の上下・左右)で動くので、
// 「手首の位置 3 + 手の向き 3 + 肘の回り具合 1」を手ごとの状態として持つと、腕の形は 1 つに決まる。
// - 手首を動かしても、手の向きと肘の回り具合はそのまま。手首を回しても、位置と肘はそのまま。肘を回しても、手首の位置と向きはそのまま。
// - 逆の操作で元に戻る。届かなければ届く所まで(本当に届かないときだけ、届いた所を新しい状態に)。
// これを筋肉の値の空間で、Unity のヒューマノイドそのもの(SetHumanPose と骨の位置)を使って解く(レーベンバーグ・マーカート法)。
// 状態は体(腰の位置と体の向き)からの相対で持つ(体の位置・傾きを変えても、手は体についていく)。動かす向きは足元の向き(右・上・前)。
// 肘の回り具合: 肩と手首を結ぶ線の回りに、肘が「体の真後ろ」から何度回っているか(右腕で + は外へ。左腕は写したように)。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

public class HandIK
{
    public static readonly string[] ArmMuscles = { "Arm Down-Up", "Arm Front-Back", "Arm Twist In-Out", "Forearm Stretch",
                                                   "Forearm Twist In-Out", "Hand Down-Up", "Hand In-Out" };
    // 軽く握った指(Stretched は −1 で深く曲がる)
    static readonly Dictionary<string, float> GripFingers = new Dictionary<string, float>
    {
        ["Thumb.1 Stretched"] = -0.3f, ["Thumb.Spread"] = 0.2f, ["Thumb.2 Stretched"] = -0.4f, ["Thumb.3 Stretched"] = -0.4f,
        ["Index.1 Stretched"] = -0.45f, ["Index.Spread"] = 0f, ["Index.2 Stretched"] = -0.6f, ["Index.3 Stretched"] = -0.5f,
        ["Middle.1 Stretched"] = -0.55f, ["Middle.Spread"] = 0f, ["Middle.2 Stretched"] = -0.7f, ["Middle.3 Stretched"] = -0.55f,
        ["Ring.1 Stretched"] = -0.6f, ["Ring.Spread"] = 0f, ["Ring.2 Stretched"] = -0.75f, ["Ring.3 Stretched"] = -0.6f,
        ["Little.1 Stretched"] = -0.65f, ["Little.Spread"] = 0f, ["Little.2 Stretched"] = -0.8f, ["Little.3 Stretched"] = -0.6f,
    };

    public class Side
    {
        public bool HasState;           // 状態(位置・向き・肘)がある = 腕を IK の値にしている
        public Vector3 Pos;             // 手首(体から。体の向きで)
        public Quaternion Rot;          // 手の向き(体の向きで)
        public float Swivel;            // 肘の回り具合(度)
        public float[] Arm;             // 腕の 7 つの筋肉
        public bool Grip;               // 指を軽く握る形に
        public int ElbowBtn, ElbowDir;  // 肘のボタンを続けて押している間の向き(0 = 決め直す)
        public void Reset() { HasState = false; Arm = null; Grip = false; ElbowBtn = ElbowDir = 0; }
        public bool Active => HasState || Grip;
    }

    public readonly Side R = new Side(), L = new Side();
    public Side Get(bool left) => left ? L : R;
    public bool Active => R.Active || L.Active;

    public static int[] ArmIdx(bool left) => ArmMuscles.Select(n => Muscles.Index[$"{(left ? "Left" : "Right")} {n}"]).ToArray();
    public static int[] FingerIdx(bool left) => GripFingers.Keys.Select(n => Muscles.Index[$"{(left ? "Left" : "Right")}Hand.{n}"]).ToArray();
    static float[] GripValues => GripFingers.Values.ToArray();

    // 今の手の状態を筋肉に当てる
    public void Apply(float[] m)
    {
        foreach (var left in new[] { false, true })
        {
            var s = Get(left);
            if (s.HasState && s.Arm != null)
            {
                var idx = ArmIdx(left);
                for (int k = 0; k < idx.Length; k++) m[idx[k]] = s.Arm[k];
            }
            if (s.Grip)
            {
                var idx = FingerIdx(left);
                var v = GripValues;
                for (int k = 0; k < idx.Length; k++) m[idx[k]] = v[k];
            }
        }
    }

    // ---- 解く ----

    public class Request
    {
        public bool Left;
        public Vector3 Move;              // 右・上・前(m。足元の向きで。アバターの大きさは掛ける前)
        public Vector3 MoveAbs;           // 足元の向きで m(大きさを掛けない。画面のハンドルから)
        public Quaternion LocalRot = Quaternion.identity;   // 手の骨の向きで右から掛ける
        public float SwivelDelta;         // 度
        public float Away;                // 度。+ で肘を胸から離す、− で寄せる
    }

    public class Result
    {
        public float[] Arm;
        public Vector3 Pos; public Quaternion Rot; public float Swivel;
        public float Reach, RotError, Scale;
        public float ElbowMoved = -1, ElbowDir;
        public string Note;
    }

    // 肘の回り具合(度)。肩 s・肘 e・手首 w、基準は体の真後ろ。右腕は外へ回すと +、左腕は写したように。
    public static float SwivelOf(Quaternion frame, Vector3 s, Vector3 e, Vector3 w, bool left, out float bend)
    {
        var a = (w - s).normalized;
        var refv = frame * Vector3.back;
        refv -= Vector3.Dot(refv, a) * a;
        if (refv.sqrMagnitude < 1e-6f) { refv = frame * Vector3.down; refv -= Vector3.Dot(refv, a) * a; }
        var ep = (e - s) - Vector3.Dot(e - s, a) * a;
        bend = ep.magnitude / Mathf.Max(1e-4f, (e - s).magnitude);   // 0 = 腕がまっすぐ(回り具合が決まらない)
        if (ep.sqrMagnitude < 1e-10f) return 0f;
        float ang = Vector3.SignedAngle(refv, ep, a);
        var outward = frame * Vector3.right * (left ? -1f : 1f);
        var test = Quaternion.AngleAxis(10f, a) * refv;
        if (Vector3.Dot(test - refv, outward) < 0) ang = -ang;
        return ang;
    }

    static Vector3 RotVec(Quaternion q)   // 回転を「軸 × 角度(ラジアン)」に
    {
        if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
        q.ToAngleAxis(out var deg, out var axis);
        if (float.IsNaN(axis.x) || float.IsInfinity(axis.x)) return Vector3.zero;
        return axis * (deg * Mathf.Deg2Rad);
    }

    // 解く(コルーチン。重いので、1 フレームに 25 ミリ秒ずつ)。start は今の姿勢(この手の IK も当てたもの)。
    // 終わると done(結果)。解いている間も、画面は前の姿勢を出す(Update が毎フレーム当て直すので、ここでも毎回当て直す)。
    public IEnumerator Solve(Animator animator, HumanPoseHandler handler, float[] start, Vector3 bodyPos, Quaternion bodyRot,
                             Request req, Action<Result> done)
    {
        bool left = req.Left;
        var s0 = Get(left);
        var root = animator.transform;
        var ua = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        var la = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        var hd = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        var chest = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        var idx = ArmIdx(left);
        float scale = animator.humanScale;   // 動かす量はアバターの大きさに合わせる
        var hp = new HumanPose();
        var cur = (float[])start.Clone();
        var frame = root.rotation * bodyRot;   // 体の向き
        var frameInv = Quaternion.Inverse(frame);

        void Apply(float[] v)
        {
            var m = (float[])cur.Clone();
            for (int k = 0; k < idx.Length; k++) m[idx[k]] = v[k];
            hp.muscles = m;
            hp.bodyPosition = bodyPos;
            hp.bodyRotation = bodyRot;
            handler.SetHumanPose(ref hp);
        }
        Vector3 P() => frameInv * (hd.position - hips.position);
        Quaternion Rq() => frameInv * hd.rotation;
        float S(out float bend) => SwivelOf(frame, ua.position, la.position, hd.position, left, out bend);

        float[] x = idx.Select(i => cur[i]).ToArray();
        Apply(x);
        Vector3 pos = s0.HasState ? s0.Pos : P();
        Quaternion rot = s0.HasState ? s0.Rot : Rq();
        float swivel = s0.HasState ? s0.Swivel : S(out _);
        var res = new Result { Scale = scale };

        // 目標
        var tPos = pos + frameInv * (root.rotation * (req.Move * scale + req.MoveAbs));
        var tRot = rot * req.LocalRot;
        float dSwivel = req.SwivelDelta;
        if (Mathf.Abs(req.Away) > 1e-3f)
        {
            // 肘を肩と手首を結ぶ線の回りに回して、胸から遠く(近く)なる向きへ。腕の形で外・内は変わるので、
            // 小さく回して確かめ、遠く(近く)なり続ける所まで(いちばん遠い・近い所を越えない)
            var sp = ua.position; var ax = (hd.position - sp).normalized; var el = la.position - sp;
            float Dist(float deg) => ((sp + Quaternion.AngleAxis(deg, ax) * el) - chest.position).magnitude;
            float want = Mathf.Sign(req.Away);
            float dir = (Dist(2f) - Dist(-2f)) * want >= 0 ? 1f : -1f;
            float ang = 0f, d0 = Dist(0f);
            while (ang < Mathf.Abs(req.Away) - 1e-3f)
            {
                float next = Mathf.Min(Mathf.Abs(req.Away), ang + 2f);
                float dn = Dist(dir * next);
                if ((dn - d0) * want <= 1e-5f) break;
                d0 = dn; ang = next;
            }
            float sa = SwivelOf(frame, sp, la.position, hd.position, left, out _);
            float sb = SwivelOf(frame, sp, sp + Quaternion.AngleAxis(5f, ax) * el, hd.position, left, out _);
            float plusSign = Mathf.DeltaAngle(sa, sb) >= 0 ? 1f : -1f;
            dSwivel += ang * dir * plusSign;
            res.ElbowDir = dir * plusSign;
            res.ElbowMoved = ang;
        }
        float tSw = swivel + dSwivel;

        float Wp = 30f, Wr = 60f, Ws = 6f;
        int kStretch = Array.IndexOf(ArmMuscles, "Forearm Stretch");
        int kTwist = Array.IndexOf(ArmMuscles, "Arm Twist In-Out");
        float[] lo = x.Select(_ => -4f).ToArray();
        float[] hi = x.Select(_ => 4f).ToArray();
        hi[kStretch] = Mathf.Max(1f, x[kStretch]);   // 肘は逆には反らせない
        float[] Res(float[] v)
        {
            Apply(v);
            var dp = (P() - tPos) * Wp;
            var dr = RotVec(tRot * Quaternion.Inverse(Rq())) * Wr;
            float sw = S(out var bend);
            float ds = Mathf.DeltaAngle(tSw, sw) * Mathf.Deg2Rad * Ws * Mathf.Clamp01(bend * 3f);
            return new[] { dp.x, dp.y, dp.z, dr.x, dr.y, dr.z, ds };
        }
        float Norm2(float[] r) => r.Sum(t => t * t);

        var watch = Stopwatch.StartNew();
        float[] solved = null, solvedRes = null;
        IEnumerator Lm(float[] x0)
        {
            var xc = (float[])x0.Clone();
            float lambda = 1e-3f;
            var rc = Res(xc);
            int n = xc.Length;
            for (int it = 0; it < 150 && Norm2(rc) > 1e-10f; it++)
            {
                if (watch.ElapsedMilliseconds > 25) { yield return null; watch.Restart(); }
                var J = new float[7, n];
                const float h = 0.004f;
                for (int k = 0; k < n; k++)
                {
                    var xp = (float[])xc.Clone(); xp[k] += h;
                    var rp = Res(xp);
                    for (int i = 0; i < 7; i++) J[i, k] = (rp[i] - rc[i]) / h;
                }
                var A = new double[n, n];
                var b = new double[n];
                for (int p = 0; p < n; p++)
                {
                    for (int q = 0; q < n; q++)
                    {
                        double sum = 0;
                        for (int i = 0; i < 7; i++) sum += J[i, p] * J[i, q];
                        A[p, q] = sum;
                    }
                    double bs = 0;
                    for (int i = 0; i < 7; i++) bs -= J[i, p] * rc[i];
                    b[p] = bs;
                }
                bool improved = false;
                for (int tries = 0; tries < 8 && !improved; tries++)
                {
                    var Al = (double[,])A.Clone();
                    for (int p = 0; p < n; p++) Al[p, p] += lambda * (1 + A[p, p]);
                    var dx = SolveLinear(Al, (double[])b.Clone());
                    if (dx == null) { lambda *= 10; continue; }
                    var xn = xc.Select((v, k) => Mathf.Clamp(v + (float)dx[k], lo[k], hi[k])).ToArray();
                    var rn = Res(xn);
                    if (Norm2(rn) < Norm2(rc)) { xc = xn; rc = rn; lambda = Mathf.Max(1e-6f, lambda * 0.3f); improved = true; }
                    else lambda *= 10;
                }
                if (!improved) break;
            }
            solved = xc; solvedRes = rc;
        }

        // 解き始め: 今の腕と、肘を曲げた腕 × 上腕のひねり(伸びきった腕からだと行き詰まる・肘の向きの違う解に落ちるので)
        var starts = new List<float[]> { x.Select((v, k) => Mathf.Clamp(v, lo[k], hi[k])).ToArray() };
        foreach (var bendTo in new[] { -0.2f, -0.6f })
        {
            if (x[kStretch] <= bendTo + 0.1f) continue;
            foreach (var tw in new[] { float.NaN, -1f, -0.5f, 0f, 0.5f, 1f })
            {
                var sb0 = (float[])starts[0].Clone(); sb0[kStretch] = bendTo;
                if (!float.IsNaN(tw)) sb0[kTwist] = tw;
                starts.Add(sb0);
            }
        }
        float[] best = null; float bestN = float.MaxValue;
        foreach (var st in starts)
        {
            // 強い重みで、と、弱い重み(位置を主に)で解いてから強い重みで詰めたもの、のよいほう
            Wp = 30f; Wr = 60f; Ws = 6f;
            var e = Lm(st); while (e.MoveNext()) yield return e.Current;
            float nd = Norm2(solvedRes);
            if (nd < bestN - 1e-6f) { best = solved; bestN = nd; }
            Wp = 10f; Wr = 2f; Ws = 0.5f;
            e = Lm(st); while (e.MoveNext()) yield return e.Current;
            var xa = solved;
            Wp = 30f; Wr = 60f; Ws = 6f;
            e = Lm(xa); while (e.MoveNext()) yield return e.Current;
            float nn = Norm2(solvedRes);
            if (nn < bestN - 1e-6f) { best = solved; bestN = nn; }
        }
        Apply(best);

        var gotP = P();
        var gotR = Rq();
        float gotS = S(out var bendNow);
        res.Reach = (gotP - tPos).magnitude;
        res.RotError = Quaternion.Angle(gotR, tRot);
        float armLen = (la.position - ua.position).magnitude + (hd.position - la.position).magnitude;
        float fromShoulder = (hips.position + frame * tPos - ua.position).magnitude;
        bool unreachable = fromShoulder > armLen * 0.99f;
        res.Pos = unreachable && res.Reach > 0.01f ? gotP : tPos;   // 本当に届かないときだけ、届いた所を新しい状態に
        res.Rot = tRot;                                             // 手の向きは目標を覚えたまま
        res.Swivel = !unreachable && bendNow > 0.15f && Mathf.Abs(Mathf.DeltaAngle(tSw, gotS)) > 5f ? gotS : tSw;
        res.Arm = best;
        done(res);
    }

    static double[] SolveLinear(double[,] A, double[] b)
    {
        int n = b.Length;
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
            if (Math.Abs(A[piv, c]) < 1e-12) return null;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) { var t = A[c, k]; A[c, k] = A[piv, k]; A[piv, k] = t; }
                var tb = b[c]; b[c] = b[piv]; b[piv] = tb;
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c];
                for (int k = c; k < n; k++) A[r, k] -= f * A[c, k];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= A[r, k] * x[k];
            x[r] = s / A[r, r];
        }
        return x;
    }
}
