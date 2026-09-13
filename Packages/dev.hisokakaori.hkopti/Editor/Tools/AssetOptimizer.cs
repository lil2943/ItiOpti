using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using HisokaKaori.HKOpti.Editor.Analysis;
using HisokaKaori.HKOpti.Editor.Core;
namespace HisokaKaori.HKOpti.Editor.Tools
{
    /// <summary>
    /// アセットのインポート設定を直して軽量化する。仕様 12.6「安全な即効枠」。
    ///
    /// 【この処理が扱う範囲】
    /// アバターに依存しない、アセット自体の性質だけを直す：
    ///   ・元画像にアルファが無いのにアルファ付き形式で圧縮されている
    ///   ・中身が単色なのに大きな解像度で持っている
    ///   ・メッシュ / テクスチャの Read/Write が有効になっている
    /// 前二者はどのアバターで使っても答えが同じなので、プロジェクト全体に一度適用してよい。
    /// Read/Write はビルド前処理ツールがCPU側データを読む場合があるため、候補表示だけ行い自動選択しない。
    /// 逆に「解像度が過大」はアバターごとに答えが変わるのでここでは扱わない（仕様 G-1）。
    ///
    /// 【安全策】
    ///   ・適用前に .meta を丸ごとバックアップし、いつでも元に戻せるようにする
    ///   ・Packages 配下（読み取り専用）は触らない
    ///   ・何をどう変えるかを事前に全部表示してから実行する
    /// </summary>
    public static class AssetOptimizer
    {
        /// <summary>Unity のテクスチャ最小サイズ。単色テクスチャはここまで落とす。</summary>
        private const int MinTextureSize = 32;

        /// <summary>Crunch を掛ける最小サイズ。これ未満は効果が無いわりに画質だけ落ちる。</summary>
        private const int CrunchMinimumSize = 256;

        /// <summary>
        /// Crunch の品質（0-100）。低いほど小さくなるが劣化する。
        /// Unity の既定は 50。ここでは画質側に寄せて 75 にしている。
        /// </summary>
        private const int CrunchQuality = 75;

        /// <summary>
        /// Crunch 圧縮を候補に出すか。既定は false。
        ///
        /// ダウンロードサイズにしか効かず、効果は実際にアップロードしないと測れない。
        /// またインポートに非常に時間がかかる（1 枚で数十秒かかることがある）。
        /// </summary>
        public static bool Crunch = false;

        // =================================================================
        // 検出
        // =================================================================

        /// <summary>
        /// 指定したアバター群が使っているアセットを調べ、直せるものを列挙する。
        /// </summary>
        /// <summary>
        /// 解像度削減の積極度。実測（`logs/reports/2026-09-09_texel-density.txt`、59 体）：
        ///
        ///   Safe     2048px/m + 1段余裕 … 133 枚 /  400MB / 13%
        ///   Standard 2048px/m 計算どおり … 331 枚 /  965MB / 31%
        ///   Aggressive 1536px/m          … 449 枚 / 1280MB / 41%
        ///
        /// **既定は Aggressive**（2026-09-09 に変更）。
        ///
        /// 当初は Safe を既定にしていたが、実運用で Aggressive を適用したところ
        /// `Chocolat_listening (1)` のダウンロードサイズが **120MB → 35.71MB（70% 減）**になり、
        /// **ユーザーが目視で劣化なしと判断した**。
        /// Safe（13%）はこのアセット群に対しては保守的すぎたと実測で分かったため既定を上げた。
        ///
        /// 特定のアバターで粗く見えた場合は Standard / Safe に下げて入れ直せる
        /// （バックアップから復元できる）。
        /// </summary>
        public enum ResolutionTier
        {
            Safe,
            Standard,
            Aggressive,
            None,
        }

        public static ResolutionTier Resolution = ResolutionTier.Aggressive;

