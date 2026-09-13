using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>
    /// アトラスの配置に合わせて、メッシュの UV を書き換える。
    ///
    /// 【気をつけること】
    /// 1 つの頂点が複数の島に属することは無い（島は頂点の連結成分なので）。
    /// ただし **1 つの頂点が複数の submesh に共有されている**ことはある。
    /// その場合は submesh ごとに行き先が違うので、頂点を複製しないと壊れる。
    /// ここを間違えると、服の一部だけ変な模様になる。
    /// </summary>
    public static class MeshUvRewriter
    {
        /// <summary>
        /// 元のメッシュを複製し、UV を書き換えたものを返す。元のメッシュは変更しない。
        /// </summary>
        /// <param name="mesh">対象メッシュ</param>
        /// <param name="placements">このメッシュに対する島の配置（submesh 混在で可）</param>
        public static Mesh Rewrite(Mesh mesh, IEnumerable<IslandPlacement> placements)
        {
            if (mesh == null) return null;
            var list = placements?.Where(p => p != null && p.Mesh == mesh).ToList()
                       ?? new List<IslandPlacement>();
            if (list.Count == 0) return null;

            var uv = mesh.uv;
            if (uv == null || uv.Length == 0) return null;

            // 頂点 → 新しい UV。行き先が食い違ったら頂点を複製する。
            var newUv = (Vector2[])uv.Clone();
            var assigned = new Dictionary<int, int>();   // 頂点 → 割り当てた配置の通し番号
            var duplicates = new List<(int sourceVertex, Vector2 uvValue)>();
            var remap = new Dictionary<(int vertex, int placement), int>();

            // submesh ごとの三角形を取り出しておく（書き換え後に入れ直す）
            int subCount = mesh.subMeshCount;
            var tris = new int[subCount][];
            for (int s = 0; s < subCount; s++) tris[s] = mesh.GetTriangles(s);

            int nextVertex = mesh.vertexCount;

            for (int pi = 0; pi < list.Count; pi++)
            {
                var p = list[pi];
                if (p.SubMeshIndex < 0 || p.SubMeshIndex >= subCount) continue;
                var scale = p.Scale;
                var offset = p.Offset;
                var t = tris[p.SubMeshIndex];

                foreach (var start in p.Island.TriangleStarts)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int idx = start + k;
                        if (idx >= t.Length) continue;
                        int v = t[idx];
                        if (v < 0 || v >= uv.Length) continue;

                        var mapped = new Vector2(uv[v].x * scale.x + offset.x,
                                                 uv[v].y * scale.y + offset.y);

                        if (!assigned.TryGetValue(v, out var owner))
                        {
                            assigned[v] = pi;
                            newUv[v] = mapped;
                            continue;
                        }
                        if (owner == pi) continue;

                        // 別の島（＝別の行き先）から同じ頂点を使っている。複製する。
                        var key = (v, pi);
                        if (!remap.TryGetValue(key, out var dup))
                        {
                            dup = nextVertex++;
                            remap[key] = dup;
                            duplicates.Add((v, mapped));
                        }
                        t[idx] = dup;
                    }
                }
            }

            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name + " (ItiOptimiser Atlas)";

            if (duplicates.Count > 0)
            {
                AppendDuplicatedVertices(copy, mesh, duplicates, newUv, out newUv);
            }
            else
            {
                copy.uv = newUv;
            }

            for (int s = 0; s < subCount; s++) copy.SetTriangles(tris[s], s, false);
            copy.RecalculateBounds();
            return copy;
        }

        /// <summary>
        /// 複製した頂点を末尾に足す。位置・法線・ボーンウェイト・BlendShape をすべて引き継ぐ。
        /// ここを取りこぼすと、その頂点だけ動かない／潰れるという分かりにくい壊れ方をする。
        /// </summary>
        private static void AppendDuplicatedVertices(
            Mesh copy, Mesh source,
            List<(int sourceVertex, Vector2 uvValue)> duplicates,
            Vector2[] baseUv, out Vector2[] resultUv)
        {
            int oldCount = source.vertexCount;
            int newCount = oldCount + duplicates.Count;

            var vertices = Grow(source.vertices, newCount, duplicates);
            var normals = Grow(source.normals, newCount, duplicates);
            var tangents = Grow(source.tangents, newCount, duplicates);
            var colors = Grow(source.colors, newCount, duplicates);
            resultUv = new Vector2[newCount];
            Array.Copy(baseUv, resultUv, Mathf.Min(baseUv.Length, oldCount));
            for (int i = 0; i < duplicates.Count; i++) resultUv[oldCount + i] = duplicates[i].uvValue;

            copy.Clear(false);
            copy.indexFormat = newCount > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : source.indexFormat;
            copy.vertices = vertices;
            if (normals != null) copy.normals = normals;
            if (tangents != null) copy.tangents = tangents;
            if (colors != null) copy.colors = colors;
            copy.uv = resultUv;
            // UV1..UV7 は特殊シェーダーやツールが自由に使う。Mesh.Clear 後に
            // 書き戻さないとチャンネル自体が消える。また uv2/uv3/uv4 プロパティは
            // Vector2 固定なので、元が Vector3/Vector4 の場合に成分を失う。
            // 元の次元を調べ、同じ型の SetUVs で全チャンネルを復元する。
            for (int channel = 1; channel < 8; channel++)
                CopyUvChannel(copy, source, channel, duplicates);
            CopyBoneWeights(copy, source, duplicates);
            copy.bindposes = source.bindposes;
            copy.subMeshCount = source.subMeshCount;

            CopyBlendShapes(copy, source, oldCount, duplicates);
        }

        private static void CopyUvChannel(Mesh copy, Mesh source, int channel,
            List<(int sourceVertex, Vector2 uvValue)> duplicates)
        {
            var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!source.HasVertexAttribute(attribute)) return;

            int dimension = source.GetVertexAttributeDimension(attribute);
            if (dimension <= 2)
            {
                var values = new List<Vector2>();
                source.GetUVs(channel, values);
                AppendCopies(values, duplicates);
                copy.SetUVs(channel, values);
            }
            else if (dimension == 3)
            {
                var values = new List<Vector3>();
                source.GetUVs(channel, values);
                AppendCopies(values, duplicates);
                copy.SetUVs(channel, values);
            }
            else
            {
                var values = new List<Vector4>();
                source.GetUVs(channel, values);
                AppendCopies(values, duplicates);
                copy.SetUVs(channel, values);
            }
        }

        private static void AppendCopies<T>(List<T> values,
            List<(int sourceVertex, Vector2 uvValue)> duplicates)
        {
            int originalCount = values.Count;
            foreach (var (from, _) in duplicates)
                values.Add(from >= 0 && from < originalCount ? values[from] : default(T));
        }

        /// <summary>
        /// ボーンウェイトを引き継ぐ。
        ///
        /// 【なぜ古い API を使わないか】
        /// Mesh.boneWeights は 1 頂点 4 本までしか返さない。
        /// 5 本以上刺さっているメッシュでこれを使うと、5 本目以降が黙って消える。
        /// 服がぐにゃりと歪むが、原因が非常に分かりにくい壊れ方をする。
        /// </summary>
        private static void CopyBoneWeights(Mesh copy, Mesh source,
            List<(int sourceVertex, Vector2 uvValue)> duplicates)
        {
            var bonesPerVertex = source.GetBonesPerVertex();
            if (bonesPerVertex.Length == 0)
            {
                var legacy = source.boneWeights;
                if (legacy != null && legacy.Length > 0)
                    copy.boneWeights = Grow(legacy, source.vertexCount + duplicates.Count, duplicates);
                return;
            }

            var allWeights = source.GetAllBoneWeights();

            // 頂点ごとのウェイトが、配列のどこから始まるかを先に出しておく
            var start = new int[bonesPerVertex.Length];
            int running = 0;
            for (int i = 0; i < bonesPerVertex.Length; i++)
            {
                start[i] = running;
                running += bonesPerVertex[i];
            }

            var newCounts = new List<byte>(bonesPerVertex.Length + duplicates.Count);
            for (int i = 0; i < bonesPerVertex.Length; i++) newCounts.Add(bonesPerVertex[i]);

            var newWeights = new List<UnityEngine.BoneWeight1>(allWeights.Length);
            for (int i = 0; i < allWeights.Length; i++) newWeights.Add(allWeights[i]);

            foreach (var (from, _) in duplicates)
            {
                if (from < 0 || from >= bonesPerVertex.Length) { newCounts.Add(0); continue; }
                byte n = bonesPerVertex[from];
                newCounts.Add(n);
                for (int k = 0; k < n; k++) newWeights.Add(allWeights[start[from] + k]);
            }

            using (var counts = new Unity.Collections.NativeArray<byte>(
                       newCounts.ToArray(), Unity.Collections.Allocator.Temp))
            using (var weights = new Unity.Collections.NativeArray<UnityEngine.BoneWeight1>(
                       newWeights.ToArray(), Unity.Collections.Allocator.Temp))
            {
                copy.SetBoneWeights(counts, weights);
            }
        }

        private static T[] Grow<T>(T[] src, int newCount,
            List<(int sourceVertex, Vector2 uvValue)> duplicates)
        {
            if (src == null || src.Length == 0) return null;
            var dst = new T[newCount];
            Array.Copy(src, dst, Mathf.Min(src.Length, newCount));
            for (int i = 0; i < duplicates.Count; i++)
            {
                int from = duplicates[i].sourceVertex;
                if (from >= 0 && from < src.Length) dst[src.Length + i] = src[from];
            }
            return dst;
        }

        private static void CopyBlendShapes(Mesh copy, Mesh source, int oldCount,
            List<(int sourceVertex, Vector2 uvValue)> duplicates)
        {
            int newCount = oldCount + duplicates.Count;
            var dp = new Vector3[oldCount];
            var dn = new Vector3[oldCount];
            var dt = new Vector3[oldCount];

            for (int i = 0; i < source.blendShapeCount; i++)
            {
                var name = source.GetBlendShapeName(i);
                int frames = source.GetBlendShapeFrameCount(i);
                for (int f = 0; f < frames; f++)
                {
                    float weight = source.GetBlendShapeFrameWeight(i, f);
                    source.GetBlendShapeFrameVertices(i, f, dp, dn, dt);

                    var np = Expand(dp, newCount, duplicates);
                    var nn = Expand(dn, newCount, duplicates);
                    var nt = Expand(dt, newCount, duplicates);
                    copy.AddBlendShapeFrame(name, weight, np, nn, nt);
                }
            }
        }

        private static Vector3[] Expand(Vector3[] src, int newCount,
            List<(int sourceVertex, Vector2 uvValue)> duplicates)
        {
            if (src == null) return null;
            var dst = new Vector3[newCount];
            Array.Copy(src, dst, Mathf.Min(src.Length, newCount));
            for (int i = 0; i < duplicates.Count; i++)
            {
                int from = duplicates[i].sourceVertex;
                if (from >= 0 && from < src.Length) dst[src.Length + i] = src[from];
            }
            return dst;
        }
    }
}
