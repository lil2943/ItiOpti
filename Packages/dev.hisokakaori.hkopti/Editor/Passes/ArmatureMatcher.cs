using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Passes
{
    /// <summary>B-6 の対応づけ候補。ここではデータを変更しない。</summary>
    public sealed class ArmatureMatchCandidate
    {
        public Transform Source;
        public Transform Target;
        public string SourcePath;
        public float PositionErrorMm;
        public float RotationErrorDeg;
        public float ScaleError;
        public int WeightedRendererCount;
        public bool MotionEquivalent;
        public readonly List<string> Blockers = new List<string>();

        public bool PoseEquivalentForAutomatic =>
            PositionErrorMm <= 0.01f && RotationErrorDeg <= 0.01f && ScaleError <= 0.0001f;

        /// <summary>SMRの参照だけを本体へ移せる。source Transform はまだ削除しない。</summary>
        public bool CanRemapAutomatically => PoseEquivalentForAutomatic && Blockers.Count == 0;

        /// <summary>source Transform 自体も削除できるほど同一運動が保証される。</summary>
        public bool CanDeleteAutomatically => CanRemapAutomatically && MotionEquivalent;
    }

    /// <summary>
    /// 服側の同名ボーンを Animator が返した本体ボーンの実インスタンスへ対応づける。
    /// 完全一致と姿勢一致だけを扱い、不確実な候補は自動適用しない。
    /// </summary>
    public static class ArmatureMatcher
    {
        private const float PositionToleranceMeters = 0.001f;
        private const float RotationToleranceDegrees = 0.1f;
        private const float ScaleTolerance = 0.001f;

        public static List<ArmatureMatchCandidate> FindCandidates(ReferenceIndex index)
        {
            var result = new List<ArmatureMatchCandidate>();
            if (index == null || index.AvatarRoot == null) return result;

            var targets = index.HumanoidBones
                .Where(t => t != null)
                .GroupBy(t => t.name, StringComparer.Ordinal)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var source in index.AllTransforms)
            {
                if (source == null || index.HumanoidBones.Contains(source)) continue;
                if (index.HumanoidPath.Contains(source)) continue;
                if (!targets.TryGetValue(source.name, out var target)) continue;

                float positionError = Vector3.Distance(source.position, target.position);
                float rotationError = Quaternion.Angle(source.rotation, target.rotation);
                float scaleError = MaxAbs(source.lossyScale - target.lossyScale);
                if (positionError > PositionToleranceMeters ||
                    rotationError > RotationToleranceDegrees ||
                    scaleError > ScaleTolerance)
                    continue;

                var candidate = new ArmatureMatchCandidate
                {
                    Source = source,
                    Target = target,
                    SourcePath = index.PathOf.TryGetValue(source, out var path) ? path : source.name,
                    PositionErrorMm = positionError * 1000f,
                    RotationErrorDeg = rotationError,
                    ScaleError = scaleError,
                    WeightedRendererCount = index.SkinnedMeshes.Count(smr =>
                        smr != null && smr.bones != null && Array.IndexOf(smr.bones, source) >= 0),
                    MotionEquivalent = HasIdentityPathTo(source, target),
                };

                CollectBlockers(index, candidate);
                result.Add(candidate);
            }

            return result;
        }

        /// <summary>
        /// 自動条件を満たした候補について、SMRのbones/rootBoneとprobeAnchorだけを本体へ張り替える。
        /// TransformやMeshアセットは削除・変更しない。戻り値は変更したRenderer数。
        /// </summary>
        public static int ApplyWeightRemaps(
            ReferenceIndex index, IEnumerable<ArmatureMatchCandidate> candidates)
        {
            if (index == null || candidates == null) return 0;
            var map = candidates
                .Where(c => c != null && c.CanRemapAutomatically && c.Source != null && c.Target != null)
                .GroupBy(c => c.Source)
                .ToDictionary(g => g.Key, g => g.First().Target);
            if (map.Count == 0) return 0;

            int changed = 0;
            foreach (var smr in index.SkinnedMeshes)
            {
                if (smr == null) continue;
                bool rendererChanged = false;
                var bones = smr.bones;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (!map.TryGetValue(bones[i], out var target)) continue;
                    bones[i] = target;
                    rendererChanged = true;
                }
                if (rendererChanged) smr.bones = bones;

                if (smr.rootBone != null && map.TryGetValue(smr.rootBone, out var rootTarget))
                {
                    smr.rootBone = rootTarget;
                    rendererChanged = true;
                }
                if (smr.probeAnchor != null && map.TryGetValue(smr.probeAnchor, out var probeTarget))
                {
                    smr.probeAnchor = probeTarget;
                    rendererChanged = true;
                }
                if (rendererChanged) changed++;
            }
            return changed;
        }

        /// <summary>
        /// source が target の子孫で、間の全Transformが単位変換なら、
        /// source と target はアニメーション中も同じ姿勢になる。
        /// </summary>
        public static bool HasIdentityPathTo(Transform source, Transform target)
        {
            if (source == null || target == null || source == target) return false;
            for (var current = source; current != null && current != target; current = current.parent)
            {
                if (current.localPosition.sqrMagnitude > PositionToleranceMeters * PositionToleranceMeters)
                    return false;
                if (Quaternion.Angle(current.localRotation, Quaternion.identity) > RotationToleranceDegrees)
                    return false;
                if (MaxAbs(current.localScale - Vector3.one) > ScaleTolerance)
                    return false;
                if (current.parent == null) return false;
            }

            return source.IsChildOf(target);
        }

        private static void CollectBlockers(ReferenceIndex index, ArmatureMatchCandidate candidate)
        {
            foreach (var usage in index.UsageOf(candidate.Source))
            {
                switch (usage.Kind)
                {
                    case UsageKind.SkinWeight:
                    case UsageKind.SkinnedMeshRootBone:
                    case UsageKind.ProbeAnchor:
                        continue;
                    case UsageKind.ComponentReference when usage.Source is Renderer:
                        continue;
                    default:
                        var label = usage.Kind.ToString();
                        if (!candidate.Blockers.Contains(label)) candidate.Blockers.Add(label);
                        break;
                }
            }
        }

        private static float MaxAbs(Vector3 value)
        {
            return Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }
    }
}