        public static List<AssetFix> Scan(IEnumerable<GameObject> avatars)
        {
            var fixes = new List<AssetFix>();
            var seenTextures = new HashSet<Texture2D>();
            var seenModels = new HashSet<string>();
            bool canReadPixels = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

            // 解像度は「アバターごとに答えが変わる」ので、走査した全アバターぶんを
            // 合成してから判断する。1 体でも計算できなかったテクスチャは対象外にする。
            var densityMerged = new TexelDensity.Result();
            var densityOptions = OptionsFor(Resolution);

            foreach (var avatar in avatars)
            {
                if (avatar == null) continue;
                var index = ReferenceIndex.Build(avatar);

                if (densityOptions != null)
                    TexelDensity.MergeConservative(
                        densityMerged, TexelDensity.Analyze(index, densityOptions));

                foreach (var tex in index.TextureUsage.Keys)
                {
                    var t2 = tex as Texture2D;
                    if (t2 == null || !seenTextures.Add(t2)) continue;
                    ScanTexture(t2, canReadPixels, fixes);
                }

                // Expression Menu の画像は Renderer / Material から参照されない。
                foreach (var t2 in index.ExpressionMenuTextures)
                {
                    if (t2 == null || !seenTextures.Add(t2)) continue;
                    ScanTexture(t2, canReadPixels, fixes);
                }

                foreach (var smr in index.SkinnedMeshes)
                {
                    var mesh = smr != null ? smr.sharedMesh : null;
                    if (mesh == null || !mesh.isReadable) continue;

                    var path = AssetDatabase.GetAssetPath(mesh);
                    if (string.IsNullOrEmpty(path) || !seenModels.Add(path)) continue;
                    ScanMesh(mesh, path, fixes);
                }
            }

            if (densityOptions != null) AddResolutionFixes(densityMerged, fixes);
            return fixes;
        }

        private static TexelDensity.Options OptionsFor(ResolutionTier tier)
        {
            switch (tier)
            {
                case ResolutionTier.Safe:
                    return new TexelDensity.Options
                    {
                        TargetPixelsPerMeter = 2048f,
                        SafetyDoublings = 1,
                    };
                case ResolutionTier.Standard:
                    return new TexelDensity.Options
                    {
                        TargetPixelsPerMeter = 2048f,
                        SafetyDoublings = 0,
                    };
                case ResolutionTier.Aggressive:
                    return new TexelDensity.Options
                    {
                        TargetPixelsPerMeter = 1536f,
                        SafetyDoublings = 0,
                    };
                default:
                    return null;
            }
        }

