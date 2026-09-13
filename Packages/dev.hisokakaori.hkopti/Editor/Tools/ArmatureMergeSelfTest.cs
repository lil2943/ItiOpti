using System;
using System.IO;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using HisokaKaori.HKOpti.Editor.Core;
using HisokaKaori.HKOpti.Editor.Passes;
using Object = UnityEngine.Object;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>実アバターの複製にNDMFを実行し、B-6参照張り替えを検証する。</summary>
    public static class ArmatureMergeSelfTest
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            bool ok = false;
            GameObject clone = null;
            try
            {
                var scene = GetArg("-hkoptiScenes");
                if (string.IsNullOrEmpty(scene)) throw new ArgumentException("-hkoptiScenes が必要です");
                EditorSceneManager.OpenScene(scene.Split(';')[0].Trim(), OpenSceneMode.Single);

                var source = Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Select(d => d.gameObject)
                    .FirstOrDefault(a =>
                    {
                        if (ToolConflictDetector.Detect(a, OptimizerFeature.ArmatureMerge)
                            .Any(c => c.Severity == ConflictSeverity.Blocking)) return false;
                        return ArmatureMatcher.FindCandidates(ReferenceIndex.Build(a))
                            .Any(c => c.CanRemapAutomatically);
                    });
                if (source == null) throw new InvalidOperationException("検証可能なアバターがありません");

                clone = Object.Instantiate(source);
                clone.name = source.name + "__HKOptiSelfTest";
                clone.AddComponent<HKOArmatureMerge>();

                var beforeIndex = ReferenceIndex.Build(clone);
                int beforeBones = CountUniqueBones(beforeIndex);
                int beforeNullBones = CountNullBoneSlots(beforeIndex);
                int beforeCandidates = ArmatureMatcher.FindCandidates(beforeIndex)
                    .Count(c => c.CanRemapAutomatically);

                HKOptiPlugin.ResetDiagnostics();
                AvatarProcessor.ProcessAvatar(clone);

                var afterIndex = ReferenceIndex.Build(clone);
                int afterBones = CountUniqueBones(afterIndex);
                int afterNullBones = CountNullBoneSlots(afterIndex);
                bool componentRemoved = clone.GetComponentInChildren<HKOArmatureMerge>(true) == null;
                bool noAddedNullBones = afterNullBones <= beforeNullBones;
                var run = HKOptiPlugin.LastRunResult;

                sb.AppendLine("B-6 NDMF 統合テスト（複製アバターのみ）");
                sb.AppendLine($"元アバター: {source.name}");
                sb.AppendLine($"NDMF前のHKOpti候補: {beforeCandidates}");
                sb.AppendLine($"NDMF全体のユニークボーン: {beforeBones} → {afterBones}");
                if (run != null)
                {
                    sb.AppendLine($"MA後のHKOpti候補: {run.CandidateCount} / " +
                                  $"自動対象 {run.RemappableCount}");
                    sb.AppendLine($"HKOpti追加張り替え: Renderer {run.ChangedRendererCount} / " +
                                  $"ユニークボーン {run.UniqueBonesBefore} → {run.UniqueBonesAfter}");
                }
                else
                {
                    sb.AppendLine("HKOptiパス実行: NG");
                }
                sb.AppendLine($"設定コンポーネント除去: {(componentRemoved ? "OK" : "NG")}");
                sb.AppendLine($"nullボーン枠: {beforeNullBones} → {afterNullBones} " +
                              $"（増加なし: {(noAddedNullBones ? "OK" : "NG")}）");

                ok = beforeCandidates > 0 && run != null && componentRemoved && noAddedNullBones;
            }
            catch (Exception e)
            {
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
            }

            sb.AppendLine(ok ? "=== 検証: 成功 ===" : "=== 検証: 失敗 ===");
            var text = sb.ToString();
            Debug.Log(text);
            var outPath = GetArg("-hkoptiOut");
            if (!string.IsNullOrEmpty(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
            }
            if (!ok) throw new InvalidOperationException("B-6 self-test failed");
        }

        private static int CountUniqueBones(ReferenceIndex index)
        {
            return index.SkinnedMeshes.Where(s => s != null && s.bones != null)
                .SelectMany(s => s.bones).Where(b => b != null).Distinct().Count();
        }

        private static int CountNullBoneSlots(ReferenceIndex index)
        {
            return index.SkinnedMeshes.Where(s => s != null && s.bones != null)
                .Sum(s => s.bones.Count(b => b == null));
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
