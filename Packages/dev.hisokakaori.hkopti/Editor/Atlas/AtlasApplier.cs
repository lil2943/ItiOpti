using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    public sealed class AtlasOptions
    {
        public AtlasLevel Level = AtlasLevel.Conservative;
        public int MaxAtlasSize = 4096;
        public int PaddingPixels = 8;

        /// <summary>
        /// このマテリアル数に満たないグループは飛ばす。既定は 1（＝1 個でも処理する）。
        ///
        /// 【なぜ 1 なのか】
        /// 実測では、削減の大半は「複数のマテリアルをまとめること」ではなく
        /// **1 枚のテクスチャの中の余白を詰めること**から来ている。
        /// UV 占有率が 22〜62% しかないので、1 個だけのグループでも
        /// 2048px が 1024px に落ちる。実際、統合できる組は少なく
        /// （Chocolat_listening (1) は 22 グループ中 20 個が 1 個だけ）、
        /// ここを 2 にすると削減のほとんどを取り逃がす。
        /// </summary>
        public int MinimumGroupSize = 1;

        /// <summary>アトラス化しないマテリアル。壊れたものを個別に外すのに使う。</summary>
        public HashSet<Material> ExcludeMaterials = new HashSet<Material>();
    }

    public sealed class AtlasGroupResult
    {
        public string Key;
        public List<Material> Sources = new List<Material>();
        public Material Merged;
        public AtlasLayout Layout;
        public readonly Dictionary<string, Texture2D> BakedTextures = new Dictionary<string, Texture2D>();
        public readonly List<string> Warnings = new List<string>();
    }

    public sealed class AtlasApplyResult
    {
        public readonly List<AtlasGroupResult> Groups = new List<AtlasGroupResult>();
        public readonly Dictionary<Mesh, Mesh> RewrittenMeshes = new Dictionary<Mesh, Mesh>();

        /// <summary>submesh をまとめて新しく作ったメッシュ。ビルド成果物として保存が要る。</summary>
        public readonly List<Mesh> GeneratedMeshes = new List<Mesh>();
        public readonly List<string> Warnings = new List<string>();

        public int SlotsBefore;
        public int SlotsAfter;
        public long PixelsBefore;
        public long PixelsAfter;
    }

    /// <summary>
    /// アトラス化の全体の流れをまとめる。
    ///
    ///   1. まとめられるマテリアルをグループに分ける（AtlasGroupKey）
    ///   2. グループごとに島を詰めて配置を決める（AtlasLayoutBuilder）
    ///   3. 配置に従ってテクスチャを焼く（AtlasBaker）
    ///   4. 配置に従ってメッシュの UV を書き換える（MeshUvRewriter）
    ///   5. 統合後のマテリアルを作って、Renderer に割り当て直す
    ///
    /// **元のアセットは変更しない。** 複製に対して行う前提（NDMF のビルド中に呼ぶ）。
    /// </summary>
    public static class AtlasApplier
    {
        public static AtlasApplyResult Apply(IEnumerable<Renderer> renderers, AtlasOptions options)
        {
            options = options ?? new AtlasOptions();
            var result = new AtlasApplyResult();

            var targets = (renderers ?? Enumerable.Empty<Renderer>())
                .Where(r => r != null && MeshOf(r) != null)
                .ToList();
            if (targets.Count == 0) return result;

            // submesh より多いマテリアルスロットを持つ Renderer は、余りのスロットで
            // 「最後の submesh をもう一度、別のマテリアルで描く」多重描画をしている
            // （輪郭線の手法など。実測で 1 マテリアル 17 スロットのアバターがあった）。
            // このとき同じ UV を複数のマテリアルが共有しているので、
            // アトラス化して UV を書き換えると必ず壊れる。そのマテリアルは全部除外する。
            var unsafeMaterials = new HashSet<Material>();
            foreach (var r in targets)
            {
                var mesh = MeshOf(r);
                var mats = r.sharedMaterials;
                if (mesh == null || mats == null || mats.Length <= mesh.subMeshCount) continue;
                foreach (var m in mats) if (m != null) unsafeMaterials.Add(m);
                result.Warnings.Add(
                    $"{r.name} はスロット {mats.Length} > submesh {mesh.subMeshCount} の" +
                    "多重描画なので、そのマテリアルはアトラス化しない");
            }

            // 同じメッシュを複数の Renderer が使い、同じ submesh に別のマテリアルを
            // 割り当てている場合（色違いなどでよくある）、UV の行き先が 1 つに定まらない。
            // メッシュは 1 つなのに、マテリアルごとに別のアトラス配置を要求するため。
            // 片方に合わせて書き換えると、もう片方が必ず壊れる。→ どちらも対象外にする。
            var bySubMesh = new Dictionary<(Mesh, int), HashSet<Material>>();
            foreach (var r in targets)
            {
                var mesh = MeshOf(r);
                var mats = r.sharedMaterials;
                if (mesh == null || mats == null) continue;
                for (int s = 0; s < mats.Length && s < mesh.subMeshCount; s++)
                {
                    if (mats[s] == null) continue;
                    if (!bySubMesh.TryGetValue((mesh, s), out var set))
                    {
                        set = new HashSet<Material>();
                        bySubMesh[(mesh, s)] = set;
                    }
                    set.Add(mats[s]);
                }
            }
            foreach (var kv in bySubMesh)
            {
                if (kv.Value.Count < 2) continue;
                foreach (var m in kv.Value) unsafeMaterials.Add(m);
                result.Warnings.Add(
                    $"{kv.Key.Item1.name}[{kv.Key.Item2}] を複数の Renderer が" +
                    $"別のマテリアル {kv.Value.Count} 種類で使っているのでアトラス化しない");
            }

            // 1) グループ分け
            var allMaterials = new List<Material>();
            var allMaterialSet = new HashSet<Material>();
            var sourcesByMaterial = new Dictionary<Material, List<AtlasSource>>();
            var allSources = new List<AtlasSource>();
            foreach (var r in targets)
            {
                var mesh = MeshOf(r);
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                result.SlotsBefore += mats.Length;
                for (int s = 0; s < mats.Length; s++)
                {
                    var m = mats[s];
                    if (m == null) continue;
                    if (unsafeMaterials.Contains(m)) continue;
                    if (options.ExcludeMaterials != null && options.ExcludeMaterials.Contains(m)) continue;

                    if (allMaterialSet.Add(m)) allMaterials.Add(m);
                    if (mesh == null || s >= mesh.subMeshCount) continue;

                    var source = new AtlasSource
                    { Mesh = mesh, SubMeshIndex = s, Material = m };
                    allSources.Add(source);
                    if (!sourcesByMaterial.TryGetValue(m, out var materialSources))
                    {
                        materialSources = new List<AtlasSource>();
                        sourcesByMaterial[m] = materialSources;
                    }
                    materialSources.Add(source);
                }
            }

            // タイリング（UV が 0-1 の外）のマテリアルは、グループを作る前に外す。
            // レイアウトを組む段階で気づいて丸ごと捨てると、
            // 同じアトラスに入るはずだった他のマテリアルまで巻き添えになる。
            if (!AtlasGroupKey.AllowsTiling(options.Level))
            {
                var tiled = AtlasLayoutBuilder.FindTiledMaterials(allSources);
                if (tiled.Count > 0)
                {
                    allMaterials.RemoveAll(m => tiled.Contains(m));
                    result.Warnings.Add(
                        $"タイリングを使う {tiled.Count} 個のマテリアルはアトラス化しない");
                }
            }

            var groups = AtlasGroupKey.Group(allMaterials, options.Level);
            var mergedFor = new Dictionary<Material, Material>();

            // どのテクスチャを、どのマテリアルが使っているか（アバター全体）。
            // アトラス化しないマテリアルも含めて数える。
            // 他でも使われているテクスチャは、アトラスを作っても消えないため。
            var textureOwners = BuildTextureOwners(targets);

            foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Value.Count < options.MinimumGroupSize) continue;

                var g = new AtlasGroupResult { Key = kv.Key, Sources = kv.Value };

                // 2) 配置を決める
                var sources = new List<AtlasSource>();
                foreach (var material in kv.Value)
                {
                    if (material == null ||
                        !sourcesByMaterial.TryGetValue(material, out var materialSources)) continue;
                    sources.AddRange(materialSources);
                }

                var layout = AtlasLayoutBuilder.Build(sources, new AtlasLayoutOptions
                {
                    PaddingPixels = options.PaddingPixels,
                    MaxAtlasSize = options.MaxAtlasSize,
                    IncludeTiled = AtlasGroupKey.AllowsTiling(options.Level),
                });
                g.Warnings.AddRange(layout.Warnings);

                if (layout.IsEmpty)
                {
                    result.Groups.Add(g);
                    continue;
                }
                // 焼く前に、本当に減るのかを見積もる。
                // 減らないなら UV も書き換えない（＝このグループには何もしない）。
                var groupMats = new HashSet<Material>(kv.Value);
                AtlasBaker.EstimatePixels(layout,
                    t => IsExclusiveTo(t, groupMats, textureOwners),
                    out long removable, out long added);
                if (added >= removable)
                {
                    g.Warnings.Add(
                        $"消えるのは {removable:N0} px なのに {added:N0} px 増えるので何もしない");
                    result.Groups.Add(g);
                    continue;
                }

                g.Layout = layout;

                // 3) テクスチャを焼く
                foreach (var prop in AtlasBaker.CollectTextureProperties(layout))
                {
                    var baked = AtlasBaker.Bake(layout, prop, MaterialFallbackColor);
                    if (baked != null) g.BakedTextures[prop] = baked;
                }

                // 5) 統合後のマテリアル
                g.Merged = BuildMergedMaterial(kv.Value, g.BakedTextures, options.Level);
                foreach (var m in kv.Value) mergedFor[m] = g.Merged;

                result.Groups.Add(g);
            }

            // 4) メッシュの UV を書き換える
            var placementsByMesh = new Dictionary<Mesh, List<IslandPlacement>>();
            foreach (var g in result.Groups)
            {
                if (g.Layout == null) continue;
                foreach (var p in g.Layout.Placements)
                {
                    if (!placementsByMesh.TryGetValue(p.Mesh, out var list))
                    {
                        list = new List<IslandPlacement>();
                        placementsByMesh[p.Mesh] = list;
                    }
                    list.Add(p);
                }
            }

            foreach (var kv in placementsByMesh)
            {
                var rewritten = MeshUvRewriter.Rewrite(kv.Key, kv.Value);
                if (rewritten != null) result.RewrittenMeshes[kv.Key] = rewritten;
                else result.Warnings.Add($"{kv.Key.name} の UV を書き換えられなかった");
            }

            // 5) Renderer に割り当て直す
            foreach (var r in targets)
            {
                var mesh = MeshOf(r);
                if (mesh != null && result.RewrittenMeshes.TryGetValue(mesh, out var newMesh))
                    SetMesh(r, newMesh);

                var mats = r.sharedMaterials;
                if (mats == null) continue;
                var replaced = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                    replaced[i] = mats[i] != null && mergedFor.TryGetValue(mats[i], out var merged)
                        ? merged : mats[i];
                r.sharedMaterials = replaced;
            }

            // 同じマテリアルになった submesh をまとめる。
            // ここまでやらないとマテリアルスロットの数は減らない。
            var mergedMeshCache = new Dictionary<string, Mesh>();
            foreach (var r in targets)
                SubMeshMerger.Merge(r, mergedMeshCache, result.GeneratedMeshes);

            foreach (var r in targets)
            {
                var mats = r.sharedMaterials;
                result.SlotsAfter += mats?.Length ?? 0;
            }

            foreach (var g in result.Groups)
            {
                if (g.Layout == null) continue;
                foreach (var m in g.Sources)
                    result.PixelsBefore += TexturePixels(m);
                foreach (var t in g.BakedTextures.Values)
                    result.PixelsAfter += (long)t.width * t.height;
            }

            return result;
        }

        /// <summary>
        /// 統合後のマテリアルを作る。1 個目を土台にして、焼いたテクスチャを刺す。
        /// タイリング/オフセットは焼き込み済みなので初期値に戻す。
        /// </summary>
        private static Material BuildMergedMaterial(
            List<Material> sources, Dictionary<string, Texture2D> baked, AtlasLevel level)
        {
            var basis = sources.FirstOrDefault(m => m != null);
            if (basis == null) return null;

            var merged = new Material(basis) { name = basis.name + " (ItiOptimiser Atlas)" };

            foreach (var kv in baked)
            {
                if (!merged.HasProperty(kv.Key)) continue;
                merged.SetTexture(kv.Key, kv.Value);
                merged.SetTextureScale(kv.Key, Vector2.one);
                merged.SetTextureOffset(kv.Key, Vector2.zero);
            }

            if (AtlasGroupKey.AbsorbsVariants(level))
            {
                // 閾値がばらばらのものを 1 つにまとめたので、
                // いちばん緩い値に寄せる。厳しい方に寄せると本来見えるところが消える。
                if (merged.HasProperty("_Cutoff"))
                {
                    float min = sources.Where(m => m != null && m.HasProperty("_Cutoff"))
                        .Select(m => m.GetFloat("_Cutoff")).DefaultIfEmpty(0.5f).Min();
                    merged.SetFloat("_Cutoff", min);
                }
                if (merged.HasProperty("_OutlineWidth"))
                {
                    float avg = sources.Where(m => m != null && m.HasProperty("_OutlineWidth"))
                        .Select(m => m.GetFloat("_OutlineWidth")).DefaultIfEmpty(0f).Average();
                    merged.SetFloat("_OutlineWidth", avg);
                }
            }

            return merged;
        }

        /// <summary>テクスチャを持たないマテリアルの領域に塗る色。</summary>
        private static Color MaterialFallbackColor(Material m)
        {
            if (m == null) return Color.white;
            foreach (var p in new[] { "_Color", "_BaseColor", "_MainColor" })
                if (m.HasProperty(p)) return m.GetColor(p);
            return Color.white;
        }

        /// <summary>テクスチャ → それを使っているマテリアル一覧（アバター全体）。</summary>
        private static Dictionary<Texture2D, HashSet<Material>> BuildTextureOwners(
            List<Renderer> targets)
        {
            var owners = new Dictionary<Texture2D, HashSet<Material>>();
            var seenMaterials = new HashSet<Material>();

            foreach (var r in targets)
            {
                foreach (var m in r.sharedMaterials ?? Array.Empty<Material>())
                {
                    if (m == null || m.shader == null || !seenMaterials.Add(m)) continue;
                    int count = UnityEditor.ShaderUtil.GetPropertyCount(m.shader);
                    for (int i = 0; i < count; i++)
                    {
                        if (UnityEditor.ShaderUtil.GetPropertyType(m.shader, i)
                            != UnityEditor.ShaderUtil.ShaderPropertyType.TexEnv) continue;
                        var name = UnityEditor.ShaderUtil.GetPropertyName(m.shader, i);
                        if (!(m.GetTexture(name) is Texture2D t)) continue;
                        if (!owners.TryGetValue(t, out var set))
                        {
                            set = new HashSet<Material>();
                            owners[t] = set;
                        }
                        set.Add(m);
                    }
                }
            }
            return owners;
        }

        /// <summary>そのテクスチャが、このグループの中でしか使われていないか。</summary>
        private static bool IsExclusiveTo(Texture2D t, HashSet<Material> group,
            Dictionary<Texture2D, HashSet<Material>> owners)
        {
            if (t == null) return false;
            if (!owners.TryGetValue(t, out var users)) return true;
            foreach (var m in users) if (!group.Contains(m)) return false;
            return true;
        }

        private static long TexturePixels(Material m)
        {
            if (m == null || m.shader == null) return 0;
            long sum = 0;
            var seen = new HashSet<Texture>();
            int count = UnityEditor.ShaderUtil.GetPropertyCount(m.shader);
            for (int i = 0; i < count; i++)
            {
                if (UnityEditor.ShaderUtil.GetPropertyType(m.shader, i)
                    != UnityEditor.ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var t = m.GetTexture(UnityEditor.ShaderUtil.GetPropertyName(m.shader, i));
                if (t == null || !seen.Add(t)) continue;
                sum += (long)t.width * t.height;
            }
            return sum;
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
