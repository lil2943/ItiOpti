using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>
    /// アトラス化で同じマテリアルになった submesh どうしを 1 つにまとめる。
    ///
    /// 【なぜ必要か】
    /// テクスチャを 1 枚にまとめただけでは **マテリアルスロットの数は減らない**。
    /// スロットは submesh の数で決まるので、submesh をまとめて初めて減る。
    /// VRChat のパフォーマンスランクとドローコールに効くのはこちら。
    /// </summary>
    public static class SubMeshMerger
    {
        /// <summary>
        /// 1 つの Renderer について、同じマテリアルを指す submesh をまとめる。
        /// メッシュは複製して差し替える。まとめる対象が無ければ何もしない。
        /// </summary>
        /// <returns>減ったスロット数</returns>
        public static int Merge(Renderer renderer, Dictionary<string, Mesh> meshCache,
            List<Mesh> generated)
        {
            if (renderer == null) return 0;
            var mesh = MeshOf(renderer);
            var mats = renderer.sharedMaterials;
            if (mesh == null || mats == null || mats.Length < 2) return 0;

            // submesh の数よりマテリアルスロットが多い場合、余ったスロットは
            // 「最後の submesh をもう一度描く」という意図的な多重描画（輪郭線の手法など）。
            // 実測でも 1 マテリアル 17 スロットのアバターがあった。
            // ここを詰めると見た目が変わるので触らない。
            if (mats.Length > mesh.subMeshCount) return 0;

            int subCount = Mathf.Min(mesh.subMeshCount, mats.Length);
            if (subCount < 2) return 0;

            // 同じマテリアルを指すスロットを、最初に出てきた順にまとめる
            var order = new List<Material>();
            var slotsOf = new Dictionary<Material, List<int>>();
            for (int s = 0; s < subCount; s++)
            {
                var m = mats[s];
                if (m == null) continue;
                if (!slotsOf.TryGetValue(m, out var list))
                {
                    list = new List<int>();
                    slotsOf[m] = list;
                    order.Add(m);
                }
                list.Add(s);
            }

            // 余分なスロットが無いなら触らない
            if (order.Count == subCount) return 0;

            // 同じメッシュを使っていても、Renderer ごとにマテリアルの並びが違えば
            // まとめ方も変わる。キーはメッシュとマテリアルの並びの両方で作る。
            var key = mesh.GetInstanceID() + "|" +
                      string.Join(",", mats.Select(m => m == null ? "0" : m.GetInstanceID().ToString()));
            if (meshCache != null && meshCache.TryGetValue(key, out var cached) && cached != null)
            {
                SetMesh(renderer, cached);
                renderer.sharedMaterials = order.ToArray();
                return mats.Length - order.Count;
            }

            var merged = UnityEngine.Object.Instantiate(mesh);
            merged.name = mesh.name + " (ItiOptimiser Merged)";

            var triangleSets = new List<int[]>(order.Count);
            foreach (var m in order)
            {
                var combined = new List<int>();
                foreach (var s in slotsOf[m]) combined.AddRange(mesh.GetTriangles(s));
                triangleSets.Add(combined.ToArray());
            }

            // まとめた後に残る余りの submesh（マテリアルが null のスロットなど）は捨てる
            merged.subMeshCount = triangleSets.Count;
            for (int i = 0; i < triangleSets.Count; i++)
                merged.SetTriangles(triangleSets[i], i, false);
            merged.RecalculateBounds();

            if (meshCache != null) meshCache[key] = merged;
            generated?.Add(merged);

            SetMesh(renderer, merged);
            renderer.sharedMaterials = order.ToArray();
            return mats.Length - order.Count;
        }

        private static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static void SetMesh(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer smr) { smr.sharedMesh = mesh; return; }
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = mesh;
        }
    }
}
