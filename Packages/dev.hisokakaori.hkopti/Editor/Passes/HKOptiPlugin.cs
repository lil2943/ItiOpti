using System;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

[assembly: ExportsPlugin(typeof(HisokaKaori.HKOpti.Editor.Passes.HKOptiPlugin))]

namespace HisokaKaori.HKOpti.Editor.Passes
{
    internal sealed class ArmatureMergeRunResult
    {
        public int CandidateCount;
        public int RemappableCount;
        public int ChangedRendererCount;
        public int UniqueBonesBefore;
        public int UniqueBonesAfter;
        public int NullBoneSlotsBefore;
        public int NullBoneSlotsAfter;
    }

    /// <summary>B-6 の非破壊ビルド入口。</summary>
    [RunsOnAllPlatforms]
    public sealed class HKOptiPlugin : Plugin<HKOptiPlugin>
    {
        public override string QualifiedName => "dev.hisokakaori.hkopti";
        public override string DisplayName => "ItiOptimiser";

        internal static ArmatureMergeRunResult LastRunResult { get; private set; }

        internal static void ResetDiagnostics()
        {
            LastRunResult = null;
        }

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")
                .Run("Remap duplicate clothing bone references", Execute);

            // アトラス化は、服の統合が終わって実際に残るメッシュが確定してから。
            InPhase(BuildPhase.Optimizing)
                .Run("Atlas textures", TextureAtlasPass.Execute);
        }

        private static void Execute(BuildContext context)
        {
            var settings = context.AvatarRootObject
                .GetComponentsInChildren<HKOArmatureMerge>(true);
            if (settings.Length == 0 || settings.All(s => s == null || !s.Enabled)) return;

            // 以前は例外を投げてビルドを止めていた。
            // しかし AAO は広く使われているので、既存シーンに Armature Merge が残っていると
            // **アップロードそのものが失敗する**。この機能は実測で見送り済みなので、
            // 衝突時は何もせずに素通りさせる（ビルドは止めない）。
            var conflicts = ToolConflictDetector.Detect(
                    context.AvatarRootObject, OptimizerFeature.ArmatureMerge)
                .Where(c => c.Severity == ConflictSeverity.Blocking).ToList();
            if (conflicts.Count > 0)
            {
                Debug.LogWarning(
                    "[ItiOptimiser] Armature Merge は " +
                    string.Join(", ", conflicts.Select(c => c.ToolName)) +
                    " と重複するため実行しませんでした。ビルドは続行します。");
                foreach (var component in settings)
                    if (component != null) UnityEngine.Object.DestroyImmediate(component);
                return;
            }

            var index = ReferenceIndex.Build(context.AvatarRootObject);
            var candidates = ArmatureMatcher.FindCandidates(index);
            int before = CountUniqueBones(index);
            int nullBonesBefore = CountNullBoneSlots(index);
            int renderers = ArmatureMatcher.ApplyWeightRemaps(index, candidates);
            int after = CountUniqueBones(index);
            int nullBonesAfter = CountNullBoneSlots(index);
            if (nullBonesAfter > nullBonesBefore)
                throw new InvalidOperationException(
                    $"ItiOptimiserの処理によりnullボーンが増加しました: {nullBonesBefore} → {nullBonesAfter}");

            LastRunResult = new ArmatureMergeRunResult
            {
                CandidateCount = candidates.Count,
                RemappableCount = candidates.Count(c => c.CanRemapAutomatically),
                ChangedRendererCount = renderers,
                UniqueBonesBefore = before,
                UniqueBonesAfter = after,
                NullBoneSlotsBefore = nullBonesBefore,
                NullBoneSlotsAfter = nullBonesAfter,
            };

            Debug.Log($"[ItiOptimiser] B-6 参照張り替え: Renderer {renderers} 個、" +
                      $"候補 {candidates.Count} / 自動対象 {LastRunResult.RemappableCount}、" +
                      $"ユニークボーン {before} → {after}（Transform削除なし）");

            foreach (var component in settings)
            {
                if (component != null) UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static int CountUniqueBones(ReferenceIndex index)
        {
            return index.SkinnedMeshes
                .Where(smr => smr != null && smr.bones != null)
                .SelectMany(smr => smr.bones)
                .Where(b => b != null)
                .Distinct()
                .Count();
        }

        private static int CountNullBoneSlots(ReferenceIndex index)
        {
            return index.SkinnedMeshes
                .Where(smr => smr != null && smr.bones != null)
                .Sum(smr => smr.bones.Count(b => b == null));
        }
    }
}
