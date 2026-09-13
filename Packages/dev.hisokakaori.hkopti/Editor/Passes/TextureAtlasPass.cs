using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Atlas;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Passes
{
    internal sealed class TextureAtlasRunResult
    {
        public int GroupsConsidered;
        public int GroupsAtlased;
        public int SlotsBefore;
        public int SlotsAfter;
        public long PixelsBefore;
        public long PixelsAfter;
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// ビルド中にテクスチャアトラス化を行う。
    ///
    /// 実行位置は Optimizing フェーズ。Modular Avatar が服を統合し終わり、
    /// 実際に残るメッシュとマテリアルが確定してからでないと、
    /// 消える予定のものまでアトラスに詰めてしまう。
    /// （B-6・フェーズ0.5 で「ビルド前を見て判断して外した」失敗を 2 回している）
    /// </summary>
    internal static class TextureAtlasPass
    {
        internal static TextureAtlasRunResult LastRunResult { get; private set; }

        internal static void Reset() => LastRunResult = null;

        internal static void Execute(BuildContext context)
        {
            var settings = context.AvatarRootObject
                .GetComponentsInChildren<HKOTextureAtlas>(true)
                .Where(s => s != null && s.Enabled)
                .ToList();
            if (settings.Count == 0) return;

            // TexTransTool のアトラス化と二重にかけると、同じテクスチャを両方が詰め直して壊れる。
            // ビルドを止めるとアップロードが失敗するので、例外は投げずにこちらが身を引く。
            var blocking = ToolConflictDetector.Detect(
                    context.AvatarRootObject, OptimizerFeature.TextureAtlas)
                .Where(c => c.Severity == ConflictSeverity.Blocking)
                .ToList();
            if (blocking.Count > 0)
            {
                Debug.LogWarning(
                    "[ItiOptimiser] " + string.Join(", ", blocking.Select(c => c.ToolName)) +
                    " のアトラス化と重複するため、Texture Atlas は実行しませんでした。ビルドは続行します。");
                foreach (var s in settings)
                    if (s != null) UnityEngine.Object.DestroyImmediate(s);
                return;
            }

            var setting = settings[0];
            if (settings.Count > 1)
                Debug.LogWarning("[ItiOptimiser] Texture Atlasが複数あります。最初の1つだけ使います。");

            var excludedRenderers = new HashSet<Renderer>(
                settings.SelectMany(s => s.ExcludeRenderers ?? new List<Renderer>())
                        .Where(r => r != null));

            var renderers = context.AvatarRootObject
                .GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null && !excludedRenderers.Contains(r))
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
                .OrderBy(r => r.transform.GetHierarchyPathSafe(), StringComparer.Ordinal)
                .ToList();

            var options = new AtlasOptions
            {
                Level = (AtlasLevel)(int)setting.Level,
                MaxAtlasSize = Mathf.Clamp(setting.MaxAtlasSize, 256, 8192),
                PaddingPixels = Mathf.Clamp(setting.PaddingPixels, 0, 32),
                ExcludeMaterials = new HashSet<Material>(
                    settings.SelectMany(s => s.Exclude ?? new List<Material>())
                            .Where(m => m != null)),
            };

            var result = AtlasApplier.Apply(renderers, options);

            // 生成したテクスチャ・マテリアル・メッシュを、ビルド成果物として登録する。
            // これをしないとアップロード時に参照が切れる。
            foreach (var g in result.Groups)
            {
                if (g.Layout == null) continue;
                foreach (var t in g.BakedTextures.Values) Save(context, t);
                if (g.Merged != null)
                {
                    Save(context, g.Merged);
                    // 元のどれを置き換えたものかを記録しておくと、
                    // 他ツールがエラーを出したときに元のマテリアル名で表示される。
                    RegisterReplacement(g.Sources.FirstOrDefault(), g.Merged);
                }
            }
            foreach (var kv in result.RewrittenMeshes)
            {
                Save(context, kv.Value);
                RegisterReplacement(kv.Key, kv.Value);
            }
            foreach (var m in result.GeneratedMeshes) Save(context, m);

            LastRunResult = new TextureAtlasRunResult
            {
                GroupsConsidered = result.Groups.Count,
                GroupsAtlased = result.Groups.Count(g => g.Layout != null),
                SlotsBefore = result.SlotsBefore,
                SlotsAfter = result.SlotsAfter,
                PixelsBefore = result.PixelsBefore,
                PixelsAfter = result.PixelsAfter,
            };
            LastRunResult.Warnings.AddRange(result.Warnings);
            LastRunResult.Warnings.AddRange(result.Groups.SelectMany(g => g.Warnings));

            double cut = result.PixelsBefore > 0
                ? (result.PixelsBefore - result.PixelsAfter) * 100.0 / result.PixelsBefore : 0;
            Debug.Log($"[ItiOptimiser] アトラス化: グループ {LastRunResult.GroupsAtlased}/" +
                      $"{LastRunResult.GroupsConsidered} を統合、" +
                      $"ピクセル {result.PixelsBefore:N0} → {result.PixelsAfter:N0}（{cut:F0}% 削減）、" +
                      $"マテリアルスロット {result.SlotsBefore} → {result.SlotsAfter}");

            foreach (var w in LastRunResult.Warnings.Distinct().Take(20))
                Debug.LogWarning($"[ItiOptimiser] アトラス化: {w}");

            foreach (var s in settings)
                if (s != null) UnityEngine.Object.DestroyImmediate(s);
        }

        /// <summary>
        /// 生成物をビルド成果物として保存する。これをしないとアップロード時に参照が切れる。
        /// </summary>
        private static void Save(BuildContext context, UnityEngine.Object obj)
        {
            if (obj == null) return;
            context.AssetSaver.SaveAsset(obj);
        }

        private static void RegisterReplacement(UnityEngine.Object original, UnityEngine.Object replacement)
        {
            if (original == null || replacement == null) return;
            try { nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(original, replacement); }
            catch (Exception) { /* 登録できなくてもビルドは続行できる */ }
        }
    }

    internal static class TransformPathExtensions
    {
        /// <summary>並び順を安定させるためのパス。実行ごとに結果が変わるのを防ぐ。</summary>
        internal static string GetHierarchyPathSafe(this Transform t)
        {
            if (t == null) return string.Empty;
            var parts = new List<string>();
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
