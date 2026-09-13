using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace HisokaKaori.HKOpti.Editor.UI
{
    /// <summary>
    /// メニューからの入口。
    ///
    /// アトラス化はコンポーネントを手で付ける方式だが、
    /// **どこにあるか分からないと初見の人は辿り着けない。**
    /// Add Component から探させずに、メニュー 1 つで付けられるようにする。
    /// </summary>
    internal static class HKOptiMenu
    {
        [MenuItem("Tools/ItiOptimiser/選択中のアバターにテクスチャアトラス化を追加", false, 20)]
        private static void AddTextureAtlas()
        {
            var target = ResolveAvatarRoot(Selection.activeGameObject);
            if (target == null)
            {
                EditorUtility.DisplayDialog("ItiOptimiser",
                    "アバターを選択してから実行してください。\n\n" +
                    "ヒエラルキーで、VRC Avatar Descriptor が付いている" +
                    "いちばん上のオブジェクトを選びます。",
                    "OK");
                return;
            }

            if (target.GetComponent<HKOTextureAtlas>() != null)
            {
                EditorUtility.DisplayDialog("ItiOptimiser",
                    $"「{target.name}」には既に追加されています。\n" +
                    "設定はインスペクタから変更できます。",
                    "OK");
                Selection.activeGameObject = target;
                return;
            }

            Undo.AddComponent<HKOTextureAtlas>(target);
            Selection.activeGameObject = target;

            EditorUtility.DisplayDialog("ItiOptimiser",
                $"「{target.name}」にテクスチャアトラス化を追加しました。\n\n" +
                "このままアップロードすれば効果が出ます。\n" +
                "シーン上のアバターとアセットは変更されません。\n\n" +
                "最初は「保守的」のままお使いください。",
                "OK");
        }

        [MenuItem("Tools/ItiOptimiser/選択中のアバターにテクスチャアトラス化を追加", true, 20)]
        private static bool AddTextureAtlasValidate()
            => ResolveAvatarRoot(Selection.activeGameObject) != null;

        /// <summary>
        /// 選択物から、アバターのルートを探す。
        /// 服や髪など途中のオブジェクトを選んでいても、親をたどって本体を見つける。
        /// </summary>
        private static GameObject ResolveAvatarRoot(GameObject selected)
        {
            if (selected == null) return null;

            var descriptor = selected.GetComponentInParent<VRCAvatarDescriptor>();
            if (descriptor != null) return descriptor.gameObject;

            // 選択物の下にアバターが 1 体だけあるなら、それを対象にする
            var inChildren = selected.GetComponentsInChildren<VRCAvatarDescriptor>(true)
                .Where(d => d != null)
                .ToList();
            return inChildren.Count == 1 ? inChildren[0].gameObject : null;
        }
    }
}
