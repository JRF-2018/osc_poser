# Changelog

## 0.2.2 (2026-11-06)

- OscPoser.exe: "Auto send" keeps your setting again. At startup and after opening an .anim it is only paused ("paused until Send"); pressing Send or All resumes it. Turning it back on resumes it right away.
- 「動かしたら送る」は選んだ設定を覚える形に戻した。起動したときと .anim を開いたときは「送るまで休み」になり、「送る」か「全部」で再開する。チェックを付け直してもすぐ再開する。
- The prefab (unitypackage) is unchanged since 0.2.0. / prefab(unitypackage)の中身は 0.2.0 から変わっていない。

## 0.2.1 (2026-11-05)

- OscPoser.exe: "Auto send" is now off at startup and after opening an .anim, so nothing is sent until you press Send (or turn Auto send on and move something). Turning Auto send on no longer sends the current pose right away.
- 「動かしたら送る」は、起動したときと .anim を開いたときは OFF に。開いただけでは送らない。ON にした瞬間も送らず、そのあと動かした分から送る。

## 0.2.0 (2026-11-05)

First public release.

- Modular Avatar prefab: arbitrary pose over OSC (93 muscles, body position and rotation), synced with 17 bits.
- OscPoser.exe: VRM 0.x / 1.0 preview of a humanoid .anim at any time, sending over OSC, pose on/off, body offsets, Japanese and English UI.
- Choosing another emote while posing ends the pose. Sending during an emote stops the emote first.
