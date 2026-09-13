using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using HisokaKaori.HKOpti.Editor.Core;
using HisokaKaori.HKOpti.Editor.Tools;

namespace HisokaKaori.HKOpti.Editor.UI
{
    /// <summary>
    /// アセット最適化ウィンドウ。仕様 12.6「安全な即効枠」。
    ///
    /// 何をどう変えるかを全部見せてから実行する。バックアップは必須。
    /// </summary>
    public sealed class AssetOptimizerWindow : EditorWindow
    {
        private enum Scope { SelectedAvatar, AllInScene }
        private enum MainTab { Settings, History }

        private MainTab _tab;
        private Scope _scope = Scope.SelectedAvatar;
        private bool _crunch;
        private bool _blockedByConflict;
        private GameObject _avatar;
        private List<AssetFix> _fixes;
        private Vector2 _settingsScroll;
        private Vector2 _historyScroll;
        private string _lastBackup;
        private GameObject _historyAvatar;
        private bool _showAllHistory;
        private readonly List<GameObject> _scannedTargets = new List<GameObject>();
        private List<BackupRecord> _historyRecords;

        private readonly Dictionary<AssetFixKind, bool> _expanded =
            new Dictionary<AssetFixKind, bool>
            {
                { AssetFixKind.DropUnusedAlpha, true },
                { AssetFixKind.ShrinkSolidColor, false },
                { AssetFixKind.DisableReadWrite, false },
                { AssetFixKind.DisableTextureReadWrite, false },
                { AssetFixKind.ReduceTextureResolution, false },
                { AssetFixKind.EnableCrunchCompression, false },
            };

        private AssetOptimizer.ResolutionTier _resolutionTier = AssetOptimizer.ResolutionTier.Aggressive;
        private int _scannedAvatars;

        /// <summary>
        /// タブ名はウィンドウが表示されるたびに付け直す。
        ///
        /// Unity はタブ名を配置情報（レイアウト）ごと保存して復元する。
        /// GetWindow の引数で名前を付けるだけだと、**改名前に開いていたウィンドウは
        /// 古い名前（HKOpti アセット最適化）のまま復元される**（実機で確認）。
        /// </summary>
        private void OnEnable()
        {
            titleContent = new GUIContent("ItiOptimiser");
        }

        [MenuItem("Tools/ItiOptimiser/開く", false, 0)]
        public static void Open()
        {
            var w = GetWindow<AssetOptimizerWindow>("ItiOptimiser");
            w.minSize = new Vector2(620, 460);
            w.Show();
        }

        private void OnGUI()
        {
            _tab = (MainTab)GUILayout.Toolbar((int)_tab,
                new[] { "設定・実行", "変更履歴" }, GUILayout.Height(24));
            EditorGUILayout.Space(4);

            if (_tab == MainTab.Settings) DrawSettingsTab();
            else DrawHistoryTab();
        }

        private void DrawSettingsTab()
        {
            _settingsScroll = EditorGUILayout.BeginScrollView(_settingsScroll);

            EditorGUILayout.LabelField("ItiOptimiser", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "アセット自体の性質だけを直します（元画像にアルファが無い、中身が単色、など）。\n" +
                "見た目を変えない候補だけを示しますが、Read/Write は他のビルドツールとの互換性確認が必要です。\n" +
                "実行前に .meta をバックアップし、いつでも元に戻せます。",
                MessageType.Info);

            DrawTargetSelection();
            DrawResolutionSetting();
            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(_scope == Scope.SelectedAvatar && _avatar == null))
            {
                if (GUILayout.Button("調べる", GUILayout.Height(26))) Scan();
            }

            if (_fixes != null)
            {
                EditorGUILayout.Space(6);
                _blockedByConflict = DrawConflicts();
                DrawSummary();
                DrawList();
                EditorGUILayout.Space(6);
                DrawActions();
            }

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------

