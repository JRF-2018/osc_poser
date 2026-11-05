// OscPoser の配布物(Assets/OscPoser)を作る、開発用のエディターのスクリプト。配布物には入れない。
//
//   Tools → OscPoser → Build              … Assets/OscPoser にクリップ・コントローラー・prefab を作る
//   Tools → OscPoser → Export unitypackage … Build/OscPoser_<版>.unitypackage に書き出す(バッチモードでは -exportOut DIR)
//
// 仕組み(便利セット 2026 の任意ポーズと同じ):
//   同期するのは OscPoser/On(Bool)・OscPoser/Idx(Int)・OscPoser/Val(Float)の 17 bit だけ。
//   FX の「OscPoser Copy」レイヤー: Idx == i のステートに入ると、Parameter Driver の Copy で Val → i 番の値に写す
//     (localOnly なし = 全員の側で写る)。
//   Action の「OscPoser Pose」レイヤー: On で Direct ブレンドツリーのポーズに入る。子は筋肉 93 個と体の位置 3 個
//     (重み 1/K、i 番だけを ±K×範囲にしたクリップを混ぜる)と、体の向きの子(重み RotW = 50)。
//   レイヤーの重みは 0 で、ポーズの間だけ VRC Animator Layer Control で 1 にする(アバター自身のエモートを邪魔しない)。
//   番号: 1〜93 筋肉(pose_muscles.json と同じ順)、94 Y、95 X、96 Z、97 欠番、98 Yaw、99 Pitch、100 Roll。

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace OscPoserDev
{
    public static class OscPoserBuilder
    {
        public const string Version = "0.2.2";
        const string Root = "Assets/OscPoser";
        const string ClipDir = Root + "/Clips";
        const string P = "OscPoser/";

        const float MuscleRange = 4f;   // 値 ±1 で筋肉 ±4(エモートには 1 を超える筋肉がある)
        const float PosRange = 1.5f;    // 値 ±1 で体の位置 ±1.5(人型の単位)
        const float BaseY = 1f;         // 立った姿勢の RootT.y
        const float RotWeight = 50f;    // 向きの子の重み(ほかの子の RootQ は単位なので、向きはこの子がほぼ決める)
        const float ClearWait = 0.6f;   // エモートを終わらせてから任意ポーズに入るまで(秒)。エモートの BlendOut が済むのを待つ
        const float YawRange = 180f, PitchRange = 180f, RollRange = 90f;
        static readonly int[] Yaws = { -180, -135, -90, -45, 0, 45, 90, 135, 180 };
        static readonly int[] Pitches = { -180, -135, -90, -45, 0, 45, 90, 135, 180 };
        static readonly int[] Rolls = { -90, -45, 0, 45, 90 };

        static readonly string[] Muscles =
        {
            "Chest Front-Back",
            "Chest Left-Right",
            "Chest Twist Left-Right",
            "Head Nod Down-Up",
            "Head Tilt Left-Right",
            "Head Turn Left-Right",
            "Left Arm Down-Up",
            "Left Arm Front-Back",
            "Left Arm Twist In-Out",
            "Left Eye Down-Up",
            "Left Eye In-Out",
            "Left Foot Twist In-Out",
            "Left Foot Up-Down",
            "Left Forearm Stretch",
            "Left Forearm Twist In-Out",
            "Left Hand Down-Up",
            "Left Hand In-Out",
            "Left Lower Leg Stretch",
            "Left Lower Leg Twist In-Out",
            "Left Shoulder Down-Up",
            "Left Shoulder Front-Back",
            "Left Toes Up-Down",
            "Left Upper Leg Front-Back",
            "Left Upper Leg In-Out",
            "Left Upper Leg Twist In-Out",
            "LeftHand.Index.1 Stretched",
            "LeftHand.Index.2 Stretched",
            "LeftHand.Index.3 Stretched",
            "LeftHand.Index.Spread",
            "LeftHand.Little.1 Stretched",
            "LeftHand.Little.2 Stretched",
            "LeftHand.Little.3 Stretched",
            "LeftHand.Little.Spread",
            "LeftHand.Middle.1 Stretched",
            "LeftHand.Middle.2 Stretched",
            "LeftHand.Middle.3 Stretched",
            "LeftHand.Middle.Spread",
            "LeftHand.Ring.1 Stretched",
            "LeftHand.Ring.2 Stretched",
            "LeftHand.Ring.3 Stretched",
            "LeftHand.Ring.Spread",
            "LeftHand.Thumb.1 Stretched",
            "LeftHand.Thumb.2 Stretched",
            "LeftHand.Thumb.3 Stretched",
            "LeftHand.Thumb.Spread",
            "Neck Nod Down-Up",
            "Neck Tilt Left-Right",
            "Neck Turn Left-Right",
            "Right Arm Down-Up",
            "Right Arm Front-Back",
            "Right Arm Twist In-Out",
            "Right Eye Down-Up",
            "Right Eye In-Out",
            "Right Foot Twist In-Out",
            "Right Foot Up-Down",
            "Right Forearm Stretch",
            "Right Forearm Twist In-Out",
            "Right Hand Down-Up",
            "Right Hand In-Out",
            "Right Lower Leg Stretch",
            "Right Lower Leg Twist In-Out",
            "Right Shoulder Down-Up",
            "Right Shoulder Front-Back",
            "Right Toes Up-Down",
            "Right Upper Leg Front-Back",
            "Right Upper Leg In-Out",
            "Right Upper Leg Twist In-Out",
            "RightHand.Index.1 Stretched",
            "RightHand.Index.2 Stretched",
            "RightHand.Index.3 Stretched",
            "RightHand.Index.Spread",
            "RightHand.Little.1 Stretched",
            "RightHand.Little.2 Stretched",
            "RightHand.Little.3 Stretched",
            "RightHand.Little.Spread",
            "RightHand.Middle.1 Stretched",
            "RightHand.Middle.2 Stretched",
            "RightHand.Middle.3 Stretched",
            "RightHand.Middle.Spread",
            "RightHand.Ring.1 Stretched",
            "RightHand.Ring.2 Stretched",
            "RightHand.Ring.3 Stretched",
            "RightHand.Ring.Spread",
            "RightHand.Thumb.1 Stretched",
            "RightHand.Thumb.2 Stretched",
            "RightHand.Thumb.3 Stretched",
            "RightHand.Thumb.Spread",
            "Spine Front-Back",
            "Spine Left-Right",
            "Spine Twist Left-Right",
            "UpperChest Front-Back",
            "UpperChest Left-Right",
            "UpperChest Twist Left-Right"
        };

        // 値の一覧: (番号, パラメーター名, 中身)
        static List<(int index, string param, string what)> Values()
        {
            var v = new List<(int, string, string)>();
            for (var i = 0; i < Muscles.Length; i++) v.Add((i + 1, $"{P}M{i + 1:000}", Muscles[i]));
            v.Add((94, P + "Y", "(RootT.y)"));
            v.Add((95, P + "X", "(RootT.x)"));
            v.Add((96, P + "Z", "(RootT.z)"));
            v.Add((98, P + "Yaw", "(RootQ.yaw)"));
            v.Add((99, P + "Pitch", "(RootQ.pitch)"));
            v.Add((100, P + "Roll", "(RootQ.roll)"));
            return v;
        }

        [MenuItem("Tools/OscPoser/Build")]
        public static void Build()
        {
            var values = Values();
            var k = Muscles.Length + 3; // 重み 1/K の子の数
            if (AssetDatabase.IsValidFolder(ClipDir)) AssetDatabase.DeleteAsset(ClipDir);
            foreach (var c in new[] { "OscPoser_FX.controller", "OscPoser_Action.controller" })
                AssetDatabase.DeleteAsset($"{Root}/{c}");
            Folder(Root);
            Folder(ClipDir);
            Folder(ClipDir + "/Pose");
            Folder(ClipDir + "/Rot");

            var clips = new List<(string param, AnimationClip lo, AnimationClip hi)>();
            var rot = new Dictionary<(int, int, int), AnimationClip>();
            try
            {
                AssetDatabase.StartAssetEditing();
                for (var i = 0; i < Muscles.Length; i++)
                {
                    var name = $"M{i + 1:000}";
                    clips.Add((values[i].param,
                        Clip($"Pose/{name}_n", new Dictionary<string, float> { [Muscles[i]] = -k * MuscleRange }, new Vector3(0, BaseY, 0), Quaternion.identity),
                        Clip($"Pose/{name}_p", new Dictionary<string, float> { [Muscles[i]] = k * MuscleRange }, new Vector3(0, BaseY, 0), Quaternion.identity)));
                }
                var none = new Dictionary<string, float>();
                clips.Add((P + "Y", Clip("Pose/Y_n", none, new Vector3(0, BaseY - k * PosRange, 0), Quaternion.identity),
                                    Clip("Pose/Y_p", none, new Vector3(0, BaseY + k * PosRange, 0), Quaternion.identity)));
                clips.Add((P + "X", Clip("Pose/X_n", none, new Vector3(-k * PosRange, BaseY, 0), Quaternion.identity),
                                    Clip("Pose/X_p", none, new Vector3(k * PosRange, BaseY, 0), Quaternion.identity)));
                clips.Add((P + "Z", Clip("Pose/Z_n", none, new Vector3(0, BaseY, -k * PosRange), Quaternion.identity),
                                    Clip("Pose/Z_p", none, new Vector3(0, BaseY, k * PosRange), Quaternion.identity)));
                foreach (var y in Yaws)
                foreach (var p in Pitches)
                foreach (var r in Rolls)
                    rot[(y, p, r)] = Clip($"Rot/Rot_y{y:+000;-000}_p{p:+000;-000}_r{r:+000;-000}", none, Vector3.zero, EulerYXZ(y, p, r));
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            var fx = MakeFx(values);
            var action = MakeAction(clips, rot, WaitClip(), k);
            MakePrefab(fx, action, values);
            WriteTable(values);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"OscPoser {Version}: クリップ {clips.Count * 2 + rot.Count} 個、値 {values.Count} 個で作りました({Root})");
        }

        [MenuItem("Tools/OscPoser/Export unitypackage")]
        public static void Export()
        {
            // バッチモードでは -exportOut DIR で書き出し先を変えられる(既定はプロジェクトの Build/)
            var a = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(a, "-exportOut");
            var dir = i >= 0 && i + 1 < a.Length ? a[i + 1] : "Build";
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"OscPoser_{Version}.unitypackage");
            AssetDatabase.ExportPackage(Root, path, ExportPackageOptions.Recurse);
            Debug.Log($"OscPoser: {Path.GetFullPath(path)} に書き出しました");
            if (!Application.isBatchMode) EditorUtility.RevealInFinder(path);
        }

        // ---- クリップ ----

        static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
        }

        static AnimationClip Clip(string name, Dictionary<string, float> muscles, Vector3 rootT, Quaternion rootQ)
        {
            var clip = new AnimationClip { name = Path.GetFileName(name), frameRate = 60 };
            var attrs = new Dictionary<string, float>(muscles)
            {
                ["RootT.x"] = rootT.x, ["RootT.y"] = rootT.y, ["RootT.z"] = rootT.z,
                ["RootQ.x"] = rootQ.x, ["RootQ.y"] = rootQ.y, ["RootQ.z"] = rootQ.z, ["RootQ.w"] = rootQ.w,
            };
            foreach (var a in attrs)
            {
                // 2 キー(長さ 1 秒)。0.1.0 で exit time に使ったなごり(今は時間を使わない)
                var curve = new AnimationCurve(new Keyframe(0, a.Value), new Keyframe(1, a.Value));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), a.Key), curve);
            }
            var s = AnimationUtility.GetAnimationClipSettings(clip);
            s.loopTime = true;
            s.loopBlendOrientation = s.loopBlendPositionY = s.loopBlendPositionXZ = true;   // Bake Into Pose
            s.keepOriginalOrientation = s.keepOriginalPositionY = s.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, s);
            AssetDatabase.CreateAsset(clip, $"{ClipDir}/{name}.anim");
            return clip;
        }

        // 待つためのクリップ: 長さ 1 秒。アバターにない物の値を書く(何も動かさない)
        static AnimationClip WaitClip()
        {
            var clip = new AnimationClip { name = "Wait", frameRate = 60 };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("_OscPoserWait", typeof(GameObject), "m_IsActive"),
                new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 0)));
            AssetDatabase.CreateAsset(clip, $"{ClipDir}/Wait.anim");
            return clip;
        }

        // Unity の YXZ(Yaw → Pitch → Roll)。連続になるよう半角の式のまま作る(±180 度でも符号が飛ばない)
        static Quaternion EulerYXZ(float yaw, float pitch, float roll)
        {
            Quaternion Axis(float deg, Vector3 ax)
            {
                var h = deg * Mathf.Deg2Rad / 2;
                return new Quaternion(ax.x * Mathf.Sin(h), ax.y * Mathf.Sin(h), ax.z * Mathf.Sin(h), Mathf.Cos(h));
            }
            return Axis(yaw, Vector3.up) * Axis(pitch, Vector3.right) * Axis(roll, Vector3.forward);
        }

        // ---- FX: 値を写す ----

        static AnimatorController MakeFx(List<(int index, string param, string what)> values)
        {
            var c = AnimatorController.CreateAnimatorControllerAtPath($"{Root}/OscPoser_FX.controller");
            c.RemoveLayer(0);
            c.AddParameter(P + "Idx", AnimatorControllerParameterType.Int);
            c.AddParameter(P + "Val", AnimatorControllerParameterType.Float);
            foreach (var v in values) c.AddParameter(v.param, AnimatorControllerParameterType.Float);

            var sm = NewStateMachine(c, "OscPoser Copy");
            var idle = sm.AddState("Idle", new Vector3(300, 0));
            idle.writeDefaultValues = false;
            sm.defaultState = idle;
            var row = 0;
            foreach (var v in values)
            {
                var st = sm.AddState($"Copy_{v.index:000}", new Vector3(600, 40 * row++));
                st.writeDefaultValues = false;
                var drv = st.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                drv.localOnly = false;
                drv.parameters = new List<VRC_AvatarParameterDriver.Parameter>
                {
                    new VRC_AvatarParameterDriver.Parameter
                        { type = VRC_AvatarParameterDriver.ChangeType.Copy, name = v.param, source = P + "Val" },
                };
                var tin = idle.AddTransition(st);
                Instant(tin);
                tin.AddCondition(AnimatorConditionMode.Equals, v.index, P + "Idx");
                var tout = st.AddTransition(idle);
                Instant(tout);
                tout.AddCondition(AnimatorConditionMode.NotEqual, v.index, P + "Idx");
            }
            c.AddLayer(new AnimatorControllerLayer { name = "OscPoser Copy", stateMachine = sm, defaultWeight = 1f });
            EditorUtility.SetDirty(c);
            return c;
        }

        // ---- Action: ポーズ ----

        static AnimatorController MakeAction(List<(string param, AnimationClip lo, AnimationClip hi)> clips,
            Dictionary<(int, int, int), AnimationClip> rot, AnimationClip wait, int k)
        {
            var path = $"{Root}/OscPoser_Action.controller";
            var c = AnimatorController.CreateAnimatorControllerAtPath(path);
            c.RemoveLayer(0);
            c.AddParameter(P + "On", AnimatorControllerParameterType.Bool);
            c.AddParameter("VRCEmote", AnimatorControllerParameterType.Int);   // VRChat の標準のエモート(ほかのエモートに譲るため)
            c.AddParameter(new AnimatorControllerParameter { name = P + "Norm", type = AnimatorControllerParameterType.Float, defaultFloat = 1f / k });
            c.AddParameter(new AnimatorControllerParameter { name = P + "RotW", type = AnimatorControllerParameterType.Float, defaultFloat = RotWeight });
            foreach (var (param, _, _) in clips) c.AddParameter(param, AnimatorControllerParameterType.Float);
            foreach (var n in new[] { "Yaw", "Pitch", "Roll" }) c.AddParameter(P + n, AnimatorControllerParameterType.Float);

            BlendTree Tree(string name)
            {
                var t = new BlendTree { name = name, hideFlags = HideFlags.HideInHierarchy, useAutomaticThresholds = false };
                AssetDatabase.AddObjectToAsset(t, path);
                return t;
            }

            var direct = Tree("Pose");
            direct.blendType = BlendTreeType.Direct;
            var children = new List<ChildMotion>();
            foreach (var (param, lo, hi) in clips)
            {
                var t = Tree(param);
                t.blendType = BlendTreeType.Simple1D;
                t.blendParameter = param;
                t.AddChild(lo, -1f);
                t.AddChild(hi, 1f);
                children.Add(new ChildMotion { motion = t, directBlendParameter = P + "Norm", timeScale = 1 });
            }
            var yawTree = Tree("Rot");
            yawTree.blendType = BlendTreeType.Simple1D;
            yawTree.blendParameter = P + "Yaw";
            foreach (var y in Yaws)
            {
                var inner = Tree($"Rot_yaw{y:+0;-0}");
                inner.blendType = BlendTreeType.FreeformCartesian2D;
                inner.blendParameter = P + "Pitch";
                inner.blendParameterY = P + "Roll";
                foreach (var p in Pitches)
                foreach (var r in Rolls)
                    inner.AddChild(rot[(y, p, r)], new Vector2(p / PitchRange, r / RollRange));
                yawTree.AddChild(inner, y / YawRange);
            }
            children.Add(new ChildMotion { motion = yawTree, directBlendParameter = P + "RotW", timeScale = 1 });
            direct.children = children.ToArray();

            // 状態:
            //   Idle(読み込んだとき)・Out(OFF)→ On で、エモートが出ていなければ(VRCEmote == 0)Pose、出ていれば Clear。
            //   Clear: エモートを終わらせ(VRCEmote = 0)、その BlendOut(Action の重みとトラッキングを戻す)が済むまで待ってから Pose。
            //     (0.1.2 は待たずに入ったので、エモートの BlendOut が後から Action の重みを 0 にし、任意ポーズが一瞬で消えた)
            //   Pose 中にほかのエモート(VRCEmote ≠ 0)が始まったら Yielded(任意ポーズをやめて、そのエモートに譲る)。
            //   On が切れたら Out(Clear・Pose から)か Idle(Yielded から)。
            //   時間で移るのは Clear → Pose だけで、長さのはっきりした専用のクリップ(Wait)を使う(Direct ツリーの exit time は使わない)。
            var sm = NewStateMachine(c, "OscPoser Pose");
            var idle = sm.AddState("Idle", new Vector3(300, 0));
            var clear = sm.AddState("Clear", new Vector3(0, 120));
            var pose = sm.AddState("Pose", new Vector3(300, 240));
            var yielded = sm.AddState("Yielded", new Vector3(600, 300));
            var outSt = sm.AddState("Out", new Vector3(600, 60));
            foreach (var s in new[] { idle, clear, pose, yielded, outSt }) s.writeDefaultValues = false;
            pose.motion = direct;
            clear.motion = wait;
            clear.speed = 1f / ClearWait;   // Wait は 1 秒
            sm.defaultState = idle;

            // Idle: このレイヤーの重みは既定で 0(ポーズ以外のときに、アバター自身の Action を邪魔しない)
            LayerWeight(idle, 0f, 0f);
            // Clear: 出ているエモート(トグル式のもの)を終わらせる(自分の側だけ。同期で全員に伝わる)。最後に選んだものが勝つ
            Drive(clear, "VRCEmote", 0);
            // Pose: レイヤーと Action の重みを 1 に、全身をアニメーションに、移動を止める
            LayerWeight(pose, 1f, 0f);
            var pin = pose.AddStateMachineBehaviour<VRCPlayableLayerControl>();
            pin.layer = VRC_PlayableLayerControl.BlendableLayer.Action;
            pin.goalWeight = 1f;
            pin.blendDuration = 0.25f;
            Tracking(pose, VRC_AnimatorTrackingControl.TrackingType.Animation);
            pose.AddStateMachineBehaviour<VRCAnimatorLocomotionControl>().disableLocomotion = true;
            // Yielded: ほかのエモートに譲る。このレイヤーの重みを 0 に、移動を戻し、On を切る(自分の側だけ)。
            // Action の重みとトラッキングは、そのエモートが扱う(触ると、エモートの始まりとぶつかる)
            LayerWeight(yielded, 0f, 0.1f);
            yielded.AddStateMachineBehaviour<VRCAnimatorLocomotionControl>().disableLocomotion = false;
            Drive(yielded, P + "On", 0);
            // Out: Action の重みとこのレイヤーの重みを、どちらも 0.25 秒で 0 に。動きはなし(WD OFF なので、薄れる間は最後の姿勢のまま)。
            // 0.1.0 は「Out(ポーズのまま)→ exit time で Idle に移ってからレイヤーの重みを 0」だったが、Direct ツリーの長さが
            // 子の重み(向きの子 50)で伸びるらしく、Idle に移るまで 20 秒ほどかかり、その間エモートが任意ポーズに上書きされた。
            var pout = outSt.AddStateMachineBehaviour<VRCPlayableLayerControl>();
            pout.layer = VRC_PlayableLayerControl.BlendableLayer.Action;
            pout.goalWeight = 0f;
            pout.blendDuration = 0.25f;
            LayerWeight(outSt, 0f, 0.25f);
            Tracking(outSt, VRC_AnimatorTrackingControl.TrackingType.Tracking);
            outSt.AddStateMachineBehaviour<VRCAnimatorLocomotionControl>().disableLocomotion = false;

            AnimatorStateTransition Go(AnimatorState from, AnimatorState to, params (AnimatorConditionMode mode, float th, string param)[] conds)
            {
                var tr = from.AddTransition(to);
                Instant(tr);
                foreach (var (mode, th, param) in conds) tr.AddCondition(mode, th, param);
                return tr;
            }
            var on = (AnimatorConditionMode.If, 0f, P + "On");
            var off = (AnimatorConditionMode.IfNot, 0f, P + "On");
            var noEmote = (AnimatorConditionMode.Equals, 0f, "VRCEmote");
            var emote = (AnimatorConditionMode.NotEqual, 0f, "VRCEmote");
            foreach (var from in new[] { idle, outSt })
            {
                Go(from, pose, on, noEmote);
                Go(from, clear, on, emote);
            }
            Go(clear, outSt, off);
            var cp = Go(clear, pose, noEmote);
            cp.hasExitTime = true;
            cp.exitTime = 1f;
            Go(pose, outSt, off);
            Go(pose, yielded, emote);
            Go(yielded, idle, off);

            c.AddLayer(new AnimatorControllerLayer { name = "OscPoser Pose", stateMachine = sm, defaultWeight = 0f });
            EditorUtility.SetDirty(c);
            return c;
        }

        static AnimatorStateMachine NewStateMachine(AnimatorController c, string name)
        {
            var sm = new AnimatorStateMachine { name = name, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(sm, c);
            return sm;
        }

        static void Instant(AnimatorStateTransition t)
        {
            t.hasExitTime = false;
            t.duration = 0;
            t.hasFixedDuration = true;
        }

        // このコントローラーの 0 番(= OscPoser Pose)の重み。合成すると番号がずれるが、MA(NDMF)が直す
        static void LayerWeight(AnimatorState s, float weight, float duration)
        {
            var b = s.AddStateMachineBehaviour<VRCAnimatorLayerControl>();
            b.playable = VRC_AnimatorLayerControl.BlendableLayer.Action;
            b.layer = 0;
            b.goalWeight = weight;
            b.blendDuration = duration;
        }

        // パラメーターを書き換える(自分の側だけ。同期するパラメーターなら、同期でほかの人にも伝わる)
        static void Drive(AnimatorState s, string name, float value)
        {
            var d = s.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            d.localOnly = true;
            d.parameters = new List<VRC_AvatarParameterDriver.Parameter>
            {
                new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = name, value = value },
            };
        }

        static void Tracking(AnimatorState s, VRC_AnimatorTrackingControl.TrackingType t)
        {
            var b = s.AddStateMachineBehaviour<VRCAnimatorTrackingControl>();
            b.trackingHead = b.trackingLeftHand = b.trackingRightHand = b.trackingHip =
                b.trackingLeftFoot = b.trackingRightFoot = b.trackingLeftFingers = b.trackingRightFingers = t;
        }

        // ---- prefab ----

        static void MakePrefab(AnimatorController fx, AnimatorController action, List<(int index, string param, string what)> values)
        {
            var go = new GameObject("OscPoser");
            try
            {
                var mfx = go.AddComponent<ModularAvatarMergeAnimator>();
                mfx.animator = fx;
                mfx.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
                mfx.pathMode = MergeAnimatorPathMode.Absolute;
                mfx.matchAvatarWriteDefaults = true;
                var mac = go.AddComponent<ModularAvatarMergeAnimator>();
                mac.animator = action;
                mac.layerType = VRCAvatarDescriptor.AnimLayerType.Action;
                mac.pathMode = MergeAnimatorPathMode.Absolute;
                mac.matchAvatarWriteDefaults = false;

                var ps = go.AddComponent<ModularAvatarParameters>();
                ps.parameters.Add(Param("On", ParameterSyncType.Bool, false));
                ps.parameters.Add(Param("Idx", ParameterSyncType.Int, false));
                ps.parameters.Add(Param("Val", ParameterSyncType.Float, false));
                // 写し先の値: 同期しない(枠を使わない)。Parameter Driver で書けるよう、Expression Parameters には載せる
                foreach (var v in values)
                    ps.parameters.Add(Param(v.param.Substring(P.Length), ParameterSyncType.Float, true));
                PrefabUtility.SaveAsPrefabAsset(go, $"{Root}/OscPoser.prefab");   // 既にあれば GUID はそのまま
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static ParameterConfig Param(string name, ParameterSyncType type, bool localOnly) => new ParameterConfig
        {
            nameOrPrefix = P + name, syncType = type, localOnly = localOnly, saved = false, defaultValue = 0,
        };

        // exe と pose_send が使う対応表
        static void WriteTable(List<(int index, string param, string what)> values)
        {
            Directory.CreateDirectory("Build");
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($" \"version\": \"{Version}\",\n \"prefix\": \"{P}\",\n");
            sb.Append($" \"muscle_scale\": {MuscleRange},\n \"pos_range\": {PosRange},\n");
            sb.Append($" \"rot_ranges_deg\": {{\"Yaw\": {YawRange}, \"Pitch\": {PitchRange}, \"Roll\": {RollRange}}},\n");
            sb.Append(" \"params\": [\n");
            sb.Append(string.Join(",\n", values.Select(v => $"  {{\"index\": {v.index}, \"param\": \"{v.param}\", \"muscle\": \"{v.what}\"}}")));
            sb.Append("\n ]\n}\n");
            File.WriteAllText("Build/oscposer_params.json", sb.ToString(), new UTF8Encoding(false));
        }
    }
}
