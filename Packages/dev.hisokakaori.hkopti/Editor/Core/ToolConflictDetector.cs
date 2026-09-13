using System;
using System.Collections.Generic;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Core
{
    /// <summary>
    /// 検出したツールに対する扱い。仕様 6.6 を参照。
    /// </summary>
    public enum ConflictSeverity
    {
        /// <summary>共存不可。その機能の処理を止める。</summary>
        Blocking,

        /// <summary>共存はできるが、実行順や結果に注意が必要。警告のみ。</summary>
        Warning,
    }

    /// <summary>
    /// ItiOptimiser の機能。衝突は「ツール単位」ではなく「機能単位」で判定する。
    /// </summary>
    [Flags]
    public enum OptimizerFeature
    {
        None = 0,

        /// <summary>アセットのインポート設定（解像度・アルファ・Read/Write・Crunch）</summary>
        ImportSettings = 1 << 0,

        /// <summary>ビルド時のテクスチャアトラス化</summary>
        TextureAtlas = 1 << 1,

        /// <summary>服ボーンの参照張り替え。実測で見送り済み。既存シーン互換のためだけに残している。</summary>
        ArmatureMerge = 1 << 2,

        All = ImportSettings | TextureAtlas | ArmatureMerge,
    }

    public sealed class ToolConflict
    {
        public string ToolName;
        public string TypeName;
        public GameObject Owner;
        public ConflictSeverity Severity;
        public string Message;

        /// <summary>このツールが止める機能（問い合わせた機能のうち該当するもの）。</summary>
        public OptimizerFeature BlockedFeatures;

        public string OwnerPath => Owner != null ? HierarchyPath.Of(Owner.transform) : "(不明)";
    }

    /// <summary>
    /// ItiOptimiser と機能が重なるツールを検出する。仕様 6.6。
    ///
    /// 【機能単位で判定する理由】
    /// 以前はツール単位で「AAO が付いていたら全部止める」判定をしていた。
    /// その結果、**AAO と一切重ならないインポート設定の最適化まで適用できなくなった**
    /// （AAO は VRChat で広く使われているので、多くの人が主要機能を使えない）。
    /// AAO はインポート設定に触れないし、アトラス化とは実測で共存できている。
    /// 重なるのは、見送ったボーン統合（Armature Merge）だけ。
    ///
    /// 型は名前空間の接頭辞（文字列）で判定する。アセンブリ参照を張らないので、
    /// そのツールが入っていない環境でもコンパイルが通る。
    /// </summary>
    public static class ToolConflictDetector
    {
        private readonly struct Rule
        {
            public readonly string Prefix;
            public readonly string Tool;
            public readonly OptimizerFeature Blocks;
            public readonly OptimizerFeature Warns;
            public readonly string Reason;

            public Rule(string prefix, string tool, OptimizerFeature blocks,
                OptimizerFeature warns, string reason)
            {
                Prefix = prefix;
                Tool = tool;
                Blocks = blocks;
                Warns = warns;
                Reason = reason;
            }
        }

        private static readonly Rule[] Rules =
        {
            // AAO はボーン統合が重なるだけ。インポート設定には触れず、
            // アトラス化とはビルド後の実測（2026-09-10）で共存できている。
            new Rule("Anatawa12.AvatarOptimizer.", "Avatar Optimizer (AAO)",
                blocks: OptimizerFeature.ArmatureMerge,
                warns: OptimizerFeature.None,
                reason: "ボーンの統合が ItiOptimiser の Armature Merge と重複します。"),

            // TexTransTool はアトラス化が重なる。両方が同じテクスチャを詰め直すと結果が壊れる。
            new Rule("net.rs64.TexTransTool.", "TexTransTool",
                blocks: OptimizerFeature.TextureAtlas,
                warns: OptimizerFeature.None,
                reason: "テクスチャのアトラス化が ItiOptimiser と重複します。" +
                        "アトラス化はどちらか一方だけを使ってください。"),

            new Rule("VF.Model.", "VRCFury",
                blocks: OptimizerFeature.None,
                warns: OptimizerFeature.TextureAtlas | OptimizerFeature.ArmatureMerge,
                reason: "VRCFury はビルド時にオブジェクトを移動・改名します。" +
                        "ItiOptimiser は VRCFury の後に動くよう順序を宣言していますが、" +
                        "結果は必ず目視で確認してください。"),

            new Rule("KRT.VRCQuestTools.", "VRC Quest Tools",
                blocks: OptimizerFeature.None,
                warns: OptimizerFeature.TextureAtlas,
                reason: "VRC Quest Tools はビルド時にマテリアルとテクスチャを作り直します。" +
                        "アトラス化と併用する場合は、Quest 版の見た目を必ず確認してください。"),
        };

        /// <summary>
        /// アバター配下を走査して、指定した機能と衝突するツールを列挙する。
        /// </summary>
        /// <param name="features">判定したい機能。省略すると全機能で判定する。</param>
        public static List<ToolConflict> Detect(GameObject avatarRoot,
            OptimizerFeature features = OptimizerFeature.All)
        {
            var found = new List<ToolConflict>();
            if (avatarRoot == null || features == OptimizerFeature.None) return found;

            // 同じツールを何十個も並べても意味がないので、ツールごとに最初の 1 件だけ記録する。
            var seenTools = new HashSet<string>();

            foreach (var comp in avatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue; // Missing Script
                var typeName = comp.GetType().FullName;
                if (string.IsNullOrEmpty(typeName)) continue;

                foreach (var rule in Rules)
                {
                    if (!typeName.StartsWith(rule.Prefix, StringComparison.Ordinal)) continue;

                    var blocked = rule.Blocks & features;
                    var warned = rule.Warns & features;
                    if (blocked == OptimizerFeature.None && warned == OptimizerFeature.None) break;
                    if (!seenTools.Add(rule.Tool)) break;

                    found.Add(new ToolConflict
                    {
                        ToolName = rule.Tool,
                        TypeName = typeName,
                        Owner = comp.gameObject,
                        Severity = blocked != OptimizerFeature.None
                            ? ConflictSeverity.Blocking
                            : ConflictSeverity.Warning,
                        BlockedFeatures = blocked,
                        Message = rule.Reason,
                    });
                    break;
                }
            }

            return found;
        }

        /// <summary>処理を止めるべき競合が 1 つでもあるか。</summary>
        public static bool HasBlocking(List<ToolConflict> conflicts)
        {
            if (conflicts == null) return false;
            foreach (var c in conflicts)
            {
                if (c.Severity == ConflictSeverity.Blocking) return true;
            }
            return false;
        }
    }
}
