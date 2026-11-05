# OscPoser(exe)を Unity のバッチモードでビルドする(Unity のエディターの画面は開かない)。
# Unity のプロジェクトは D:\Unity\OscPoserApp に置き(sync_to_d.sh がそこへ送る)、
# できたアプリ(OscPoser.exe と、その横に要る OscPoser_Data・UnityPlayer.dll・MonoBleedingEdge)は D:\osc_poser に書き出す。
# 初回は UniVRM を GitHub から取ってくるので数分かかる。記録は build.log。
#   powershell -ExecutionPolicy Bypass -File D:\Unity\OscPoserApp\build.ps1
param([string]$Out = "D:\osc_poser")
$unity = "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
& $unity -batchmode -quit -projectPath $here -buildTarget Win64 -executeMethod BuildApp.Build -appOut "$Out\OscPoser.exe" -logFile "$here\build.log" | Out-Null
if ($LASTEXITCODE -eq 0) { Write-Host "ビルドできました: $Out\OscPoser.exe" }
else { Write-Host "ビルドに失敗しました(終了コード $LASTEXITCODE)。build.log を見てください。" }
exit $LASTEXITCODE
