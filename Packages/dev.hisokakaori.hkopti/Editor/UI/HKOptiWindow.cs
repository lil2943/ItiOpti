using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Analysis;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.UI
{
    /// <summary>
    /// 解析レポートを表示するウィンドウ。仕様 7.A / 8.4。
    ///
    /// 【フェーズ 0】この段階では読むだけ。データは一切変更しない。
    /// </summary>
    public sealed class HKOptiWindow : EditorWindow
    {
        private GameObject _avatar;
        private bool _mobile;

        private ReferenceIndex _index;
        private ProtectionMap _protection;
        private AvatarStats _stats;
        private List<ToolConflict> _conflicts;

        private Vector2 _scroll;
        private bool _showTextures = true;
        private bool _showMaterials;
        private bool _showBones;
        private bool _showHazards;
        private bool _showDiagnostics;

        /// <summary>
        /// タブ名は表示のたびに付け直す。Unity はタブ名をレイアウトごと保存して復元するので、
        /// 改名前に開いていたウィンドウが古い名前のまま出てくるのを防ぐ。
        /// </summary>
        private void OnEnable()
        {
            titleContent = new GUIContent("ItiOptimiser 解析");
        }

        [MenuItem("Tools/ItiOptimiser/解析")]
        public static void Open()
        {
            var w = GetWindow<HKOptiWindow>("ItiOptimiser 解析");
            w.minSize = new Vector2(520, 400);
            w.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawHeader();

            if (_index != null)
            {
                EditorGUILayout.Space(6);
                DrawConflicts();
                DrawStats();
                DrawSections();
                DrawDiagnostics();
            }

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("解析", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "アバターの負荷を調べて表示します。データは一切変更しません。\n" +
                "※ ここに出るのは「ビルド前」の状態です。Modular Avatar や VRCFury が" +
                "ビルド時に足すものは含まれません。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                _avatar = (GameObject)EditorGUILayout.ObjectField(
                    "対象アバター", _avatar, typeof(GameObject), true);

                if (GUILayout.Button("選択中を使う", GUILayout.Width(90)) && Selection.activeGameObject != null)
                {
                    _avatar = Selection.activeGameObject;
                }
            }

            _mobile = EditorGUILayout.ToggleLeft(
                "Quest / Android の基準で判定する", _mobile);

            using (new EditorGUI.DisabledScope(_avatar == null))
            {
                if (GUILayout.Button("解析する", GUILayout.Height(28)))
                {
                    Analyze();
                }
            }

            if (_avatar != null && _avatar.GetComponent<Animator>() == null)
            {
                EditorGUILayout.HelpBox(
                    "Animator がありません。アバターのルートを指定してください。",
                    MessageType.Warning);
            }
        }

        private void Analyze()
        {
            try
            {
                EditorUtility.DisplayProgressBar("ItiOptimiser", "参照インデックスを構築中...", 0.3f);
                _index = ReferenceIndex.Build(_avatar);

                EditorUtility.DisplayProgressBar("ItiOptimiser", "保護レベルを判定中...", 0.6f);
                _protection = ProtectionMap.Build(_index);

                EditorUtility.DisplayProgressBar("ItiOptimiser", "負荷を計測中...", 0.8f);
                _stats = AvatarStats.Measure(_index, _mobile);
                // ユーザーが実際に使う機能だけで判定する。
                // 見送った Armature Merge まで含めると、AAO が「併用不可」と表示されて
                // 誤解を招く（AAO はインポート設定ともアトラス化とも共存できる）。
                _conflicts = ToolConflictDetector.Detect(_avatar,
                    OptimizerFeature.ImportSettings | OptimizerFeature.TextureAtlas);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // ------------------------------------------------------------------

        private void DrawConflicts()
        {
            if (_conflicts == null || _conflicts.Count == 0) return;

            foreach (var c in _conflicts)
            {
                var type = c.Severity == ConflictSeverity.Blocking
                    ? MessageType.Error
                    : MessageType.Warning;

                var head = c.Severity == ConflictSeverity.Blocking
                    ? $"【併用不可】{c.ToolName} が使われています"
                    : $"【注意】{c.ToolName} が使われています";

                EditorGUILayout.HelpBox($"{head}\n{c.Message}\n場所: {c.OwnerPath}", type);
            }

            if (ToolConflictDetector.HasBlocking(_conflicts))
            {
                EditorGUILayout.HelpBox(
                    "併用できないツールが見つかりました。上に書かれた機能だけが使えません。" +
                    "（解析レポートの表示と、それ以外の機能は問題ありません）",
                    MessageType.Error);
            }
        }

        private void DrawStats()
        {
            if (_stats == null) return;

            EditorGUILayout.Space(4);
            var overall = _stats.Overall;

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                normal = { textColor = PerformanceThresholds.ColorOf(overall) },
            };

            EditorGUILayout.LabelField(
                $"総合ランク: {PerformanceThresholds.DisplayName(overall)}" +
                $"　（{(_mobile ? "Quest" : "PC")} 基準）", style);

            var necks = _stats.Bottlenecks.Select(e => e.Label).ToList();
            if (necks.Count > 0)
            {
                EditorGUILayout.LabelField(
                    "足を引っ張っている項目: " + string.Join(", ", necks),
                    EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(PerformanceThresholds.LoadError))
            {
                EditorGUILayout.HelpBox(PerformanceThresholds.LoadError, MessageType.Error);
            }

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.miniLabel))
            {
                EditorGUILayout.LabelField("項目", GUILayout.Width(180));
                EditorGUILayout.LabelField("現在", GUILayout.Width(90));
                EditorGUILayout.LabelField("判定", GUILayout.Width(80));
                EditorGUILayout.LabelField("次のランクの上限");
            }

            foreach (var e in _stats.Entries)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(e.Label, GUILayout.Width(180));
                    EditorGUILayout.LabelField(e.ValueText, GUILayout.Width(90));

                    var rankStyle = new GUIStyle(EditorStyles.label)
                    {
                        normal = { textColor = PerformanceThresholds.ColorOf(e.Rank) },
                    };
                    EditorGUILayout.LabelField(
                        PerformanceThresholds.DisplayName(e.Rank), rankStyle, GUILayout.Width(80));

                    // 1 つ上のランクに上がるための目標値を出す
                    if (e.Rank > PerfRank.Excellent)
                    {
                        var next = (PerfRank)((int)e.Rank - 1);
                        var limit = e.LimitFor(next, _mobile);
                        var text = e.Unit == "MB" ? $"{limit} MB" : limit.ToString("N0");
                        EditorGUILayout.LabelField(
                            $"{PerformanceThresholds.DisplayName(next)} まで {text}",
                            EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("—", EditorStyles.miniLabel);
                    }
                }
            }
        }

        private void DrawSections()
        {
            EditorGUILayout.Space(8);

            _showTextures = EditorGUILayout.Foldout(_showTextures, "テクスチャの内訳", true);
            if (_showTextures) DrawItems(CostBreakdown.Textures(_index));

            _showMaterials = EditorGUILayout.Foldout(_showMaterials, "マテリアルの内訳", true);
            if (_showMaterials) DrawItems(CostBreakdown.Materials(_index));

            _showBones = EditorGUILayout.Foldout(_showBones, "ボーンの内訳", true);
            if (_showBones) DrawBones();

            _showHazards = EditorGUILayout.Foldout(_showHazards, "警告（自動では触りません）", true);
            if (_showHazards)
            {
                var hz = CostBreakdown.Hazards(_index);
                if (hz.Count == 0) EditorGUILayout.LabelField("  問題は見つかりませんでした。");
                else DrawItems(hz);
            }
        }

        private void DrawBones()
        {
            if (_protection == null) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    $"絶対保護 {_protection.CountOf(ProtectionLevel.Absolute)}　" +
                    $"規約保護 {_protection.CountOf(ProtectionLevel.Convention)}　" +
                    $"参照あり {_protection.CountOf(ProtectionLevel.Referenced)}　" +
                    $"参照なし {_protection.CountOf(ProtectionLevel.Free)}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField(
                "※「参照なし」は、どこからも使われていないという意味です。" +
                "この画面が何かを削除することはありません。",
                EditorStyles.miniLabel);

            DrawItems(CostBreakdown.BonesByGroup(_index, _protection));
        }

        private static void DrawItems(List<CostItem> items)
        {
            foreach (var item in items)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(12);

                    if (item.Target != null)
                    {
                        if (GUILayout.Button("→", GUILayout.Width(24)))
                            EditorGUIUtility.PingObject(item.Target);
                    }
                    else
                    {
                        GUILayout.Space(24);
                    }

                    EditorGUILayout.LabelField(item.Name, GUILayout.Width(300));
                    EditorGUILayout.LabelField(item.WeightText, GUILayout.Width(70));
                    EditorGUILayout.LabelField(item.Advice, EditorStyles.miniLabel);
                }
            }
        }

        private void DrawDiagnostics()
        {
            EditorGUILayout.Space(8);
            _showDiagnostics = EditorGUILayout.Foldout(_showDiagnostics, "解析の内部情報", true);
            if (!_showDiagnostics) return;

            EditorGUILayout.LabelField($"  Transform 総数: {_index.AllTransforms.Count}");
            EditorGUILayout.LabelField($"  ヒューマノイドボーン: {_index.HumanoidBones.Count}");
            EditorGUILayout.LabelField($"  ヒューマノイドへの経路: {_index.HumanoidPath.Count}");
            EditorGUILayout.LabelField($"  AnimatorController: {_index.Animations.ControllerCount}");
            EditorGUILayout.LabelField($"  AnimationClip: {_index.Animations.ClipCount}");
            EditorGUILayout.LabelField($"  アニメが参照するパス: {_index.Animations.Paths.Count}");
            EditorGUILayout.LabelField($"  マテリアル: {_index.MaterialUsage.Count}");
            EditorGUILayout.LabelField($"  テクスチャ: {_index.TextureUsage.Count}");
            EditorGUILayout.LabelField($"  Expression Menu アイコン: {_index.ExpressionMenuTextures.Count}");
            EditorGUILayout.LabelField($"  解析時間: {_index.BuildMilliseconds} ms");

            if (_index.UnreadableMeshes.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "以下のメッシュはウェイトを読み取れませんでした。\n" +
                    "使用ボーンを判定できないため、安全側（全ボーンを保持）で扱っています。\n\n" +
                    string.Join("\n", _index.UnreadableMeshes.Take(10)),
                    MessageType.Warning);
            }
        }
    }
}