        private static void AddResolutionFixes(TexelDensity.Result merged, List<AssetFix> fixes)
        {
            foreach (var kv in TexelDensity.SafeReductions(merged))
            {
                var t2 = kv.Key;
                int recommended = kv.Value;

                var path = AssetDatabase.GetAssetPath(t2);
                if (string.IsNullOrEmpty(path)) continue;
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;

                // 「今どれだけの解像度で読み込まれているか」で判断する。
                // importer の maxTextureSize は platformSettings と食い違うことがあり、
                // これを基準にすると実際には効かない修正を出してしまう（SetMaxTextureSize 参照）。
                int effective = Math.Max(t2.width, t2.height);
                if (effective <= recommended) continue;

                // 単色として既に最小化される予定のものとは重複させない
                if (fixes.Any(f => f.Kind == AssetFixKind.ShrinkSolidColor &&
                                   string.Equals(f.AssetPath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                long cur = AvatarStats.EstimateTextureBytes(t2);
                double ratio = (double)recommended * recommended /
                               ((double)t2.width * t2.height);
                long saved = Math.Max(0, cur - (long)(cur * ratio));

                fixes.Add(Make(AssetFixKind.ReduceTextureResolution, path, t2.name,
                    before: $"{t2.width}x{t2.height}",
                    after: $"max {recommended}",
                    reason: "テクセル密度から見て解像度が過大。" +
                            "走査した全アバター中で最大の要求値に合わせている",
                    saved: saved,
                    selectedByDefault: false));
            }
        }

        /// <summary>
        /// 自己テストや個別診断向けに、指定アセットだけを同じ規則で調べる。
        /// アバターから現在参照されていないアセットも検証できる。
        /// </summary>
        public static List<AssetFix> ScanAssets(IEnumerable<string> assetPaths)
        {
            var fixes = new List<AssetFix>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool canReadPixels = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

            foreach (var path in assetPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null)
                {
                    ScanTexture(texture, canReadPixels, fixes);
                    continue;
                }

                var mesh = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault();
                if (mesh != null) ScanMesh(mesh, path, fixes);
            }

            return fixes;
        }

        private static void ScanTexture(Texture2D t2, bool canReadPixels, List<AssetFix> fixes)
        {
            var path = AssetDatabase.GetAssetPath(t2);
            if (string.IsNullOrEmpty(path)) return;

            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;

            long current = AvatarStats.EstimateTextureBytes(t2);

            ScanCrunch(t2, imp, path, fixes);

            if (imp.isReadable)
            {
                fixes.Add(Make(AssetFixKind.DisableTextureReadWrite, path, t2.name,
                    before: "Read/Write 有効",
                    after: "Read/Write 無効",
                    reason: "CPU側の画素コピーが不要になる。ビルド前処理ツールが読む場合は除外が必要",
                    saved: current,
                    selectedByDefault: false));
            }

            // --- 単色かどうか（画素を実際に見る） ---
            if (canReadPixels && Math.Max(t2.width, t2.height) > MinTextureSize)
            {
                Color32[] px = null;
                try { px = SamplePixels(t2, 16); } catch (Exception) { }

                if (px != null && px.Length > 0 && IsSolidColor(px) &&
                    IsTextureExactlySolid(t2))
                {
                    fixes.Add(Make(AssetFixKind.ShrinkSolidColor, path, t2.name,
                        before: $"{t2.width}x{t2.height}",
                        after: $"最大 {MinTextureSize}x{MinTextureSize}",
                        reason: "全画素が同じ色。縮小しても見た目は変わらない",
                        saved: Math.Max(0, current - 4096)));
                    return; // 単色なら解像度を落とすので、アルファの話は不要
                }
            }

            // --- アルファが不要かどうか ---
            if (imp.textureType == TextureImporterType.NormalMap) return;

            bool formatHasAlpha;
            switch (t2.format)
            {
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    formatHasAlpha = true;
                    break;
                default:
                    formatHasAlpha = false;
                    break;
            }
            if (!formatHasAlpha) return;
            if (imp.alphaSource == TextureImporterAlphaSource.None) return;
            if (imp.DoesSourceTextureHaveAlpha()) return;

            fixes.Add(Make(AssetFixKind.DropUnusedAlpha, path, t2.name,
                before: $"{t2.format} ({t2.width}x{t2.height})",
                after: "アルファ無し形式（DXT1 相当）",
                reason: "元画像にアルファチャンネルが無い。捨てても情報は失われない",
                saved: current / 2));
        }

        /// <summary>
        /// Crunch 圧縮の候補かどうかを見る。
        ///
        /// 【この項目だけ扱いが違う】
        /// VRAM は減らない。減るのは配信されるファイルだけ。
        /// 効果は実際にアップロードしないと測れないので、**バイト数を出さない。**
        /// 見積もりを出すと必ず「実際より良い」数字になる（地雷 3.6）。
        ///
        /// 既定では選択しない。画質が落ちるうえ、インポートに非常に時間がかかるため。
        /// </summary>
        private static void ScanCrunch(Texture2D t2, TextureImporter imp, string path,
            List<AssetFix> fixes)
        {
            if (!Crunch) return;
            if (imp.crunchedCompression) return;

            // 法線マップは Crunch すると陰影に目立つノイズが出る。対象外にする。
            if (imp.textureType == TextureImporterType.NormalMap) return;

            // 小さいものは効果が無いわりに画質だけ落ちる
            if (Math.Max(t2.width, t2.height) < CrunchMinimumSize) return;

            // Crunch できるのは DXT 系だけ。BC7 のままでは無視される。
            switch (t2.format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                    break;
                default:
                    return;
            }

            fixes.Add(Make(AssetFixKind.EnableCrunchCompression, path, t2.name,
                before: $"{t2.format} ({t2.width}x{t2.height})",
                after: $"{t2.format}Crunched（品質 {CrunchQuality}）",
                reason: "ダウンロードサイズのみ縮む。VRAM は変わらない。" +
                        "画質がわずかに落ちるので、適用後は目視確認すること",
                saved: 0,
                selectedByDefault: false,
                downloadOnly: true));
        }

        private static void ScanMesh(Mesh mesh, string path, List<AssetFix> fixes)
        {
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null || !imp.isReadable) return;

            fixes.Add(Make(AssetFixKind.DisableReadWrite, path, mesh.name,
                before: "Read/Write 有効",
                after: "Read/Write 無効",
                reason: "CPU 側のコピーを外せる可能性がある。ビルド前処理ツールが読む場合は除外が必要",
                saved: (long)mesh.vertexCount * 64,
                selectedByDefault: false));
        }

        private static AssetFix Make(AssetFixKind kind, string path, string name,
            string before, string after, string reason, long saved,
            bool selectedByDefault = true, bool downloadOnly = false)
        {
            bool editable = path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            return new AssetFix
            {
                DownloadOnly = downloadOnly,
                Kind = kind,
                AssetPath = path,
                ObjectName = name,
                Before = before,
                After = after,
                Reason = reason,
                SavedBytes = saved,
                Applicable = editable,
                NotApplicableReason = editable ? null : "Packages 配下は読み取り専用のため変更できません",
                Selected = editable && selectedByDefault,
            };
        }

        // =================================================================
        // 適用
        // =================================================================

        /// <summary>
        /// 選択された修正を適用する。適用前に必ず .meta をバックアップする。
        /// </summary>
        /// <returns>バックアップフォルダのパス。何も適用しなかった場合は null。</returns>
        public static string Apply(List<AssetFix> fixes, string backupRoot,
            IEnumerable<GameObject> avatars = null)
        {
            var targets = fixes.Where(f => f.Selected && f.Applicable).ToList();
            if (targets.Count == 0) return null;

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var backupDir = UniqueBackupDirectory(backupRoot, stamp);
            Directory.CreateDirectory(backupDir);
            var manifest = new BackupManifest
            {
                CreatedAt = stamp,
                UnityVersion = Application.unityVersion,
            };

            foreach (var avatar in avatars ?? Enumerable.Empty<GameObject>())
            {
                if (avatar == null) continue;
                var id = GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();
                if (manifest.Avatars.Any(a => a.GlobalObjectId == id)) continue;
                manifest.Avatars.Add(new BackupAvatar
                {
                    Name = avatar.name,
                    GlobalObjectId = id,
                    ScenePath = avatar.scene.path,
                    HierarchyPath = HierarchyPathOf(avatar.transform),
                });
            }

            // --- 1) 先に全部バックアップする ---
            // 途中で失敗しても、それまでの変更を戻せるようにするため
            // 「バックアップ → 変更」ではなく「全部バックアップ → 全部変更」の順にする。
            foreach (var group in targets.GroupBy(f => f.AssetPath, StringComparer.OrdinalIgnoreCase))
            {
                var f = group.First();
                var metaPath = f.AssetPath + ".meta";
                if (!File.Exists(metaPath))
                {
                    Debug.LogWarning($"[ItiOptimiser] .metaが見つからないので飛ばします: {metaPath}");
                    continue;
                }

                var rel = f.AssetPath.Replace('/', '_').Replace('\\', '_') + ".meta";
                var dst = Path.Combine(backupDir, rel);
                File.Copy(metaPath, dst, true);

                manifest.Entries.Add(new BackupEntry
                {
                    AssetPath = f.AssetPath,
                    MetaBackup = rel,
                    Kind = string.Join(",", group.Select(x => x.Kind.ToString())),
                    Before = string.Join(" / ", group.Select(x => x.Before)),
                    After = string.Join(" / ", group.Select(x => x.After)),
                });
            }

            WriteManifest(backupDir, manifest);
            File.WriteAllText(Path.Combine(backupDir, "README.txt"),
                "ItiOptimiser がインポート設定を変更する前に取ったバックアップです。\n" +
                "元に戻すには Unity で Tools > ItiOptimiser を開き、\n" +
                "「変更履歴」タブから対象アバターを選んでください。\n\n" +
                $"作成日時: {stamp}\nアバター: " +
                $"{string.Join(", ", manifest.Avatars.Select(a => a.Name))}\n" +
                $"対象: {manifest.Entries.Count} 件\n");

            // --- 2) 変更を適用する ---
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var f in targets)
                {
                    if (ApplyOne(f)) changed.Add(f.AssetPath);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var p in changed)
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);

