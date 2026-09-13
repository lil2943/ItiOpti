using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    /// <summary>
    /// 「テクスチャアトラス化にどれだけ余地があるか」を、実装前に測るためのもの。
    ///
    /// 【なぜ作ったか】
    /// B-6（服アーマチュア統合）とフェーズ0.5後半で、
    /// 「ビルド前の数字を根拠に実装を決めて、実際は既存ツールが済ませていた」
    /// という失敗を 2 回した。アトラス化は仕様上いちばん重い機能なので、
    /// **作る前に、ビルド後の状態で本当に余地があるかを数える。**
    ///
    /// 測るもの:
    ///   1. マテリアルごとの UV 占有率（テクスチャのうち実際にメッシュが使う面積）
    ///      → ここが低いほどアトラス化で削れる。
    ///   2. シェーダー別のグループ分け（アトラス化は同一シェーダーでしかできない）
    ///   3. UV が 0-1 の外に出ているマテリアル（タイリング。素直にはアトラス化できない）
    ///
    /// データは変更しない。
    /// </summary>
    public static class AtlasHeadroom
    {
        /// <summary>アトラスの詰め込み効率の見積もり。島詰めの実測に近い保守的な値。</summary>
        private const float PackingEfficiency = 0.85f;

        private sealed class MatInfo
        {
            public Material Mat;
            public float UvArea;          // UV 三角形面積の合計（0-1 空間。1.0 = テクスチャ全面）
            public bool UvOutOfRange;     // 0-1 の外に出ている（タイリング）
            public int SlotCount;         // 何スロットで使われているか
            public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        }

        public static void Report(ReferenceIndex idx, StringBuilder sb)
        {
            sb.AppendLine("  [アトラス化の余地（ビルド後）]");

            var infos = new Dictionary<Material, MatInfo>();
            int meshesWithoutUv = 0;

            var renderers = new List<Renderer>();
            renderers.AddRange(idx.SkinnedMeshes.Where(r => r != null).Cast<Renderer>());
            renderers.AddRange(idx.MeshRenderers.Where(r => r != null).Cast<Renderer>());

            foreach (var r in renderers)
            {
                var mesh = MeshOf(r);
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                var uv = mesh.uv;
                if (uv == null || uv.Length == 0) { meshesWithoutUv++; continue; }

                int subs = mesh.subMeshCount;
                for (int s = 0; s < subs && s < mats.Length; s++)
                {
                    var mat = mats[s];
                    if (mat == null) continue;
                    if (!infos.TryGetValue(mat, out var info))
                    {
                        info = new MatInfo { Mat = mat };
                        infos[mat] = info;
                    }
                    info.SlotCount++;

                    int[] tris;
                    try { tris = mesh.GetTriangles(s); }
                    catch (Exception) { continue; }

                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                        if (i0 >= uv.Length || i1 >= uv.Length || i2 >= uv.Length) continue;
                        var a = uv[i0]; var b = uv[i1]; var c = uv[i2];
                        float cross = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                        info.UvArea += Mathf.Abs(cross) * 0.5f;

                        if (!info.UvOutOfRange && OutOfRange(a, b, c)) info.UvOutOfRange = true;
                    }
                }
            }

            // マテリアル → テクスチャ
            foreach (var kv in idx.TextureUsage)
            {
                var t2 = kv.Key as Texture2D;
                if (t2 == null) continue;
                foreach (var use in kv.Value)
                {
                    if (use.Mat == null) continue;
                    if (infos.TryGetValue(use.Mat, out var info)) info.Textures.Add(t2);
                }
            }

            if (infos.Count == 0)
            {
                sb.AppendLine("    対象マテリアルなし");
                sb.AppendLine();
                return;
            }

            // シェーダー別に集計（アトラス化は同一シェーダー内でしかできない）
            var byShader = infos.Values
                .GroupBy(i => i.Mat.shader != null ? i.Mat.shader.name : "(シェーダー無し)")
                .OrderByDescending(g => g.Sum(i => CurrentPixels(i)))
                .ToList();

            sb.AppendLine($"    {"シェーダー",-34}{"マテ",5}{"スロ",5}{"UV占有",8}{"現在",10}{"アトラス後",12}");

            long totalCur = 0, totalAtlas = 0;
            int totalMats = 0, totalSlots = 0, totalTiled = 0;

            foreach (var g in byShader)
            {
                var list = g.ToList();
                long cur = list.Sum(CurrentPixels);
                // 実際にメッシュが使っているピクセル数。詰め込み効率で割って必要量を出す。
                double used = list.Sum(i => UsedPixels(i)) / PackingEfficiency;
                long atlas = RoundUpToAtlas(used);
                if (atlas > cur) atlas = cur;   // アトラス化して増えることはしない

                int tiled = list.Count(i => i.UvOutOfRange);
                float avgCoverage = list.Count > 0
                    ? list.Sum(i => Mathf.Min(i.UvArea, 4f)) / list.Count : 0f;

                sb.AppendLine($"    {Trim(g.Key, 33),-34}" +
                              $"{list.Count,5}{list.Sum(i => i.SlotCount),5}" +
                              $"{avgCoverage * 100f,7:F0}%" +
                              $"{Px(cur),10}{Px(atlas),12}" +
                              (tiled > 0 ? $"  ※タイリング {tiled} 件" : ""));

                totalCur += cur;
                totalAtlas += atlas;
                totalMats += list.Count;
                totalSlots += list.Sum(i => i.SlotCount);
                totalTiled += tiled;
            }

            sb.AppendLine($"    {"合計",-34}{totalMats,5}{totalSlots,5}{"",8}{Px(totalCur),10}{Px(totalAtlas),12}");
            sb.AppendLine();

            // シェーダー名だけで数えると楽観的すぎる。
            // 仕様 J-0-3 の実測では、統合を妨げているのはシェーダーの違いではなく
            // 同じ lilToon 内での設定の散らばり（_Cull / _Cutoff / _OutlineWidth など）だった。
            // そこで「本当に統合できる組」= 阻害プロパティまで一致する組でも数える。
            var byBlockingKey = infos.Values
                .GroupBy(i => BlockingKey(i.Mat), StringComparer.Ordinal)
                .ToList();
            int realGroups = byBlockingKey.Count;
            int biggest = byBlockingKey.Max(g => g.Count());
            int singletons = byBlockingKey.Count(g => g.Count() == 1);

            sb.AppendLine($"    [統合を妨げる設定まで見た場合のグループ数]");
            sb.AppendLine($"      シェーダー名だけで分けた場合 : {byShader.Count} グループ");
            sb.AppendLine($"      阻害プロパティまで見た場合   : {realGroups} グループ" +
                          $"（最大 {biggest} 個、1 個だけのグループ {singletons}）");
            sb.AppendLine("      ※ 阻害プロパティ = シェーダー / _Cull / _ZWrite / レンダーキュー /");
            sb.AppendLine("         _Cutoff / _OutlineWidth / _AlphaMaskMode（仕様 J-0-3 の実測に基づく）");
            sb.AppendLine();

            double cut = totalCur > 0 ? (totalCur - totalAtlas) * 100.0 / totalCur : 0;
            sb.AppendLine($"    → アトラス化で減らせるピクセル数の見込み: {cut:F0}%" +
                          $"（{Px(totalCur)} → {Px(totalAtlas)}）");
            sb.AppendLine($"    → スロット数: {totalSlots} → シェーダー {byShader.Count} 種類" +
                          "（理論上の下限。実際はレンダーキュー等でもう少し増える）");
            if (totalTiled > 0)
                sb.AppendLine($"    → うち {totalTiled} マテリアルは UV が 0-1 の外（タイリング）で、" +
                              "そのままではアトラス化できない");
            if (meshesWithoutUv > 0)
                sb.AppendLine($"    → UV を持たないメッシュ {meshesWithoutUv} 個は除外した");
            sb.AppendLine();
            sb.AppendLine("    ※ UV占有 = テクスチャのうちメッシュが実際に使っている面積の割合。");
            sb.AppendLine("      100% なら詰める隙間が無く、アトラス化しても減らない。");
            sb.AppendLine($"      詰め込み効率は {PackingEfficiency:P0} と仮定した保守的な見積もり。");
            sb.AppendLine("      左右対称で UV を重ねているメッシュは面積を二重に数えるため、");
            sb.AppendLine("      占有率は実際より高めに出る＝削減見込みは低めに出る（安全側）。");
            sb.AppendLine();
        }

        /// <summary>
        /// これが一致しないマテリアル同士はアトラス化で 1 スロットにまとめられない、というキー。
        /// 仕様 J-0-3 の実測（lilToon 2,725 個の値の散らばり調査）に基づく。
        /// </summary>
        private static string BlockingKey(Material m)
        {
            if (m == null) return "(null)";
            var sb = new StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "(no shader)");
            sb.Append('|').Append(m.renderQueue);
            foreach (var p in new[] { "_Cull", "_ZWrite", "_Cutoff", "_OutlineWidth", "_AlphaMaskMode" })
            {
                sb.Append('|');
                if (m.HasProperty(p)) sb.Append(m.GetFloat(p).ToString("F4"));
                else sb.Append('-');
            }
            return sb.ToString();
        }

        private static bool OutOfRange(Vector2 a, Vector2 b, Vector2 c)
        {
            const float eps = 0.001f;
            return a.x < -eps || a.x > 1f + eps || a.y < -eps || a.y > 1f + eps
                || b.x < -eps || b.x > 1f + eps || b.y < -eps || b.y > 1f + eps
                || c.x < -eps || c.x > 1f + eps || c.y < -eps || c.y > 1f + eps;
        }

        private static long CurrentPixels(MatInfo i)
            => i.Textures.Sum(t => (long)t.width * t.height);

        /// <summary>そのマテリアルが実際に使っているピクセル数。UV 占有率 × テクスチャ面積。</summary>
        private static double UsedPixels(MatInfo i)
        {
            // タイリングしているものは丸ごと必要とみなす（安全側）
            float coverage = i.UvOutOfRange ? 1f : Mathf.Clamp01(i.UvArea);
            return i.Textures.Sum(t => (double)t.width * t.height * coverage);
        }

        /// <summary>必要ピクセル数を、実際に作れる正方形テクスチャの大きさに切り上げる。</summary>
        private static long RoundUpToAtlas(double neededPixels)
        {
            if (neededPixels <= 0) return 0;
            long total = 0;
            double remain = neededPixels;
            const long maxSheet = 4096L * 4096L;
            while (remain > maxSheet)      // 4096 に収まらない分は複数枚
            {
                total += maxSheet;
                remain -= maxSheet;
            }
            int size = 32;
            while ((long)size * size < remain && size < 4096) size *= 2;
            return total + (long)size * size;
        }

        private static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static string Px(long pixels)
        {
            if (pixels >= 1000000) return $"{pixels / 1000000.0:F1}Mpx";
            if (pixels >= 1000) return $"{pixels / 1000.0:F0}Kpx";
            return $"{pixels}px";
        }

        private static string Trim(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
