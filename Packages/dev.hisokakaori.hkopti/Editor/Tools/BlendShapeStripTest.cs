using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// 「BlendShape の法線・接線がゼロなら捨てられるのか」を実物で確かめる。
    ///
    /// 【なぜ確かめるか】
    /// 計測では Chocolat の BlendShape 法線/接線が 24.1MB あり、130 フレーム全部がゼロだった。
    /// しかし Unity は BlendShape を「位置・法線・接線」1 組の固定サイズで
    /// 持っている可能性があり、その場合ゼロを空にしてもファイルは縮まない。
    ///
    /// **縮むと決めつけて実装すると、24MB 減ると報告して実際は 0 という事故になる。**
    /// なので実際に .asset として書き出してファイルサイズを比べる。
    /// ここがダウンロードサイズに効くかどうかの唯一の確かめ方。
    ///
    /// 使い方:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod HisokaKaori.HKOpti.Editor.Tools.BlendShapeStripTest.Run
    ///     -hkoptiScenes "Assets/log/Chocolat.unity"
    ///     -hkoptiAvatarName "Chocolat_listening (1)"
    ///     -hkoptiOut "C:\path\blendshape-test.txt"
    ///
    /// 一時アセットは最後に削除する。シーンは保存しない。
    /// </summary>
    public static class BlendShapeStripTest
    {
        private const string TempDir = "Assets/HKOpti_TempTest";

        public static void Run()
        {
            var sb = new StringBuilder();
            try { Execute(sb); }
            catch (Exception e)
            {
                sb.AppendLine($"例外: {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.DeleteAsset(TempDir);
                    AssetDatabase.Refresh();
                }
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
            if (string.IsNullOrEmpty(scenes)) { sb.AppendLine("-hkoptiScenes 未指定"); return; }

            if (!AssetDatabase.IsValidFolder(TempDir))
                AssetDatabase.CreateFolder("Assets", "HKOpti_TempTest");

            foreach (var scenePath in scenes.Split(';'))
            {
                var path = scenePath.Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                var avatars = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                    .Where(a => a != null)
                    .Where(a => string.IsNullOrEmpty(wanted) || a.name == wanted)
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ToList();

                foreach (var a in avatars) Test(a.gameObject, sb);
            }
        }

        private static void Test(GameObject source, StringBuilder sb)
        {
            sb.AppendLine(new string('=', 78));
            sb.AppendLine($"■ {source.name}");
            sb.AppendLine(new string('=', 78));

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source);
                clone.name = source.name + " (HKOpti検証用)";
                clone.SetActive(true);
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(clone);

                // BlendShape をいちばん多く持つメッシュで試す
                var mesh = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Select(r => r != null ? r.sharedMesh : null)
                    .Where(m => m != null && m.blendShapeCount > 0 && m.vertexCount <= 200000)
                    .OrderByDescending(m => m.blendShapeCount)
                    .FirstOrDefault();

                if (mesh == null)
                {
                    sb.AppendLine("  BlendShape を持つメッシュが見つからない");
                    return;
                }

                sb.AppendLine($"  対象メッシュ : {mesh.name}");
                sb.AppendLine($"  頂点数       : {mesh.vertexCount:N0}");
                sb.AppendLine($"  BlendShape   : {mesh.blendShapeCount}");
                sb.AppendLine();

                long before = WriteAndMeasure(mesh, "before");
                var stripped = StripZeroNormals(mesh, out int strippedFrames, out int totalFrames);
                long after = WriteAndMeasure(stripped, "after");

                sb.AppendLine($"  法線/接線がゼロのフレーム : {strippedFrames} / {totalFrames}");
                sb.AppendLine();
                sb.AppendLine($"  書き出しサイズ 変更前 : {Mb(before)}");
                sb.AppendLine($"  書き出しサイズ 変更後 : {Mb(after)}");

                if (before > 0)
                {
                    double cut = (before - after) * 100.0 / before;
                    sb.AppendLine($"  → 差 : {Mb(before - after)}（{cut:F1}%）");
                    sb.AppendLine();
                    if (cut < 1.0)
                    {
                        sb.AppendLine("  【結論】縮まない。");
                        sb.AppendLine("  Unity は BlendShape を固定サイズで持っているため、");
                        sb.AppendLine("  法線・接線がゼロでも空にできない。この機能は作っても無駄。");
                    }
                    else
                    {
                        sb.AppendLine("  【結論】実際に縮む。実装する価値がある。");
                    }
                }

                UnityEngine.Object.DestroyImmediate(stripped);
            }
            catch (Exception e)
            {
                sb.AppendLine($"  !! 失敗: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
            }
            sb.AppendLine();
        }

        /// <summary>法線・接線の差分が全部ゼロの BlendShape フレームを、位置だけにして作り直す。</summary>
        private static Mesh StripZeroNormals(Mesh source, out int stripped, out int total)
        {
            stripped = 0;
            total = 0;

            var copy = UnityEngine.Object.Instantiate(source);
            copy.name = source.name + "_stripped";
            copy.ClearBlendShapes();

            int vc = source.vertexCount;
            var dp = new Vector3[vc];
            var dn = new Vector3[vc];
            var dt = new Vector3[vc];

            for (int i = 0; i < source.blendShapeCount; i++)
            {
                var name = source.GetBlendShapeName(i);
                int frames = source.GetBlendShapeFrameCount(i);
                for (int f = 0; f < frames; f++)
                {
                    total++;
                    float weight = source.GetBlendShapeFrameWeight(i, f);
                    source.GetBlendShapeFrameVertices(i, f, dp, dn, dt);

                    bool zero = AllZero(dn) && AllZero(dt);
                    if (zero)
                    {
                        stripped++;
                        copy.AddBlendShapeFrame(name, weight, dp, null, null);
                    }
                    else
                    {
                        copy.AddBlendShapeFrame(name, weight, dp, dn, dt);
                    }
                }
            }
            return copy;
        }

        private static bool AllZero(Vector3[] v)
        {
            for (int i = 0; i < v.Length; i++)
                if (v[i].sqrMagnitude > 1e-12f) return false;
            return true;
        }

        /// <summary>
        /// .asset として実際に書き出してファイルサイズを測る。
        /// 実行時メモリではなく**シリアライズされた大きさ**を見たいので、この方法しかない。
        /// </summary>
        private static long WriteAndMeasure(Mesh mesh, string label)
        {
            var path = $"{TempDir}/{label}_{Guid.NewGuid():N}.asset";
            var copy = UnityEngine.Object.Instantiate(mesh);
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();

            var full = Path.GetFullPath(path);
            long size = File.Exists(full) ? new FileInfo(full).Length : 0;

            AssetDatabase.DeleteAsset(path);
            return size;
        }

        private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F2} MB";

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
