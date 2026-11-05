// 任意ポーズの番号と値。Assets/OscPoser の prefab(OscPoserBuilder.cs)と同じ順・同じ範囲。
//   1〜93 筋肉(値 ±1 = 筋肉 ±4)、94 上下・95 左右・96 前後(±1 = 体の位置 ±1.5)、97 欠番、
//   98 Yaw(±1 = ±180 度)、99 Pitch(±180 度)、100 Roll(±90 度)。
using System.Collections.Generic;
using UnityEngine;

public static class PoseParams
{
    public const float MuscleRange = 4f, PosRange = 1.5f, BaseY = 1f;
    public const float YawRange = 180f, PitchRange = 180f, RollRange = 90f;
    public const int Y = 94, X = 95, Z = 96, Yaw = 98, Pitch = 99, Roll = 100;

    public static readonly string[] MuscleNames =
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

    // 体の位置と向き(送る値の単位: 位置は ±1、角度は度)
    public struct Body
    {
        public float Y, X, Z, Yaw, Pitch, Roll;
    }

    // 筋肉(HumanTrait の順の配列)と体から、送る値(番号 → -1〜1)を作る
    public static Dictionary<int, float> Values(float[] muscles, Body b)
    {
        var d = new Dictionary<int, float>();
        for (int i = 0; i < MuscleNames.Length; i++)
            d[i + 1] = Muscles.Index.TryGetValue(MuscleNames[i], out var h) ? muscles[h] / MuscleRange : 0f;
        d[Y] = b.Y;
        d[X] = b.X;
        d[Z] = b.Z;
        d[Yaw] = b.Yaw / YawRange;
        d[Pitch] = b.Pitch / PitchRange;
        d[Roll] = b.Roll / RollRange;
        return d;
    }

    // 体の向き(RootQ)を、Unity の YXZ の順の Yaw・Pitch・Roll(度)に(pose_send の anim_pose.euler_yxz と同じ式)
    public static Vector3 EulerYXZ(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float x = q.x, y = q.y, z = q.z, w = q.w;
        float yaw = Mathf.Atan2(2 * (x * z + y * w), 1 - 2 * (x * x + y * y)) * Mathf.Rad2Deg;
        float pitch = Mathf.Asin(Mathf.Clamp(-2 * (y * z - x * w), -1, 1)) * Mathf.Rad2Deg;
        float roll = Mathf.Atan2(2 * (x * y + z * w), 1 - 2 * (x * x + z * z)) * Mathf.Rad2Deg;
        return new Vector3(yaw, pitch, roll);
    }

    public static float Wrap180(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }
}
