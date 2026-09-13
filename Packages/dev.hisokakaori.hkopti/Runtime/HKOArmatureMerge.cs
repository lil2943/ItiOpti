using UnityEngine;

namespace HisokaKaori.HKOpti
{
    /// <summary>
    /// ビルド時に、姿勢が一致する服側Humanoid複製ボーンのSMR参照を本体へ移す。
    /// シーン上の元アバターは変更しない。
    ///
    /// 【見送り済みの機能】
    /// ビルド後（Modular Avatar 適用後）の実測で、統合できる余地が
    /// 1 体あたり 0.2 本しか無かったため見送った（Modular Avatar が既に行っている）。
    /// **新しく付けられないよう Add Component から隠している。**
    /// クラスを消さないのは、既に付けているシーンで Missing Script にしないため。
    /// </summary>
    /// <remarks>
    /// <see cref="VRC.SDKBase.IEditorOnly"/> を付けること。
    /// これが無いと VRChat SDK が「クライアント側で消される」という警告を出す。
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class HKOArmatureMerge : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        [Tooltip("この機能は見送り済みです。新しく使う必要はありません。" +
                 "Avatar Optimizer と併用している場合、ビルド時に自動で無効になります。")]
        public bool Enabled = true;
    }
}
