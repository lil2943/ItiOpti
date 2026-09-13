using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    /// <summary>
    /// メッシュの中に「使われていないのに容量を食っているデータ」がどれだけあるかを数える。
    ///
    /// 【なぜ measure するか】
    /// ビルド後の内訳では テクスチャ 87% / メッシュ 12% で、
    /// メッシュは 2 番目に大きい。ただし**実装する前に、
    /// 本当に削れる量があるかを確かめる**（B-6・フェーズ0.5 と同じ手順）。
    ///
    /// 数えるもの:
    ///   1. 使われていない UV チャンネル（uv2 / uv3 / uv4）
    ///   2. 頂点カラー（シェーダーが使っていなければ無駄）
    ///   3. 接線（法線マップを使うマテリアルが 1 つも無ければ無駄）
    ///   4. BlendShape の法線・接線の差分が全部ゼロのフレーム
    ///      （1 フレームあたり 頂点数 × 24 バイトを占める）
    ///
    /// データは変更しない。
    /// </summary>
    public static class MeshHeadroom
    {
        // 1 頂点あたりのバイト数（32bit 精度の場合）
        private const int BytesUv = 8;        // Vector2
        private const int BytesColor = 4;     // Color32
        private const int BytesTangent = 16;  // Vector4
        private const int BytesVector3 = 12;

        public static void Report(ReferenceIndex idx, StringBuilder sb)
        {
            sb.AppendLine("  [メッシュの中の無駄（ビルド後）]");

            var renderers = new List<Renderer>();
            renderers.AddRange(idx.SkinnedMeshes.Where(r => r != null).Cast<Renderer>());
            renderers.AddRange(idx.MeshRenderers.Where(r => r != null).Cast<Renderer>());

            long uvExtra = 0, colorExtra = 0, tangentExtra = 0, blendShapeExtra = 0;
            int meshesWithUv2 = 0, meshesWithColor = 0, meshesWithTangent = 0;
            int zeroNormalFrames = 0, totalFrames = 0;
            long totalMeshBytes = 0;

            var seen = new HashSet<Mesh>();

            foreach (var r in renderers)
            {
                var mesh = MeshOf(r);
                if (mesh == null || !seen.Add(mesh)) continue;

                int vc = mesh.vertexCount;
                if (vc <= 0) continue;
                totalMeshBytes += EstimateMeshBytes(mesh);

                // --- 使われていない UV チャンネル ---
                // uv2 以降を実際に読むシェーダーは限られる。
                // ライトマップ用（uv2）はアバターでは使われない。
                int extraUvChannels = 0;
                if (mesh.uv2 != null && mesh.uv2.Length > 0) extraUvChannels++;
                if (mesh.uv3 != null && mesh.uv3.Length > 0) extraUvChannels++;
                if (mesh.uv4 != null && mesh.uv4.Length > 0) extraUvChannels++;
                if (extraUvChannels > 0)
                {
                    meshesWithUv2++;
                    uvExtra += (long)vc * BytesUv * extraUvChannels;
                }

                // --- 頂点カラー ---
                var colors = mesh.colors32;
                if (colors != null && colors.Length > 0)
                {
                    meshesWithColor++;
                    colorExtra += (long)vc * BytesColor;
                }

                // --- 接線（法線マップが 1 つも無ければ不要） ---
                var tangents = mesh.tangents;
                if (tangents != null && tangents.Length > 0)
                {
                    meshesWithTangent++;
                    if (!UsesNormalMap(r)) tangentExtra += (long)vc * BytesTangent;
                }

                // --- BlendShape の法線・接線がゼロのフレーム ---
                if (mesh.blendShapeCount > 0 && vc <= 200000)
                {
                    var dp = new Vector3[vc];
                    var dn = new Vector3[vc];
                    var dt = new Vector3[vc];

                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        int frames = mesh.GetBlendShapeFrameCount(i);
                        for (int f = 0; f < frames; f++)
                        {
                            totalFrames++;
                            try { mesh.GetBlendShapeFrameVertices(i, f, dp, dn, dt); }
                            catch (Exception) { break; }

                            bool normalsZero = AllZero(dn);
                            bool tangentsZero = AllZero(dt);
                            if (normalsZero && tangentsZero)
                            {
                                zeroNormalFrames++;
                                blendShapeExtra += (long)vc * BytesVector3 * 2;
                            }
                            else if (normalsZero || tangentsZero)
                            {
                                blendShapeExtra += (long)vc * BytesVector3;
                            }
                        }
                    }
                }
            }

            sb.AppendLine($"    メッシュ合計（概算）       : {Mb(totalMeshBytes)}");
            sb.AppendLine($"    ├ uv2/uv3/uv4            : {Mb(uvExtra),10}" +
                          $"　（{meshesWithUv2} 個のメッシュが保持）");
            sb.AppendLine($"    ├ 頂点カラー              : {Mb(colorExtra),10}" +
                          $"　（{meshesWithColor} 個のメッシュが保持）");
            sb.AppendLine($"    ├ 接線（法線マップ無し）   : {Mb(tangentExtra),10}" +
                          $"　（接線を持つのは {meshesWithTangent} 個）");
            sb.AppendLine($"    └ BlendShape の法線/接線   : {Mb(blendShapeExtra),10}" +
                          $"　（{zeroNormalFrames} / {totalFrames} フレームが全部ゼロ）");

            long total = uvExtra + colorExtra + tangentExtra + blendShapeExtra;
            double pct = totalMeshBytes > 0 ? total * 100.0 / totalMeshBytes : 0;
            sb.AppendLine();
            sb.AppendLine($"    → 削れる可能性がある合計: {Mb(total)}（メッシュの {pct:F0}%）");
            sb.AppendLine();
            sb.AppendLine("    ※ uv2/頂点カラーは「シェーダーが読んでいないか」の確認が別途要る。");
            sb.AppendLine("      ここの数字は上限。実際に削れる量はこれより小さい。");
            sb.AppendLine("      BlendShape の法線/接線がゼロのフレームは、確実に削れる。");
            sb.AppendLine();
        }

        /// <summary>その Renderer のマテリアルが法線マップを 1 つでも使っているか。</summary>
        private static bool UsesNormalMap(Renderer r)
        {
            foreach (var m in r.sharedMaterials ?? Array.Empty<Material>())
            {
                if (m == null || m.shader == null) continue;
                int count = ShaderUtil.GetPropertyCount(m.shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(m.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                        continue;
                    var name = ShaderUtil.GetPropertyName(m.shader, i);
                    if (name.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (m.GetTexture(name) != null) return true;
                }
            }
            return false;
        }

        private static bool AllZero(Vector3[] values)
        {
            if (values == null) return true;
            for (int i = 0; i < values.Length; i++)
                if (values[i].sqrMagnitude > 1e-12f) return false;
            return true;
        }

        private static long EstimateMeshBytes(Mesh m)
        {
            long perVertex = BytesVector3;                    // position
            if (m.normals != null && m.normals.Length > 0) perVertex += BytesVector3;
            if (m.tangents != null && m.tangents.Length > 0) perVertex += BytesTangent;
            if (m.uv != null && m.uv.Length > 0) perVertex += BytesUv;
            if (m.uv2 != null && m.uv2.Length > 0) perVertex += BytesUv;
            if (m.uv3 != null && m.uv3.Length > 0) perVertex += BytesUv;
            if (m.uv4 != null && m.uv4.Length > 0) perVertex += BytesUv;
            if (m.colors32 != null && m.colors32.Length > 0) perVertex += BytesColor;
            if (m.boneWeights != null && m.boneWeights.Length > 0) perVertex += 32;

            long bytes = perVertex * m.vertexCount;
            bytes += (long)m.triangles.Length * 4;

            // BlendShape は 1 フレームあたり 頂点数 ×（位置 + 法線 + 接線）
            for (int i = 0; i < m.blendShapeCount; i++)
                bytes += (long)m.GetBlendShapeFrameCount(i) * m.vertexCount * BytesVector3 * 3;

            return bytes;
        }

        private static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
