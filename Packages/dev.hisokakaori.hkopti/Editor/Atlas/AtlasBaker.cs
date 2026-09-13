using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>
    /// 決めた配置に従って、実際に 1 枚のテクスチャへ島を描き込む。
    ///
    /// テクスチャのプロパティ（_MainTex, _BumpMap, _EmissionMap ...）ごとに
    /// **同じ配置で** 1 枚ずつ作る。配置がずれると、色と法線がずれて陰影が壊れる。
    /// </summary>
    public static class AtlasBaker
    {
        /// <summary>島の周りに色をにじませる幅（ピクセル）。縮小時に隣の島の色が混ざるのを防ぐ。</summary>
        public const int DilatePixels = 4;

        /// <summary>
        /// 1 つのテクスチャプロパティについてアトラスを焼く。
        /// そのプロパティにテクスチャを持つマテリアルが 1 つも無ければ null。
        /// </summary>
        /// <param name="propertyName">_MainTex など</param>
        /// <param name="fallback">テクスチャを持たないマテリアルの領域を塗る色（_Color などを使う）</param>
        public static Texture2D Bake(
            AtlasLayout layout, string propertyName,
            Func<Material, Color> fallback = null)
        {
            if (layout == null || layout.IsEmpty || layout.AtlasSize <= 0) return null;

            var sources = new Dictionary<Material, Texture2D>();
            bool anyTexture = false;
            foreach (var m in layout.Materials)
            {
                Texture2D t = null;
                if (m != null && m.HasProperty(propertyName))
                    t = m.GetTexture(propertyName) as Texture2D;
                sources[m] = t;
                if (t != null) anyTexture = true;
            }
            if (!anyTexture) return null;

            bool linear = sources.Values.Any(t => t != null && !IsSrgb(t));
            var rw = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;

            int size = SizeForProperty(layout, sources);
            float scale = (float)size / layout.AtlasSize;

            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, rw);

            var prevActive = RenderTexture.active;
            var prevFilters = new Dictionary<Texture2D, FilterMode>();

            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, DefaultBackground(propertyName));

                GL.PushMatrix();
                // GUI と同じ「左上原点・下向き y」の座標系にする。
                GL.LoadPixelMatrix(0, size, size, 0);

                foreach (var p in layout.Placements)
                {
                    if (p.Material == null) continue;
                    sources.TryGetValue(p.Material, out var tex);

                    var dest = ToPixelRect(p.Target, size);

                    if (tex == null)
                    {
                        var c = fallback != null ? fallback(p.Material) : DefaultBackground(propertyName);
                        DrawSolid(dest, c);
                        continue;
                    }

                    if (!prevFilters.ContainsKey(tex))
                    {
                        prevFilters[tex] = tex.filterMode;
                        tex.filterMode = FilterMode.Bilinear;
                    }

                    // まず外側にずらして描いて、島の縁の色を隙間に広げる（にじみ止め）。
                    int dilate = Mathf.Max(1, Mathf.RoundToInt(DilatePixels * scale));
                    for (int dx = -dilate; dx <= dilate; dx += dilate)
                    for (int dy = -dilate; dy <= dilate; dy += dilate)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var spread = new Rect(dest.x + dx, dest.y + dy, dest.width, dest.height);
                        Graphics.DrawTexture(spread, tex, p.Source, 0, 0, 0, 0);
                    }
                    // 本体を上から描く
                    Graphics.DrawTexture(dest, tex, p.Source, 0, 0, 0, 0);
                }

                GL.PopMatrix();

                var result = new Texture2D(size, size, TextureFormat.RGBA32, true, linear);
                result.name = $"ItiOptimiser_Atlas_{propertyName.TrimStart('_')}";
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                result.Apply(true, false);

                Compress(result, NeedsAlpha(propertyName, sources));
                return result;
            }
            finally
            {
                foreach (var kv in prevFilters) if (kv.Key != null) kv.Key.filterMode = kv.Value;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// このグループをアトラス化したら、テクスチャの総ピクセル数がどうなるかを先に見積もる。
        ///
        /// 【なぜ必要か】
        /// 実測で **アトラス化してかえって 3.3 倍太った**（118M → 390M px）。
        /// lilToon はテクスチャプロパティを何十個も持つので、
        /// グループが 20 個に分かれると、めったに使わないマスクまで 20 セット複製される。
        /// 元は 1 枚を全マテリアルで共有していたものが、グループごとに 1 枚ずつになるため。
        ///
        /// → **焼く前に見積もって、減らないグループはアトラス化しない。**
        /// </summary>
        /// <param name="isExclusive">
        /// そのテクスチャが「このグループの中でしか使われていない」かを返す関数。
        /// 他のグループでも使われているテクスチャは、アトラス化しても**消えない**。
        /// 消えないものを削減量に数えると、必ず「実際より良い」見積もりになる。
        /// </param>
        /// <param name="removable">アトラス化によって本当に消えるピクセル数</param>
        /// <param name="added">アトラス化によって新しく増えるピクセル数</param>
        public static void EstimatePixels(AtlasLayout layout,
            Func<Texture2D, bool> isExclusive, out long removable, out long added)
        {
            removable = 0;
            added = 0;
            if (layout == null || layout.IsEmpty) return;

            var counted = new HashSet<Texture2D>();
            foreach (var prop in CollectTextureProperties(layout))
            {
                var sources = new Dictionary<Material, Texture2D>();
                foreach (var m in layout.Materials)
                {
                    Texture2D t = null;
                    if (m != null && m.HasProperty(prop)) t = m.GetTexture(prop) as Texture2D;
                    sources[m] = t;
                    if (t == null || !counted.Add(t)) continue;
                    if (isExclusive == null || isExclusive(t)) removable += (long)t.width * t.height;
                }
                if (!sources.Values.Any(t => t != null)) continue;

                int size = SizeForProperty(layout, sources);
                added += (long)size * size;
            }
        }

        /// <summary>
        /// そのプロパティのアトラスを何ピクセルで作るか。
        ///
        /// 【なぜ主テクスチャと同じ大きさにしないか】
        /// lilToon はテクスチャプロパティを何十個も持つ。
        /// 128px のマスク 1 枚のために 2048px のアトラスを作ると、
        /// **アトラス化した結果かえって太る。**
        /// 元テクスチャの一番大きいものに合わせて縮める。
        /// </summary>
        private static int SizeForProperty(AtlasLayout layout, Dictionary<Material, Texture2D> sources)
        {
            int maxSource = 0;
            foreach (var t in sources.Values)
                if (t != null) maxSource = Mathf.Max(maxSource, Mathf.Max(t.width, t.height));
            if (maxSource <= 0) return Mathf.Min(layout.AtlasSize, 256);

            // 主テクスチャに対する比率で縮める。主テクスチャより大きくはしない。
            int mainMax = 0;
            foreach (var m in layout.Materials)
            {
                if (m == null) continue;
                foreach (var prop in new[] { "_MainTex", "_BaseMap", "_BaseColorMap" })
                {
                    if (!m.HasProperty(prop)) continue;
                    var t = m.GetTexture(prop);
                    if (t != null) mainMax = Mathf.Max(mainMax, Mathf.Max(t.width, t.height));
                }
            }
            if (mainMax <= 0) mainMax = maxSource;

            float ratio = Mathf.Clamp01(maxSource / (float)mainMax);
            int target = Mathf.RoundToInt(layout.AtlasSize * ratio);

            int size = 32;
            while (size * 2 <= target && size < layout.AtlasSize) size *= 2;
            return Mathf.Clamp(size, 32, layout.AtlasSize);
        }

        /// <summary>
        /// 焼き上がったアトラスを圧縮する。
        ///
        /// 【なぜ必須か】
        /// 焼いた直後は RGBA32（1 画素 4 バイト）で、元の DXT1（0.5 バイト）の **8 倍**ある。
        /// 圧縮しないと「ピクセル数は減ったのにファイルは太る」という本末転倒になる。
        /// アルファを使っていなければ DXT1、使っていれば DXT5 にする
        /// （G-4-2 でアルファ不要テクスチャを BC1 化しているのと同じ判断）。
        /// </summary>
        private static void Compress(Texture2D tex, bool needsAlpha)
        {
            try
            {
                var format = needsAlpha ? TextureFormat.DXT5 : TextureFormat.DXT1;
                UnityEditor.EditorUtility.CompressTexture(tex, format,
                    UnityEditor.TextureCompressionQuality.Normal);
            }
            catch (Exception e)
            {
                // 圧縮できなくても絵は正しい。太るだけなので続行する。
                Debug.LogWarning($"[ItiOptimiser] アトラスを圧縮できなかった: {e.Message}");
            }
        }

        /// <summary>
        /// アルファが要るか。
        ///
        /// 【なぜ焼き上がりを走査しないか】
        /// 4096x4096 を GetPixels32 で全画素見ると 1 枚 1,600 万画素。
        /// アトラスは何十枚も作るので、ここで数分単位の時間を食う。
        /// 元テクスチャの形式を見れば十分に判断できる。
        /// </summary>
        private static bool NeedsAlpha(string propertyName, Dictionary<Material, Texture2D> sources)
        {
            // アルファを持たないマテリアルが混ざると、その領域は塗りつぶし色（アルファ 0）で
            // 埋まる。切り抜き系のプロパティでは必ずアルファを残す。
            if (propertyName.IndexOf("MainTex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                propertyName.IndexOf("BaseMap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                propertyName.IndexOf("BaseColor", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            foreach (var t in sources.Values)
            {
                if (t == null) continue;
                try
                {
                    if (GraphicsFormatUtility.HasAlphaChannel(t.graphicsFormat)) return true;
                }
                catch (Exception) { return true; }
            }
            return false;
        }

        /// <summary>
        /// このグループのマテリアルが使っているテクスチャプロパティを全部集める。
        /// 焼くのはここに出たものだけでよい。
        /// </summary>
        public static List<string> CollectTextureProperties(AtlasLayout layout)
        {
            var props = new List<string>();
            foreach (var m in layout?.Materials ?? new List<Material>())
            {
                if (m == null || m.shader == null) continue;
                int count = UnityEditor.ShaderUtil.GetPropertyCount(m.shader);
                for (int i = 0; i < count; i++)
                {
                    if (UnityEditor.ShaderUtil.GetPropertyType(m.shader, i)
                        != UnityEditor.ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    var name = UnityEditor.ShaderUtil.GetPropertyName(m.shader, i);
                    if (props.Contains(name)) continue;
                    if (m.GetTexture(name) is Texture2D) props.Add(name);
                }
            }
            props.Sort(StringComparer.Ordinal);   // 実行ごとに順番が変わらないように
            return props;
        }

        /// <summary>アトラスの UV 矩形を、左上原点のピクセル矩形に直す。</summary>
        private static Rect ToPixelRect(Rect uvRect, int size)
        {
            float x = uvRect.x * size;
            float w = uvRect.width * size;
            float h = uvRect.height * size;
            // UV は下から上、GUI は上から下なので y を反転する
            float yTop = size - (uvRect.y + uvRect.height) * size;
            return new Rect(x, yTop, w, h);
        }

        /// <summary>
        /// テクスチャを持たないマテリアルの領域を単色で塗る。
        /// GUI.color 経由だと反映されるかがコンテキスト依存なので、
        /// 1x1 のテクスチャを実際に作って描く。
        /// </summary>
        private static void DrawSolid(Rect dest, Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                tex.SetPixel(0, 0, c);
                tex.Apply(false, false);
                Graphics.DrawTexture(dest, tex, new Rect(0, 0, 1, 1), 0, 0, 0, 0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// 何も描かれない場所を塗る色。
        /// 法線マップを白や黒で塗ると、その部分だけ変な向きの陰影が出る。
        /// </summary>
        private static Color DefaultBackground(string propertyName)
        {
            if (propertyName.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                propertyName.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Color(0.5f, 0.5f, 1f, 1f);   // まっすぐ上を向いた法線
            return new Color(0f, 0f, 0f, 0f);
        }

        private static bool IsSrgb(Texture2D t)
        {
            try { return GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat); }
            catch (Exception) { return true; }
        }
    }
}
