OscPoser {VERSION}
==================

Pose your VRChat avatar from any humanoid .anim later, over OSC.
Set up the prefab and upload once; after that, you don't need to open Unity to use new poses.
This is an unofficial tool, not affiliated with VRChat.
It is also unrelated to OSCPoser by ymchn (for Cluster), which has a similar name.
Full documentation: https://github.com/JRF-2018/osc_poser

* Contents
  OscPoser.exe (with OscPoser_Data, MonoBleedingEdge, UnityPlayer.dll and UnityCrashHandler64.exe next to it)
      ... the app that shows and sends poses. Keep the whole folder together.
  OscPoser_{VERSION}.unitypackage ... the prefab for your avatar (for Modular Avatar)

* Install
  1. Install Modular Avatar into your avatar's Unity project with VCC (or ALCOM, etc.).
  2. Import OscPoser_{VERSION}.unitypackage into Unity.
  3. In the Hierarchy, drag Assets/OscPoser/OscPoser (the prefab) onto your avatar.
  4. Upload as usual.
  5. In VRChat, turn on Action Menu -> Options -> OSC.

* Usage
  1. In VRChat, wear the avatar with the prefab.
  2. Start OscPoser.exe and drop a VRM (with the same body as your avatar) and an .anim
     (humanoid, text format) onto the window.
  3. Choose the moment you want on the timeline at the bottom.
  4. Press "Send". When all values are sent, the pose turns on (about 12 s for everything).
  5. Press "Pose ON" to turn it off and go back to normal.
  You can add height, position, turn and tilt with the sliders at the bottom.
  Choosing another emote ends the pose.

* Troubleshooting
  - The port must be VRChat's OSC input port (usually 9000).
  - If you added the prefab to an avatar you uploaded before, press
    Action Menu -> Options -> OSC -> Reset Config.
  - For people who joined later, or after you reload the avatar, press "All" to resend.
  - If the pose looks wrong to other people, make the waits longer in "Settings".

* Author
  JRF ( http://jrf.cocolog-nifty.com/statuses , Twitter (X): @jion_rockford )
  A tool that Claude Code (Claude Opus 5.5) made to order for JRF, shared with others.
  Claude Opus 5.5 wrote most of it.

* License
  MIT License (LICENSE.txt). The app includes UniVRM (MIT License) (THIRD_PARTY_NOTICES.txt).
