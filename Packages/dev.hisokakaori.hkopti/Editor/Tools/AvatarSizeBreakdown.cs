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
    /// 1 体のアバターについて「何がサイズを食っているか」を種類別に出す。
    ///
    /// 【なぜ作ったか】
    /// これまでの計測は VRAM（テクスチャのメモリ量）を見ていた。
    /// しかしユーザーが実際に気にするのは **VRChat のダウンロードサイズ**で、
    /// これは別物。ダウンロードサイズはアセットバンドルの圧縮後サイズなので、
    /// テクスチャだけでなくメッシュ・アニメーション・音声も含む。
    ///
    /// 「テクスチャ VRAM を 36% 減らせる」＝「ダウンロードサイズが 36% 減る」ではない。
    /// どこを削ればダウンロードサイズに効くのかを、まず数えて確かめる。
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.AvatarSizeBreakdown.Run
    ///     -hkoptiScenes "Assets/log/Chocolat.unity"
    ///     -hkoptiAvatarName "Chocolat_listening (1)"
    ///     -hkoptiOut "C:\path\breakdown.txt"
    ///
    /// データは変更しない。
    /// </summary>
    public static class AvatarSizeBreakdown
    {
        public static void Run()
        {
            var sb = new StringBuilder();
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
            var wanted = GetArg("-hkoptiAvatarName");
            if (string.IsNullOrEmpty(scenes)) { sb.AppendLine("-hkoptiScenes 未指定"); return; }

            // 複数シーンをまとめて回すとき用に、名前は ';' 区切りで複数指定できる。
            // Unity の起動が 1 回で済む（1 回あたり 1〜2 分かかるので効く）。
            var wantedSet = string.IsNullOrEmpty(wanted)
                ? null
                : new HashSet<string>(wanted.Split(';').Select(s => s.Trim())
                    .Where(s => s.Length > 0), StringComparer.Ordinal);

            foreach (var scenePath in scenes.Split(';'))
            {
                var path = scenePath.Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null)
                    .Where(a => wantedSet == null || wantedSet.Contains(a.name))
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ToList();

                foreach (var a in avatars) Report(a.gameObject, sb);
            }
        }

        private static void Report(GameObject avatar, StringBuilder sb)
        {
            var index = ReferenceIndex.Build(avatar);

            sb.AppendLine(new string('=', 78));
            sb.AppendLine($"■ {avatar.name}");
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();

            // ---------- テクスチャ ----------
            var texRows = new List<(Texture2D tex, long vram, long disk, string fmt)>();
            foreach (var t in index.TextureUsage.Keys)
            {
                var t2 = t as Texture2D;
                if (t2 == null) continue;
                texRows.Add((t2,
                    AvatarStats.EstimateTextureBytes(t2),
                    FileSizeOf(t2),
                    t2.format.ToString()));
            }
            foreach (var t2 in index.ExpressionMenuTextures)
            {
                if (t2 == null) continue;
                if (texRows.Any(r => r.tex == t2)) continue;
                texRows.Add((t2, AvatarStats.EstimateTextureBytes(t2), FileSizeOf(t2),
                    t2.format.ToString()));
            }

            long texVram = texRows.Sum(r => r.vram);

            // ---------- メッシュ ----------
            long meshBytes = 0;
            var seenMesh = new HashSet<Mesh>();
            int blendShapes = 0;
            long vertexTotal = 0;
            foreach (var smr in index.SkinnedMeshes)
            {
                var m = smr != null ? smr.sharedMesh : null;
                if (m == null || !seenMesh.Add(m)) continue;
                meshBytes += EstimateMeshBytes(m);
                blendShapes += m.blendShapeCount;
                vertexTotal += m.vertexCount;
            }
            foreach (var mr in index.MeshRenderers)
            {
                var mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
                var m = mf != null ? mf.sharedMesh : null;
                if (m == null || !seenMesh.Add(m)) continue;
                meshBytes += EstimateMeshBytes(m);
                vertexTotal += m.vertexCount;
            }

            // ---------- アニメーション ----------
            long animBytes = 0;
            int clipCount = 0;
            var seenClip = new HashSet<AnimationClip>();
            foreach (var a in avatar.GetComponentsInChildren<Animator>(true))
            {
                var rac = a != null ? a.runtimeAnimatorController : null;
                if (rac == null) continue;
                foreach (var c in rac.animationClips)
                {
                    if (c == null || !seenClip.Add(c)) continue;
                    clipCount++;
                    animBytes += FileSizeOf(c);
                }
            }
            var desc = avatar.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                foreach (var layers in new[] { desc.baseAnimationLayers, desc.specialAnimationLayers })
                {
                    if (layers == null) continue;
                    foreach (var l in layers)
                    {
                        if (l.animatorController == null) continue;
                        foreach (var c in l.animatorController.animationClips)
                        {
                            if (c == null || !seenClip.Add(c)) continue;
                            clipCount++;
                            animBytes += FileSizeOf(c);
                        }
                    }
                }
            }

            // ---------- 音声 ----------
            long audioBytes = 0;
            int audioCount = 0;
            var seenClips = new HashSet<AudioClip>();
            foreach (var src in avatar.GetComponentsInChildren<AudioSource>(true))
            {
                var c = src != null ? src.clip : null;
                if (c == null || !seenClips.Add(c)) continue;
                audioCount++;
                audioBytes += FileSizeOf(c);
            }

            sb.AppendLine("[種類別のサイズ]");
            sb.AppendLine($"  テクスチャ VRAM 換算 : {Mb(texVram),10}   ({texRows.Count} 枚)");
            sb.AppendLine($"  メッシュ（概算）     : {Mb(meshBytes),10}   ({seenMesh.Count} 個 / " +
                          $"{vertexTotal:N0} 頂点 / BlendShape {blendShapes})");
            sb.AppendLine($"  アニメーション       : {Mb(animBytes),10}   ({clipCount} クリップ)");
            sb.AppendLine($"  音声                 : {Mb(audioBytes),10}   ({audioCount} 個)");
            sb.AppendLine();
            sb.AppendLine("  ※ VRChat のダウンロードサイズは圧縮後のアセットバンドルなので、");
            sb.AppendLine("     ここの合計とは一致しない。どこが支配的かを見るための内訳。");
            sb.AppendLine();

            // ---------- テクスチャの形式別内訳 ----------
            sb.AppendLine("[テクスチャの形式別]");
            foreach (var g in texRows.GroupBy(r => r.fmt).OrderByDescending(g => g.Sum(r => r.vram)))
            {
                sb.AppendLine($"  {g.Key,-16} {g.Count(),4} 枚  VRAM {Mb(g.Sum(r => r.vram)),10}");
            }
            sb.AppendLine();

            sb.AppendLine("[解像度別]");
            foreach (var g in texRows.GroupBy(r => Math.Max(r.tex.width, r.tex.height))
                         .OrderByDescending(g => g.Key))
            {
                sb.AppendLine($"  {g.Key,5}px  {g.Count(),4} 枚  VRAM {Mb(g.Sum(r => r.vram)),10}");
            }
            sb.AppendLine();

            // ---------- HKOpti が何をどれだけ削れると見ているか ----------
            sb.AppendLine("[ItiOptimiserの削減見込み（このアバター単体で走査した場合）]");
            foreach (AssetOptimizer.ResolutionTier tier in
                     Enum.GetValues(typeof(AssetOptimizer.ResolutionTier)))
            {
                if (tier == AssetOptimizer.ResolutionTier.None) continue;
                AssetOptimizer.Resolution = tier;
                var fixes = AssetOptimizer.Scan(new[] { avatar });

                sb.AppendLine($"  --- 解像度の積極度: {tier} ---");
                foreach (var g in fixes.GroupBy(f => f.KindLabel)
                             .OrderByDescending(g => g.Sum(f => f.SavedBytes)))
                {
                    sb.AppendLine($"      {g.Key,-22} {g.Count(),4} 件  {Mb(g.Sum(f => f.SavedBytes)),10}");
                }
            }
            AssetOptimizer.Resolution = AssetOptimizer.ResolutionTier.Safe;
            sb.AppendLine();

            // ---------- 走査範囲を変えると結果がどう変わるか ----------
            //
            // 解像度の推奨値は「走査した全アバター中の最大」を採る安全規則にしてある。
            // つまり同じシーンに、同じテクスチャをもっと大きく使うアバターがいると
            // 削減対象から外れる。UI で「シーン内の全アバター」を選ぶと、
            // 単体で走査したときより削減量が激減しうる。
            // その差を実際に出して、体感と数字の食い違いの原因を切り分ける。
            var sceneAvatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                .Where(x => x != null).Select(x => x.gameObject).ToList();

            if (sceneAvatars.Count > 1)
            {
                sb.AppendLine($"[走査範囲による違い（シーン内 {sceneAvatars.Count} 体すべてを走査した場合）]");

                foreach (AssetOptimizer.ResolutionTier tier in
                         Enum.GetValues(typeof(AssetOptimizer.ResolutionTier)))
                {
                    if (tier == AssetOptimizer.ResolutionTier.None) continue;
                    AssetOptimizer.Resolution = tier;
                    var allFixes = AssetOptimizer.Scan(sceneAvatars);

                    var res = allFixes.Where(f => f.Kind == AssetFixKind.ReduceTextureResolution).ToList();

                    // このアバターが使っているテクスチャに限った内訳も出す
                    var myPaths = new HashSet<string>(
                        index.TextureUsage.Keys.OfType<Texture2D>()
                            .Select(AssetDatabase.GetAssetPath)
                            .Where(p => !string.IsNullOrEmpty(p)),
                        StringComparer.OrdinalIgnoreCase);
                    var mine = res.Where(f => myPaths.Contains(f.AssetPath)).ToList();

                    sb.AppendLine($"  {tier,-11} 解像度削減 全体 {res.Count,4} 件 " +
                                  $"{Mb(res.Sum(f => f.SavedBytes)),10}" +
                                  $"　／ このアバター分 {mine.Count,3} 件 {Mb(mine.Sum(f => f.SavedBytes)),10}");
                }
                AssetOptimizer.Resolution = AssetOptimizer.ResolutionTier.Safe;
                sb.AppendLine();
                sb.AppendLine("  ※ 単体走査より大きく減っている場合、原因はこれ。");
                sb.AppendLine("    「そのテクスチャをもっと大きく使うアバターが同じシーンにいる」ため、");
                sb.AppendLine("    安全規則により縮小対象から外れている。");
                sb.AppendLine();
            }

            // ---------- ビルド後（AAO / MA / VRCFury 適用後）に何が残るか ----------
            //
            // 【ここが一番大事】
            // 上の数字はすべて「シーン上の状態」＝ビルド前。
            // 実際にアップロードされるのは AAO / MA / VRCFury が処理した後のもの。
            // B-6 で同じ間違いをしている：ビルド前の状態を根拠に効果を見積もると、
            // 「他のツールが既に消しているもの」まで自分の成果として数えてしまう。
            ReportAfterBuild(avatar, texVram, texRows.Count, sb);

            // ---------- 大きいテクスチャ ----------
            sb.AppendLine("[VRAM が大きいテクスチャ 上位15]");
            foreach (var r in texRows.OrderByDescending(r => r.vram).Take(15))
            {
                sb.AppendLine($"  {Mb(r.vram),9}  {r.tex.width}x{r.tex.height} {r.fmt,-14} {r.tex.name}");
            }
            sb.AppendLine();

            // ---------- 元ファイルが大きいもの（ダウンロードサイズの手がかり） ----------
            sb.AppendLine("[元ファイルが大きいテクスチャ 上位15]（アップロードサイズの手がかり）");
            foreach (var r in texRows.Where(r => r.disk > 0).OrderByDescending(r => r.disk).Take(15))
            {
                sb.AppendLine($"  {Mb(r.disk),9}  {r.tex.width}x{r.tex.height} {r.tex.name}");
            }
            sb.AppendLine();
        }

        /// <summary>
        /// アバターの複製に NDMF（＝ AAO / MA / VRCFury を含むビルド処理）を通し、
        /// 実際にアップロードされる状態で測り直す。元のアバターは触らない。
        /// </summary>
        private static void ReportAfterBuild(GameObject source, long beforeVram, int beforeCount,
            StringBuilder sb)
        {
            sb.AppendLine("[ビルド後（AAO / MA / VRCFury 適用後）に実際に残るもの]");

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source);
                clone.name = source.name + " (ItiOptimiser計測用複製)";
                clone.SetActive(true);

                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(clone);

                var after = ReferenceIndex.Build(clone);
                var afterTex = after.TextureUsage.Keys.OfType<Texture2D>().ToList();
                long afterVram = afterTex.Sum(AvatarStats.EstimateTextureBytes);

                long afterMesh = 0;
                var seen = new HashSet<Mesh>();
                long verts = 0;
                foreach (var smr in after.SkinnedMeshes)
                {
                    var m = smr != null ? smr.sharedMesh : null;
                    if (m == null || !seen.Add(m)) continue;
                    afterMesh += EstimateMeshBytes(m);
                    verts += m.vertexCount;
                }

                long matSlots = after.SkinnedMeshes.Where(r => r != null)
                    .Sum(r => r.sharedMaterials?.Length ?? 0);

                sb.AppendLine($"  テクスチャ : {beforeCount,4} 枚 {Mb(beforeVram),10}" +
                              $"  →  {afterTex.Count,4} 枚 {Mb(afterVram),10}");
                sb.AppendLine($"  メッシュ   : {seen.Count,4} 個 {Mb(afterMesh),10}  ({verts:N0} 頂点)");
                sb.AppendLine($"  マテリアルスロット : {matSlots}");
                sb.AppendLine($"  SkinnedMesh        : {after.SkinnedMeshes.Count}");
                sb.AppendLine($"  Transform          : {after.AllTransforms.Count}");
                sb.AppendLine();

                // フェーズ 0.5 後半（変位ゼロ BlendShape・重複マテリアル）が
                // ビルド後にも本当に残っているかを確認する。
                // AAO が既に消しているなら実装する意味が無い（B-6 と同じ検証）。
                ReportPhase05Headroom(after, sb);

                // アトラス化（G-5）も、作る前に余地を数える。
                AtlasHeadroom.Report(after, sb);

                // メッシュの中の無駄も同じく、作る前に数える。
                MeshHeadroom.Report(after, sb);

                double removed = beforeVram > 0
                    ? (beforeVram - afterVram) * 100.0 / beforeVram : 0;
                sb.AppendLine($"  → 他ツールが既に落としているテクスチャ量: " +
                              $"{Mb(beforeVram - afterVram)} ({removed:F0}%)");
                sb.AppendLine();

                // ビルド後の状態に対して HKOpti が何をできるか
                sb.AppendLine("  [ビルド後の状態に対するItiOptimiserの削減見込み]");
                foreach (AssetOptimizer.ResolutionTier tier in
                         Enum.GetValues(typeof(AssetOptimizer.ResolutionTier)))
                {
                    if (tier == AssetOptimizer.ResolutionTier.None) continue;
                    AssetOptimizer.Resolution = tier;
                    var fixes = AssetOptimizer.Scan(new[] { clone });
                    var res = fixes.Where(f => f.Kind == AssetFixKind.ReduceTextureResolution).ToList();
                    sb.AppendLine($"    {tier,-11} 解像度削減 {res.Count,4} 件 " +
                                  $"{Mb(res.Sum(f => f.SavedBytes)),10}");
                }
                AssetOptimizer.Resolution = AssetOptimizer.ResolutionTier.Safe;
                sb.AppendLine();
                sb.AppendLine("  ※ ここの数字が「ビルド前」より大幅に小さいなら、");
                sb.AppendLine("     ItiOptimiserは他ツールが既に消しているものを数えていたことになる。");
            }
            catch (Exception e)
            {
                sb.AppendLine($"  !! NDMF 実行に失敗: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
            }
            sb.AppendLine();
        }

        /// <summary>
        /// フェーズ 0.5 後半の候補が、ビルド後にも残っているかを数える。
        ///
        /// 【なぜビルド後で測るか】
        /// B-6 では「ビルド前の状態」を根拠に最重要機能と判断し、実際は Modular Avatar が
        /// 既に処理済みで実効余地が 1 体 0.2 本しかなかった。
        /// AAO の TraceAndOptimize は未使用 BlendShape を削除する機能を持つので、
        /// **実装する前に、ビルド後にも本当に残っているかを確かめる。**
        /// </summary>
        private static void ReportPhase05Headroom(ReferenceIndex after, StringBuilder sb)
        {
            sb.AppendLine("  [フェーズ0.5後半の候補がビルド後に残っているか]");

            int totalShapes = 0, zeroDelta = 0, unused = 0;
            long zeroDeltaMeshes = 0;
            var seen = new HashSet<Mesh>();

            foreach (var smr in after.SkinnedMeshes)
            {
                var mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                if (!seen.Add(mesh)) continue;

                var path = after.PathOf.TryGetValue(smr.transform, out var p) ? p : "";
                after.Animations.BlendShapeRefs.TryGetValue(path, out var referenced);

                int vc = mesh.vertexCount;
                if (vc <= 0 || vc > 200000) { totalShapes += mesh.blendShapeCount; continue; }

                var dp = new Vector3[vc];
                var dn = new Vector3[vc];
                var dt = new Vector3[vc];
                bool anyZero = false;

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    totalShapes++;
                    var name = mesh.GetBlendShapeName(i);
                    bool isRef = referenced != null && referenced.Contains(name);
                    bool isSet = smr.GetBlendShapeWeight(i) > 0.01f;
                    if (!isRef && !isSet) unused++;

                    long affected = 0;
                    int frames = mesh.GetBlendShapeFrameCount(i);
                    for (int f = 0; f < frames; f++)
                    {
                        try { mesh.GetBlendShapeFrameVertices(i, f, dp, dn, dt); }
                        catch (Exception) { break; }
                        for (int v = 0; v < vc; v++)
                        {
                            if (dp[v].sqrMagnitude > 1e-12f) { affected++; break; }
                        }
                        if (affected > 0) break;
                    }
                    if (affected == 0) { zeroDelta++; anyZero = true; }
                }
                if (anyZero) zeroDeltaMeshes++;
            }

            sb.AppendLine($"    BlendShape 合計      : {totalShapes}");
            sb.AppendLine($"    └ 変位ゼロ（安全に削除可）: {zeroDelta}" +
                          $"　（{zeroDeltaMeshes} 個のメッシュに存在）");
            sb.AppendLine($"    └ 未参照かつ現在値 0      : {unused}");

            // 重複マテリアル
            var groups = new Dictionary<string, List<Material>>();
            foreach (var mat in after.MaterialUsage.Keys)
            {
                if (mat == null || mat.shader == null) continue;
                var sig = mat.shader.name + "|" + mat.renderQueue + "|" +
                          string.Join(",", mat.shaderKeywords.OrderBy(k => k)) + "|" +
                          MaterialPropertySignature(mat);
                if (!groups.TryGetValue(sig, out var list))
                {
                    list = new List<Material>();
                    groups[sig] = list;
                }
                list.Add(mat);
            }
            int dupExtra = groups.Values.Where(v => v.Count > 1).Sum(v => v.Count - 1);

            sb.AppendLine($"    マテリアル           : {after.MaterialUsage.Count}");
            sb.AppendLine($"    └ 完全重複（統合可）      : {dupExtra}");
            sb.AppendLine();
            sb.AppendLine("    ※ ここが 0 に近いなら、その機能を作っても効果が無い。");
            sb.AppendLine("      実装する前に必ずこの数字を見ること（B-6 の教訓）。");
            sb.AppendLine();
        }

        private static string MaterialPropertySignature(Material mat)
        {
            var sb = new StringBuilder();
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

        private static long EstimateMeshBytes(Mesh m)
        {
            try
            {
                // 位置12 + 法線12 + 接線16 + UV8 + ウェイト32 ≒ 80 バイト/頂点
                long v = (long)m.vertexCount * 80;
                long idx = 0;
                for (int i = 0; i < m.subMeshCount; i++) idx += m.GetIndexCount(i) * 4;

                // BlendShape は疎だが、ここでは概算として頂点数の 1 割が動くとみなす
                long bs = (long)m.vertexCount / 10 * m.blendShapeCount * 40;
                return v + idx + bs;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static long FileSizeOf(UnityEngine.Object o)
        {
            var path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 0; }
        }

        private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

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
