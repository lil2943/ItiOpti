using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// 実在するツールのコンポーネントが付いたアバターで、機能ごとの衝突判定を確かめる。
    ///
    /// 【なぜ実物で確かめるか】
    /// 以前、AAO が付いているだけでインポート設定の最適化の適用ボタンが押せなくなっていた。
    /// 判定は「型名の接頭辞」で行うので、**本物の AAO コンポーネントで試さないと
    /// 名前空間の食い違いに気づけない。**
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.ConflictSelfTest.Run
    ///     -hkoptiScenes "Assets/log/zome.unity;Assets/log/Chocolat.unity"
    ///     -hkoptiOut "C:\path\conflict.txt"
    ///
    /// データは変更しない。シーンは保存しない。
    /// </summary>
    public static class ConflictSelfTest
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try { ok = Execute(sb); }
            catch (Exception e)
            {
                ok = false;
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }

            sb.AppendLine();
            sb.AppendLine(ok ? "=== 検証: 成功 ===" : "=== 検証: 失敗 ===");
            var text = sb.ToString();
            Debug.Log(text);

            var outPath = Arg("-hkoptiOut");
            if (!string.IsNullOrEmpty(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
            }
        }

        private static bool Execute(StringBuilder sb)
        {
            var scenes = Arg("-hkoptiScenes");
            if (string.IsNullOrEmpty(scenes)) { sb.AppendLine("-hkoptiScenes 未指定"); return false; }

            bool ok = true;
            int checkedWithAao = 0;

            foreach (var scenePath in scenes.Split(';'))
            {
                var path = scenePath.Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                sb.AppendLine($"■ {path}");

                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null)
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ToList();

                foreach (var d in avatars)
                {
                    var root = d.gameObject;

                    // 実際に AAO が付いているかを、判定器を通さずに直接確かめる
                    bool hasAao = root.GetComponentsInChildren<Component>(true)
                        .Any(c => c != null && c.GetType().FullName != null &&
                                  c.GetType().FullName.StartsWith(
                                      "Anatawa12.AvatarOptimizer.", StringComparison.Ordinal));
                    if (!hasAao) continue;
                    checkedWithAao++;

                    bool importBlocked = ToolConflictDetector.HasBlocking(
                        ToolConflictDetector.Detect(root, OptimizerFeature.ImportSettings));
                    bool atlasBlockedByAao = ToolConflictDetector
                        .Detect(root, OptimizerFeature.TextureAtlas)
                        .Any(c => c.Severity == ConflictSeverity.Blocking &&
                                  c.ToolName.StartsWith("Avatar Optimizer", StringComparison.Ordinal));
                    bool armatureBlocked = ToolConflictDetector.HasBlocking(
                        ToolConflictDetector.Detect(root, OptimizerFeature.ArmatureMerge));

                    // 期待値：インポート設定は止めない / アトラス化も AAO では止めない /
                    //          Armature Merge は止める
                    bool pass = !importBlocked && !atlasBlockedByAao && armatureBlocked;
                    if (!pass) ok = false;

                    sb.AppendLine($"  {(pass ? "OK" : "NG")}  {root.name}");
                    sb.AppendLine($"      インポート設定 : {(importBlocked ? "止める ← 誤り" : "止めない")}");
                    sb.AppendLine($"      アトラス化     : {(atlasBlockedByAao ? "AAO で止める ← 誤り" : "AAO では止めない")}");
                    sb.AppendLine($"      Armature Merge : {(armatureBlocked ? "止める" : "止めない ← 誤り")}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"AAO が付いたアバターを {checkedWithAao} 体確認した。");
            if (checkedWithAao == 0)
            {
                // 0 体では何も確かめていないのに「成功」になってしまう。
                sb.AppendLine("AAO が付いたアバターが見つからないので、検証になっていない。");
                return false;
            }
            return ok;
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
