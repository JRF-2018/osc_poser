OscPoser {VERSION}
==================

どんな人型の .anim のポーズでも、あとから OSC で VRChat のアバターにとらせるツールです。
一度 prefab を入れてアップロードすれば、あとはポーズを増やすのに Unity を開く必要はありません。
VRChat とは関係のない、非公式のツールです。
Cluster 用の OSCPoser(ymchn さん)とは、名前が似ていますが別のツールです。
詳しい説明: https://github.com/JRF-2018/osc_poser

■ 中身
  OscPoser.exe(と、横の OscPoser_Data・MonoBleedingEdge・UnityPlayer.dll・UnityCrashHandler64.exe)
      … ポーズを見て送るアプリ。フォルダごと使ってください。
  OscPoser_{VERSION}.unitypackage … アバターに入れる prefab(Modular Avatar 用)

■ 入れ方
  1. アバターの Unity プロジェクトに、Modular Avatar を VCC(ALCOM など)で入れる。
  2. OscPoser_{VERSION}.unitypackage を Unity に入れる。
  3. Assets/OscPoser/OscPoser(prefab)を、ヒエラルキーでアバターの直下にドラッグする。
  4. いつもどおりアップロードする。
  5. VRChat で、アクションメニュー → オプション → OSC を ON にする。

■ 使い方
  1. VRChat で、prefab を入れたアバターを着る。
  2. OscPoser.exe を起動し、VRM(アバターと同じ体型のもの)と .anim(人型、テキスト形式)を窓にドロップする。
  3. 下の時間のバーで、とりたい瞬間を選ぶ。
  4. 「送る」を押す。送り終えると任意ポーズが ON になる(全部で約 12 秒)。
  5. 「ポーズ中」を押すと OFF になり、普通に戻る。
  体の上下・左右・前後・向き・傾きは、下のスライダーで足せます。
  ほかのエモートを選ぶと、任意ポーズは終わります。
  送り終えたあとも、送った値を少しずつ送り直し続けます(巡回送信。「設定」で切れます)。
  手だけ・上半身だけなどの .anim を開くと、今のポーズに重ねます。
  別のポートの VRChat 用には「OscPoser.exe -port 9010」のように起動します(ショートカットに付けると便利)。
  「設定」の「上級機能」で、物を持つ・持った物を回す・手を動かす(☛)の行が出ます。
  説明: https://github.com/JRF-2018/osc_poser#advanced

■ うまくいかないとき
  ・ポートは VRChat の OSC の受信ポート(普通は 9000)にしてください。
  ・前にアップロードしたアバターに prefab を足した場合は、アクションメニュー → オプション → OSC →
    Reset Config を押してください。
  ・後から入った人には巡回送信で 30 秒ほどで届きます。アバターを読み直したあとや急ぐときは、「全部」で送り直してください。
  ・ほかの人から見て姿勢がおかしいときは、「設定」で待ち時間を長くしてください。

■ 作者
  JRF ( http://jrf.cocolog-nifty.com/statuses , Twitter (X): @jion_rockford )
  Claude Code(Claude Opus 5.5)が、JRF のためにオーダーメイドで作ってくれたツールのおすそわけです。
  ほとんどを Claude Opus 5.5 が書きました。

■ ライセンス
  MIT License(LICENSE.txt)。exe には UniVRM(MIT License)が入っています(THIRD_PARTY_NOTICES.txt)。
