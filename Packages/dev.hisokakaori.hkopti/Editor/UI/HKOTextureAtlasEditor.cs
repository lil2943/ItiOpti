using UnityEditor;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.UI
{
    /// <summary>
    /// アトラス化コンポーネントの説明つきインスペクタ。
    /// ユーザーはプログラミングの知識が無い前提なので、
    /// 各項目が何をするのかを平易な日本語で出す。
    /// </summary>
    [CustomEditor(typeof(HKOTextureAtlas))]
    public sealed class HKOTextureAtlasEditor : UnityEditor.Editor
    {
        private static readonly string[] LevelLabels =
        {
            "保守的（まず これ で試す）",
            "設定の違いを吸収する",
            "とにかく最大限まとめる",
        };

        private static readonly string[] LevelHelp =
        {
            "見た目が変わる要素に触りません。テクスチャの中の余白を詰めるのが主な効果です。\n" +
            "実測ではこれだけでピクセル数が半分程度になります。",

            "Cutout の閾値やアウトラインの太さが違うマテリアルもまとめます。\n" +
            "まとまる数が増えますが、その差のぶん見た目がわずかにずれる可能性があります。",

            "タイリング（模様の繰り返し）を使っているマテリアルもまとめます。\n" +
            "壊れる可能性が上がります。壊れたら下の「除外するマテリアル」に入れてください。",
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "ビルド時に、テクスチャを 1 枚にまとめ直して軽くします。\n" +
                "シーン上の元のアバターとアセットは変更しません。",
                MessageType.Info);

            var enabled = serializedObject.FindProperty("Enabled");
            EditorGUILayout.PropertyField(enabled, new GUIContent("有効にする"));

            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                EditorGUILayout.Space();

                var level = serializedObject.FindProperty("Level");
                level.enumValueIndex = EditorGUILayout.Popup(
                    "踏み込み具合", Mathf.Clamp(level.enumValueIndex, 0, 2), LevelLabels);
                EditorGUILayout.HelpBox(
                    LevelHelp[Mathf.Clamp(level.enumValueIndex, 0, 2)], MessageType.None);

                EditorGUILayout.Space();

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("MaxAtlasSize"),
                    new GUIContent("アトラスの最大サイズ",
                        "1 枚のアトラスの上限。大きくすると画質が保たれますが重くなります。"));

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("PaddingPixels"),
                    new GUIContent("すき間の広さ",
                        "継ぎ目に別の色がにじむときは増やしてください。"));

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("うまくいかないときの除外指定", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("Exclude"),
                    new GUIContent("除外するマテリアル"), true);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("ExcludeRenderers"),
                    new GUIContent("除外する Renderer"), true);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
