using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using HisokaKaori.HKOpti.Editor.Passes;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// アトラス化を実際のアバターに通して、壊れていないかを確かめる。
    ///
    /// 【何を確かめるか】
    ///   1. ビルドが例外を出さずに通るか
    ///   2. マテリアルスロット・ピクセル数が実際に減ったか
    ///   3. 頂点数・BlendShape 数が保たれているか（UV 書き換えで壊していないか）
    ///   4. 焼いたアトラスを PNG で書き出す（人間が目で見て確認するため）
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.AtlasSelfTest.Run
    ///     -hkoptiScenes "Assets/log/Chocolat.unity"
    ///     -hkoptiAvatarName "Chocolat_listening (1)"
    ///     -hkoptiLevel 0
    ///     -hkoptiOut "C:\path\atlas-selftest.txt"
    ///     -hkoptiDumpDir "C:\path\atlas-png"
    ///
    /// **-nographics を付けないこと。** テクスチャを GPU で描くので描画が要る。
    /// シーンは開くだけで保存しない。
    /// </summary>
    public static class AtlasSelfTest
    {
        public static void Run()
        {
            var sb = new StringBuilder();
            try { Execute(sb); }
            catch (Exception e)
            {
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }

            var text = sb.ToString();
            Debug.Log(text);

            var outPath = Arg("-hkoptiOut");
            if (!string.IsNullOrEmpty(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                File.WriteAllText(outPath, text, new UTF8Encoding(true));
            }
        }

        private static void Execute(StringBuilder sb)
        {
            var scenes = Arg("-hkoptiScenes");
            var wanted = Arg("-hkoptiAvatarName");
            var dumpDir = Arg("-hkoptiDumpDir");
            if (string.IsNullOrEmpty(scenes)) { sb.AppendLine("-hkoptiScenes 未指定"); return; }

            // 段階は ';' 区切りで複数指定できる。
            // 比較用（アトラス化なし）のビルドを 1 回で済ませられるので、
            // 段階ごとに Unity を起動し直すより大幅に速い。
            var levels = new List<HKOAtlasLevel>();
            var levelArg = Arg("-hkoptiLevel");
            if (!string.IsNullOrEmpty(levelArg))
            {
                foreach (var part in levelArg.Split(';'))
                {
                    if (int.TryParse(part.Trim(), out var lv))
                    {
                        var parsed = (HKOAtlasLevel)Mathf.Clamp(lv, 0, 2);
                        if (!levels.Contains(parsed)) levels.Add(parsed);
                    }
                }
            }
            if (levels.Count == 0) levels.Add(HKOAtlasLevel.Conservative);

            var names = string.IsNullOrEmpty(wanted)
                ? null
                : new HashSet<string>(wanted.Split(';').Select(s => s.Trim())
                    .Where(s => s.Length > 0), StringComparer.Ordinal);

            sb.AppendLine($"踏み込み具合: {string.Join(", ", levels)}");
            sb.AppendLine();

            foreach (var scenePath in scenes.Split(';'))
            {
                var path = scenePath.Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null)
                    .Where(a => names == null || names.Contains(a.name))
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ToList();

                foreach (var a in avatars) Test(a.gameObject, levels, dumpDir, sb);
            }
        }

        private static void Test(GameObject source, List<HKOAtlasLevel> levels,
            string dumpDir, StringBuilder sb)
        {
            sb.AppendLine(new string('=', 78));
            sb.AppendLine($"■ {source.name}");
            sb.AppendLine(new string('=', 78));

            GameObject without = null;
            Stats baseStats;
            try
            {
                // 比較のもとになる「アトラス化なし」のビルドは 1 回だけ。
                // 段階ごとに作り直すと、1 体あたり 10 分以上を無駄にする。
                without = UnityEngine.Object.Instantiate(source);
                without.name = source.name + " (ItiOptimiser比較用:なし)";
                without.SetActive(true);
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(without);
                baseStats = Measure(without);
            }
            catch (Exception e)
            {
                sb.AppendLine($"  !! 比較用のビルドに失敗: {e.GetType().Name}: {e.Message}");
                sb.AppendLine();
                return;
            }
            finally
            {
                if (without != null) UnityEngine.Object.DestroyImmediate(without);
            }

            sb.AppendLine($"  アトラス化なし      : スロット {baseStats.Slots} / " +
                          $"テクスチャ {baseStats.Textures} 枚 / {Mb(baseStats.Bytes)}");
            sb.AppendLine();

            foreach (var level in levels) TestOneLevel(source, level, baseStats, dumpDir, sb);
        }

        private static void TestOneLevel(GameObject source, HKOAtlasLevel level,
            Stats baseStats, string dumpDir, StringBuilder sb)
        {
            sb.AppendLine($"  ── {LevelLabel(level)} ──");

            GameObject withAtlas = null;
            try
            {
                withAtlas = UnityEngine.Object.Instantiate(source);
                withAtlas.name = source.name + " (ItiOptimiser比較用:あり)";
                withAtlas.SetActive(true);
                var comp = withAtlas.AddComponent<HKOTextureAtlas>();
                comp.Enabled = true;
                comp.Level = level;

                TextureAtlasPass.Reset();
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(withAtlas);
                var atlasStats = Measure(withAtlas);

                var run = TextureAtlasPass.LastRunResult;
                if (run == null)
                {
                    sb.AppendLine("  !! アトラス化のパスが動かなかった");
                    sb.AppendLine();
                    return;
                }

                sb.AppendLine($"  グループ            : {run.GroupsAtlased} / {run.GroupsConsidered} を統合");
                sb.AppendLine($"  マテリアルスロット  : {baseStats.Slots} → {atlasStats.Slots}");
                sb.AppendLine($"  ユニークマテリアル  : {baseStats.Materials} → {atlasStats.Materials}");
                sb.AppendLine($"  テクスチャ枚数      : {baseStats.Textures} → {atlasStats.Textures}");
                sb.AppendLine($"  総ピクセル数        : {baseStats.Pixels:N0} → {atlasStats.Pixels:N0}" +
                              $"（{Cut(baseStats.Pixels, atlasStats.Pixels):F0}% 削減）");
                sb.AppendLine($"  テクスチャ実サイズ  : {Mb(baseStats.Bytes)} → {Mb(atlasStats.Bytes)}" +
                              $"（{Cut(baseStats.Bytes, atlasStats.Bytes):F0}% 削減）" +
                              "  ← ここが増えていたら圧縮が効いていない");
                sb.AppendLine();

                sb.AppendLine("  [壊れていないかの確認]");
                Check(sb, "頂点数", baseStats.Vertices, atlasStats.Vertices);
                Check(sb, "三角形数", baseStats.Triangles, atlasStats.Triangles);
                Check(sb, "BlendShape 数", baseStats.BlendShapes, atlasStats.BlendShapes);
                Check(sb, "Renderer 数", baseStats.Renderers, atlasStats.Renderers);
                sb.AppendLine();

                if (run.Warnings.Count > 0)
                {
                    sb.AppendLine("  [警告]");
                    foreach (var w in run.Warnings.Distinct().Take(15))
                        sb.AppendLine($"    {w}");
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(dumpDir))
                    DumpAtlases(withAtlas, Path.Combine(dumpDir, level.ToString()), source.name, sb);
            }
            catch (Exception e)
            {
                sb.AppendLine($"  !! 失敗: {e.GetType().Name}: {e.Message}");
                sb.AppendLine("  " + (e.StackTrace ?? "").Replace("\n", "\n  "));
            }
            finally
            {
                if (withAtlas != null) UnityEngine.Object.DestroyImmediate(withAtlas);
            }
            sb.AppendLine();
        }

        private static string LevelLabel(HKOAtlasLevel level)
        {
            switch (level)
            {
                case HKOAtlasLevel.Conservative: return "保守的 (Conservative)";
                case HKOAtlasLevel.AbsorbVariants: return "設定の違いを吸収 (AbsorbVariants)";
                case HKOAtlasLevel.Maximum: return "最大限まとめる (Maximum)";
                default: return level.ToString();
            }
        }

        private struct Stats
        {
            public int Slots, Materials, Textures, Renderers, BlendShapes;
            public long Vertices, Triangles, Pixels, Bytes;
        }

        private static Stats Measure(GameObject root)
        {
            var s = new Stats();
            var mats = new HashSet<Material>();
            var texs = new HashSet<Texture>();
            var meshes = new HashSet<Mesh>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                s.Renderers++;
                var ms = r.sharedMaterials;
                if (ms != null)
                {
                    s.Slots += ms.Length;
                    foreach (var m in ms) if (m != null) mats.Add(m);
                }

                var mesh = r is SkinnedMeshRenderer smr
                    ? smr.sharedMesh
                    : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || !meshes.Add(mesh)) continue;
                s.Vertices += mesh.vertexCount;
                s.BlendShapes += mesh.blendShapeCount;
                for (int i = 0; i < mesh.subMeshCount; i++)
                    s.Triangles += mesh.GetIndexCount(i) / 3;
            }

            foreach (var m in mats)
            {
                if (m == null || m.shader == null) continue;
                int count = ShaderUtil.GetPropertyCount(m.shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(m.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                        continue;
                    var t = m.GetTexture(ShaderUtil.GetPropertyName(m.shader, i));
                    if (t != null) texs.Add(t);
                }
            }

            s.Materials = mats.Count;
            s.Textures = texs.Count;
            foreach (var t in texs)
            {
                s.Pixels += (long)t.width * t.height;
                if (t is Texture2D t2) s.Bytes += Analysis.AvatarStats.EstimateTextureBytes(t2);
            }
            return s;
        }

        private static void Check(StringBuilder sb, string label, long before, long after)
        {
            bool ok = before == after;
            sb.AppendLine($"    {(ok ? "OK  " : "NG !")} {label,-16}: {before:N0} → {after:N0}" +
                          (ok ? "" : "  ← 変わってはいけない値が変わっている"));
        }

        /// <summary>焼いたアトラスを PNG で書き出す。人間が目で見て確認するため。</summary>
        private static void DumpAtlases(GameObject root, string dir, string avatarName, StringBuilder sb)
        {
            Directory.CreateDirectory(dir);
            var written = new HashSet<Texture2D>();
            int n = 0;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r?.sharedMaterials ?? Array.Empty<Material>())
                {
                    if (m == null || m.shader == null) continue;
                    int count = ShaderUtil.GetPropertyCount(m.shader);
                    for (int i = 0; i < count; i++)
                    {
                        if (ShaderUtil.GetPropertyType(m.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                            continue;
                        var prop = ShaderUtil.GetPropertyName(m.shader, i);
                        if (!(m.GetTexture(prop) is Texture2D t)) continue;
                        if (t.name == null || !t.name.StartsWith("ItiOptimiser_Atlas")) continue;
                        if (!written.Add(t)) continue;

                        try
                        {
                            var png = ToPng(t);
                            if (png == null) continue;
                            var safe = Sanitize($"{avatarName}_{t.name}_{n++}");
                            File.WriteAllBytes(Path.Combine(dir, safe + ".png"), png);
                        }
                        catch (Exception e)
                        {
                            sb.AppendLine($"    PNG 書き出しに失敗: {e.Message}");
                        }
                    }
                }
            }
            sb.AppendLine($"  アトラス画像 {written.Count} 枚を書き出した: {dir}");
        }

        /// <summary>
        /// PNG に変換する。焼いたアトラスは圧縮済みで EncodeToPNG が使えないので、
        /// 一度 RenderTexture に描いてから読み戻す。
        /// </summary>
        private static byte[] ToPng(Texture2D t)
        {
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                readable.Apply(false, false);
                var bytes = readable.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(readable);
                return bytes;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

        private static double Cut(long before, long after)
            => before > 0 ? (before - after) * 100.0 / before : 0;

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
