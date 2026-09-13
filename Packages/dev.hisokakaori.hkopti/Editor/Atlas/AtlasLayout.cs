using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>アトラス化の対象になる「このメッシュのこの submesh は、このマテリアル」という 1 件。</summary>
    public struct AtlasSource
    {
        public Mesh Mesh;
        public int SubMeshIndex;
        public Material Material;
    }

    /// <summary>1 個の島が、アトラスのどこに、どの大きさで入るか。</summary>
    public sealed class IslandPlacement
    {
        public Mesh Mesh;
        public int SubMeshIndex;
        public Material Material;
        public UvIsland Island;

        /// <summary>元の UV 空間での位置と大きさ。</summary>
        public Rect Source;

        /// <summary>アトラスの UV 空間（0-1）での位置と大きさ。</summary>
        public Rect Target;

        /// <summary>元 UV をアトラス UV に移す変換。uvNew = uvOld * Scale + Offset</summary>
        public Vector2 Scale => new Vector2(
            Source.width > 0 ? Target.width / Source.width : 0f,
            Source.height > 0 ? Target.height / Source.height : 0f);

        public Vector2 Offset
        {
            get
            {
                var s = Scale;
                return new Vector2(Target.x - Source.x * s.x, Target.y - Source.y * s.y);
            }
        }
    }

    public sealed class AtlasLayout
    {
        public int AtlasSize;
        public readonly List<IslandPlacement> Placements = new List<IslandPlacement>();
        public readonly List<Material> Materials = new List<Material>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>アトラスのうち実際に使われた面積の割合。</summary>
        public float Occupancy;

        /// <summary>島を縮めた倍率。1 未満なら解像度が落ちている。</summary>
        public float AppliedScale = 1f;

        public bool IsEmpty => Placements.Count == 0;
    }

    public sealed class AtlasLayoutOptions
    {
        /// <summary>島どうしの隙間（ピクセル）。狭すぎると縮小時に隣の色がにじむ。</summary>
        public int PaddingPixels = 4;

        /// <summary>アトラス 1 枚の最大サイズ。</summary>
        public int MaxAtlasSize = 4096;

        /// <summary>島の大きさを一律に掛ける倍率。1 より小さくすると解像度が落ちる代わりに詰まる。</summary>
        public float ScaleMultiplier = 1f;

        /// <summary>タイリング（UV が 0-1 の外）の島を対象にするか。</summary>
        public bool IncludeTiled = false;
    }

    public static class AtlasLayoutBuilder
    {
        /// <summary>
        /// まとめる 1 グループ分の配置を決める。
        ///
        /// 島の大きさは「元テクスチャ上で何ピクセルを占めていたか」で決める。
        /// こうすると、細部が細かいところは大きく、のっぺりしたところは小さく入り、
        /// 見た目の解像度が保たれる。
        /// </summary>
        public static AtlasLayout Build(IEnumerable<AtlasSource> sources, AtlasLayoutOptions options)
        {
            options = options ?? new AtlasLayoutOptions();
            var layout = new AtlasLayout();

            // 同じ (メッシュ, submesh) を 2 回処理しない。
            // 服を複数着ると同じメッシュが別の Renderer から参照されることがある。
            var seen = new HashSet<(Mesh, int)>();
            var candidates = new List<IslandPlacement>();
            var pixelSizes = new List<Vector2Int>();

            // 途中で読めなかったマテリアルを覚えておく。
            //
            // 【なぜ必要か】
            // 1 つのマテリアルが複数の submesh に使われているとき、
            // 片方だけ処理に失敗して飛ばすと、**そのマテリアルはアトラスを指すのに
            // 飛ばした側の UV は元のまま**になり、その部分だけ別の絵が出る。
            // 失敗したら、そのマテリアルは丸ごとアトラス化しない。
            var failed = new HashSet<Material>();

            foreach (var src in sources ?? Enumerable.Empty<AtlasSource>())
            {
                if (src.Mesh == null || src.Material == null) continue;
                if (src.SubMeshIndex < 0 || src.SubMeshIndex >= src.Mesh.subMeshCount) continue;
                if (!seen.Add((src.Mesh, src.SubMeshIndex))) continue;

                var uv = src.Mesh.uv;
                if (uv == null || uv.Length == 0)
                {
                    layout.Warnings.Add(
                        $"{src.Mesh.name} が UV を持たないので {src.Material.name} はアトラス化しない");
                    failed.Add(src.Material);
                    continue;
                }

                int[] tris;
                try { tris = src.Mesh.GetTriangles(src.SubMeshIndex); }
                catch (Exception e)
                {
                    layout.Warnings.Add(
                        $"{src.Mesh.name}[{src.SubMeshIndex}] の三角形を取得できないので " +
                        $"{src.Material.name} はアトラス化しない: {e.Message}");
                    failed.Add(src.Material);
                    continue;
                }

                int texSize = MainTextureSize(src.Material);
                var islands = UvIslandFinder.Find(tris, uv);

                foreach (var island in islands)
                {
                    if (UvIslandFinder.IsTiled(island) && !options.IncludeTiled)
                    {
                        // 一部の島だけ抜くとそのマテリアルの見た目が破綻するので、
                        // グループごとアトラス化しない。
                        layout.Warnings.Add(
                            $"{src.Material.name} にタイリングの島があるのでこのグループは対象外");
                        layout.Placements.Clear();
                        layout.Materials.Clear();
                        return layout;
                    }

                    var srcRect = new Rect(island.Min.x, island.Min.y,
                        Mathf.Max(island.Size.x, 0f), Mathf.Max(island.Size.y, 0f));

                    int w = Mathf.CeilToInt(srcRect.width * texSize * options.ScaleMultiplier);
                    int h = Mathf.CeilToInt(srcRect.height * texSize * options.ScaleMultiplier);
                    w = Mathf.Clamp(w, 1, options.MaxAtlasSize);
                    h = Mathf.Clamp(h, 1, options.MaxAtlasSize);

                    candidates.Add(new IslandPlacement
                    {
                        Mesh = src.Mesh,
                        SubMeshIndex = src.SubMeshIndex,
                        Material = src.Material,
                        Island = island,
                        Source = srcRect,
                    });
                    pixelSizes.Add(new Vector2Int(w, h));
                }

                if (!layout.Materials.Contains(src.Material)) layout.Materials.Add(src.Material);
            }

            // 一部でも読めなかったマテリアルは、その配置ごと捨てる。
            if (failed.Count > 0)
            {
                for (int i = candidates.Count - 1; i >= 0; i--)
                {
                    if (!failed.Contains(candidates[i].Material)) continue;
                    candidates.RemoveAt(i);
                    pixelSizes.RemoveAt(i);
                }
                layout.Materials.RemoveAll(m => failed.Contains(m));
            }

            if (candidates.Count == 0) return layout;

            // 詰める。入りきらなければ全体を縮めて再挑戦する。
            float scale = 1f;
            PackResult packed = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var reqs = new List<PackRequest>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    reqs.Add(new PackRequest
                    {
                        Id = i,
                        Width = Mathf.Max(1, Mathf.CeilToInt(pixelSizes[i].x * scale)) + options.PaddingPixels,
                        Height = Mathf.Max(1, Mathf.CeilToInt(pixelSizes[i].y * scale)) + options.PaddingPixels,
                    });
                }

                packed = IslandPacker.PackToSmallestSquare(reqs, 32, options.MaxAtlasSize);
                if (packed.Success) break;

                scale *= 0.75f;
                layout.Warnings.Add(
                    $"{options.MaxAtlasSize}px に入りきらないので島を {scale:P0} に縮めて再挑戦");
            }

            if (packed == null || !packed.Success)
            {
                layout.Warnings.Add("縮めても入りきらなかったのでアトラス化しない");
                layout.Placements.Clear();
                layout.Materials.Clear();
                return layout;
            }

            layout.AtlasSize = packed.AtlasWidth;
            layout.Occupancy = packed.Occupancy;
            layout.AppliedScale = scale * options.ScaleMultiplier;

            float inv = 1f / packed.AtlasWidth;
            int half = options.PaddingPixels / 2;
            foreach (var p in packed.Placements)
            {
                if (p.Id < 0 || p.Id >= candidates.Count) continue;
                var c = candidates[p.Id];

                // 隙間は島の周りに均等に取る。中身は隙間を除いた範囲に入る。
                int x = p.X + half;
                int y = p.Y + half;
                int w = Mathf.Max(1, p.Width - options.PaddingPixels);
                int h = Mathf.Max(1, p.Height - options.PaddingPixels);

                c.Target = new Rect(x * inv, y * inv, w * inv, h * inv);
                layout.Placements.Add(c);
            }

            return layout;
        }

        /// <summary>
        /// UV が 0-1 の外に出ている島を持つマテリアルを洗い出す。
        ///
        /// 【なぜ先に洗い出すか】
        /// タイリングの島が 1 つでもあると、そのマテリアルはアトラス化できない。
        /// レイアウトを組む段階で気づいて丸ごと捨てると、
        /// **同じアトラスに入るはずだった他のマテリアルまで巻き添えで捨てる。**
        /// 実測（Zome_igo）では 9 個の塊のうち 4 個がこれで消えた。
        /// 先に外しておけば、残りのマテリアルだけで塊を作り直せる。
        /// </summary>
        public static HashSet<Material> FindTiledMaterials(IEnumerable<AtlasSource> sources)
        {
            var tiled = new HashSet<Material>();
            var seen = new HashSet<(Mesh, int)>();

            foreach (var src in sources ?? Enumerable.Empty<AtlasSource>())
            {
                if (src.Mesh == null || src.Material == null) continue;
                if (src.SubMeshIndex < 0 || src.SubMeshIndex >= src.Mesh.subMeshCount) continue;
                if (tiled.Contains(src.Material)) continue;
                if (!seen.Add((src.Mesh, src.SubMeshIndex))) continue;

                var uv = src.Mesh.uv;
                if (uv == null || uv.Length == 0) continue;

                int[] tris;
                try { tris = src.Mesh.GetTriangles(src.SubMeshIndex); }
                catch (Exception) { continue; }

                foreach (var island in UvIslandFinder.Find(tris, uv))
                {
                    if (!UvIslandFinder.IsTiled(island)) continue;
                    tiled.Add(src.Material);
                    break;
                }
            }
            return tiled;
        }

        /// <summary>そのマテリアルの主テクスチャの大きさ。無ければ控えめな既定値。</summary>
        private static int MainTextureSize(Material m)
        {
            foreach (var prop in new[] { "_MainTex", "_BaseMap", "_BaseColorMap" })
            {
                if (!m.HasProperty(prop)) continue;
                var t = m.GetTexture(prop);
                if (t != null) return Mathf.Max(t.width, t.height);
            }
            return 256;
        }
    }
}