            AssetDatabase.Refresh();
            Debug.Log($"[ItiOptimiser] {changed.Count} 件のアセットを処理しました。" +
                      $"バックアップ: {backupDir}");
            return backupDir;
        }

        private static bool ApplyOne(AssetFix f)
        {
            var imp = AssetImporter.GetAtPath(f.AssetPath);
            if (imp == null) return false;

            switch (f.Kind)
            {
                case AssetFixKind.DropUnusedAlpha:
                {
                    if (!(imp is TextureImporter ti)) return false;

                    // 元画像にアルファが無いので、読み込み自体を「アルファ無し」にする
                    ti.alphaSource = TextureImporterAlphaSource.None;
                    ti.alphaIsTransparency = false;

                    // Automatic (High Quality) はアルファが無くても Standalone で BC7 を
                    // 選び続けるため、override が無い場合も不透明向け形式を明示する。
                    SetOpaquePlatformFormat(ti, "Standalone", TextureImporterFormat.DXT1);
                    SetOpaquePlatformFormat(ti, "Android", TextureImporterFormat.ETC2_RGB4);
                    return true;
                }

                case AssetFixKind.ShrinkSolidColor:
                {
                    if (!(imp is TextureImporter ti2)) return false;
                    return SetMaxTextureSize(ti2, MinTextureSize);
                }

                case AssetFixKind.ReduceTextureResolution:
                {
                    if (!(imp is TextureImporter ti3)) return false;

                    // "max 2048" のように後ろに数値が入っている
                    var token = f.After.Split(' ').LastOrDefault();
                    if (!int.TryParse(token, out var target)) return false;
                    if (target <= 0) return false;

                    return SetMaxTextureSize(ti3, target);
                }

                case AssetFixKind.DisableReadWrite:
                {
                    if (!(imp is ModelImporter mi)) return false;
                    mi.isReadable = false;
                    return true;
                }

                case AssetFixKind.DisableTextureReadWrite:
                {
                    if (!(imp is TextureImporter ti3)) return false;
                    ti3.isReadable = false;
                    return true;
                }

                case AssetFixKind.EnableCrunchCompression:
                {
                    if (!(imp is TextureImporter ti4)) return false;
                    return SetCrunchCompression(ti4);
                }

                default:
                    return false;
            }
        }

