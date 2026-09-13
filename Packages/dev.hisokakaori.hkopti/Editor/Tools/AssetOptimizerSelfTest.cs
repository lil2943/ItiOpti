using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// AssetOptimizer の「適用 → 復元」が本当に往復できるかを実アセットで検証する。
    ///
    /// ここが安全網なので、動作確認せずに使わせるわけにいかない。
    /// 少数のアセットだけを対象に、
    ///   1) 変更前の状態を記録
    ///   2) 適用して、狙い通りに変わったか確認
    ///   3) 復元して、元に戻ったか確認
    /// を自動で行う。
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.AssetOptimizerSelfTest.Run
    ///     -hkoptiScenes "Assets/log/zome.unity"
    ///     -hkoptiLimit 3
    ///     -hkoptiOut "C:\path\selftest.txt"
    /// </summary>
    public static class AssetOptimizerSelfTest
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            bool previousCrunch = AssetOptimizer.Crunch;

            try
            {
                // 往復テストではCrunchも必ず候補に含め、ロード後の実形式まで検査する。
                AssetOptimizer.Crunch = true;
                ok = Execute(sb);
            }
            catch (Exception e)
            {
                ok = false;
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }
            finally
            {
                AssetOptimizer.Crunch = previousCrunch;
            }

            sb.AppendLine();
            sb.AppendLine(ok ? "=== 検証: 成功 ===" : "=== 検証: 失敗 ===");

            var text = sb.ToString();
            Debug.Log(text);

            var outPath = GetArg("-hkoptiOut");
            if (!string.IsNullOrEmpty(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
            }
        }

        private static bool Execute(StringBuilder sb)
        {
            var scene = GetArg("-hkoptiScenes");
            var assetsArg = GetArg("-hkoptiAssets");
            var limitArg = GetArg("-hkoptiLimit");
            int limit = int.TryParse(limitArg, out var l) ? l : 3;

            sb.AppendLine("AssetOptimizer 往復テスト");
            sb.AppendLine($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"対象シーン: {scene}");
            if (!string.IsNullOrEmpty(assetsArg)) sb.AppendLine($"明示対象: {assetsArg}");
            sb.AppendLine($"対象件数の上限: {limit}");
            sb.AppendLine(new string('=', 70));
            sb.AppendLine();

            List<AssetFix> all;

            // -hkoptiForceMax N を付けると、指定アセットに対して解像度削減を直接組み立てる。
            // 解像度の推奨値はアバターの面積から出すので ScanAssets では作れない。
            // 「実際に解像度が下がるか」だけを実物で確かめたいときに使う。
            var forceMaxArg = GetArg("-hkoptiForceMax");
            if (!string.IsNullOrEmpty(assetsArg) && int.TryParse(forceMaxArg, out var forced))
            {
                var manual = new List<AssetFix>();
                foreach (var p in assetsArg.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                    if (tex == null) { sb.AppendLine($"読み込めない: {p}"); continue; }
                    manual.Add(new AssetFix
                    {
                        Kind = AssetFixKind.ReduceTextureResolution,
                        AssetPath = p,
                        ObjectName = tex.name,
                        Before = $"{tex.width}x{tex.height}",
                        After = $"max {forced}",
                        Reason = "検証用に直接指定",
                        Applicable = p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase),
                        Selected = true,
                    });
                }
                all = manual;
                sb.AppendLine($"検証用に解像度 {forced} を直接指定");
            }
            else if (!string.IsNullOrEmpty(assetsArg))
            {
                all = AssetOptimizer.ScanAssets(
                    assetsArg.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0));
            }
            else
            {
                if (string.IsNullOrEmpty(scene)) { sb.AppendLine("シーン未指定"); return false; }
                EditorSceneManager.OpenScene(scene.Split(';')[0].Trim(), OpenSceneMode.Single);

                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null).Select(a => a.gameObject).Take(3).ToList();
                if (avatars.Count == 0) { sb.AppendLine("アバターが見つからない"); return false; }
                all = AssetOptimizer.Scan(avatars);
            }
            sb.AppendLine($"検出: {all.Count} 件");

            // 種類ごとに少しずつ選ぶ（全種類を検証したいので）
            var picked = new List<AssetFix>();
            foreach (AssetFixKind kind in Enum.GetValues(typeof(AssetFixKind)))
            {
                picked.AddRange(all.Where(f => f.Kind == kind && f.Applicable).Take(limit));
            }
            if (picked.Count == 0) { sb.AppendLine("適用できる対象が無い"); return false; }

            foreach (var f in all) f.Selected = false;
            foreach (var f in picked) f.Selected = true;

            sb.AppendLine($"検証対象: {picked.Count} 件");
            sb.AppendLine();

            // --- 1) 変更前の状態を記録 ---
            var paths = picked.Select(f => f.AssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = paths.ToDictionary(p => p, Snapshot, StringComparer.OrdinalIgnoreCase);
            sb.AppendLine("[変更前]");
            foreach (var f in picked) sb.AppendLine($"  {f.KindLabel,-12} {before[f.AssetPath]}  {f.AssetPath}");
            sb.AppendLine();

            // --- 2) 適用 ---
            var backupRoot = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName ?? ".",
                "HKOpti_Backup", "SelfTest");
            var backupDir = AssetOptimizer.Apply(picked, backupRoot);
            if (backupDir == null) { sb.AppendLine("適用されなかった"); return false; }

            AssetDatabase.Refresh();
            var after = paths.ToDictionary(p => p, Snapshot, StringComparer.OrdinalIgnoreCase);

            sb.AppendLine("[適用後]");
            bool changedAll = true;
            foreach (var f in picked)
            {
                bool changed = before[f.AssetPath] != after[f.AssetPath];
                bool actualCrunch = f.Kind != AssetFixKind.EnableCrunchCompression ||
                    after[f.AssetPath].Contains("Crunched");
                if (!changed || !actualCrunch) changedAll = false;
                var verdict = !changed ? "変化なし" : actualCrunch ? "変化あり" : "実形式が非Crunch";
                sb.AppendLine($"  {verdict}  {f.KindLabel,-12} " +
                              $"{before[f.AssetPath]} → {after[f.AssetPath]}");
            }
            sb.AppendLine($"  バックアップ: {backupDir}");
            sb.AppendLine();

            // --- 3) 復元 ---
            int restored = AssetOptimizer.Restore(backupDir);
            AssetDatabase.Refresh();
            var back = paths.ToDictionary(p => p, Snapshot, StringComparer.OrdinalIgnoreCase);

            sb.AppendLine("[復元後]");
            bool restoredAll = true;
            foreach (var f in picked)
            {
                bool same = before[f.AssetPath] == back[f.AssetPath];
                if (!same) restoredAll = false;
                sb.AppendLine($"  {(same ? "元通り" : "戻っていない")}  {f.KindLabel,-12} " +
                              $"{back[f.AssetPath]}  （元: {before[f.AssetPath]}）");
            }
            sb.AppendLine($"  復元件数: {restored} / {paths.Count}");
            sb.AppendLine();

            sb.AppendLine("[判定]");
            sb.AppendLine($"  適用で設定が変わったか : {(changedAll ? "OK" : "NG（変化しなかったものがある）")}");
            sb.AppendLine($"  復元で元に戻ったか     : {(restoredAll ? "OK" : "NG（戻らなかったものがある）")}");

            return changedAll && restoredAll;
        }

        /// <summary>インポート設定のうち、この機能が触る部分だけを文字列化する。</summary>
        private static string Snapshot(string assetPath)
        {
            var imp = AssetImporter.GetAtPath(assetPath);
            switch (imp)
            {
                case TextureImporter ti:
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    var fmt = tex != null ? tex.format.ToString() : "?";
                    var size = tex != null ? $"{tex.width}x{tex.height}" : "?";
                    return $"alphaSource={ti.alphaSource} max={ti.maxTextureSize} " +
                           $"isReadable={ti.isReadable} 実際={fmt} {size}";
                }
                case ModelImporter mi:
                    return $"isReadable={mi.isReadable}";
                default:
                    return "(対象外)";
            }
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
