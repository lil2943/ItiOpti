using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>
    /// アトラス化の踏み込み具合。3 段階。
    /// </summary>
    public enum AtlasLevel
    {
        /// <summary>
        /// 見た目が変わる要素に一切触らない。
        /// タイリング（UV が 0-1 の外）のマテリアルは対象外。
        /// Cutout 閾値・アウトライン太さが違うものは別グループのまま。
        /// </summary>
        Conservative = 0,

        /// <summary>
        /// Cutout 閾値とアウトライン太さの違いを、マスクに焼き込んで吸収する。
        /// まとめられるマテリアルが大きく増える（仕様 J-0-3）。
        /// タイリングは引き続き対象外。
        /// </summary>
        AbsorbVariants = 1,

        /// <summary>
        /// タイリングも含めて可能な限り統合する。壊れたら個別に除外指定して直す前提。
        /// </summary>
        Maximum = 2,
    }

    /// <summary>
    /// 「このマテリアル同士は 1 枚のアトラス・1 マテリアルにまとめられるか」を表すキー。
    ///
    /// 仕様 J-0-3 の実測では、統合を妨げているのはシェーダーの違いではなく、
    /// 同じ lilToon の中での設定の散らばりだった（_Cutoff 27 種、_OutlineWidth 51 種）。
    /// なのでキーはシェーダー名だけでなく、統合を妨げるプロパティまで含める。
    /// </summary>
    public static class AtlasGroupKey
    {
        /// <summary>どの段階でも必ず一致していなければならないプロパティ。</summary>
        private static readonly string[] AlwaysBlocking =
        {
            "_Cull",           // 両面 / 片面。描画そのものが変わるので絶対に混ぜない
            "_ZWrite",         // 深度書き込み。混ぜると前後関係が壊れる
            "_AlphaMaskMode",
            "_ZTest",
            "_BlendOp",
            "_SrcBlend",
            "_DstBlend",
        };

        /// <summary>
        /// 一致していなければならない色。統合後は 1 色しか持てないため。
        /// （テクスチャ側に焼き込めば統合できるが、現時点では未実装）
        /// </summary>
        private static readonly string[] BlockingColors =
        {
            "_Color",
            "_BaseColor",
            "_MainColor",
            "_EmissionColor",
            "_OutlineColor",
        };

        /// <summary>Conservative でだけ効くプロパティ。上の段階ではマスクに焼き込んで吸収する。</summary>
        private static readonly string[] AbsorbableBlocking =
        {
            "_Cutoff",         // Cutout 閾値。アルファ値を事前調整すれば吸収できる
            "_OutlineWidth",   // アウトライン太さ。_OutlineWidthMask に焼き込めば吸収できる
        };

        public static string Of(Material m, AtlasLevel level)
        {
            if (m == null) return "(null)";

            var sb = new StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "(no shader)");
            sb.Append('|').Append(m.renderQueue);

            foreach (var p in AlwaysBlocking) AppendProp(sb, m, p);

            // 色は必ず一致させる。
            // 統合後のマテリアルは 1 つしか色を持てないので、色が違うものを混ぜると
            // 片方の色がもう片方に化ける。テクスチャに焼き込めば統合できるが未実装。
            foreach (var p in BlockingColors) AppendColor(sb, m, p);

            if (level == AtlasLevel.Conservative)
                foreach (var p in AbsorbableBlocking) AppendProp(sb, m, p);

            // 「どのテクスチャ項目を実際に使っているか」も一致させる。
            //
            // 【なぜ必要か】
            // 10 個のマテリアルのうち 2 個しか使っていないマスク項目があると、
            // そのアトラスは 8 割が空白のまま作られる。実測ではこれが積み重なって
            // **アトラス化した結果 3.5 倍に太った**（2026-09-10）。
            // 使う項目が同じもの同士でまとめれば、どのアトラスも無駄なく埋まる。
            sb.Append('|').Append(UsedTextureSignature(m));

            // キーワードは分岐したシェーダーコードそのものなので、違えば結果が変わる。
            // ただしテクスチャの有無で立つキーワードは統合後に変わるため除外する。
            var keywords = m.shaderKeywords
                .Where(k => !IsTextureDrivenKeyword(k))
                .OrderBy(k => k, StringComparer.Ordinal);
            sb.Append('|').Append(string.Join(",", keywords));

            return sb.ToString();
        }

        /// <summary>
        /// テクスチャが刺さっているかどうかで自動的に立つキーワード。
        /// アトラス化すると全員テクスチャを持つ状態になるので、グループ分けの条件から外す。
        /// </summary>
        private static bool IsTextureDrivenKeyword(string k)
            => k == "_EMISSION"
            || k.EndsWith("_MAP", StringComparison.Ordinal)
            || k.StartsWith("_NORMALMAP", StringComparison.Ordinal);

        /// <summary>そのマテリアルが実際にテクスチャを刺している項目の一覧。</summary>
        private static string UsedTextureSignature(Material m)
        {
            if (m.shader == null) return "";
            var used = new List<string>();
            int count = UnityEditor.ShaderUtil.GetPropertyCount(m.shader);
            for (int i = 0; i < count; i++)
            {
                if (UnityEditor.ShaderUtil.GetPropertyType(m.shader, i)
                    != UnityEditor.ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var name = UnityEditor.ShaderUtil.GetPropertyName(m.shader, i);
                if (m.GetTexture(name) != null) used.Add(name);
            }
            used.Sort(StringComparer.Ordinal);
            return string.Join(",", used);
        }

        private static void AppendColor(StringBuilder sb, Material m, string prop)
        {
            sb.Append('|');
            if (!m.HasProperty(prop)) { sb.Append('-'); return; }
            try
            {
                var c = m.GetColor(prop);
                sb.Append($"{c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3}");
            }
            catch (Exception) { sb.Append('?'); }
        }

        private static void AppendProp(StringBuilder sb, Material m, string prop)
        {
            sb.Append('|');
            if (!m.HasProperty(prop)) { sb.Append('-'); return; }
            try { sb.Append(m.GetFloat(prop).ToString("F4")); }
            catch (Exception) { sb.Append('?'); }
        }

        /// <summary>
        /// マテリアルを、まとめられる組ごとに分ける。
        /// 1 個しかない組はアトラス化しても意味が無いので、呼び出し側で捨ててよい。
        /// </summary>
        public static Dictionary<string, List<Material>> Group(
            IEnumerable<Material> materials, AtlasLevel level)
        {
            var result = new Dictionary<string, List<Material>>(StringComparer.Ordinal);
            foreach (var m in materials ?? Enumerable.Empty<Material>())
            {
                if (m == null) continue;
                var key = Of(m, level);
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<Material>();
                    result[key] = list;
                }
                if (!list.Contains(m)) list.Add(m);
            }
            return result;
        }

        /// <summary>その段階でタイリングのマテリアルを対象にしてよいか。</summary>
        public static bool AllowsTiling(AtlasLevel level) => level == AtlasLevel.Maximum;

        /// <summary>その段階で Cutout 閾値・アウトライン太さの差を焼き込んで吸収するか。</summary>
        public static bool AbsorbsVariants(AtlasLevel level) => level != AtlasLevel.Conservative;
    }
}
