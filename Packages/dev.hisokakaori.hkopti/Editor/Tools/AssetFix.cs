using System;
using System.Collections.Generic;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Tools
{
    public enum AssetFixKind
    {
        /// <summary>元画像にアルファが無いのにアルファ付き形式 → アルファを捨てる</summary>
        DropUnusedAlpha,

        /// <summary>全面が単色 → 最小サイズに縮小</summary>
        ShrinkSolidColor,

        /// <summary>メッシュの Read/Write を無効化</summary>
        DisableReadWrite,

        /// <summary>テクスチャの Read/Write を無効化（他のビルドツールを確認して手動選択）</summary>
        DisableTextureReadWrite,

        /// <summary>
        /// テクセル密度から見て解像度が過大 → maxTextureSize を下げる（仕様 G-1）。
        ///
        /// 【他の項目と性質が違う】
        /// これだけは「アセット自体の性質」ではなく**アバターごとに答えが変わる**。
        /// スキャンしたアバター全体で最大の要求値を採って安全側に倒しているが、
        /// スキャンしていないアバターが同じテクスチャをもっと大きく使っていると足りなくなる。
        /// → 既定では安全マージン付きの推奨値を使い、UI で対象範囲を明示する。
        /// </summary>
        ReduceTextureResolution,

        /// <summary>
        /// Crunch 圧縮を有効にする（仕様 G-3）。
        ///
        /// 【他の項目と決定的に違う点】
        /// **VRAM は 1 バイトも減らない。** 縮むのは配信されるファイルだけ。
        /// なので削減見込みを VRAM の合計に足してはいけない（地雷 3.7 の二重計上）。
        /// 効果は実際にアップロードしないと分からないので、
        /// **バイト数の見積もりを出さない**（出すと必ず「実際より良い」数字になる）。
        /// </summary>
        EnableCrunchCompression,
    }

    /// <summary>
    /// アセットのインポート設定に対する 1 件の修正案。
    ///
    /// ここで扱うのは「アバターに依存しない、アセット自体の性質」に限る。
    /// （元画像にアルファがあるか、中身が単色か、など）
    /// 解像度のようにアバターごとに最適値が変わるものは扱わない。
    /// それは非破壊パスの仕事（仕様 G-1）。
    /// </summary>
    public sealed class AssetFix
    {
        public AssetFixKind Kind;
        public string AssetPath;
        public string ObjectName;

        /// <summary>今どうなっているか（表示用）</summary>
        public string Before;

        /// <summary>どう変えるか（表示用）</summary>
        public string After;

        /// <summary>なぜ安全と言えるか</summary>
        public string Reason;

        /// <summary>削減見込み（バイト）。VRAM ベース。</summary>
        public long SavedBytes;

        /// <summary>
        /// ダウンロードサイズだけに効き、VRAM は減らない項目か。
        /// true のものを VRAM の削減合計に足してはいけない（地雷 3.7）。
        /// </summary>
        public bool DownloadOnly;

        /// <summary>ユーザーが適用対象として選んでいるか</summary>
        public bool Selected = true;

        /// <summary>この修正を適用してよいか（Packages 配下などは不可）</summary>
        public bool Applicable;
        public string NotApplicableReason;

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case AssetFixKind.DropUnusedAlpha: return "アルファ不要";
                    case AssetFixKind.ShrinkSolidColor: return "単色";
                    case AssetFixKind.DisableReadWrite: return "Read/Write";
                    case AssetFixKind.DisableTextureReadWrite: return "Texture Read/Write";
                    case AssetFixKind.ReduceTextureResolution: return "解像度が過大";
                    case AssetFixKind.EnableCrunchCompression: return "Crunch 圧縮";
                    default: return Kind.ToString();
                }
            }
        }
    }

    /// <summary>バックアップの記録。元に戻すときに使う。</summary>
    [Serializable]
    public sealed class BackupManifest
    {
        public string CreatedAt;
        public string UnityVersion;
        public List<BackupAvatar> Avatars = new List<BackupAvatar>();
        public List<BackupEntry> Entries = new List<BackupEntry>();
    }

    [Serializable]
    public sealed class BackupAvatar
    {
        public string Name;
        public string GlobalObjectId;
        public string ScenePath;
        public string HierarchyPath;
    }

    [Serializable]
    public sealed class BackupEntry
    {
        public string AssetPath;   // 例: Assets/Foo/bar.png
        public string MetaBackup;  // バックアップフォルダ内の相対パス
        public string Kind;
        public string Before;
        public string After;
    }
}
