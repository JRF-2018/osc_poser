# Changelog

## 0.4.0 (2026-11-10)

- MCP server: start with `-api` (default port 9330) and an AI such as Claude can use OscPoser as MCP tools at `http://127.0.0.1:9330/mcp` (open a pose, set the time, send, on/off, body, hands, read/save the pose, held objects, and a screenshot of the view as an image). `oscposer_mcp.ps1` in the zip bridges stdio-only clients. The same port takes plain HTTP (`GET /state`, `POST /cmd`). Listens on 127.0.0.1 only; requests from browsers (Origin) and other host names are refused. / MCP のサーバー: `-api`(既定 9330)で起動すると、AI が MCP のツールとして使える(ポーズを開く・時刻・送る・ON/OFF・体・手・姿勢の読み書き・物・画面を画像で)。stdio だけの相手には zip の `oscposer_mcp.ps1`。同じ口でふつうの HTTP も。127.0.0.1 だけで、ブラウザからの要求は断る。
- A pose .json (saved with `save_pose`) can be dropped and opened like an .anim. / ポーズの .json もドロップで開ける。
- `-targets 9000,9010,9020`: one window keeps a VRM, pose, body, hands and sending per port; changing the Port field switches. / ポートごとに VRM・ポーズ・体・手・送信を持つ窓(ポートの欄で切り替える)。
- `-help` (or `/?`) shows the command-line options. / `-help`(`/?` でも)で起動オプションの一覧。
- The prefab (unitypackage) is unchanged since 0.2.0 (only the version). / prefab の中身は 0.2.0 から変わっていない(版だけ)。

## 0.3.0 (2026-11-10)

- Cycle send (on by default; Settings): after sending, the sent values keep being resent one pair at a time in shuffled order (0.3 s per pair). Missed values and the pose seen by late joiners fix themselves. It starts after the first send, keeps going while the pose is OFF, and resends the sent values, not the ones being edited. / 巡回送信(既定 ON、設定で切れる・間隔を変えられる): 送り終えたあとも、送った値を順番をばらして 1 組ずつ送り直し続ける。最初の送信のあとから始まり、ポーズ OFF の間も回す。回すのは送り終えた値で、編集中の値ではない。
- Partial .anim (less than 80% of the main body muscles, such as hands only) is layered on the current pose. / 一部のポーズの .anim(手だけなど)は今のポーズに重ねる。
- `-port N` starts a window for that port; the last VRM, animation and time are remembered per port, and the port is not saved. / `-port N` で起動すると、そのポート用の窓に(最後のファイルはポートごとに覚え、ポートは保存しない)。
- Advanced features (Settings → Advanced): Grab (keeps holding; brings VRChat's window to the front, found by its OSC port), Release, turning the held object (SpinHold), MoveHoldFB (does not work in desktop VRChat as of 2026-11), and ☛ moving the pose's hands (IK: wrist position, hand rotation and elbow swing). / 上級機能: 持つ(持ち続ける)・離す・持った物を回す・MoveHoldFB(今のデスクトップでは効かない)・☛ 手を動かす(IK)。
- Advanced: on-screen handles for ☛ in the 3D view (Move W: arrows and a screen-plane ball; Rotate E: rings around the hand bone's axes; elbow: drag a ball along a ring around the shoulder–wrist line; Hide Q). / 3D の画面で ☛ をハンドルで(移動・回転・肘の輪)。
- Tooltips have an almost opaque background. / ツールチップの背景をほぼ不透明に。
- The prefab (unitypackage) is unchanged since 0.2.0 (only the version). / prefab の中身は 0.2.0 から変わっていない(版だけ)。
- README: a whole send occasionally does not arrive (the pose stays as it was), regardless of the waits. Press "All" to resend. / ときどき一回の送信がまるごと届かず、前のポーズのままになることがある(待ち時間に関係なく)。「全部」で送り直す、と書いた。

## 0.2.2 (2026-11-06)

- OscPoser.exe: "Auto send" keeps your setting again. At startup and after opening an .anim it is only paused ("paused until Send"); pressing Send or All resumes it. Turning it back on resumes it right away.
- 「動かしたら送る」は選んだ設定を覚える形に戻した。起動したときと .anim を開いたときは「休止中」になり、「送る」か「全部」で再開する。チェックを付け直してもすぐ再開する。
- The prefab (unitypackage) is unchanged since 0.2.0. / prefab(unitypackage)の中身は 0.2.0 から変わっていない。

## 0.2.1 (2026-11-05)

- OscPoser.exe: "Auto send" is now off at startup and after opening an .anim, so nothing is sent until you press Send (or turn Auto send on and move something). Turning Auto send on no longer sends the current pose right away.
- 「動かしたら送る」は、起動したときと .anim を開いたときは OFF に。開いただけでは送らない。ON にした瞬間も送らず、そのあと動かした分から送る。

## 0.2.0 (2026-11-05)

First public release.

- Modular Avatar prefab: arbitrary pose over OSC (93 muscles, body position and rotation), synced with 17 bits.
- OscPoser.exe: VRM 0.x / 1.0 preview of a humanoid .anim at any time, sending over OSC, pose on/off, body offsets, Japanese and English UI.
- Choosing another emote while posing ends the pose. Sending during an emote stops the emote first.
