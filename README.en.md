# OscPoser

Pose your VRChat avatar from any humanoid .anim later, over OSC.
OscPoser is a Modular Avatar prefab plus a small app (exe) that shows the pose and sends it.
Set up the prefab and upload once; after that, you don't need to open Unity to use new poses.

(日本語: [README.md](README.md))

![OscPoser](docs/images/01_cover.jpg)

This is a tool that Claude Code (Claude Opus 5.5) made to order for me (JRF), shared in case it helps others. I use it to photograph avatars when I report on VRChat worlds.

This is an unofficial tool, not affiliated with VRChat.
It is also unrelated to [OSCPoser by ymchn](https://github.com/ymchn/OSCPoser), which is for Cluster and has a similar name.

## What it does

- Put one prefab on your avatar and upload. Your avatar can then take any pose sent over OSC. No Expressions menu setup is needed.
- Drop a VRM and an .anim onto the app, pick a moment on the timeline, and press Send. Your avatar in VRChat takes that pose.
- You can also add height, sideways and forward offsets, turn, and tilt. This lets you float in the air, lie down, or sit on nothing, which emotes can't do.
- The pose is synced, so other people (such as a camera person) see the same pose.
- The app shows both VRM 0.x and 1.0. The app's UI is in Japanese and English.

## Requirements

- Windows 10 / 11 (64-bit). The app is for Windows.
- VRChat (PC). I have tested it in desktop mode.
- **Modular Avatar** in your avatar's Unity project (Unity 2022.3, VRChat SDK3 Avatars). Install it with VCC (or ALCOM, etc.).
- A VRM file with the same body as your avatar (for the app's preview, for example one exported from VRoid Studio). You can send poses without it, but you can't see them while choosing.
- Humanoid animation .anim files (text format). The ones in your Unity project or in assets work as they are. Animations inside FBX files can't be read.

## Install

Download the zip from [Releases](https://github.com/JRF-2018/osc_poser/releases) or Booth, and extract it anywhere.

![Drop the prefab under your avatar](docs/images/02_install.jpg)

1. Install Modular Avatar into your avatar's Unity project with VCC (or ALCOM, etc.).
2. Import `OscPoser_x.y.z.unitypackage` from the zip into Unity (drag it in, or use Assets → Import Package → Custom Package).
3. In the Hierarchy, drag `Assets/OscPoser/OscPoser` (the prefab) onto your avatar so it is directly under the avatar.
4. Upload as usual.

![Enable OSC in VRChat](docs/images/03_osc.jpg)

5. In VRChat, open the Action Menu → Options → OSC and turn it on.

## Usage

![Open an .anim and press Send](docs/images/04_use.jpg)

1. In VRChat, wear the avatar with the prefab.
2. Start `OscPoser.exe` and drop a VRM and an .anim onto the window (or use the "VRM…" and "Anim…" buttons). The app remembers the last VRM.
3. Choose the moment you want on the timeline at the bottom (▶ plays, < > steps one frame).
4. Press "Send". When all values are sent, the pose turns on.
5. Press "Pose ON" to turn it off. Your avatar goes back to normal and can walk.

### Controls

| Control | What it does |
|---|---|
| Port | VRChat's OSC input port. Usually leave it at 9000 |
| Send | Send the current pose and turn the pose on. Only values that changed since the last send are sent |
| Pose ON / Pose OFF | Turn the pose on or off |
| All | Resend every value (after you reload the avatar in VRChat, or for people who joined the instance later) |
| Auto send | When you move the timeline or a body slider and stop, the changed values are sent. Off at startup and after opening an .anim |
| Body (added to the pose) | Up, Side and Fwd (±1.5 in humanoid units), Turn (±180°), Pitch (±180°), Roll (±90°). "0" resets one axis, "Reset body" resets all |
| In place | Ignore the animation's sideways and forward movement (on by default) |
| Settings | Sending speed (see "How it works" below) |
| On top | Keep the app window on top |
| 日本語 / English | UI language |

Mouse: left drag to orbit, right drag to pan, wheel to zoom. Keys: Space to play, ← → to step one frame, F for the front view.

### With other emotes

- If you choose another emote while posing, the pose ends and that emote plays.
- If you press Send during an emote, the emote is stopped, and the pose starts after a short wait (0.6 s).

## How it works, and notes

- Only 17 bits of sync are used (`OscPoser/On` Bool, `OscPoser/Idx` Int, `OscPoser/Val` Float). The pose values (93 muscles and 6 body values) are copied into unsynced parameters, one index and value pair at a time. A Parameter Driver Copy runs on everyone's side, so other people see the same pose.
- Each pair takes about 0.125 s, so all 99 values take about 12 s. For another moment of the same animation, only the changed values are sent, so it is faster. While values are being sent, the pose changes little by little.
- If the index changes faster than the sync interval, other people can miss values. If the pose looks wrong to a camera person, make the waits longer in "Settings" (defaults: 0.025 s after a value, 0.1 s after an index, 0.1 s after 0, and "No 0" on).
- People who join the instance later don't receive the earlier values. Press "All" to resend them.
- If you add the prefab to an avatar you uploaded before, VRChat's OSC config file for that avatar may be out of date, and the new parameters may not arrive. In that case, press Action Menu → Options → OSC → Reset Config, or delete that avatar's config file in `%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\OSC\`.
- Muscle values go up to ±4 (emotes often use values above 1). Body position goes up to ±1.5, turn and pitch up to ±180°, and roll up to ±90°.
- The pose runs in the Action layer (Modular Avatar adds it to your avatar's Action). The layer that copies values is added to FX. It works even if your avatar uses its own FX (such as XAvatar).
- Tested with: VRChat (PC, desktop mode), a VRoid VRM 0.x avatar converted with VRM Converter for VRChat, and an XAvatar (XWear Packager). Not tested in VR mode.

### Parameters (for other tools)

To send: set `OscPoser/Val` to a value (-1 to 1) → wait a little → set `OscPoser/Idx` to the index → wait → (next pair) → finally set `OscPoser/Idx` to 0. Set `OscPoser/On` to true to turn the pose on.

| Index | Meaning | Value ±1 means |
|---|---|---|
| 1–93 | Humanoid muscles (order: `MuscleNames` in [PoseParams.cs](app/Assets/OscPoserApp/Scripts/PoseParams.cs)) | muscle ±4 |
| 94 / 95 / 96 | Body up / side / forward (RootT.y − 1 / x / z) | ±1.5 |
| 97 | (unused) | |
| 98 / 99 / 100 | Body yaw / pitch / roll, in Unity's YXZ order | ±180° / ±180° / ±90° |

## Earlier tools

There are earlier tools for giving your avatar free poses in desktop VRChat, using OSC and other means (I learned about them after publishing OscPoser). If you want to build poses from scratch, please check them out too.

- ["ポーズ送り" (Pose Sender)](https://booth.pm/ja/items/8715878) by negilemon
- [VRChat Desktop Pose OSC Tool (VRCDP)](https://booth.pm/ja/items/8837110) by RIOKUN

OscPoser is a small tool for making your avatar take poses from .anim files you already have, for photos.

## Building from source (for developers)

- `unity/`: an editor script that builds the prefab (`Assets/OscPoser`). Create an Avatar project with VCC (or ALCOM, etc.), install Modular Avatar, copy `unity/Assets/OscPoserDev` into its `Assets`, and run Tools → OscPoser → Build. Tools → OscPoser → Export unitypackage writes the unitypackage.
- `app/`: the Unity project for the app (Unity 2022.3.22f1, UniVRM 0.128.1). `app/build.ps1` builds it in batch mode (the output folder is `$Out` in the script).

## Author

JRF ( http://jrf.cocolog-nifty.com/statuses , Twitter (X): @jion_rockford )
AI

Claude Opus 5.5 wrote most of it.

## License

MIT License (LICENSE).

The app includes UniVRM (MIT License). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
