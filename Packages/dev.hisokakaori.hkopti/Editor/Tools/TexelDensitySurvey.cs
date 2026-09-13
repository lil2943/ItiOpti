using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using HisokaKaori.HKOpti.Editor.Analysis;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// 解像度削減の設定（目標密度・安全マージン）を変えたときに、
    /// 実際どれだけ減るのかを測る。**既定値を勘で決めないため**の調査。
    ///
    /// 全アバターを走査し、テクスチャごとに「全アバター中の最大要求値」を取る。
    /// 1 体でも計算できなかったテクスチャは対象外にする（安全側）。
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.TexelDensitySurvey.Run
    ///     -hkoptiScenes "Assets/log/zome.unity;..."
    ///     -hkoptiOut "C:\path\texel.txt"
    ///
    /// データは一切変更しない。シーンは開くだけで保存しない。
    /// </summary>
    public static class TexelDensitySurvey
    {
        private sealed class Setting
        {
            public string Label;
            public float Target;
            public int SafetyDoublings;
        }

        // 「見た目が変わらない」から「積極的」まで並べて比較する
        private static readonly Setting[] Settings =
        {
            new Setting { Label = "2048px/m + 1段余裕", Target = 2048f, SafetyDoublings = 1 },
            new Setting { Label = "2048px/m 計算どおり", Target = 2048f, SafetyDoublings = 0 },
            new Setting { Label = "1536px/m 計算どおり", Target = 1536f, SafetyDoublings = 0 },
            new Setting { Label = "1024px/m 計算どおり", Target = 1024f, SafetyDoublings = 0 },
        };

        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("テクセル密度による解像度削減の実効量（設定別）");
            sb.AppendLine($"生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();
            sb.AppendLine("テクスチャごとに『全アバター中で最大の要求値』を採る。");
            sb.AppendLine("1 体でも計算できなかったテクスチャは対象外（縮小しない）。");
            sb.AppendLine("→ どのアバターでも劣化しない下げ幅だけを数えている。");
            sb.AppendLine();

            try
            {
                Execute(sb);
            }
            catch (Exception e)
            {
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }

            var text = sb.ToString();
            Debug.Log(text);

            var outPath = GetArg("-hkoptiOut");
            if (!string.IsNullOrEmpty(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
            }
        }

        private static void Execute(StringBuilder sb)
        {
            var scenes = GetArg("-hkoptiScenes");
            if (string.IsNullOrEmpty(scenes)) { sb.AppendLine("-hkoptiScenes 未指定"); return; }

            // 設定ごとに合成結果を持つ
            var merged = Settings.ToDictionary(s => s.Label, _ => new TexelDensity.Result());
            var allTextures = new HashSet<Texture2D>();
            int avatarCount = 0;

            foreach (var scenePath in scenes.Split(';'))
            {
                var path = scenePath.Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null)
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ToList();

                foreach (var desc in avatars)
                {
                    avatarCount++;
                    var index = ReferenceIndex.Build(desc.gameObject);

                    foreach (var t in index.TextureUsage.Keys)
                    {
                        if (t is Texture2D t2) allTextures.Add(t2);
                    }

                    foreach (var s in Settings)
                    {
                        var r = TexelDensity.Analyze(index, new TexelDensity.Options
                        {
                            TargetPixelsPerMeter = s.Target,
                            SafetyDoublings = s.SafetyDoublings,
                        });
                        TexelDensity.MergeConservative(merged[s.Label], r);
                    }
                }
            }

            long totalBytes = allTextures.Sum(t => AvatarStats.EstimateTextureBytes(t));

            sb.AppendLine($"対象アバター: {avatarCount} 体");
            sb.AppendLine($"ユニークテクスチャ: {allTextures.Count} 枚 / " +
                          $"{totalBytes / (1024.0 * 1024.0):F0} MB");
            sb.AppendLine();

            sb.AppendLine($"{"設定",-24}{"削減枚数",10}{"削減量",12}{"削減率",9}{"対象外",9}");
            sb.AppendLine(new string('-', 66));

            foreach (var s in Settings)
            {
                var m = merged[s.Label];
                var reductions = TexelDensity.SafeReductions(m);

                long saved = 0;
                foreach (var kv in reductions)
                {
                    var t2 = kv.Key;
                    long cur = AvatarStats.EstimateTextureBytes(t2);
                    double ratio = (double)kv.Value * kv.Value /
                                   ((double)t2.width * t2.height);
                    saved += cur - (long)(cur * ratio);
                }

                int excluded = m.Unknown.Count;
                sb.AppendLine($"{s.Label,-24}{reductions.Count,10}" +
                              $"{saved / (1024.0 * 1024.0),11:F0}MB" +
                              $"{saved * 100.0 / Math.Max(totalBytes, 1),8:F0}%" +
                              $"{excluded,9}");
            }

            // 既定候補の内訳を詳しく出す
            var defaultLabel = Settings[0].Label;
            var def = TexelDensity.SafeReductions(merged[defaultLabel]);
            sb.AppendLine();
            sb.AppendLine($"[{defaultLabel} の内訳]");

            var byStep = def.GroupBy(kv =>
                    $"{Math.Max(kv.Key.width, kv.Key.height)} → {kv.Value}")
                .OrderByDescending(g => g.Count());
            foreach (var g in byStep.Take(12))
                sb.AppendLine($"    {g.Key,-16} {g.Count(),5} 枚");

            sb.AppendLine();
            sb.AppendLine("[削減量が大きいテクスチャ 上位20]");
            foreach (var kv in def.OrderByDescending(kv =>
                         AvatarStats.EstimateTextureBytes(kv.Key)).Take(20))
            {
                var t2 = kv.Key;
                long cur = AvatarStats.EstimateTextureBytes(t2);
                sb.AppendLine($"    {cur / (1024.0 * 1024.0),6:F1}MB  " +
                              $"{t2.width}x{t2.height} → {kv.Value}x{kv.Value}  {t2.name}");
            }

            sb.AppendLine();
            sb.AppendLine("[読み取り方]");
            sb.AppendLine(" ・「対象外」は面積を計算できなかったテクスチャ。安全のため縮小しない。");
            sb.AppendLine(" ・「1段余裕」は計算値より 2 の冪で 1 段大きめを推奨する設定。");
            sb.AppendLine("   推定が外れても足りなくならない側に倒れるので、既定にするならこれ。");
            sb.AppendLine(" ・数字を見て、余裕を持たせても十分な削減が出るなら安全側を既定にする。");
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
