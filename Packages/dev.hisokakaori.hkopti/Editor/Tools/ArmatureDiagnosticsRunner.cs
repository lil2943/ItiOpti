using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using nadena.dev.ndmf;
using HisokaKaori.HKOpti.Editor.Core;
using HisokaKaori.HKOpti.Editor.Passes;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// B-6 の頭打ちを、MA 適用「前」と「後」の両方で測る。
    ///
    /// 前だけ見ても意味が無い（MA が処理してくれるぶんが混ざる）。
    /// 後だけ見ても意味が無い（MA が何をしたのか分からない）。
    /// **差分こそが「HKOpti が追加で刈り取れる余地」**なので、両方を出す。
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.ArmatureDiagnosticsRunner.Run
    ///     -hkoptiScenes "Assets/log/zome.unity"
    ///     -hkoptiAvatars 5
    ///     -hkoptiOut "C:\path\armature-diag.txt"
    ///
    /// 【重要】元のアバターは触らない。必ず複製に対して NDMF を走らせる。
    /// シーンも保存しない。
    /// </summary>
    public static class ArmatureDiagnosticsRunner
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("B-6 頭打ち診断（MA 適用前 / 適用後）");
            sb.AppendLine($"生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();
            sb.AppendLine("同名ボーンのうち、何が理由で本体へ統合できないのかを数える。");
            sb.AppendLine("データは一切変更しない。NDMF は複製に対してのみ実行する。");
            sb.AppendLine();

            try
            {
                Execute(sb);
            }
            catch (Exception e)
            {
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }

            var text = sb.ToString();
            Debug.Log(text);

            var outPath = GetArg("-hkoptiOut");
            if (!string.IsNullOrEmpty(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
                Debug.Log($"[ItiOptimiser] 診断を書き出しました: {outPath}");
            }
        }

        private static void Execute(StringBuilder sb)
        {
            var scenes = GetArg("-hkoptiScenes");
            int limit = int.TryParse(GetArg("-hkoptiAvatars"), out var n) ? n : 5;
            if (string.IsNullOrEmpty(scenes)) { sb.AppendLine("-hkoptiScenes 未指定"); return; }

            var totalBefore = new Dictionary<ArmatureDiagnostics.BlockReason, int>();
            var totalAfter = new Dictionary<ArmatureDiagnostics.BlockReason, int>();
            int dupBefore = 0, dupAfter = 0, freeBefore = 0, freeAfter = 0;
            int genuineBefore = 0, genuineAfter = 0, coincidentalAfter = 0;
            int transformsBefore = 0, transformsAfter = 0, avatarCount = 0;

            foreach (var scenePath in scenes.Split(';'))
            {
                var path = scenePath.Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                sb.AppendLine(new string('-', 78));
                sb.AppendLine($"シーン: {path}");
                sb.AppendLine(new string('-', 78));

                // FindObjectsOfType の順序は保証されないので、必ず名前で整列する。
                // 整列しないと実行のたびに対象が変わり、数字が比較できなくなる
                // （実際に 1 回目と 2 回目で別のアバターを測ってしまった）。
                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null)
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ThenBy(a => a.GetInstanceID())
                    .Take(limit)
                    .ToList();

                sb.AppendLine($"対象アバター {avatars.Count} 体（名前順、上限 {limit}）");
                sb.AppendLine();

                foreach (var desc in avatars)
                {
                    avatarCount++;
                    var before = AnalyzeInPlace(desc.gameObject);
                    sb.Append("【MA 適用前】");
                    sb.Append(ArmatureDiagnostics.Format(before));
                    Accumulate(totalBefore, before);
                    dupBefore += before.DuplicateBones;
                    freeBefore += before.FreeCount;
                    genuineBefore += before.GenuineCount;
                    transformsBefore += before.TotalTransforms;

                    var after = AnalyzeAfterNdmf(desc.gameObject, sb);
                    if (after != null)
                    {
                        sb.Append("【MA 適用後】");
                        sb.Append(ArmatureDiagnostics.Format(after));
                        Accumulate(totalAfter, after);
                        dupAfter += after.DuplicateBones;
                        freeAfter += after.FreeCount;
                        genuineAfter += after.GenuineCount;
                        coincidentalAfter += after.CoincidentalCount;
                        transformsAfter += after.TotalTransforms;

                        sb.AppendLine($"    → MA による削減: Transform " +
                                      $"{before.TotalTransforms} → {after.TotalTransforms}　" +
                                      $"重複ボーン {before.DuplicateBones} → {after.DuplicateBones}");
                        sb.AppendLine($"    → ItiOptimiserが追加で扱える余地: {after.FreeCount} 本" +
                                      "（阻害要因なし）");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine(new string('=', 78));
            sb.AppendLine($"合計（アバター {avatarCount} 体）");
            sb.AppendLine(new string('=', 78));
            sb.AppendLine($"  Transform         : MA 前 {transformsBefore,7} → MA 後 {transformsAfter,7}" +
                          $"　（MA が {transformsBefore - transformsAfter} 本削減）");
            sb.AppendLine($"  同名ボーン        : MA 前 {dupBefore,7} → MA 後 {dupAfter,7}");
            sb.AppendLine($"    ├ 偶然の同名    :                    {coincidentalAfter,7}" +
                          "　※対象外。統合したら壊れる");
            sb.AppendLine($"    └ 真の複製      : MA 前 {genuineBefore,7} → MA 後 {genuineAfter,7}");
            sb.AppendLine($"  阻害要因なし      : MA 前 {freeBefore,7} → MA 後 {freeAfter,7}" +
                          "　← ここがItiOptimiserの実効余地");
            sb.AppendLine();
            sb.AppendLine("  [MA 後に残った「真の複製」の阻害要因]");
            foreach (var kv in totalAfter.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"    {ArmatureDiagnostics.Label(kv.Key),-32} {kv.Value,6}");

            sb.AppendLine();
            sb.AppendLine("  [読み取り方]");
            sb.AppendLine("   ・「偶然の同名」は別々の衣装が同じ名前のボーンを持っているだけで、");
            sb.AppendLine("     位置が違う。統合対象ではない（緩めるべき条件ではない）。");
            sb.AppendLine("   ・「真の複製」のうち阻害要因が無いものだけが、今すぐ安全に統合できる。");
            sb.AppendLine("   ・Modular Avatar が既に大半を処理しているので、");
            sb.AppendLine("     ItiOptimiserが追加で扱える量が投資に見合うかをこの数字で判断する。");
        }

        private static ArmatureDiagnostics.Report AnalyzeInPlace(GameObject avatar)
        {
            var index = ReferenceIndex.Build(avatar);
            var protection = ProtectionMap.Build(index);
            return ArmatureDiagnostics.Analyze(index, protection);
        }

        /// <summary>
        /// 複製に NDMF（＝ MA を含むビルド処理）を走らせてから測る。
        /// 元のアバターは絶対に触らない。
        /// </summary>
        private static ArmatureDiagnostics.Report AnalyzeAfterNdmf(GameObject source, StringBuilder sb)
        {
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source);
                clone.name = source.name + " (ItiOptimiser診断用複製)";
                clone.SetActive(true);

                AvatarProcessor.ProcessAvatar(clone);
                return AnalyzeInPlace(clone);
            }
            catch (Exception e)
            {
                sb.AppendLine($"    !! NDMF 実行に失敗: {e.GetType().Name}: {e.Message}");
                return null;
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        private static void Accumulate(Dictionary<ArmatureDiagnostics.BlockReason, int> into,
            ArmatureDiagnostics.Report r)
        {
            foreach (var kv in r.CountByReason())
            {
                into.TryGetValue(kv.Key, out var n);
                into[kv.Key] = n + kv.Value;
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
