using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Passes
{
    /// <summary>
    /// B-6（服アーマチュアの本体統合）の「頭打ちの実体」を測る診断。
    ///
    /// 【なぜ要るか】
    /// 第一段の実装（ArmatureMatcher）は対応先をヒューマノイドボーンに限定していたため、
    /// MA 適用後の実アバターで候補が 0 件だった。MA は完全同名のヒューマノイド複製を
    /// 既に処理済みなので、これは想定内の結果でしかない。
    ///
    /// 安全条件を闇雲に緩める前に、
    ///   「同名ボーンが何本残っていて、それぞれ何が理由で統合できないのか」
    /// を数える。そこで初めて、どの条件を緩めると何本増えるかが分かる。
    ///
    /// このクラスは**データを一切変更しない**。数えて報告するだけ。
    /// </summary>
    public static class ArmatureDiagnostics
    {
        /// <summary>統合を妨げている理由。1 本のボーンに複数付くことがある。</summary>
        public enum BlockReason
        {
            /// <summary>静止姿勢が本体ボーンと一致しない</summary>
            PoseMismatch,

            /// <summary>PhysBone のチェーンに含まれる</summary>
            InPhysBoneChain,

            /// <summary>PhysBone / Collider / Contact が乗っている、または参照されている</summary>
            DynamicsComponent,

            /// <summary>Constraint の参照先になっている</summary>
            Constraint,

            /// <summary>アニメーションからパス参照されている</summary>
            Animated,

            /// <summary>Transform 以外のコンポーネントが乗っている</summary>
            HasComponent,

            /// <summary>他コンポーネントから Transform 参照されている</summary>
            ReferencedByComponent,

            /// <summary>保護レベルが 0 または 1</summary>
            Protected,

            /// <summary>子孫に上記のいずれかを持つ</summary>
            BlockedDescendant,
        }

        public sealed class DuplicateBone
        {
            public Transform Source;
            public Transform Target;
            public string SourcePath;
            public string TargetPath;
            public float PositionErrorMm;
            public float RotationErrorDeg;
            public int WeightedRenderers;
            public readonly List<BlockReason> Reasons = new List<BlockReason>();

            public bool IsFree => Reasons.Count == 0;
        }

        public sealed class Report
        {
            public string AvatarName;
            public int TotalTransforms;
            public int DuplicateNameGroups;
            public int DuplicateBones;
            public readonly List<DuplicateBone> Bones = new List<DuplicateBone>();

            /// <summary>
            /// 名前は同じだが位置が違うもの。
            ///
            /// 【重要】これは「緩めれば統合できる候補」ではない。
            /// 別々の衣装が偶然同じ名前のボーンを持っているだけ（例：服 A と服 B が
            /// どちらも Skirt_1_L を持つ）で、**統合したら見た目が壊れる**。
            /// 阻害要因ではなく「そもそも対象外」として数える。
            /// </summary>
            public IEnumerable<DuplicateBone> CoincidentalNames =>
                Bones.Where(b => b.Reasons.Contains(BlockReason.PoseMismatch));

            /// <summary>
            /// 名前も静止姿勢も一致する＝本当に同じボーンの複製。B-6 の真の対象。
            /// </summary>
            public IEnumerable<DuplicateBone> GenuineDuplicates =>
                Bones.Where(b => !b.Reasons.Contains(BlockReason.PoseMismatch));

            public int CoincidentalCount => CoincidentalNames.Count();
            public int GenuineCount => GenuineDuplicates.Count();

            public int FreeCount => Bones.Count(b => b.IsFree);

            /// <summary>
            /// 理由ごとの本数。**真の複製だけ**を数える（偶然の同名は除く）。
            /// 1 本が複数理由を持つので合計は本数を超える。
            /// </summary>
            public Dictionary<BlockReason, int> CountByReason()
            {
                var d = new Dictionary<BlockReason, int>();
                foreach (var b in GenuineDuplicates)
                {
                    foreach (var r in b.Reasons)
                    {
                        d.TryGetValue(r, out var n);
                        d[r] = n + 1;
                    }
                }
                return d;
            }

            /// <summary>その理由「だけ」で止まっている本数＝その条件を緩めれば解ける本数。</summary>
            public Dictionary<BlockReason, int> CountBySoleReason()
            {
                var d = new Dictionary<BlockReason, int>();
                foreach (var b in GenuineDuplicates)
                {
                    if (b.Reasons.Count != 1) continue;
                    var r = b.Reasons[0];
                    d.TryGetValue(r, out var n);
                    d[r] = n + 1;
                }
                return d;
            }
        }

        // 姿勢一致の許容。ArmatureMatcher の自動適用条件より緩く取って、
        // 「どのくらいズレているのか」を可視化する。
        private const float PositionToleranceMeters = 0.001f;
        private const float RotationToleranceDegrees = 0.1f;

        public static Report Analyze(ReferenceIndex index, ProtectionMap protection)
        {
            var report = new Report();
            if (index == null || index.AvatarRoot == null) return report;

            report.AvatarName = index.AvatarRoot.name;
            report.TotalTransforms = index.AllTransforms.Count;

            // 名前ごとにまとめる。2 本以上あるものが「同名ボーン」。
            var byName = index.AllTransforms
                .Where(t => t != null)
                .GroupBy(t => t.name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            report.DuplicateNameGroups = byName.Count;

            // PhysBone チェーンに含まれる Transform を先に集めておく
            var inChain = new HashSet<Transform>();
            foreach (var kv in index.PhysBoneChains)
            {
                foreach (var t in kv.Value)
                {
                    if (t != null) inChain.Add(t);
                }
            }

            foreach (var group in byName)
            {
                var keeper = PickKeeper(index, group);
                if (keeper == null) continue;

                foreach (var src in group)
                {
                    if (src == keeper) continue;
                    report.DuplicateBones++;

                    var dup = new DuplicateBone
                    {
                        Source = src,
                        Target = keeper,
                        SourcePath = index.PathOf.TryGetValue(src, out var sp) ? sp : src.name,
                        TargetPath = index.PathOf.TryGetValue(keeper, out var tp) ? tp : keeper.name,
                        PositionErrorMm = Vector3.Distance(src.position, keeper.position) * 1000f,
                        RotationErrorDeg = Quaternion.Angle(src.rotation, keeper.rotation),
                        WeightedRenderers = index.SkinnedMeshes.Count(smr =>
                            smr != null && smr.bones != null && Array.IndexOf(smr.bones, src) >= 0),
                    };

                    CollectReasons(index, protection, inChain, dup);
                    report.Bones.Add(dup);
                }
            }

            return report;
        }

        /// <summary>
        /// 同名グループの中で「残す方」を選ぶ。
        /// ヒューマノイドボーンの実インスタンスがあればそれ。
        /// 無ければアバタールートに一番近いもの（＝本体側である可能性が高い）。
        /// </summary>
        private static Transform PickKeeper(ReferenceIndex index, IEnumerable<Transform> group)
        {
            Transform humanoid = null;
            Transform shallowest = null;
            int bestDepth = int.MaxValue;

            foreach (var t in group)
            {
                if (index.HumanoidBones.Contains(t)) humanoid = t;

                int depth = 0;
                for (var p = t.parent; p != null; p = p.parent) depth++;
                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    shallowest = t;
                }
            }

            return humanoid ?? shallowest;
        }

        private static void CollectReasons(ReferenceIndex index, ProtectionMap protection,
            HashSet<Transform> inChain, DuplicateBone dup)
        {
            var t = dup.Source;

            if (dup.PositionErrorMm > PositionToleranceMeters * 1000f ||
                dup.RotationErrorDeg > RotationToleranceDegrees)
                dup.Reasons.Add(BlockReason.PoseMismatch);

            if (inChain.Contains(t))
                dup.Reasons.Add(BlockReason.InPhysBoneChain);

            if (protection != null)
            {
                var lv = protection.LevelOf(t);
                if (lv == ProtectionLevel.Absolute || lv == ProtectionLevel.Convention)
                    dup.Reasons.Add(BlockReason.Protected);
            }

            foreach (var u in index.UsageOf(t))
            {
                switch (u.Kind)
                {
                    case UsageKind.PhysBone:
                    case UsageKind.PhysBoneIgnore:
                    case UsageKind.PhysBoneCollider:
                    case UsageKind.Contact:
                        Add(dup, BlockReason.DynamicsComponent);
                        break;
                    case UsageKind.Constraint:
                        Add(dup, BlockReason.Constraint);
                        break;
                    case UsageKind.Animation:
                        Add(dup, BlockReason.Animated);
                        break;
                    case UsageKind.ComponentOwner:
                        Add(dup, BlockReason.HasComponent);
                        break;
                    case UsageKind.ComponentReference:
                        Add(dup, BlockReason.ReferencedByComponent);
                        break;
                }
            }

            // 子孫に阻害要因があるか（親を消すと子ごと消えるため）
            if (HasBlockedDescendant(index, protection, inChain, t))
                Add(dup, BlockReason.BlockedDescendant);
        }

        private static void Add(DuplicateBone dup, BlockReason r)
        {
            if (!dup.Reasons.Contains(r)) dup.Reasons.Add(r);
        }

        private static bool HasBlockedDescendant(ReferenceIndex index, ProtectionMap protection,
            HashSet<Transform> inChain, Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (inChain.Contains(c)) return true;
                if (protection != null)
                {
                    var lv = protection.LevelOf(c);
                    if (lv == ProtectionLevel.Absolute || lv == ProtectionLevel.Convention) return true;
                }
                foreach (var u in index.UsageOf(c))
                {
                    switch (u.Kind)
                    {
                        case UsageKind.PhysBone:
                        case UsageKind.PhysBoneCollider:
                        case UsageKind.Contact:
                        case UsageKind.Constraint:
                        case UsageKind.Animation:
                        case UsageKind.ComponentOwner:
                            return true;
                    }
                }
                if (HasBlockedDescendant(index, protection, inChain, c)) return true;
            }
            return false;
        }

        // =================================================================

        public static string Format(Report r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"■ {r.AvatarName}");
            sb.AppendLine($"    Transform 総数        : {r.TotalTransforms}");
            sb.AppendLine($"    同名グループ          : {r.DuplicateNameGroups}");
            sb.AppendLine($"    同名ボーン            : {r.DuplicateBones}");
            sb.AppendLine($"      ├ 偶然の同名(対象外) : {r.CoincidentalCount}" +
                          "　※位置が違う＝別のボーン。統合したら壊れる");
            sb.AppendLine($"      └ 真の複製          : {r.GenuineCount}" +
                          "　※名前も位置も一致＝B-6 の対象");
            sb.AppendLine($"    うち阻害要因なし      : {r.FreeCount}　← 実効余地");

            if (r.GenuineCount == 0)
            {
                sb.AppendLine();
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("    [真の複製の阻害要因（1 本が複数該当しうる）]");
            foreach (var kv in r.CountByReason().OrderByDescending(kv => kv.Value))
                sb.AppendLine($"      {Label(kv.Key),-32} {kv.Value,5}");

            var sole = r.CountBySoleReason();
            if (sole.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    [その理由「だけ」で止まっている本数（＝緩めれば解ける）]");
                foreach (var kv in sole.OrderByDescending(kv => kv.Value))
                    sb.AppendLine($"      {Label(kv.Key),-32} {kv.Value,5}");
            }

            var weighted = r.Bones.Where(b => b.WeightedRenderers > 0).ToList();
            sb.AppendLine();
            sb.AppendLine($"    頂点ウェイトが乗っている重複ボーン: {weighted.Count}");

            sb.AppendLine();
            return sb.ToString();
        }

        public static string Label(BlockReason r)
        {
            switch (r)
            {
                case BlockReason.PoseMismatch: return "静止姿勢が一致しない";
                case BlockReason.InPhysBoneChain: return "PhysBone チェーンに含まれる";
                case BlockReason.DynamicsComponent: return "PhysBone/Collider/Contact";
                case BlockReason.Constraint: return "Constraint の参照先";
                case BlockReason.Animated: return "アニメーションから参照";
                case BlockReason.HasComponent: return "コンポーネントが乗っている";
                case BlockReason.ReferencedByComponent: return "他コンポーネントから参照";
                case BlockReason.Protected: return "保護レベル 0/1";
                case BlockReason.BlockedDescendant: return "子孫に阻害要因がある";
                default: return r.ToString();
            }
        }
    }
}