        private static void SetOpaquePlatformFormat(
            TextureImporter importer, string platform, TextureImporterFormat format)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.name = platform;
            settings.overridden = true;

            // Crunch が要求されているなら、override を作る時点で Crunch 版の形式にする。
            //
            // 【なぜ必要か】
            // ここで素の DXT1 を入れると、先に立てた crunchedCompression が打ち消され、
            // **設定は書いたのに実形式は非 Crunch** になる。
            // 実測で再現済み（logs/reports/2026-09-13_crunch-combined.txt）：
            //   DXT5 2048 → DXT1 1024（DXT1Crunched にならない）
            // 同じテクスチャが「アルファ不要」と「Crunch」の両方に該当すると必ず起きる。
            //
            // ScanTexture は Crunch の候補を先に積むので、ここに来る時点で
            // importer.crunchedCompression は既に立っている。
            if (importer.crunchedCompression)
            {
                settings.crunchedCompression = true;
                settings.compressionQuality = importer.compressionQuality;
                format = CrunchedVariantOf(format);
            }

            settings.format = format;
            // override を新設すると platform 側の既定値（例: 2048）が共通設定より
            // 大きい場合がある。形式変更で実解像度を逆に増やしてはいけない。
            settings.maxTextureSize = Math.Min(settings.maxTextureSize, importer.maxTextureSize);
            importer.SetPlatformTextureSettings(settings);
        }

        /// <summary>
        /// 圧縮形式を、対応する Crunch 版に読み替える。無ければそのまま返す。
        ///
        /// ETC2_RGB4 に Crunch 版は存在しない（Unity 2022.3 で確認）。
        /// ETC2_RGBA8Crunched はあるがアルファ付きなので、
        /// 不透明化した直後にこれへ替えると逆に太る。Android は素の形式のままにする。
        /// </summary>
        private static TextureImporterFormat CrunchedVariantOf(TextureImporterFormat format)
        {
            switch (format)
            {
                case TextureImporterFormat.DXT1: return TextureImporterFormat.DXT1Crunched;
                case TextureImporterFormat.DXT5: return TextureImporterFormat.DXT5Crunched;
                case TextureImporterFormat.ETC_RGB4: return TextureImporterFormat.ETC_RGB4Crunched;
                default: return format;
            }
        }

        private static bool SetCrunchCompression(TextureImporter importer)
        {
            bool changed = false;
            if (!importer.crunchedCompression)
            {
                importer.crunchedCompression = true;
                changed = true;
            }
            if (importer.compressionQuality != CrunchQuality)
            {
                importer.compressionQuality = CrunchQuality;
                changed = true;
            }

            var defaultSettings = importer.GetDefaultPlatformTextureSettings();
            if (defaultSettings != null && ApplyCrunchTo(defaultSettings))
            {
                importer.SetPlatformTextureSettings(defaultSettings);
                changed = true;
            }

            // overridden なプラットフォームすべてに適用する。
            //
            // 【フラグを立てるだけでは足りない】
            // 「アルファ不要」が先に走ると、その override には形式が DXT1 と
            // **明示**されている。crunchedCompression だけ true にしても
            // 明示された形式が優先され、実形式は非 Crunch のままになる。
            // 実測で再現（logs/reports/2026-09-13_crunch-combined.txt）：
            //   DXT5 2048 → DXT1 1024（DXT1Crunched にならない）
            // なので形式そのものを Crunch 版へ読み替える。
            //
            // 名前の列挙だけ SerializedObject で行い（将来のプラットフォームも拾うため）、
            // 書き換えは公開 API で行う。内部フィールド名に依存した書き込みを避ける。
            foreach (var platform in OverriddenPlatformNames(importer))
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                if (settings == null || !settings.overridden) continue;
                if (!ApplyCrunchTo(settings)) continue;
                importer.SetPlatformTextureSettings(settings);
                changed = true;
            }

            return changed;
        }

        /// <summary>Crunch を有効にし、形式が明示されていれば Crunch 版へ読み替える。</summary>
        private static bool ApplyCrunchTo(TextureImporterPlatformSettings settings)
        {
            bool changed = false;
            if (!settings.crunchedCompression)
            {
                settings.crunchedCompression = true;
                changed = true;
            }
            if (settings.compressionQuality != CrunchQuality)
            {
                settings.compressionQuality = CrunchQuality;
                changed = true;
            }
            var crunchedFormat = CrunchedVariantOf(settings.format);
            if (crunchedFormat != settings.format)
            {
                settings.format = crunchedFormat;
                changed = true;
            }
            return changed;
        }

        /// <summary>override が設定されているプラットフォーム名を列挙する。</summary>
        private static List<string> OverriddenPlatformNames(TextureImporter importer)
        {
            var names = new List<string>();
            var serialized = new SerializedObject(importer);
            var platformSettings = serialized.FindProperty("m_PlatformSettings") ??
                                   serialized.FindProperty("platformSettings");
            if (platformSettings == null || !platformSettings.isArray) return names;

            for (int i = 0; i < platformSettings.arraySize; i++)
            {
                var item = platformSettings.GetArrayElementAtIndex(i);
                var overridden = item.FindPropertyRelative("m_Overridden") ??
                                 item.FindPropertyRelative("overridden");
                var buildTarget = item.FindPropertyRelative("m_BuildTarget") ??
                                  item.FindPropertyRelative("buildTarget");
                if (overridden == null || buildTarget == null || !overridden.boolValue) continue;
                var name = buildTarget.stringValue;
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }

        // =================================================================
        // 復元
        // =================================================================

        /// <summary>バックアップフォルダから .meta を書き戻す。</summary>
        public static int Restore(string backupDir)
        {
            var manifest = ReadManifest(backupDir);
            if (manifest == null)
            {
                Debug.LogError($"[ItiOptimiser] 有効なmanifestがありません: {backupDir}");
                return 0;
            }
            if (manifest?.Entries == null) return 0;

            int restored = 0;
            var paths = new List<string>();

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var e in manifest.Entries)
                {
                    if (!TryResolveRestorePaths(backupDir, e, out var src, out var dst))
                    {
                        Debug.LogWarning("[ItiOptimiser] 安全でない復元記録を飛ばします。");
                        continue;
                    }
                    if (!File.Exists(src)) continue;
                    if (!File.Exists(dst))
                    {
                        Debug.LogWarning($"[ItiOptimiser] 復元先が見つかりません（アセットが移動/削除された？）: {dst}");
                        continue;
                    }
                    File.Copy(src, dst, true);
                    paths.Add(e.AssetPath);
                    restored++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var p in paths)
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);

            AssetDatabase.Refresh();
            Debug.Log($"[ItiOptimiser] {restored} 件を元に戻しました。");
            return restored;
        }

        /// <summary>
        /// 改変・破損したmanifestによるディレクトリ外への書き込みを防ぐ。
        /// バックアップは指定フォルダ直下、復元先は現在のUnityプロジェクトのAssets配下に限定する。
        /// </summary>
        internal static bool TryResolveRestorePaths(string backupDir, BackupEntry entry,
            out string source, out string destination)
        {
            source = null;
            destination = null;
            if (string.IsNullOrWhiteSpace(backupDir) || entry == null ||
                string.IsNullOrWhiteSpace(entry.MetaBackup) ||
                string.IsNullOrWhiteSpace(entry.AssetPath)) return false;

            try
            {
                var backupFull = Path.GetFullPath(backupDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!string.Equals(entry.MetaBackup, Path.GetFileName(entry.MetaBackup),
                        StringComparison.Ordinal)) return false;
                var sourceFull = Path.GetFullPath(Path.Combine(backupFull, entry.MetaBackup));
                if (!sourceFull.StartsWith(backupFull, StringComparison.OrdinalIgnoreCase))
                    return false;

                var normalizedAsset = entry.AssetPath.Replace('\\', '/');
                if (!normalizedAsset.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    Path.IsPathRooted(entry.AssetPath)) return false;

                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot)) return false;
                var assetsFull = Path.GetFullPath(Application.dataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                var destinationFull = Path.GetFullPath(
                    Path.Combine(projectRoot, normalizedAsset.Replace('/', Path.DirectorySeparatorChar)) +
                    ".meta");
                if (!destinationFull.StartsWith(assetsFull, StringComparison.OrdinalIgnoreCase))
                    return false;

                source = sourceFull;
                destination = destinationFull;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>主manifestが壊れていても予備から読み込む。</summary>
        public static BackupManifest ReadManifest(string backupDir)
        {
            foreach (var name in new[] { "manifest.json", "manifest.backup.json" })
            {
                var path = Path.Combine(backupDir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    var manifest = JsonUtility.FromJson<BackupManifest>(File.ReadAllText(path));
                    if (manifest != null && manifest.Entries != null) return manifest;
                }
                catch (Exception) { /* 次の予備manifestを試す */ }
            }
            return null;
        }

        private static void WriteManifest(string backupDir, BackupManifest manifest)
        {
            var json = JsonUtility.ToJson(manifest, true);
            File.WriteAllText(Path.Combine(backupDir, "manifest.json"), json);
            File.WriteAllText(Path.Combine(backupDir, "manifest.backup.json"), json);
        }

        private static string UniqueBackupDirectory(string root, string stamp)
        {
            var candidate = Path.Combine(root, stamp);
            int suffix = 2;
            while (Directory.Exists(candidate))
                candidate = Path.Combine(root, stamp + "_" + suffix++);
            return candidate;
        }

        private static string HierarchyPathOf(Transform transform)
        {
            var parts = new List<string>();
            while (transform != null)
            {
                parts.Add(transform.name);
                transform = transform.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // =================================================================

        private static Color32[] SamplePixels(Texture tex, int size)
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

        /// <summary>
        /// テクスチャの最大解像度を、実際に効く形で設定する。
        ///
        /// 【一度これで失敗している。実運用で判明した】
        /// `TextureImporter.maxTextureSize` に代入するだけでは足りない。
        /// `.meta` の `platformSettings` に `DefaultTexturePlatform` の項目があると、
        /// Unity はそちらの `maxTextureSize` を使う。
        /// 実アセットで legacy フィールドが 2048、DefaultTexturePlatform が 4096 のまま
        /// という状態になり、**解像度がまったく下がっていなかった**
        /// （ユーザーが Aggressive で適用してもダウンロードサイズが 120→111MB しか減らなかった原因）。
        ///
        /// なので次の 3 つをまとめて下げる。上げることは絶対にしない。
        ///   1. 既定プラットフォーム設定の maxTextureSize ← これが本命
        ///   2. legacy の maxTextureSize
        ///   3. 有効化されている個別プラットフォーム設定（上限を超えているものだけ）
        /// </summary>
        private static bool SetMaxTextureSize(TextureImporter ti, int target)
        {
            if (ti == null || target <= 0) return false;

            bool changed = false;

            var def = ti.GetDefaultPlatformTextureSettings();
            if (def != null && def.maxTextureSize > target)
            {
                def.maxTextureSize = target;
                ti.SetPlatformTextureSettings(def);
                changed = true;
            }

            if (ti.maxTextureSize > target)
            {
                ti.maxTextureSize = target;
                changed = true;
            }

            // 個別プラットフォームが有効化されていて上限より大きいものも下げる。
            // 有効化されていない（overridden = false）ものは Unity が見ないので触らない。
            foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
            {
                var ps = ti.GetPlatformTextureSettings(platform);
                if (ps == null || !ps.overridden) continue;
                if (ps.maxTextureSize <= target) continue;

                ps.maxTextureSize = target;
                ti.SetPlatformTextureSettings(ps);
                changed = true;
            }

            return changed;
        }

        private static bool IsTextureExactlySolid(Texture tex)
        {
            var prev = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tmp = null;
            try
            {
                rt = RenderTexture.GetTemporary(tex.width, tex.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                tmp.Apply(false);
                return IsSolidColor(tmp.GetPixels32());
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

        private static bool IsSolidColor(Color32[] px)
        {
            if (px == null || px.Length == 0) return false;
            var c = px[0];
            foreach (var p in px)
            {
                if (p.r != c.r || p.g != c.g || p.b != c.b || p.a != c.a) return false;
            }
            return true;
        }
    }
}
