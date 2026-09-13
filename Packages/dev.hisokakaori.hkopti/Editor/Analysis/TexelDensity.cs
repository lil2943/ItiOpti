using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    /// <summary>
    /// テクセル密度から「そのテクスチャに必要な解像度」を求める。仕様 G-1。
    ///
    ///   密度 = 解像度 × sqrt(UV面積 / ワールド面積)
    ///        = アバター表面 1 メートルあたり何ピクセル使っているか
    ///
    /// 【この機能の性質】
    /// 必要解像度は**アバターごとに変わる**。同じテクスチャでも、大きく貼るアバターと
    /// 小さく貼るアバターでは答えが違う。だからアルファ不要や単色のように
    /// 「アセット自体の性質」として一律に決めることはできない。
    ///
    /// プロジェクト全体のインポート設定として適用する場合は、
    /// **そのテクスチャを使う全アバターの中で最大の要求値**を採る（<see cref="MergeConservative"/>）。
    /// そうすればどのアバターでも劣化しない。
    /// </summary>
    public static class TexelDensity
    {
        /// <summary>
        /// 目標テクセル密度 (px/m)。
        ///
        /// 【この値の決め方で一度失敗している】
        /// 最初 1024 px/m にしたところ、ほぼ全テクスチャが「過大」と判定され、
        /// 削減見込みがテクスチャ総容量を超えるという明らかにおかしな結果になった。
        ///
        /// VRChat は VR で至近距離から見られるため、一般的な 3D ゲームより高い密度が要る。
        /// 実アバターは体の表面積 1.5〜2 m² に 2K〜4K を貼っており、1,400〜2,900 px/m にあたる。
        /// 既定は 2048 px/m（＝体に 2K 相当）。
        /// </summary>
        public const float DefaultTargetPixelsPerMeter = 2048f;

        public sealed class Options
        {
            public float TargetPixelsPerMeter = DefaultTargetPixelsPerMeter;

            /// <summary>
            /// 安全マージン。計算値から 2 の冪で何段上を推奨するか。
            /// 0 = 計算どおり、1 = 1 段大きめ（＝面積 4 倍ぶんの余裕）。
            /// 推定を外したときに「足りない」側へ倒れないための保険。
            /// </summary>
            public int SafetyDoublings = 0;

            /// <summary>これ未満の解像度は最初から対象にしない。</summary>
            public int MinimumSizeToConsider = 512;

            /// <summary>顔・目など寄りで見る部位は密度を何倍にするか。</summary>
            public float CloseUpMultiplier = 2f;

            /// <summary>ノーマル / マスク系の密度倍率。本体の半分で足りることが多い。</summary>
            public float AuxiliaryMapMultiplier = 0.5f;
        }

        public sealed class Result
        {
            /// <summary>テクスチャ → 推奨解像度（2 の冪）。現在値より大きい場合も含む。</summary>
            public readonly Dictionary<Texture2D, int> Recommended =
                new Dictionary<Texture2D, int>();

            /// <summary>
            /// 面積が取れず推奨値を計算できなかったテクスチャ。
            /// **縮小してはいけない。** 判断材料が無いものを縮めると事故になる。
            /// </summary>
            public readonly HashSet<Texture2D> Unknown = new HashSet<Texture2D>();
        }

        /// <summary>近くで見られる部位。</summary>
        private static readonly string[] CloseUpHints =
            { "face", "eye", "mouth", "head", "hair", "顔", "目", "髪" };

        // =================================================================

        public static Result Analyze(ReferenceIndex index, Options options = null)
        {
            var opt = options ?? new Options();
            var result = new Result();
            if (index == null) return result;

            var area = CollectMaterialAreas(index);

            foreach (var kv in index.TextureUsage)
            {
                var t2 = kv.Key as Texture2D;
                if (t2 == null) continue;

                int size = Math.Max(t2.width, t2.height);
                if (size < opt.MinimumSizeToConsider) continue;

                int recommend = 0;
                bool anyComputed = false;

                foreach (var (mat, prop) in kv.Value)
                {
                    if (mat == null || !area.TryGetValue(mat, out var a)) continue;
                    if (a.world <= 0 || a.uv <= 0) continue;
                    anyComputed = true;

                    float target = opt.TargetPixelsPerMeter;

                    var lower = (mat.name + " " + t2.name).ToLowerInvariant();
                    if (CloseUpHints.Any(h => lower.Contains(h)))
                        target *= opt.CloseUpMultiplier;

                    if (prop.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0)
                        target *= opt.AuxiliaryMapMultiplier;

                    double needed = target * Math.Sqrt(a.world / a.uv);
                    int pow2 = NextPow2((int)Math.Ceiling(needed));

                    for (int i = 0; i < opt.SafetyDoublings; i++) pow2 <<= 1;
                    pow2 = Mathf.Clamp(pow2, 64, 8192);

                    // 同じテクスチャを複数マテリアルが使うなら、一番大きい要求に合わせる
                    if (pow2 > recommend) recommend = pow2;
                }

                if (anyComputed && recommend > 0) result.Recommended[t2] = recommend;
                else result.Unknown.Add(t2);
            }

            return result;
        }

        /// <summary>
        /// 複数アバターの結果を安全側に合成する。
        ///
        /// 規則：
        ///   ・推奨値は**最大**を採る（一番大きく貼っているアバターに合わせる）
        ///   ・**1 体でも計算できなかったテクスチャは対象外**にする
        ///     （そのアバターで実は大きく貼っているかもしれないため）
        /// </summary>
        public static void MergeConservative(Result into, Result add)
        {
            if (into == null || add == null) return;

            foreach (var kv in add.Recommended)
            {
                if (into.Recommended.TryGetValue(kv.Key, out var cur))
                {
                    if (kv.Value > cur) into.Recommended[kv.Key] = kv.Value;
                }
                else
                {
                    into.Recommended[kv.Key] = kv.Value;
                }
            }

            foreach (var t in add.Unknown) into.Unknown.Add(t);
        }

        /// <summary>
        /// 合成結果から「安全に縮小できるもの」だけを取り出す。
        /// Unknown に入っているものは必ず除く。
        /// </summary>
        public static Dictionary<Texture2D, int> SafeReductions(Result merged)
        {
            var d = new Dictionary<Texture2D, int>();
            if (merged == null) return d;

            foreach (var kv in merged.Recommended)
            {
                var t2 = kv.Key;
                if (t2 == null) continue;
                if (merged.Unknown.Contains(t2)) continue;   // 1 体でも不明なら触らない

                int size = Math.Max(t2.width, t2.height);
                if (kv.Value < size) d[t2] = kv.Value;
            }
            return d;
        }

        // =================================================================

        /// <summary>マテリアル → (UV 面積, ワールド面積)。</summary>
        private static Dictionary<Material, (double uv, double world)> CollectMaterialAreas(
            ReferenceIndex index)
        {
            var area = new Dictionary<Material, (double uv, double world)>();

            foreach (var smr in index.SkinnedMeshes)
            {
                if (smr == null) continue;
                Accumulate(area, smr.sharedMesh, smr.sharedMaterials, smr.transform);
            }

            // MeshRenderer 側も見ないと、そこでしか使われないテクスチャが
            // ずっと Unknown 扱いになって縮小候補から漏れる。
            foreach (var mr in index.MeshRenderers)
            {
                if (mr == null) continue;
                var mf = mr.GetComponent<MeshFilter>();
                Accumulate(area, mf != null ? mf.sharedMesh : null, mr.sharedMaterials, mr.transform);
            }

            return area;
        }

        private static void Accumulate(Dictionary<Material, (double uv, double world)> area,
            Mesh mesh, Material[] mats, Transform owner)
        {
            if (mesh == null || mats == null || owner == null) return;

            Vector3[] verts;
            Vector2[] uvs;
            try
            {
                verts = mesh.vertices;
                uvs = mesh.uv;
            }
            catch (Exception)
            {
                return;
            }
            if (verts == null || uvs == null || uvs.Length != verts.Length) return;

            var scale = owner.lossyScale;
            float scaleFactor = (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;

            int sub = Math.Min(mesh.subMeshCount, mats.Length);
            for (int s = 0; s < sub; s++)
            {
                var mat = mats[s];
                if (mat == null) continue;

                int[] tris;
                try
                {
                    tris = mesh.GetTriangles(s);
                }
                catch (Exception)
                {
                    continue;
                }

                double uvArea = 0, worldArea = 0;

                // 三角形が多いメッシュは間引いて概算する（速度のため）
                int step = tris.Length > 30000 ? 3 * (tris.Length / 30000) : 3;
                if (step % 3 != 0) step += 3 - (step % 3);

                int sampled = 0;
                for (int i = 0; i + 2 < tris.Length; i += step)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    if (a >= verts.Length || b >= verts.Length || c >= verts.Length) continue;

                    var p0 = verts[a] * scaleFactor;
                    var p1 = verts[b] * scaleFactor;
                    var p2 = verts[c] * scaleFactor;
                    worldArea += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;

                    var t0 = uvs[a]; var t1 = uvs[b]; var t2 = uvs[c];
                    uvArea += Math.Abs((t1.x - t0.x) * (t2.y - t0.y) -
                                       (t2.x - t0.x) * (t1.y - t0.y)) * 0.5;
                    sampled++;
                }
                if (sampled == 0 || worldArea <= 0 || uvArea <= 0) continue;

                double factor = (double)(tris.Length / 3) / sampled;
                uvArea *= factor;
                worldArea *= factor;

                area.TryGetValue(mat, out var acc);
                area[mat] = (acc.uv + uvArea, acc.world + worldArea);
            }
        }

        public static int NextPow2(int v)
        {
            int p = 64;
            while (p < v && p < 8192) p <<= 1;
            return p;
        }
    }
}
