using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using HisokaKaori.HKOpti.Editor.Analysis;
using HisokaKaori.HKOpti.Editor.Core;
using HisokaKaori.HKOpti.Editor.Passes;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// Unity をバッチモードで起動して、指定シーンのアバターを解析しテキストで書き出す。
    ///
    /// 目的は「人間が Unity を開く前に、機械的に確認できることを済ませる」こと。
    /// 実装者（AI）は Unity の GUI を見られないので、この経路が無いと
    /// 実データでの動作確認が一切できない。
    ///
    /// 使い方：
    ///   Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -quit
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.BatchReport.Run
    ///     -hkoptiScenes "Assets/log/zome.unity;Assets/log/Chocolat.unity"
    ///     -hkoptiOut "C:\path\report.txt"
    ///
    /// 【重要】シーンは開くだけで、保存は絶対にしない。
    /// </summary>
    public static class BatchReport
    {
        public static void Run()
        {
            var scenes = GetArg("-hkoptiScenes");
            var outPath = GetArg("-hkoptiOut");
            var mobileArg = GetArg("-hkoptiMobile");
            bool mobile = mobileArg != null && mobileArg.Equals("true", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("ItiOptimiser 解析レポート（バッチ実行）");
            sb.AppendLine($"生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"判定基準: {(mobile ? "Quest / Android" : "PC (Windows)")}");
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();

            if (string.IsNullOrEmpty(scenes))
            {
                sb.AppendLine("-hkoptiScenes が指定されていません。");
            }
            else
            {
                foreach (var scenePath in scenes.Split(';'))
                {
                    var path = scenePath.Trim();
                    if (path.Length == 0) continue;
                    try
                    {
                        ReportScene(path, mobile, sb);
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine($"[{path}] 解析中に例外: {e.GetType().Name}: {e.Message}");
                        sb.AppendLine(e.StackTrace);
                        sb.AppendLine();
                    }
                }
            }

            var text = sb.ToString();
            Debug.Log(text);

            if (!string.IsNullOrEmpty(outPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                    File.WriteAllText(outPath, text, new UTF8Encoding(true));
                    Debug.Log($"[ItiOptimiser] レポートを書き出しました: {outPath}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ItiOptimiser] レポートを書き出せませんでした: {e.Message}");
                }
            }
        }

        private static void ReportScene(string scenePath, bool mobile, StringBuilder sb)
        {
            sb.AppendLine(new string('-', 78));
            sb.AppendLine($"シーン: {scenePath}");
            sb.AppendLine(new string('-', 78));

            if (!File.Exists(scenePath))
            {
                sb.AppendLine("  シーンファイルが見つかりません。");
                sb.AppendLine();
                return;
            }

            // 開くだけ。保存はしない。
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var avatars = UnityEngine.Object
                .FindObjectsOfType<VRCAvatarDescriptor>(true)
                .Where(a => a != null)
                .ToList();

            if (avatars.Count == 0)
            {
                sb.AppendLine("  VRCAvatarDescriptor を持つオブジェクトがありません。");
                sb.AppendLine();
                return;
            }

            foreach (var desc in avatars)
            {
                ReportAvatar(desc.gameObject, mobile, sb);
            }
        }

        private static void ReportAvatar(GameObject avatar, bool mobile, StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine($"■ アバター: {avatar.name}  (active={avatar.activeInHierarchy})");

            var index = ReferenceIndex.Build(avatar);
            var protection = ProtectionMap.Build(index);
            var stats = AvatarStats.Measure(index, mobile);
            var conflicts = ToolConflictDetector.Detect(avatar);

            sb.AppendLine($"  総合ランク: {PerformanceThresholds.DisplayName(stats.Overall)}");
            sb.AppendLine($"  足枷: {string.Join(", ", stats.Bottlenecks.Select(e => e.Label))}");
            sb.AppendLine($"  解析時間: {index.BuildMilliseconds} ms");
            sb.AppendLine();

            sb.AppendLine("  [計測値]");
            foreach (var e in stats.Entries)
            {
                var nextText = "";
                if (e.Rank > PerfRank.Excellent)
                {
                    var next = (PerfRank)((int)e.Rank - 1);
                    var limit = e.LimitFor(next, mobile);
                    nextText = $"  → {PerformanceThresholds.DisplayName(next)} は {limit}{(e.Unit == "MB" ? " MB" : "")} 以下";
                }
                sb.AppendLine($"    {e.Label,-26} {e.ValueText,12}  {PerformanceThresholds.DisplayName(e.Rank),-10}{nextText}");
            }
            sb.AppendLine();

            var armatureMatches = ArmatureMatcher.FindCandidates(index);
            if (armatureMatches.Count > 0)
            {
                int remappable = armatureMatches.Count(c => c.CanRemapAutomatically);
                int deletable = armatureMatches.Count(c => c.CanDeleteAutomatically);
                int weighted = armatureMatches.Count(c => c.WeightedRendererCount > 0);
                sb.AppendLine("  [B-6 服ボーン→本体ボーン対応候補（解析のみ）]");
                sb.AppendLine($"    完全一致+姿勢一致: {armatureMatches.Count} / " +
                              $"ウェイト参照あり: {weighted} / 参照張替え可能: {remappable} / " +
                              $"Transform削除可能: {deletable}");
                foreach (var c in armatureMatches.Where(c => !c.CanRemapAutomatically).Take(5))
                {
                    var reason = c.MotionEquivalent
                        ? string.Join(", ", c.Blockers)
                        : "アニメーション中の同一姿勢を保証できない階層";
                    sb.AppendLine($"    保留: {c.SourcePath} → {c.Target.name} ({reason})");
                }
                sb.AppendLine();
            }

            sb.AppendLine("  [ボーンの保護レベル]");
            sb.AppendLine($"    絶対保護 (ヒューマノイド等) : {protection.CountOf(ProtectionLevel.Absolute),6}");
            sb.AppendLine($"    規約保護 (慣例名)           : {protection.CountOf(ProtectionLevel.Convention),6}");
            sb.AppendLine($"    参照あり                    : {protection.CountOf(ProtectionLevel.Referenced),6}");
            sb.AppendLine($"    参照なし (削除候補)         : {protection.CountOf(ProtectionLevel.Free),6}");
            sb.AppendLine($"    合計 Transform              : {index.AllTransforms.Count,6}");
            sb.AppendLine();

            sb.AppendLine("  [解析の内訳]");
            sb.AppendLine($"    ヒューマノイドボーン: {index.HumanoidBones.Count} / 経路: {index.HumanoidPath.Count}");
            sb.AppendLine($"    AnimatorController: {index.Animations.ControllerCount} / Clip: {index.Animations.ClipCount}");
            sb.AppendLine($"    アニメが参照するパス: {index.Animations.Paths.Count}");
            sb.AppendLine($"    SkinnedMesh: {index.SkinnedMeshes.Count} / PhysBone: {index.PhysBones.Count} / PBCollider: {index.PhysBoneColliders.Count}");
            sb.AppendLine($"    マテリアル: {index.MaterialUsage.Count} / テクスチャ: {index.TextureUsage.Count}");
            sb.AppendLine($"    Expression Menu アイコン: {index.ExpressionMenuTextures.Count}");

            if (index.UnreadableMeshes.Count > 0)
            {
                sb.AppendLine($"    !! ウェイトを読めなかったメッシュ: {index.UnreadableMeshes.Count} 件");
                foreach (var m in index.UnreadableMeshes.Take(5)) sb.AppendLine($"       {m}");
            }
            sb.AppendLine();

            if (conflicts.Count > 0)
            {
                sb.AppendLine("  [他ツールの検出]");
                foreach (var c in conflicts)
                {
                    var tag = c.Severity == ConflictSeverity.Blocking ? "併用不可" : "注意";
                    sb.AppendLine($"    [{tag}] {c.ToolName} : {c.TypeName}");
                }
                sb.AppendLine();
            }

            // ボーンをルート直下のグループごとに集計（どの服が増やしているか）
            sb.AppendLine("  [ボーンの内訳 上位15]");
            foreach (var item in CostBreakdown.BonesByGroup(index, protection, 15))
            {
                sb.AppendLine($"    {item.WeightText,8}  {item.Name,-40} {item.Advice}");
            }
            sb.AppendLine();

            sb.AppendLine("  [テクスチャ 上位10]");
            foreach (var item in CostBreakdown.Textures(index, 10))
            {
                sb.AppendLine($"    {item.WeightText,9}  {item.Name}");
                if (!string.IsNullOrEmpty(item.Advice)) sb.AppendLine($"               → {item.Advice}");
            }
            sb.AppendLine();

            sb.AppendLine("  [マテリアル: シェーダー別]");
            foreach (var item in CostBreakdown.Materials(index, 12))
            {
                sb.AppendLine($"    {item.WeightText,7}  {item.Name}");
            }
            sb.AppendLine();

            // 見た目を変えずにできる削減余地
            var ops = LosslessScan.Run(index);
            if (ops.Count > 0)
            {
                sb.AppendLine("  [見た目に影響しない削減余地]");
                sb.Append(LosslessScan.Summarize(ops));
                sb.AppendLine();
                sb.AppendLine("    -- 明細（上位20）--");
                foreach (var o in ops.OrderByDescending(o => o.SavedBytes).Take(20))
                {
                    var save = o.SavedBytes > 0 ? $"{o.SavedBytes / (1024.0 * 1024.0):F1} MB" : "";
                    sb.AppendLine($"      [{o.Category}] {save} {o.Detail}");
                    if (!string.IsNullOrEmpty(o.Risk)) sb.AppendLine($"        ※ {o.Risk}");
                }
                sb.AppendLine();
            }

            var hazards = CostBreakdown.Hazards(index);
            if (hazards.Count > 0)
            {
                sb.AppendLine($"  [警告 {hazards.Count} 件（自動では触りません）]");
                foreach (var h in hazards.Take(15))
                {
                    sb.AppendLine($"    {h.Name}");
                    sb.AppendLine($"      → {h.Advice}");
                }
                sb.AppendLine();
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
