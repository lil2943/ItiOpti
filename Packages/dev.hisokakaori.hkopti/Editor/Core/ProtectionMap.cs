using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Core
{
    /// <summary>保護の強さ。仕様 6.1。数値が小さいほど強く守られる。</summary>
    public enum ProtectionLevel
    {
        /// <summary>絶対保護。解除不可。UI にも解除欄を出さない。</summary>
        Absolute = 0,

        /// <summary>規約保護。外部ツールが名前で探すボーン。上級者設定で解除可。</summary>
        Convention = 1,

        /// <summary>参照保護。誰かに使われている。削除は不可だが統合の対象にはなり得る。</summary>
        Referenced = 2,

        /// <summary>自由。どこからも参照がない。削除候補。</summary>
        Free = 3,
    }

    public sealed class ProtectionInfo
    {
        public ProtectionLevel Level;
        public readonly List<string> Reasons = new List<string>();

        public string ReasonText => Reasons.Count == 0 ? "参照なし" : string.Join(" / ", Reasons);
    }

    /// <summary>
    /// 全 Transform に保護レベルを付ける。仕様 6.1。
    ///
    /// 【最重要】Absolute の判定は Transform のインスタンス比較で行う。名前一致で判定しない。
    /// 服が持ち込んだ Hips / Spine / Chest などの複製ボーンは Absolute ではない。
    /// ここを名前で判定すると、服 5 着で 275 本が全部保護対象になり、
    /// このツールの一番効く機能（B-6）が丸ごと死ぬ。仕様 1.2 を参照。
    /// </summary>
    public sealed class ProtectionMap
    {
        public readonly Dictionary<Transform, ProtectionInfo> Levels =
            new Dictionary<Transform, ProtectionInfo>();

        /// <summary>
        /// 規約保護（Lv1）にする名前パターン。外部ツールが名前で探しに来るボーン。
        /// 将来は設定ファイルに出す。
        /// </summary>
        private static readonly Regex[] ConventionPatterns =
        {
            new Regex(@"^Bust", RegexOptions.IgnoreCase),
            new Regex(@"^Breast", RegexOptions.IgnoreCase),
            new Regex(@"^Bip001", RegexOptions.IgnoreCase),
            new Regex(@"^Armature$", RegexOptions.IgnoreCase),
        };

        public ProtectionLevel LevelOf(Transform t) =>
            t != null && Levels.TryGetValue(t, out var info) ? info.Level : ProtectionLevel.Free;

        public ProtectionInfo InfoOf(Transform t) =>
            t != null && Levels.TryGetValue(t, out var info) ? info : null;

        public bool CanDelete(Transform t) => LevelOf(t) == ProtectionLevel.Free;

        /// <summary>統合（親にウェイトを移す）の対象になり得るか。Lv0 と Lv1 は不可。</summary>
        public bool CanMerge(Transform t)
        {
            var lv = LevelOf(t);
            return lv == ProtectionLevel.Free || lv == ProtectionLevel.Referenced;
        }

        public int CountOf(ProtectionLevel level)
        {
            int n = 0;
            foreach (var kv in Levels)
            {
                if (kv.Value.Level == level) n++;
            }
            return n;
        }

        public static ProtectionMap Build(ReferenceIndex index)
        {
            var map = new ProtectionMap();
            if (index == null || index.AvatarRoot == null) return map;

            var root = index.AvatarRoot.transform;

            foreach (var t in index.AllTransforms)
            {
                var info = new ProtectionInfo { Level = ProtectionLevel.Free };

                // ---- Lv0: 絶対保護 ----------------------------------------
                // ★ インスタンス比較。名前では判定しない。
                if (index.HumanoidBones.Contains(t))
                {
                    info.Level = ProtectionLevel.Absolute;
                    info.Reasons.Add("ヒューマノイドボーン本体");
                }
                else if (index.HumanoidPath.Contains(t))
                {
                    info.Level = ProtectionLevel.Absolute;
                    info.Reasons.Add("ヒューマノイドボーンへの経路");
                }
                else if (t == root)
                {
                    info.Level = ProtectionLevel.Absolute;
                    info.Reasons.Add("アバタールート");
                }

                // ---- Lv1: 規約保護 ----------------------------------------
                if (info.Level > ProtectionLevel.Convention && MatchesConvention(t.name))
                {
                    info.Level = ProtectionLevel.Convention;
                    info.Reasons.Add($"慣例名 ({t.name})");
                }

                // ---- Lv2: 参照保護 ----------------------------------------
                var usage = index.UsageOf(t);
                if (usage.Count > 0)
                {
                    if (info.Level > ProtectionLevel.Referenced)
                        info.Level = ProtectionLevel.Referenced;

                    foreach (var u in usage)
                    {
                        var label = LabelOf(u.Kind);
                        if (!info.Reasons.Contains(label)) info.Reasons.Add(label);
                    }
                }

                map.Levels[t] = info;
            }

            // 子孫に保護されたものがある親は削除できない（消すと階層が壊れる）
            map.PropagateToAncestors(index);

            return map;
        }

        /// <summary>
        /// 保護されたボーンの祖先も守る。
        /// 「自分は誰にも使われていないが、子が使われている」ボーンを消すと階層が崩れるため。
        /// </summary>
        private void PropagateToAncestors(ReferenceIndex index)
        {
            var root = index.AvatarRoot.transform;

            foreach (var t in index.AllTransforms)
            {
                if (!Levels.TryGetValue(t, out var info)) continue;
                if (info.Level == ProtectionLevel.Free) continue;

                for (var p = t.parent; p != null && p != root.parent; p = p.parent)
                {
                    if (!Levels.TryGetValue(p, out var pInfo)) break;
                    if (pInfo.Level <= ProtectionLevel.Referenced) break; // すでに守られている

                    pInfo.Level = ProtectionLevel.Referenced;
                    const string reason = "保護された子孫を持つ";
                    if (!pInfo.Reasons.Contains(reason)) pInfo.Reasons.Add(reason);
                }
            }
        }

        private static bool MatchesConvention(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var re in ConventionPatterns)
            {
                if (re.IsMatch(name)) return true;
            }
            return false;
        }

        private static string LabelOf(UsageKind kind)
        {
            switch (kind)
            {
                case UsageKind.SkinWeight: return "頂点ウェイト";
                case UsageKind.SkinnedMeshRootBone: return "メッシュのルートボーン";
                case UsageKind.ProbeAnchor: return "ProbeAnchor";
                case UsageKind.PhysBone: return "PhysBone";
                case UsageKind.PhysBoneIgnore: return "PhysBone の除外指定";
                case UsageKind.PhysBoneCollider: return "PhysBone コライダー";
                case UsageKind.Contact: return "Contact";
                case UsageKind.Constraint: return "Constraint";
                case UsageKind.Animation: return "アニメーション参照";
                case UsageKind.ComponentOwner: return "コンポーネントが乗っている";
                case UsageKind.ComponentReference: return "他コンポーネントから参照";
                case UsageKind.Humanoid: return "ヒューマノイドボーン";
                case UsageKind.HumanoidPath: return "ヒューマノイドへの経路";
                case UsageKind.AvatarDescriptor: return "アバター設定が参照";
                default: return kind.ToString();
            }
        }
    }
}