        private void DrawTargetSelection()
        {
            _scope = (Scope)EditorGUILayout.EnumPopup("対象", _scope);

            if (_scope == Scope.SelectedAvatar)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _avatar = (GameObject)EditorGUILayout.ObjectField(
                        "アバター", _avatar, typeof(GameObject), true);
                    if (GUILayout.Button("選択中", GUILayout.Width(60)) &&
                        Selection.activeGameObject != null)
                    {
                        _avatar = Selection.activeGameObject;
                    }
                }
            }
            else
            {
                EditorGUILayout.LabelField(
                    "　開いているシーン内の全アバターが使っているアセットを対象にします。",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawResolutionSetting()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("解像度の削減", EditorStyles.boldLabel);

            _resolutionTier = (AssetOptimizer.ResolutionTier)EditorGUILayout.EnumPopup(
                "積極度", _resolutionTier);

            string measured;
            switch (_resolutionTier)
            {
                case AssetOptimizer.ResolutionTier.Safe:
                    measured = "いちばん安全。必要な解像度を計算し、さらに 1 段分の余裕を残します。" +
                               "削減量は小さめです";
                    break;
                case AssetOptimizer.ResolutionTier.Standard:
                    measured = "必要な解像度どおりに落とします。余裕を持たせない分よく減ります";
                    break;
                case AssetOptimizer.ResolutionTier.Aggressive:
                    measured = "既定。必要な解像度より一段踏み込んで落とします。" +
                               "作者の環境では 120MB → 35.7MB になり、目視で劣化は確認されませんでした";
                    break;
                default:
                    measured = "解像度の削減を行いません";
                    break;
            }
            EditorGUILayout.LabelField("　" + measured, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "　※ どれだけ減るかはアセット次第です。「調べる」を押すと実際の件数が出ます。",
                EditorStyles.miniLabel);

            if (_resolutionTier != AssetOptimizer.ResolutionTier.None)
            {
                EditorGUILayout.HelpBox(
                    "この項目だけ性質が違います。\n" +
                    "他の項目（アルファ不要・単色）は「アセット自体の性質」なのでどのアバターでも答えが同じですが、" +
                    "必要な解像度は貼り方によってアバターごとに変わります。\n\n" +
                    "推奨値は『今回スキャンしたアバター全体で最大の要求値』です。" +
                    "同じテクスチャを別のシーンのアバターでもっと大きく使っている場合、そちらで粗くなる可能性があります。\n" +
                    "対象は「シーン内の全アバター」にして、結果は必ず目視で確認してください。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("ダウンロードサイズだけを減らす", EditorStyles.boldLabel);

            _crunch = EditorGUILayout.ToggleLeft(
                "Crunch 圧縮も候補に出す", _crunch);

            if (_crunch)
            {
                EditorGUILayout.HelpBox(
                    "Crunch はダウンロードサイズだけを縮めます。実行時のメモリ（VRAM）は変わりません。\n\n" +
                    "・効果は実際にアップロードして確かめるしかないため、削減見込みの数値は出しません\n" +
                    "・画質がわずかに落ちます。適用後は必ず目視で確認してください\n" +
                    "・インポートに非常に時間がかかります（枚数によっては数十分）\n" +
                    "・法線マップは陰影にノイズが出るため対象外にしています",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// 同種の最適化ツールが入っていないかを確かめる（仕様 6.6）。
        ///
        /// 解析ウィンドウでは以前から見ていたが、こちらは見ていなかった。
        /// **実際にアセットを書き換えるのはこちらなので、むしろこちらの方が重要。**
        /// </summary>
        private bool DrawConflicts()
        {
            var targets = new List<GameObject>();
            if (_scope == Scope.SelectedAvatar)
            {
                if (_avatar != null) targets.Add(_avatar);
            }
            else
            {
                foreach (var d in FindObjectsOfType<VRCAvatarDescriptor>(true))
                    if (d != null) targets.Add(d.gameObject);
            }

            bool blocked = false;
            var shown = new HashSet<string>();

            foreach (var t in targets)
            {
                // インポート設定の最適化と重なるツールだけを見る。
                // 以前は全機能で判定していたため、AAO が付いているだけで適用ボタンが
                // 押せなくなっていた（AAO はインポート設定に一切触れない）。
                foreach (var c in ToolConflictDetector.Detect(t, OptimizerFeature.ImportSettings))
                {
                    if (!shown.Add(c.ToolName + "|" + c.OwnerPath)) continue;
                    bool hard = c.Severity == ConflictSeverity.Blocking;
                    if (hard) blocked = true;

                    EditorGUILayout.HelpBox(
                        (hard ? $"【併用不可】{c.ToolName} が使われています"
                              : $"【注意】{c.ToolName} が使われています") +
                        $"\n{c.Message}\n場所: {c.OwnerPath}",
                        hard ? MessageType.Error : MessageType.Warning);
                }
            }

            if (blocked)
            {
                EditorGUILayout.HelpBox(
                    "併用できないツールが見つかったため、適用はできません。\n" +
                    "同じアセットを両方が書き換えると、どちらの設定が残るか分からなくなります。",
                    MessageType.Error);
            }
            return blocked;
        }

        private void Scan()
        {
            var avatars = new List<GameObject>();

            if (_scope == Scope.SelectedAvatar)
            {
                if (_avatar != null) avatars.Add(_avatar);
            }
            else
            {
                foreach (var d in FindObjectsOfType<VRCAvatarDescriptor>(true))
                {
                    if (d != null) avatars.Add(d.gameObject);
                }
            }

            if (avatars.Count == 0)
            {
                EditorUtility.DisplayDialog("ItiOptimiser",
                    "対象のアバターが見つかりませんでした。", "OK");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("ItiOptimiser", "アセットを調べています...", 0.5f);
                AssetOptimizer.Resolution = _resolutionTier;
                AssetOptimizer.Crunch = _crunch;
                _fixes = AssetOptimizer.Scan(avatars);
                _scannedAvatars = avatars.Count;
                _scannedTargets.Clear();
                _scannedTargets.AddRange(avatars);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // ------------------------------------------------------------------

        private void DrawSummary()
        {
            var selected = _fixes.Where(f => f.Selected && f.Applicable).ToList();

            // ダウンロードサイズにしか効かない項目を混ぜて合計してはいけない。
            // 混ぜると「実際より良い」数字になる（削減率 108% を出した過去の失敗）。
            long saved = selected.Where(f => !f.DownloadOnly).Sum(f => f.SavedBytes);
            int downloadOnly = selected.Count(f => f.DownloadOnly);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    $"見つかった項目: {_fixes.Count} 件　" +
                    $"選択中: {selected.Count} 件　" +
                    $"削減見込み: {saved / (1024.0 * 1024.0):F1} MB",
                    EditorStyles.boldLabel);

                if (downloadOnly > 0)
                {
                    EditorGUILayout.LabelField(
                        $"　※ うち {downloadOnly} 件は Crunch 圧縮です。" +
                        "ダウンロードサイズだけが縮むため、上の削減見込みには含めていません。",
                        EditorStyles.miniLabel);
                }

                var skipped = _fixes.Count(f => !f.Applicable);
                if (skipped > 0)
                {
                    EditorGUILayout.LabelField(
                        $"　※ {skipped} 件は Packages 配下のため変更できません（対象外）",
                        EditorStyles.miniLabel);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("安全項目を選択", GUILayout.Width(110)))
                    foreach (var f in _fixes)
                        f.Selected = f.Applicable && !IsConditional(f.Kind);
                if (GUILayout.Button("すべて解除", GUILayout.Width(90)))
                    foreach (var f in _fixes) f.Selected = false;
            }
        }

        private void DrawList()
        {
            foreach (AssetFixKind kind in System.Enum.GetValues(typeof(AssetFixKind)))
            {
                var group = _fixes.Where(f => f.Kind == kind).ToList();
                if (group.Count == 0) continue;

                long saved = group.Where(f => f.Selected && f.Applicable).Sum(f => f.SavedBytes);
                _expanded.TryGetValue(kind, out var open);

                var header = $"{group[0].KindLabel}  ({group.Count} 件, " +
                             $"{saved / (1024.0 * 1024.0):F1} MB)";
                open = EditorGUILayout.Foldout(open, header, true);
                _expanded[kind] = open;

                if (!open) continue;

                EditorGUILayout.LabelField("　　" + group[0].Reason, EditorStyles.miniLabel);
                if (IsConditional(kind))
                    EditorGUILayout.HelpBox(ConditionalWarning(kind), MessageType.Warning);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(16);
                    if (GUILayout.Button("この分類をすべて選択", GUILayout.Width(150)))
                        foreach (var f in group) f.Selected = f.Applicable;
                    if (GUILayout.Button("この分類をすべて解除", GUILayout.Width(150)))
                        foreach (var f in group) f.Selected = false;
                }

                foreach (var f in group.OrderByDescending(f => f.SavedBytes).Take(200))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(16);

                        using (new EditorGUI.DisabledScope(!f.Applicable))
                        {
                            f.Selected = EditorGUILayout.Toggle(f.Selected, GUILayout.Width(18));
                        }

                        if (GUILayout.Button("→", GUILayout.Width(22)))
                        {
                            var obj = AssetDatabase.LoadAssetAtPath<Object>(f.AssetPath);
                            if (obj != null) EditorGUIUtility.PingObject(obj);
                        }

                        EditorGUILayout.LabelField(f.ObjectName, GUILayout.Width(200));
                        EditorGUILayout.LabelField(
                            f.SavedBytes > 0 ? $"{f.SavedBytes / (1024.0 * 1024.0):F1} MB" : "",
                            GUILayout.Width(70));
                        EditorGUILayout.LabelField($"{f.Before} → {f.After}", EditorStyles.miniLabel);
                    }

                    if (!f.Applicable)
                    {
                        EditorGUILayout.LabelField("　　　　" + f.NotApplicableReason,
                            EditorStyles.miniLabel);
                    }
                }

                if (group.Count > 200)
                    EditorGUILayout.LabelField($"　　… 他 {group.Count - 200} 件", EditorStyles.miniLabel);
            }
        }

        private void DrawActions()
        {
            var selected = _fixes.Count(f => f.Selected && f.Applicable);

            using (new EditorGUI.DisabledScope(selected == 0 || _blockedByConflict))
            {
                var bg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.6f, 0.85f, 0.6f);
                if (GUILayout.Button($"バックアップして適用する（{selected} 件）", GUILayout.Height(30)))
                {
                    Apply();
                }
                GUI.backgroundColor = bg;
            }

            EditorGUILayout.LabelField(
                "　適用するとアセットのインポート設定が変わり、Unity が再インポートします。",
                EditorStyles.miniLabel);
        }

        /// <summary>
        /// 自動選択しない分類。「見た目が変わらないと機械的に証明できない」ものはここに入れる。
        /// </summary>
        private static bool IsConditional(AssetFixKind kind)
        {
            return kind == AssetFixKind.DisableReadWrite ||
                   kind == AssetFixKind.DisableTextureReadWrite ||
                   kind == AssetFixKind.ReduceTextureResolution ||
                   kind == AssetFixKind.EnableCrunchCompression;
        }

        private static string ConditionalWarning(AssetFixKind kind)
        {
            if (kind == AssetFixKind.EnableCrunchCompression)
            {
                return "ダウンロードサイズだけが縮み、実行時のメモリは変わりません。" +
                       "画質がわずかに落ちるので、適用後は必ず目視で確認してください。" +
                       "インポートに非常に時間がかかります。";
            }

            if (kind == AssetFixKind.ReduceTextureResolution)
            {
                return "推奨値は『今回スキャンしたアバター全体で最大の要求値』です。" +
                       "別のシーンのアバターが同じテクスチャをもっと大きく使っていると、そちらで粗くなります。" +
                       "適用後は必ず目視で確認してください。";
            }
            return "この分類は自動選択されません。TexTransTool、AAO、その他のビルド前処理が" +
                   "CPU側のメッシュ／画素データを読む構成では処理が失敗する可能性があるため、" +
                   "不要と確認できたアセットだけ選択してください。";
        }

        private void Apply()
        {
            var selected = _fixes.Count(f => f.Selected && f.Applicable);
            var ok = EditorUtility.DisplayDialog("ItiOptimiser",
                $"{selected} 件のアセットのインポート設定を変更します。\n\n" +
                "変更前の .meta をバックアップするので、いつでも元に戻せます。\n" +
                "アセット数が多い場合、再インポートに時間がかかります。\n\n" +
                "実行しますか？",
                "実行する", "やめる");
            if (!ok) return;

            var backupRoot = BackupRoot();
            try
            {
                EditorUtility.DisplayProgressBar("ItiOptimiser", "バックアップと適用中...", 0.5f);
                _lastBackup = AssetOptimizer.Apply(_fixes, backupRoot, _scannedTargets);
                _historyRecords = null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (_lastBackup != null)
            {
                EditorUtility.DisplayDialog("ItiOptimiser",
                    $"完了しました。\n\nバックアップ:\n{_lastBackup}", "OK");
                Scan(); // 結果を反映して再表示
            }
        }

        // ------------------------------------------------------------------

        private void DrawHistoryTab()
        {
            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll);
            EditorGUILayout.LabelField("変更履歴", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "アバターを選ぶと、そのアバターに対して作成した履歴だけを表示します。\n" +
                "ここから変更前の状態へ戻したり、不要な履歴を削除したりできます。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                _historyAvatar = (GameObject)EditorGUILayout.ObjectField(
                    "アバター", _historyAvatar, typeof(GameObject), true);
                if (GUILayout.Button("選択中", GUILayout.Width(60)) &&
                    Selection.activeGameObject != null)
                {
                    _historyAvatar = ResolveAvatarRoot(Selection.activeGameObject);
                }
            }

            _showAllHistory = EditorGUILayout.ToggleLeft("すべてのアバターの履歴を表示", _showAllHistory);

            _historyAvatar = ResolveAvatarRoot(_historyAvatar);
            if (_historyAvatar == null && !_showAllHistory)
            {
                EditorGUILayout.HelpBox("履歴を見るアバターを選択してください。", MessageType.None);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField(_showAllHistory
                ? "対象: すべて"
                : $"対象: {_historyAvatar.name}", EditorStyles.miniBoldLabel);
            if (GUILayout.Button("履歴を更新", GUILayout.Width(90))) _historyRecords = null;
            EditorGUILayout.Space(4);

            var root = BackupRoot();
            if (!Directory.Exists(root))
            {
                EditorGUILayout.LabelField("　バックアップはまだありません。", EditorStyles.miniLabel);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (_historyRecords == null)
                _historyRecords = Directory.GetDirectories(root)
                    .Select(directory => ReadBackupRecord(directory, root))
                    .Where(r => r != null)
                    .OrderByDescending(r => r.Directory)
                    .ToList();

            List<BackupRecord> records;
            bool inferred = false;
            if (_showAllHistory)
            {
                records = _historyRecords;
            }
            else
            {
                var avatarId = GlobalObjectId.GetGlobalObjectIdSlow(_historyAvatar).ToString();
                records = _historyRecords.Where(r => IsForAvatarId(r, avatarId)).ToList();
                if (records.Count == 0)
                {
                    var hierarchyPath = HierarchyPathOf(_historyAvatar.transform);
                    records = _historyRecords.Where(r => IsForAvatarFallback(
                        r, _historyAvatar.name, hierarchyPath)).ToList();
                    inferred = records.Count > 0;
                }
            }

            if (inferred)
                EditorGUILayout.HelpBox(
                    "IDが一致する履歴が無いため、アバター名と階層パスが一致する履歴を推定表示しています。",
                    MessageType.Warning);

            if (records.Count == 0)
            {
                EditorGUILayout.LabelField("　このアバターの変更履歴はありません。", EditorStyles.miniLabel);
                EditorGUILayout.EndScrollView();
                return;
            }

            foreach (var record in records)
            {
                var avatarNames = record.Manifest.Avatars == null
                    ? string.Empty
                    : string.Join(", ", record.Manifest.Avatars
                        .Where(a => a != null).Select(a => a.Name).Distinct());
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    int count = record.Manifest.Entries?.Count ?? 0;
                    EditorGUILayout.LabelField($"{Path.GetFileName(record.Directory)}　({count} 件)",
                        GUILayout.Width(220));
                    EditorGUILayout.LabelField(avatarNames + (inferred ? "（推定）" : string.Empty),
                        GUILayout.MinWidth(100));

                    if (GUILayout.Button("元に戻す", GUILayout.Width(80)))
                    {
                        if (EditorUtility.DisplayDialog("ItiOptimiser",
                                $"{count} 件のインポート設定を、このバックアップの状態に戻します。\n\n" +
                                $"記録されたアバター: {avatarNames}\n" +
                                $"{record.Directory}\n\n実行しますか？", "戻す", "やめる"))
                        {
                            int n = AssetOptimizer.Restore(record.Directory);
                            EditorUtility.DisplayDialog("ItiOptimiser", $"{n} 件を元に戻しました。", "OK");
                            _fixes = null;
                        }
                    }

                    if (GUILayout.Button("フォルダを開く", GUILayout.Width(100)))
                        EditorUtility.RevealInFinder(record.Directory);

                    var oldColor = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.65f, 0.65f);
                    if (GUILayout.Button("削除", GUILayout.Width(55)) &&
                        EditorUtility.DisplayDialog("ItiOptimiser",
                            $"この履歴を完全に削除します。\n\n" +
                            $"日時: {Path.GetFileName(record.Directory)}\n" +
                            $"記録されたアバター: {avatarNames}\n件数: {count}\n\n" +
                            "適用済みの設定は変わりませんが、この履歴からは元に戻せなくなります。",
                            "削除する", "やめる"))
                    {
                        if (DeleteBackup(record.Directory, record.Root, out var error))
                        {
                            _historyRecords = null;
                            GUIUtility.ExitGUI();
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("ItiOptimiser", error, "OK");
                        }
                    }
                    GUI.backgroundColor = oldColor;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private sealed class BackupRecord
        {
            public string Directory;
            public string Root;
            public BackupManifest Manifest;
        }

        private static BackupRecord ReadBackupRecord(string directory, string root)
        {
            try
            {
                var manifest = AssetOptimizer.ReadManifest(directory);
                return manifest == null ? null : new BackupRecord
                { Directory = directory, Root = root, Manifest = manifest };
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ItiOptimiser] 履歴を読めません: {directory}: {e.Message}");
                return null;
            }
        }

        private static bool IsForAvatarId(BackupRecord record, string avatarId)
        {
            if (record?.Manifest == null) return false;
            return record.Manifest.Avatars != null && record.Manifest.Avatars.Any(a =>
                a != null && a.GlobalObjectId == avatarId);
        }

        private static bool IsForAvatarFallback(BackupRecord record, string avatarName,
            string hierarchyPath)
        {
            if (record?.Manifest?.Avatars == null) return false;
            return record.Manifest.Avatars.Any(a => a != null &&
                string.Equals(a.Name, avatarName, System.StringComparison.Ordinal) &&
                string.Equals(a.HierarchyPath, hierarchyPath, System.StringComparison.Ordinal));
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

        private static bool DeleteBackup(string directory, string root, out string error)
        {
            error = null;
            try
            {
                var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
                var targetFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
                                 Path.DirectorySeparatorChar;
                if (!targetFull.StartsWith(rootFull, System.StringComparison.OrdinalIgnoreCase) ||
                    targetFull == rootFull || AssetOptimizer.ReadManifest(directory) == null)
                {
                    error = "安全確認に失敗したため削除しませんでした。";
                    return false;
                }

                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "リンク先を誤って削除する可能性があるため、この履歴は削除しませんでした。";
                    return false;
                }

                Directory.Delete(directory, true);
                return true;
            }
            catch (System.Exception e)
            {
                error = "履歴を削除できませんでした。\n" + e.Message;
                return false;
            }
        }

        private static GameObject ResolveAvatarRoot(GameObject selected)
        {
            if (selected == null) return null;
            var descriptor = selected.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor != null) return descriptor.gameObject;
            var children = selected.GetComponentsInChildren<VRCAvatarDescriptor>(true);
            return children.Length == 1 ? children[0].gameObject : null;
        }

        /// <summary>
        /// バックアップ置き場。Unity プロジェクトの外（HKOpti のリポジトリ側）に置く。
        /// プロジェクト内に置くと Unity が .meta を作ろうとして紛らわしいため。
        /// </summary>
        private static string BackupRoot()
        {
            // <project>/../HKOpti_Backup ではなく、プロジェクト直下の親に置く
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            return Path.Combine(projectRoot, "ItiOptimiser_Backup", "AssetImportSettings");
        }

    }
}
