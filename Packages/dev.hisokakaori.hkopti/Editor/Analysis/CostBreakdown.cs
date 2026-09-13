using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    /// <summary>「何が重いのか」を 1 件ずつ分解したもの。仕様 7.A-2。</summary>
    public sealed class CostItem
    {
        public string Name;
        public long Weight;          // 並べ替え用（バイト数やボーン数）
        public string WeightText;
        public string Advice;        // どうすれば軽くなるか。空なら問題なし
        public UnityEngine.Object Target;
    }

    /// <summary>
    /// 負荷の内訳を出す。「なぜ重いか」と「どうすれば軽くなるか」を必ずセットで出す。
    /// 仕様 7.A-2。
    /// </summary>
    public static class CostBreakdown
    {
        /// <summary>テクスチャの内訳。重い順。</summary>
        public static List<CostItem> Textures(ReferenceIndex index, int limit = 30)
        {
            var items = new List<CostItem>();
            foreach (var kv in index.TextureUsage)
            {
                var tex = kv.Key;
                if (tex == null) continue;

                long bytes = AvatarStats.EstimateTextureBytes(tex);
                var props = string.Join(", ", kv.Value.Select(v => v.Prop).Distinct().Take(3));
                var fmt = tex is Texture2D t2 ? t2.format.ToString() : tex.GetType().Name;

                items.Add(new CostItem
                {
                    Name = $"{tex.name}  ({tex.width}x{tex.height} {fmt})",
                    Weight = bytes,
                    WeightText = $"{bytes / (1024.0 * 1024.0):F1} MB",
                    Advice = AdviceForTexture(tex, bytes),
                    Target = tex,
                });
            }

            items.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            return items.Take(limit).ToList();
        }

        private static string AdviceForTexture(Texture tex, long bytes)
        {
            var notes = new List<string>();

            int size = Mathf.Max(tex.width, tex.height);
            if (size >= 4096) notes.Add("4K は過大な場合が多い（要 UV 面積の確認）");
            else if (size >= 2048 && bytes > 8L * 1024 * 1024) notes.Add("2K でも容量が大きい");

            if (tex is Texture2D t2)
            {
                switch (t2.format)
                {
                    case TextureFormat.RGBA32:
                    case TextureFormat.ARGB32:
                    case TextureFormat.RGB24:
                    case TextureFormat.BGRA32:
                        notes.Add("未圧縮。BC7 等に圧縮すると 1/4 になる");
                        break;
                }
                if (t2.mipmapCount <= 1) notes.Add("ミップマップ無効（遠景でちらつく）");
            }

            return string.Join(" / ", notes);
        }

        /// <summary>マテリアルの内訳。同じシェーダーでまとめて出す。</summary>
        public static List<CostItem> Materials(ReferenceIndex index, int limit = 30)
        {
            var byShader = new Dictionary<string, List<Material>>();
            foreach (var kv in index.MaterialUsage)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                var sh = mat.shader != null ? mat.shader.name : "(シェーダーなし)";
                if (!byShader.TryGetValue(sh, out var list))
                {
                    list = new List<Material>();
                    byShader[sh] = list;
                }
                list.Add(mat);
            }

            var items = byShader.Select(kv => new CostItem
            {
                Name = kv.Key,
                Weight = kv.Value.Count,
                WeightText = $"{kv.Value.Count} 個",
                Advice = kv.Value.Count > 1
                    ? "同じシェーダー同士はアトラス化でまとめられる可能性がある"
                    : "",
                Target = kv.Value[0],
            }).ToList();

            items.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            return items.Take(limit).ToList();
        }

        /// <summary>
        /// ボーンの内訳。ヒエラルキーの直下の子ごとに集計する。
        /// 「どの服がボーンを増やしているか」が分かるようにするのが狙い。
        /// </summary>
        public static List<CostItem> BonesByGroup(ReferenceIndex index, ProtectionMap protection,
            int limit = 30)
        {
            var root = index.AvatarRoot.transform;
            var counts = new Dictionary<Transform, (int total, int free)>();

            foreach (var t in index.AllTransforms)
            {
                if (t == root) continue;

                // ルート直下の祖先を探して、そこにまとめる
                var group = t;
                while (group.parent != null && group.parent != root) group = group.parent;

                counts.TryGetValue(group, out var c);
                c.total++;
                if (protection.LevelOf(t) == ProtectionLevel.Free) c.free++;
                counts[group] = c;
            }

            var items = counts.Select(kv => new CostItem
            {
                Name = kv.Key.name,
                Weight = kv.Value.total,
                WeightText = $"{kv.Value.total} 本",
                Advice = kv.Value.free > 0
                    ? $"うち {kv.Value.free} 本はどこからも参照されていない（削除候補）"
                    : "",
                Target = kv.Key.gameObject,
            }).ToList();

            items.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            return items.Take(limit).ToList();
        }

        /// <summary>
        /// 危険物の検出。仕様 7.A-3。自動では触らず、警告だけ出す。
        /// </summary>
        public static List<CostItem> Hazards(ReferenceIndex index)
        {
            var items = new List<CostItem>();
            if (index.AvatarRoot == null) return items;

            foreach (var comp in index.AvatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                string advice = null;

                switch (comp.GetType().Name)
                {
                    case "Cloth":
                        advice = "非常に重い。PhysBone への置き換えを推奨";
                        break;
                    case "Light":
                        advice = "VRChat では負荷になる。削除を検討";
                        break;
                    case "Camera":
                        advice = "アバターに付いていると無効化される。削除を検討";
                        break;
                    case "AudioSource":
                        advice = "数が多いと Performance Rank に影響する";
                        break;
                    case "ParentConstraint":
                    case "RotationConstraint":
                    case "PositionConstraint":
                    case "ScaleConstraint":
                    case "AimConstraint":
                    case "LookAtConstraint":
                        advice = "VRC Constraint の方が軽い（ただし Constraint 上限は緩いので優先度低）";
                        break;
                }

                if (advice == null) continue;

                items.Add(new CostItem
                {
                    Name = $"{comp.GetType().Name} @ {HierarchyPath.Of(comp.transform)}",
                    Weight = 1,
                    WeightText = "",
                    Advice = advice,
                    Target = comp,
                });
            }

            // Read/Write が有効なメッシュ（メモリを 2 倍消費する）
            var seen = new HashSet<Mesh>();
            foreach (var smr in index.SkinnedMeshes)
            {
                var m = smr != null ? smr.sharedMesh : null;
                if (m == null || !seen.Add(m)) continue;
                if (!m.isReadable) continue;

                items.Add(new CostItem
                {
                    Name = $"Read/Write 有効: {m.name}",
                    Weight = 1,
                    WeightText = "",
                    Advice = "メモリを 2 倍消費する。インポート設定で無効化を検討",
                    Target = m,
                });
            }

            return items;
        }
    }
}
