using System.Text;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Core
{
    /// <summary>
    /// アニメーションクリップが使うのと同じ形式の階層パスを作る。
    ///
    /// AnimationClip の EditorCurveBinding.path は「アバタールートからの相対パス」で、
    /// ルート自身は空文字列になる。ここでも同じ規則に揃えておかないと、
    /// パス照合（AnimationPathIndex）がすり抜けてしまう。
    /// </summary>
    public static class HierarchyPath
    {
        /// <summary>
        /// root から見た t の相対パス。t == root なら空文字列。
        /// t が root の配下でない場合は null を返す。
        /// </summary>
        public static string Relative(Transform root, Transform t)
        {
            if (root == null || t == null) return null;
            if (t == root) return string.Empty;

            var sb = new StringBuilder();
            var cur = t;
            while (cur != null && cur != root)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }

            // ルートまで辿り着けなかった＝配下ではない
            if (cur != root) return null;
            return sb.ToString();
        }

        /// <summary>
        /// デバッグ表示用のフルパス。ルートからではなくシーン最上位から辿る。
        /// </summary>
        public static string Of(Transform t)
        {
            if (t == null) return "(null)";
            var sb = new StringBuilder(t.name);
            var cur = t.parent;
            while (cur != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }
    }
}
