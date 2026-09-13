using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using HisokaKaori.HKOpti.Editor.Core;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    /// <summary>
    /// 「見た目が変わらない」削減余地を 1 件ずつ。
    /// </summary>
    public sealed class Opportunity
    {
        public string Category;
        public string Detail;
        public long SavedBytes;     // メモリ削減の見込み（分からないものは 0）
        public int SavedCount;      // 個数の削減（マテリアル数など）
        public string Risk;         // 空なら完全に安全
        public UnityEngine.Object Target;
    }

    /// <summary>
    /// 見た目に影響を与えずにできる軽量化の余地を洗い出す。
    ///
    /// 方針：**推測で候補を並べない。** 実際にアセットを調べて、
    /// 「これは確実に安全」と言えるものだけを挙げる。
    /// 判断に少しでも幅があるものは Risk に理由を書く。
    /// </summary>
    public static class LosslessScan
    {
        public static List<Opportunity> Run(ReferenceIndex index)
        {
            var found = new List<Opportunity>();
            if (index == null || index.AvatarRoot == null) return found;

            ScanTextures(index, found);
            ScanExpressionMenuIcons(index, found);
            ScanTextureImportSettings(index, found);
            ScanDuplicateTextures(index, found);
            ScanDuplicateMaterials(index, found);
            ScanStaleMaterialProperties(index, found);
            ScanDuplicateMeshes(index, found);
            ScanRendererSettings(index, found);
            ScanPhysBoneSettings(index, found);
            ScanBlendShapes(index, found);
            ScanVertexAttributes(index, found);
            ScanBrokenAnimationPaths(index, found);
            ScanMeshImportSettings(index, found);

            return found;
        }

        /// <summary>1 テクスチャに対する最適化計画。</summary>
        private sealed class TexturePlan
        {
            public Texture2D Tex;
            public long CurrentBytes;
            public long AfterBytes;
            public int RecommendedSize;
            public bool AlphaUnneeded;
            public bool FlatNormal;
            public bool SolidColor;
            public readonly List<string> Reasons = new List<string>();

            public long Saved => Math.Max(0, CurrentBytes - AfterBytes);
        }

        // =================================================================
        // テクスチャの最適化余地を「1 テクスチャにつき 1 回だけ」計算する。
        //
        // 【設計上の注意】
        // 「アルファ不要」「解像度が過大」「単色」を別々に足すと**二重計上になる**。
        // 4K でアルファ不要なテクスチャは、両方の条件に当てはまるので、
        // 単純に足すと削減量が実際の容量を超える（実際に 108% という値が出た）。
        // → 必ず「最適化後のサイズ」を 1 つ計算し、その差分を削減量とすること。
        // =================================================================
        private static void ScanTextures(ReferenceIndex index, List<Opportunity> found)
        {
            bool canReadPixels = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!canReadPixels)
            {
                found.Add(new Opportunity
                {
                    Category = "(スキップ) 画素解析",
                    Detail = "グラフィックスデバイスが無いため、単色 / フラットノーマルの判定を行えませんでした。"
                             + " -nographics を外して実行してください。",
                    Risk = "未判定",
                });
            }

            var density = ComputeRecommendedSizes(index);
            var plans = new List<TexturePlan>();

            foreach (var kv in index.TextureUsage)
            {
                var t2 = kv.Key as Texture2D;
                if (t2 == null) continue;

                var plan = new TexturePlan
                {
                    Tex = t2,
                    CurrentBytes = AvatarStats.EstimateTextureBytes(t2),
                    RecommendedSize = Math.Max(t2.width, t2.height),
                };

                // --- 画素を見て判定する ---
                if (canReadPixels)
                {
                    Color32[] px = null;
                    try { px = SampleSmall(t2, 16); } catch (Exception) { }

                    if (px != null && px.Length > 0)
                    {
                        bool usedAsNormal = kv.Value.Any(v =>
                            v.Prop.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            v.Prop.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0);

                        if (usedAsNormal && IsFlatNormal(px))
                        {
                            plan.FlatNormal = true;
                            plan.AfterBytes = 0;             // 参照ごと外せる
                            plan.Reasons.Add("凹凸の無いノーマルマップ → 参照を外せる");
                            plans.Add(plan);
                            continue;
                        }

                        if (IsSolidColor(px, out _) && IsTextureExactlySolid(t2))
                        {
                            plan.SolidColor = true;
                            plan.AfterBytes = 64;            // 4x4 で足りる
                            plan.Reasons.Add("全面が単色 → 4x4 で足りる");
                            plans.Add(plan);
                            continue;
                        }
                    }
                }

                // --- 解像度 ---
                if (density.TryGetValue(t2, out var rec) && rec < plan.RecommendedSize)
                {
                    plan.Reasons.Add($"解像度が過大 {t2.width}x{t2.height} → {rec}x{rec}");
                    plan.RecommendedSize = rec;
                }

                // --- アルファの要否 ---
                plan.AlphaUnneeded = IsAlphaUnneeded(t2);
                if (plan.AlphaUnneeded)
                    plan.Reasons.Add("元画像にアルファが無いのにアルファ付き形式");

                // --- 最適化後のサイズを 1 回で計算する ---
                double bppRatio = plan.AlphaUnneeded ? 0.5 : 1.0;   // DXT5/BC7 → DXT1 相当
                double sizeRatio =
                    (double)plan.RecommendedSize * plan.RecommendedSize /
                    ((double)t2.width * t2.height);
                plan.AfterBytes = (long)(plan.CurrentBytes * sizeRatio * bppRatio);

                if (plan.Reasons.Count > 0) plans.Add(plan);
            }

            if (plans.Count == 0) return;

            long cur = plans.Sum(p => p.CurrentBytes);
            long after = plans.Sum(p => p.AfterBytes);
            long allCurrent = index.TextureUsage.Keys
                .Select(t => AvatarStats.EstimateTextureBytes(t)).Sum();

            found.Add(new Opportunity
            {
                Category = "テクスチャ最適化（合計）",
                Detail = $"{plans.Count} 枚が対象。" +
                         $"全体 {allCurrent / (1024.0 * 1024.0):F1} MB のうち " +
                         $"{cur / (1024.0 * 1024.0):F1} MB → {after / (1024.0 * 1024.0):F1} MB",
                SavedBytes = cur - after,
                SavedCount = plans.Count,
                Risk = "解像度の判定はテクセル密度からの推定。顔など寄りで見る部位は目視確認が要る",
            });

            // 内訳は「何枚が該当したか」だけを出す。容量は上の合計にのみ計上する
            // （足すと二重計上になるため）。
            Breakdown(found, "└ うち アルファ不要", plans.Count(p => p.AlphaUnneeded), "安全");
            Breakdown(found, "└ うち 解像度が過大",
                plans.Count(p => !p.FlatNormal && !p.SolidColor &&
                                 p.RecommendedSize < Math.Max(p.Tex.width, p.Tex.height)), "要確認");
            Breakdown(found, "└ うち 単色", plans.Count(p => p.SolidColor), "安全");
            Breakdown(found, "└ うち フラットノーマル", plans.Count(p => p.FlatNormal), "安全");
        }

        private static void Breakdown(List<Opportunity> found, string label, int count, string safety)
        {
            if (count <= 0) return;
            found.Add(new Opportunity
            {
                Category = label,
                Detail = $"{count} 枚（容量は「テクスチャ最適化（合計）」に計上済み）",
                SavedCount = count,
                Risk = safety == "安全" ? "" : "テクセル密度からの推定",
            });
        }

        /// <summary>
        /// Renderer からは見えない Expression Menu の Control / Puppet ラベル画像を調べる。
        /// ここでは、単色または元画像にアルファが無い場合だけを「安全」と数える。
        /// 解像度変更は最終表示に影響し得るため、この損失なし枠では行わない。
        /// </summary>
        private static void ScanExpressionMenuIcons(ReferenceIndex index, List<Opportunity> found)
        {
            if (index.ExpressionMenuTextures.Count == 0) return;

            bool canReadPixels = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            int solid = 0, alpha = 0, oversized = 0;
            long currentAll = 0, saved = 0;

            foreach (var icon in index.ExpressionMenuTextures)
            {
                if (icon == null) continue;
                long current = AvatarStats.EstimateTextureBytes(icon);
                currentAll += current;
                if (icon.width > 256 || icon.height > 256) oversized++;

                bool isSolid = false;
                if (canReadPixels && Math.Max(icon.width, icon.height) > 32)
                {
                    try
                    {
                        var pixels = SampleSmall(icon, 16);
                        isSolid = pixels != null && pixels.Length > 0 &&
                                  IsSolidColor(pixels, out _) && IsTextureExactlySolid(icon);
                    }
                    catch (Exception) { }
                }

                if (isSolid)
                {
                    solid++;
                    saved += Math.Max(0, current - 4096);
                }
                else if (IsAlphaUnneeded(icon))
                {
                    alpha++;
                    saved += current / 2;
                }
            }

            found.Add(new Opportunity
            {
                Category = "Expression Menu アイコン",
                Detail = $"{index.ExpressionMenuTextures.Count} 枚 / " +
                         $"{currentAll / (1024.0 * 1024.0):F1} MB。" +
                         $"安全な対象: 単色 {solid}、アルファ不要 {alpha}。" +
                         $"256px超 {oversized} 枚はVRChat SDKがビルド時に縮小する",
                SavedBytes = saved,
                SavedCount = solid + alpha,
                Risk = "",
                Target = index.ExpressionMenuTextures.FirstOrDefault(),
            });
        }

        private static void ScanTextureImportSettings(ReferenceIndex index, List<Opportunity> found)
        {
            var textures = new HashSet<Texture2D>();
            foreach (var tex in index.TextureUsage.Keys)
            {
                if (tex is Texture2D t2) textures.Add(t2);
            }
            textures.UnionWith(index.ExpressionMenuTextures);

            int readable = 0;
            long bytes = 0;
            Texture2D sample = null;
            foreach (var texture in textures)
            {
                var path = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(path)) continue;
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || !importer.isReadable) continue;

                readable++;
                bytes += AvatarStats.EstimateTextureBytes(texture);
                if (sample == null) sample = texture;
            }

            if (readable == 0) return;
            found.Add(new Opportunity
            {
                Category = "Read/Write 有効なテクスチャ",
                Detail = $"{readable} 枚。CPU側コピーを保持している",
                SavedBytes = bytes,
                SavedCount = readable,
                Risk = "ビルド前処理ツールが画素をCPUから読む場合は無効化できない。" +
                       "利用ツールの参照確認後に限る",
                Target = sample,
            });
        }

        private static void ScanStaleMaterialProperties(ReferenceIndex index, List<Opportunity> found)
        {
            int materials = 0;
            int entries = 0;
            Material sample = null;

            foreach (var material in index.MaterialUsage.Keys)
            {
                if (material == null || material.shader == null) continue;
                int stale = CountStaleSavedProperties(material);
                if (stale == 0) continue;
                materials++;
                entries += stale;
                if (sample == null) sample = material;
            }

            if (entries == 0) return;
            found.Add(new Opportunity
            {
                Category = "現在のシェーダーが読まないMaterial設定",
                Detail = $"{materials} マテリアルに {entries} 個。以前のシェーダーの保存値など",
                SavedCount = entries,
                Risk = "現在の表示には使われないが、将来そのシェーダーへ戻すと設定を再利用できなくなる。" +
                       "バックアップ付きの明示選択に限定する",
                Target = sample,
            });
        }

        private static int CountStaleSavedProperties(Material material)
        {
            int count = 0;
            SerializedObject serialized;
            try { serialized = new SerializedObject(material); }
            catch (Exception) { return 0; }

            using (serialized)
            {
                foreach (var path in new[]
                {
                    "m_SavedProperties.m_TexEnvs",
                    "m_SavedProperties.m_Floats",
                    "m_SavedProperties.m_Colors",
                    "m_SavedProperties.m_Ints",
                })
                {
                    var array = serialized.FindProperty(path);
                    if (array == null || !array.isArray) continue;
                    for (int i = 0; i < array.arraySize; i++)
                    {
                        var entry = array.GetArrayElementAtIndex(i);
                        var key = entry.FindPropertyRelative("first");
                        if (key == null || string.IsNullOrEmpty(key.stringValue)) continue;
                        if (!material.HasProperty(key.stringValue)) count++;
                    }
                }
            }
            return count;
        }

        /// <summary>元画像にアルファが無いのに、アルファ付き形式で圧縮されているか。</summary>
        private static bool IsAlphaUnneeded(Texture2D t2)
        {
            switch (t2.format)
            {
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    break;
                default:
                    return false;
            }

            var path = AssetDatabase.GetAssetPath(t2);
            if (string.IsNullOrEmpty(path)) return false;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return false;

            // ノーマルマップは BC5 等の専用形式にすべきなので、ここでは触らない
            if (imp.textureType == TextureImporterType.NormalMap) return false;

            return !imp.DoesSourceTextureHaveAlpha();
        }

        // =================================================================
        // 1-2. テクセル密度から見て解像度が過大なテクスチャ（仕様 G-1 の実測）
        //
        //   密度 = テクスチャの解像度 × sqrt(UV面積 / ワールド面積)
        //        = 「アバター表面 1 メートルあたり何ピクセル使っているか」
        //
        //   目標密度を超えている分は、見た目に出ない解像度を持っていることになる。
        //   2 の冪で丸めて推奨解像度を出す。
        // =================================================================

        /// <summary>
        /// 目標テクセル密度 (px/m)。
        ///
        /// 【この値の決め方で一度失敗している】
        /// 最初 1024 px/m にしたところ、ほぼ全テクスチャが「過大」と判定され、
        /// 削減見込みがテクスチャ総容量を超えるという明らかにおかしな結果になった。
        ///
        /// VRChat は VR で至近距離から見られるため、一般的な 3D ゲームより高い密度が要る。
        /// 実際のアバターは体の表面積 1.5〜2 m² に 2K〜4K を貼っており、
        /// これは 1,400〜2,900 px/m にあたる。
        /// そこで既定を 2048 px/m（＝体に 2K 相当）とし、見た目が変わらない範囲に留める。
        /// もっと削りたい場合はユーザーが下げられるようにする。
        /// </summary>
        public static float TargetTexelDensity = 2048f;

        /// <summary>近くで見られる部位。密度を 2 倍にする。</summary>
        private static readonly string[] CloseUpHints =
            { "face", "eye", "mouth", "head", "hair", "顔", "目", "髪" };

        /// <summary>テクスチャ → 推奨解像度。</summary>
        private static Dictionary<Texture2D, int> ComputeRecommendedSizes(ReferenceIndex index)
        {
            // マテリアル → (UV面積, ワールド面積) を集める
            var area = new Dictionary<Material, (double uv, double world)>();

            foreach (var smr in index.SkinnedMeshes)
            {
                var mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null) continue;

                Vector3[] verts;
                Vector2[] uvs;
                try
                {
                    verts = mesh.vertices;
                    uvs = mesh.uv;
                }
                catch (Exception)
                {
                    continue;
                }
                if (verts == null || uvs == null || uvs.Length != verts.Length) continue;

                var mats = smr.sharedMaterials;
                var scale = smr.transform.lossyScale;
                float scaleFactor = (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;

                int sub = Math.Min(mesh.subMeshCount, mats?.Length ?? 0);
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
                        uvArea += Math.Abs((t1.x - t0.x) * (t2.y - t0.y) - (t2.x - t0.x) * (t1.y - t0.y)) * 0.5;
                        sampled++;
                    }
                    if (sampled == 0 || worldArea <= 0 || uvArea <= 0) continue;

                    // 間引いたぶんを戻す
                    double factor = (double)(tris.Length / 3) / sampled;
                    uvArea *= factor;
                    worldArea *= factor;

                    area.TryGetValue(mat, out var acc);
                    area[mat] = (acc.uv + uvArea, acc.world + worldArea);
                }
            }

            // テクスチャごとに、それを使うマテリアルの密度から推奨解像度を出す
            var perTexture = new Dictionary<Texture2D, int>();

            foreach (var kv in index.TextureUsage)
            {
                var t2 = kv.Key as Texture2D;
                if (t2 == null) continue;
                int size = Math.Max(t2.width, t2.height);
                if (size < 512) continue; // 小さいものは対象外

                int recommend = 0;
                foreach (var (mat, prop) in kv.Value)
                {
                    if (mat == null || !area.TryGetValue(mat, out var a)) continue;
                    if (a.world <= 0 || a.uv <= 0) continue;

                    float target = TargetTexelDensity;
                    var lower = (mat.name + " " + t2.name).ToLowerInvariant();
                    if (CloseUpHints.Any(h => lower.Contains(h))) target *= 2f;

                    // ノーマル / マスク系は本体の半分でよいことが多い
                    if (prop.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0)
                        target *= 0.5f;

                    // 必要解像度 = 目標密度 × sqrt(ワールド面積 / UV面積)
                    double needed = target * Math.Sqrt(a.world / a.uv);
                    int pow2 = NextPow2((int)Math.Ceiling(needed));
                    pow2 = Mathf.Clamp(pow2, 64, 4096);

                    // 同じテクスチャを複数マテリアルが使う場合は、一番大きい要求に合わせる
                    if (pow2 > recommend) recommend = pow2;
                }

                if (recommend > 0 && recommend < size)
                    perTexture[t2] = recommend;
            }

            return perTexture;
        }

        private static int NextPow2(int v)
        {
            int p = 64;
            while (p < v && p < 8192) p <<= 1;
            return p;
        }


        /// <summary>
        /// テクスチャを小さな RenderTexture に描き直して画素を読む。
        /// Read/Write が無効なテクスチャでも読めるのが利点。
        /// </summary>
        private static Color32[] SampleSmall(Texture tex, int size)
        {
            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                tmp.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tmp.Apply(false);
                var px = tmp.GetPixels32();
                UnityEngine.Object.DestroyImmediate(tmp);
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static bool IsSolidColor(Color32[] px, out Color32 color)
        {
            if (px == null || px.Length == 0)
            {
                color = default;
                return false;
            }
            color = px[0];
            foreach (var p in px)
            {
                if (p.r != color.r || p.g != color.g || p.b != color.b || p.a != color.a)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 縮小サンプルは小さな模様を消してしまうため、単色と確定する前に実解像度の全画素を照合する。
        /// 読み戻しに失敗した場合は安全側で対象外にする。
        /// </summary>
        private static bool IsTextureExactlySolid(Texture2D texture)
        {
            var prev = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tmp = null;
            try
            {
                rt = RenderTexture.GetTemporary(texture.width, texture.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                tmp.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                tmp.Apply(false);
                return IsSolidColor(tmp.GetPixels32(), out _);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (tmp != null) UnityEngine.Object.DestroyImmediate(tmp);
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>凹凸の無いノーマルマップ＝全画素が (128,128,255) 付近。</summary>
        private static bool IsFlatNormal(Color32[] px)
        {
            const int tol = 4;
            foreach (var p in px)
            {
                if (Math.Abs(p.r - 128) > tol) return false;
                if (Math.Abs(p.g - 128) > tol) return false;
                if (p.b < 250) return false;
            }
            return true;
        }

        // =================================================================
        // 3. 内容が同じテクスチャが別アセットとして複数ある
        //    衣装アセット同士が同じテクスチャを同梱しているケース。完全に安全。
        // =================================================================
        private static void ScanDuplicateTextures(ReferenceIndex index, List<Opportunity> found)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            var groups = new Dictionary<string, List<Texture2D>>();
            foreach (var tex in index.TextureUsage.Keys)
            {
                var t2 = tex as Texture2D;
                if (t2 == null) continue;

                string sig;
                try
                {
                    var px = SampleSmall(t2, 8);
                    var sb = new StringBuilder();
                    sb.Append(t2.width).Append('x').Append(t2.height).Append(':');
                    foreach (var p in px) sb.Append(p.r).Append(p.g).Append(p.b).Append(p.a).Append(',');
                    sig = sb.ToString();
                }
                catch (Exception)
                {
                    continue;
                }

                if (!groups.TryGetValue(sig, out var list))
                {
                    list = new List<Texture2D>();
                    groups[sig] = list;
                }
                list.Add(t2);
            }

            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                long each = AvatarStats.EstimateTextureBytes(kv.Value[0]);
                found.Add(new Opportunity
                {
                    Category = "重複テクスチャ",
                    Detail = $"{kv.Value.Count} 個が同じ内容: " +
                             string.Join(", ", kv.Value.Select(t => t.name).Take(4)),
                    SavedBytes = each * (kv.Value.Count - 1),
                    SavedCount = kv.Value.Count - 1,
                    Risk = "縮小画像での比較のため、完全一致は要確認",
                    Target = kv.Value[0],
                });
            }
        }

        // =================================================================
        // 4. 完全に同じマテリアルが別アセットとして複数ある
        // =================================================================
        private static void ScanDuplicateMaterials(ReferenceIndex index, List<Opportunity> found)
        {
            var groups = new Dictionary<string, List<Material>>();
            foreach (var mat in index.MaterialUsage.Keys)
            {
                if (mat == null || mat.shader == null) continue;
                var sig = MaterialSignature(mat);
                if (!groups.TryGetValue(sig, out var list))
                {
                    list = new List<Material>();
                    groups[sig] = list;
                }
                list.Add(mat);
            }

            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                found.Add(new Opportunity
                {
                    Category = "重複マテリアル",
                    Detail = $"{kv.Value.Count} 個が完全に同じ設定: " +
                             string.Join(", ", kv.Value.Select(m => m.name).Take(4)),
                    SavedCount = kv.Value.Count - 1,
                    Risk = "",
                    Target = kv.Value[0],
                });
            }
        }

        private static string MaterialSignature(Material mat)
        {
            var sb = new StringBuilder();
            sb.Append(mat.shader.name).Append('|');
            sb.Append(mat.renderQueue).Append('|');
            sb.Append(string.Join(",", mat.shaderKeywords.OrderBy(k => k))).Append('|');

            var shader = mat.shader;
            int n = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < n; i++)
            {
                var name = ShaderUtil.GetPropertyName(shader, i);
                switch (ShaderUtil.GetPropertyType(shader, i))
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        sb.Append(name).Append('=').Append(mat.GetColor(name)).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        sb.Append(name).Append('=').Append(mat.GetVector(name)).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        sb.Append(name).Append('=').Append(mat.GetFloat(name).ToString("F5")).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var t = mat.GetTexture(name);
                        sb.Append(name).Append('=')
                          .Append(t != null ? t.GetInstanceID().ToString() : "null")
                          .Append('@').Append(mat.GetTextureScale(name))
                          .Append('/').Append(mat.GetTextureOffset(name)).Append(';');
                        break;
                }
            }
            return sb.ToString();
        }

        // =================================================================
        // 5. 内容が同じメッシュが別アセットとして複数ある
        // =================================================================
        private static void ScanDuplicateMeshes(ReferenceIndex index, List<Opportunity> found)
        {
            var groups = new Dictionary<string, List<Mesh>>();
            var seen = new HashSet<Mesh>();

            foreach (var smr in index.SkinnedMeshes)
            {
                var m = smr != null ? smr.sharedMesh : null;
                if (m == null || !seen.Add(m)) continue;

                string sig;
                try
                {
                    sig = $"{m.vertexCount}|{m.subMeshCount}|{m.blendShapeCount}|" +
                          $"{m.bounds.center}|{m.bounds.extents}|{m.GetIndexCount(0)}";
                }
                catch (Exception)
                {
                    continue;
                }

                if (!groups.TryGetValue(sig, out var list))
                {
                    list = new List<Mesh>();
                    groups[sig] = list;
                }
                list.Add(m);
            }

            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                found.Add(new Opportunity
                {
                    Category = "重複メッシュの疑い",
                    Detail = $"{kv.Value.Count} 個が同じ形状の可能性: " +
                             string.Join(", ", kv.Value.Select(m => m.name).Take(4)),
                    SavedCount = kv.Value.Count - 1,
                    Risk = "頂点数と境界での比較。統合前に必ず目視で確認すること",
                    Target = kv.Value[0],
                });
            }
        }

        // =================================================================
        // 6. Renderer の設定。見た目を変えずに CPU コストを下げられる
        // =================================================================
        private static void ScanRendererSettings(ReferenceIndex index, List<Opportunity> found)
        {
            int offscreen = 0;
            SkinnedMeshRenderer sample = null;

            foreach (var smr in index.SkinnedMeshes)
            {
                if (smr == null) continue;
                if (smr.updateWhenOffscreen)
                {
                    offscreen++;
                    if (sample == null) sample = smr;
                }
            }

            if (offscreen > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "Update When Offscreen",
                    Detail = $"{offscreen} 個の SkinnedMeshRenderer で有効。" +
                             "毎フレーム境界を再計算するため CPU コストが高い",
                    SavedCount = offscreen,
                    Risk = "無効にする場合は境界(Bounds)を正しく設定しないと、" +
                           "視界の端でメッシュが消えることがある",
                    Target = sample,
                });
            }
        }

        // =================================================================
        // 7. PhysBone の設定。見た目を変えずに CPU コストを下げられる
        // =================================================================
        private static void ScanPhysBoneSettings(ReferenceIndex index, List<Opportunity> found)
        {
            int animated = 0;
            VRCPhysBone sample = null;
            var animPaths = index.Animations.Paths;

            foreach (var pb in index.PhysBones)
            {
                if (pb == null) continue;
                if (!pb.isAnimated) continue;

                // チェーン上のどれかがアニメーションで動かされているか
                bool reallyAnimated = false;
                if (index.PhysBoneChains.TryGetValue(pb, out var chain))
                {
                    foreach (var t in chain)
                    {
                        if (index.PathOf.TryGetValue(t, out var p) && animPaths.Contains(p))
                        {
                            reallyAnimated = true;
                            break;
                        }
                    }
                }

                if (!reallyAnimated)
                {
                    animated++;
                    if (sample == null) sample = pb;
                }
            }

            if (animated > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "PhysBone の Is Animated",
                    Detail = $"{animated} 個で有効だが、チェーン上にアニメーション参照が無い。" +
                             "無効にすると毎フレームの Transform 監視が省ける",
                    SavedCount = animated,
                    Risk = "MA や VRCFury がビルド時にアニメーションを足す場合は、" +
                           "この判定が外れる可能性がある",
                    Target = sample,
                });
            }
        }

        // =================================================================
        // 8. 使われていない BlendShape
        // =================================================================
        private static void ScanBlendShapes(ReferenceIndex index, List<Opportunity> found)
        {
            int totalShapes = 0;
            int unused = 0;
            int zeroDelta = 0;
            long unusedBytes = 0;
            var seen = new HashSet<Mesh>();

            foreach (var smr in index.SkinnedMeshes)
            {
                var mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                var path = index.PathOf.TryGetValue(smr.transform, out var p) ? p : null;
                index.Animations.BlendShapeRefs.TryGetValue(path ?? "", out var referenced);

                // 同じメッシュを複数の Renderer が共有している場合、容量は 1 回だけ数える
                bool firstTimeForMesh = seen.Add(mesh);

                int vc = mesh.vertexCount;
                Vector3[] dp = null, dn = null, dt = null;
                if (firstTimeForMesh && vc > 0 && vc < 200000)
                {
                    dp = new Vector3[vc];
                    dn = new Vector3[vc];
                    dt = new Vector3[vc];
                }

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    totalShapes++;
                    var name = mesh.GetBlendShapeName(i);
                    bool isReferenced = referenced != null && referenced.Contains(name);
                    bool isSet = smr.GetBlendShapeWeight(i) > 0.01f;
                    bool isUnused = !isReferenced && !isSet;

                    if (isUnused) unused++;
                    if (dp == null) continue;

                    // 実際に何頂点が動くかを数えて容量を見積もる。
                    // Unity は疎に持つので「動く頂点数 × フレーム数 × 40 バイト」が目安
                    // （インデックス 4 + 位置 12 + 法線 12 + 接線 12）。
                    int frames = mesh.GetBlendShapeFrameCount(i);
                    long affected = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        try
                        {
                            mesh.GetBlendShapeFrameVertices(i, f, dp, dn, dt);
                        }
                        catch (Exception)
                        {
                            break;
                        }
                        for (int v = 0; v < vc; v++)
                        {
                            if (dp[v].sqrMagnitude > 1e-12f) affected++;
                        }
                    }

                    if (affected == 0) zeroDelta++;
                    if (isUnused) unusedBytes += affected * 40;
                }
            }

            if (unused > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "未使用 BlendShape",
                    Detail = $"{unused} / {totalShapes} 個が、アニメーションから参照されず現在値も 0",
                    SavedBytes = unusedBytes,
                    SavedCount = unused,
                    Risk = "MA / VRCFury がビルド時に足すアニメーションは見えていない点に注意",
                });
            }

            if (zeroDelta > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "変位ゼロの BlendShape",
                    Detail = $"{zeroDelta} 個は動かす頂点が 1 つも無い（実質何も起きない）",
                    SavedCount = zeroDelta,
                    Risk = "",
                });
            }
        }

        // =================================================================
        // 9. シェーダーが読まない頂点属性
        // =================================================================
        private static void ScanVertexAttributes(ReferenceIndex index, List<Opportunity> found)
        {
            int uv2 = 0, uv3 = 0, uv4 = 0, colors = 0;
            var seen = new HashSet<Mesh>();

            foreach (var smr in index.SkinnedMeshes)
            {
                var m = smr != null ? smr.sharedMesh : null;
                if (m == null || !seen.Add(m)) continue;

                if (m.HasVertexAttribute(VertexAttribute.TexCoord1)) uv2++;
                if (m.HasVertexAttribute(VertexAttribute.TexCoord2)) uv3++;
                if (m.HasVertexAttribute(VertexAttribute.TexCoord3)) uv4++;
                if (m.HasVertexAttribute(VertexAttribute.Color)) colors++;
            }

            if (uv2 + uv3 + uv4 + colors > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "追加の頂点属性",
                    Detail = $"UV2:{uv2} UV3:{uv3} UV4:{uv4} 頂点カラー:{colors} 個のメッシュが保持している",
                    SavedCount = uv2 + uv3 + uv4 + colors,
                    Risk = "シェーダーが実際に読んでいるかの判定が必要（シェーダープロファイル待ち）",
                });
            }
        }

        // =================================================================
        // 10. 参照先が存在しないアニメーションパス（既に壊れている参照）
        // =================================================================
        private static void ScanBrokenAnimationPaths(ReferenceIndex index, List<Opportunity> found)
        {
            var existing = new HashSet<string>(index.PathOf.Values);
            var broken = index.Animations.Paths.Where(p => !existing.Contains(p)).ToList();

            if (broken.Count > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "参照先が無いアニメーションパス",
                    Detail = $"{broken.Count} 件。例: " + string.Join(" / ", broken.Take(3)),
                    SavedCount = broken.Count,
                    Risk = "MA / VRCFury がビルド時に作るオブジェクトを指している可能性があるため、" +
                           "解析時点で無いだけかもしれない。削除するなら要確認",
                });
            }
        }

        // =================================================================
        // 11. メッシュのインポート設定
        // =================================================================
        private static void ScanMeshImportSettings(ReferenceIndex index, List<Opportunity> found)
        {
            int readable = 0;
            long readableBytes = 0;
            var seen = new HashSet<Mesh>();
            Mesh sample = null;

            foreach (var smr in index.SkinnedMeshes)
            {
                var m = smr != null ? smr.sharedMesh : null;
                if (m == null || !seen.Add(m)) continue;
                if (!m.isReadable) continue;

                readable++;
                if (sample == null) sample = m;
                // 頂点あたり概算 64 バイト（位置/法線/接線/UV/ウェイト）でメモリ 2 倍ぶん
                readableBytes += (long)m.vertexCount * 64;
            }

            if (readable > 0)
            {
                found.Add(new Opportunity
                {
                    Category = "Read/Write 有効なメッシュ",
                    Detail = $"{readable} 個。CPU 側にもコピーが載るためメモリが 2 倍になる",
                    SavedBytes = readableBytes,
                    SavedCount = readable,
                    Risk = "他のツールが実行時にメッシュを読む場合は無効化できない",
                    Target = sample,
                });
            }
        }

        // =================================================================

        /// <summary>カテゴリごとに集計してテキストにする。</summary>
        public static string Summarize(List<Opportunity> ops)
        {
            var sb = new StringBuilder();
            var byCat = ops.GroupBy(o => o.Category)
                           .OrderByDescending(g => g.Sum(o => o.SavedBytes))
                           .ThenByDescending(g => g.Sum(o => o.SavedCount));

            foreach (var g in byCat)
            {
                long bytes = g.Sum(o => o.SavedBytes);
                int count = g.Sum(o => o.SavedCount);
                var parts = new List<string>();
                if (bytes > 0) parts.Add($"{bytes / (1024.0 * 1024.0):F1} MB");
                if (count > 0) parts.Add($"{count} 個");
                var risky = g.Any(o => !string.IsNullOrEmpty(o.Risk));

                sb.AppendLine($"    {g.Key,-28} {string.Join(" / ", parts),-18} " +
                              $"{(risky ? "[要確認]" : "[安全]")}  ({g.Count()} 件)");
            }
            return sb.ToString();
        }
    }
}
