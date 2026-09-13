using System.Collections.Generic;
using UnityEngine;

namespace HisokaKaori.HKOpti
{
    /// <summary>アトラス化の踏み込み具合。3 段階。</summary>
    public enum HKOAtlasLevel
    {
        /// <summary>見た目が変わる要素に触らない。タイリングは対象外。設定が違うものは別のまま。</summary>
        Conservative = 0,

        /// <summary>Cutout 閾値・アウトライン太さの違いを吸収してまとめる。</summary>
        AbsorbVariants = 1,

        /// <summary>タイリングも含めて可能な限りまとめる。</summary>
        Maximum = 2,
    }

    /// <summary>
    /// ビルド時に、まとめられるマテリアルのテクスチャを 1 枚に詰め直す。
    /// シーン上の元アバター・元アセットは変更しない。
    /// </summary>
    /// <remarks>
    /// <see cref="VRC.SDKBase.IEditorOnly"/> を付けること。
    /// これが無いと VRChat SDK が「知らないコンポーネントなのでクライアント側で消される」
    /// という警告を出す。ビルド時に HKOpti 自身が削除するので実害は無いが、
    /// アップロードのたびに赤い警告が出て、本物の問題が埋もれる。
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("ItiOptimiser/Texture Atlas")]
    public sealed class HKOTextureAtlas : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        [Tooltip("無効時は解析も変更も行いません。")]
        public bool Enabled = true;

        [Tooltip("踏み込み具合。まず「保守的」で試し、問題が無ければ上げてください。")]
        public HKOAtlasLevel Level = HKOAtlasLevel.Conservative;

        [Tooltip("アトラス 1 枚の最大の大きさ。")]
        public int MaxAtlasSize = 4096;

        [Tooltip("島どうしの隙間（ピクセル）。継ぎ目に別の色がにじむときは増やしてください。")]
        [Range(0, 32)]
        public int PaddingPixels = 8;

        [Tooltip("ここに入れたマテリアルはアトラス化しません。壊れたものを個別に外すのに使います。")]
        public List<Material> Exclude = new List<Material>();

        [Tooltip("ここに入れた Renderer はアトラス化しません。")]
        public List<Renderer> ExcludeRenderers = new List<Renderer>();
    }
}
