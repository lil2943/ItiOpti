using System;
using System.Collections.Generic;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>
    /// UV 上でつながっている三角形のかたまり（＝島）。
    ///
    /// アトラス化は「テクスチャを 1 枚にまとめる」処理だが、
    /// 素直に縮小して並べても空白だらけになる。実測では UV 占有率が 22〜62% しかなく、
    /// 残りは使われていない余白だった（logs/reports/2026-09-10_atlas-headroom.txt）。
    /// そこで島単位でバラして詰め直す。これが 50〜60% の削減の出どころ。
    /// </summary>
    public sealed class UvIsland
    {
        /// <summary>この島に属する三角形の、submesh 三角形配列内での開始位置（3 の倍数）。</summary>
        public readonly List<int> TriangleStarts = new List<int>();

        /// <summary>UV 空間での外接矩形。</summary>
        public Vector2 Min = new Vector2(float.MaxValue, float.MaxValue);
        public Vector2 Max = new Vector2(float.MinValue, float.MinValue);

        public Vector2 Size => Max - Min;
        public float Area => Mathf.Max(0f, Size.x) * Mathf.Max(0f, Size.y);
    }

    public static class UvIslandFinder
    {
        /// <summary>
        /// UV が同じとみなす距離。頂点は法線の切れ目などで複製されていることが多く、
        /// UV が同一なのに別の島として扱うと、詰め直したとき継ぎ目が出る。
        /// そのため位置が一致する UV 頂点は同じ島に融合する。
        /// </summary>
        public const float WeldEpsilon = 1e-5f;

        /// <summary>
        /// submesh の三角形を、UV 上でつながった島に分ける。
        /// </summary>
        /// <param name="triangles">submesh の三角形配列（頂点インデックス、3 個ずつ）</param>
        /// <param name="uv">メッシュ全体の UV 配列</param>
        public static List<UvIsland> Find(int[] triangles, Vector2[] uv)
        {
            var result = new List<UvIsland>();
            if (triangles == null || uv == null || triangles.Length < 3) return result;

            // 1) UV 位置が同じ頂点を 1 つにまとめる（代表頂点を決める）
            var weld = BuildWeldMap(triangles, uv);

            // 2) 三角形が共有する頂点で union-find
            var parent = new Dictionary<int, int>();
            int Find2(int x)
            {
                int root = x;
                while (parent.TryGetValue(root, out var p) && p != root) root = p;
                while (parent.TryGetValue(x, out var p) && p != x) { parent[x] = root; x = p; }
                return root;
            }
            void Union(int a, int b)
            {
                if (!parent.ContainsKey(a)) parent[a] = a;
                if (!parent.ContainsKey(b)) parent[b] = b;
                int ra = Find2(a), rb = Find2(b);
                if (ra != rb) parent[ra] = rb;
            }

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = weld[triangles[t]];
                int b = weld[triangles[t + 1]];
                int c = weld[triangles[t + 2]];
                Union(a, b);
                Union(b, c);
            }

            // 3) 代表頂点ごとに島へ振り分ける
            var byRoot = new Dictionary<int, UvIsland>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int root = Find2(weld[triangles[t]]);
                if (!byRoot.TryGetValue(root, out var island))
                {
                    island = new UvIsland();
                    byRoot[root] = island;
                    result.Add(island);
                }
                island.TriangleStarts.Add(t);
                for (int k = 0; k < 3; k++)
                {
                    var p = uv[triangles[t + k]];
                    if (p.x < island.Min.x) island.Min.x = p.x;
                    if (p.y < island.Min.y) island.Min.y = p.y;
                    if (p.x > island.Max.x) island.Max.x = p.x;
                    if (p.y > island.Max.y) island.Max.y = p.y;
                }
            }

            return result;
        }

        /// <summary>
        /// UV 位置が一致する頂点を、いちばん小さいインデックスの頂点に寄せる表を作る。
        /// 三角形に現れる頂点だけを対象にする（メッシュ全体を舐めない）。
        /// </summary>
        private static Dictionary<int, int> BuildWeldMap(int[] triangles, Vector2[] uv)
        {
            var map = new Dictionary<int, int>();
            var byCell = new Dictionary<(long, long), int>();
            const float inv = 1f / WeldEpsilon;

            foreach (var vi in triangles)
            {
                if (vi < 0 || vi >= uv.Length) { map[vi] = vi; continue; }
                if (map.ContainsKey(vi)) continue;
                var p = uv[vi];
                long cx = (long)Mathf.Round(p.x * inv);
                long cy = (long)Mathf.Round(p.y * inv);

                // 量子化の境目でちょうど分かれてしまうことがあるので、隣のマスも見る。
                int found = -1;
                for (long dx = -1; dx <= 1 && found < 0; dx++)
                for (long dy = -1; dy <= 1 && found < 0; dy++)
                {
                    if (!byCell.TryGetValue((cx + dx, cy + dy), out var cand)) continue;
                    if ((uv[cand] - p).sqrMagnitude <= WeldEpsilon * WeldEpsilon) found = cand;
                }

                if (found >= 0) map[vi] = found;
                else { byCell[(cx, cy)] = vi; map[vi] = vi; }
            }
            return map;
        }

        /// <summary>UV が 0-1 の外に出ている島かどうか（タイリング）。</summary>
        public static bool IsTiled(UvIsland island, float eps = 0.001f)
            => island.Min.x < -eps || island.Min.y < -eps
            || island.Max.x > 1f + eps || island.Max.y > 1f + eps;
    }
}
