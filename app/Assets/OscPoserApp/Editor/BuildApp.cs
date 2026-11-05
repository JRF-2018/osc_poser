// バッチモードでビルドする: シーン(PoserApp だけ)を作り、プレイヤーの設定と、実行時に VRM が使うシェーダーを入れて、
// -appOut で渡された場所(既定 Build/OscPoser.exe)に書き出す。build.ps1 から呼ぶ。(VrmPoseViewer の BuildViewer.cs と同じ作り)
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildApp
{
    const string ScenePath = "Assets/OscPoserApp/OscPoser.unity";

    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("PoserApp").AddComponent<PoserApp>();
        EditorSceneManager.SaveScene(scene, ScenePath);

        // UniVRM が実行時に作るマテリアルのシェーダー(シーンから参照されないので、常に含める)。
        // 線を引く Hidden/Internal-Colored は Unity の内蔵なので入れない(入れるとビルドが失敗する)。
        AlwaysInclude("VRM10/MToon10", "UniGLTF/UniUnlit", "Standard");

        PlayerSettings.companyName = "JRF";
        PlayerSettings.productName = "OscPoser";
        PlayerSettings.bundleVersion = PoserApp.Version;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 560;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.colorSpace = ColorSpace.Linear;

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutPath(),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        Debug.Log($"ビルドの結果: {report.summary.result}({report.summary.totalErrors} 件のエラー)");
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    static string OutPath()
    {
        var a = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(a, "-appOut");
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : "Build/OscPoser.exe";
    }

    static void AlwaysInclude(params string[] names)
    {
        var gs = new SerializedObject(AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset"));
        var list = gs.FindProperty("m_AlwaysIncludedShaders");
        for (int i = list.arraySize - 1; i >= 0; i--)
        {
            var s = list.GetArrayElementAtIndex(i).objectReferenceValue;
            if (s != null && s.name == "Hidden/Internal-Colored") list.DeleteArrayElementAtIndex(i);
        }
        foreach (var name in names)
        {
            var shader = Shader.Find(name);
            if (shader == null) { Debug.LogError($"シェーダーがありません: {name}"); continue; }
            bool has = Enumerable.Range(0, list.arraySize).Any(i => list.GetArrayElementAtIndex(i).objectReferenceValue == shader);
            if (has) continue;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
        }
        gs.ApplyModifiedProperties();
    }
}
