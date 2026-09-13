using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace HisokaKaori.HKOpti.Editor.Core
{
    /// <summary>
    /// アバターに紐づく全 AnimationClip が参照している「パス」の集合。仕様 5.2。
    ///
    /// ボーンを消したり移動したりする前に必ずここを見る。
    /// ここに載っているパスのオブジェクトを消すと、アニメーションが無言で効かなくなる。
    /// </summary>
    public sealed class AnimationPathIndex
    {
        /// <summary>参照されているパスの集合（アバタールートからの相対パス）。</summary>
        public readonly HashSet<string> Paths = new HashSet<string>();

        /// <summary>パス → そのパスを参照しているクリップ名（表示用、重複は 1 回だけ）。</summary>
        public readonly Dictionary<string, HashSet<string>> PathToClips =
            new Dictionary<string, HashSet<string>>();

        /// <summary>収集できた AnimationClip の数。</summary>
        public int ClipCount { get; private set; }

        /// <summary>走査した AnimatorController の数。</summary>
        public int ControllerCount { get; private set; }

        /// <summary>SkinnedMeshRenderer のパス → 参照されている BlendShape 名。</summary>
        public readonly Dictionary<string, HashSet<string>> BlendShapeRefs =
            new Dictionary<string, HashSet<string>>();

        public bool IsReferenced(string path) => path != null && Paths.Contains(path);

        public static AnimationPathIndex Build(GameObject avatarRoot)
        {
            var index = new AnimationPathIndex();
            if (avatarRoot == null) return index;

            var controllers = new HashSet<RuntimeAnimatorController>();

            // 1) VRCAvatarDescriptor のプレイアブルレイヤー
            var desc = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                CollectFromLayers(desc.baseAnimationLayers, controllers);
                CollectFromLayers(desc.specialAnimationLayers, controllers);
            }

            // 2) 配下にある Animator（衣装ギミックが独自に持っていることがある）
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator != null && animator.runtimeAnimatorController != null)
                    controllers.Add(animator.runtimeAnimatorController);
            }

            var seenClips = new HashSet<AnimationClip>();
            foreach (var rac in controllers)
            {
                if (rac == null) continue;
                index.ControllerCount++;

                // AnimatorOverrideController の場合は元と差し替え後の両方を見る
                foreach (var clip in rac.animationClips)
                {
                    if (clip == null) continue;
                    if (!seenClips.Add(clip)) continue;
                    index.CollectFromClip(clip);
                }
            }

            index.ClipCount = seenClips.Count;
            return index;
        }

        private static void CollectFromLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<RuntimeAnimatorController> into)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                // isDefault が true でも animatorController が入っていることがあるので、
                // フラグではなく中身の有無で判断する。
                if (layer.animatorController != null) into.Add(layer.animatorController);
            }
        }

        private void CollectFromClip(AnimationClip clip)
        {
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                Add(b.path, clip.name);
                TryRecordBlendShape(b, clip);
            }

            // マテリアル差し替えなど、オブジェクト参照のカーブ
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                Add(b.path, clip.name);
            }
        }

        private void TryRecordBlendShape(EditorCurveBinding b, AnimationClip clip)
        {
            const string prefix = "blendShape.";
            if (b.type != typeof(SkinnedMeshRenderer)) return;
            if (b.propertyName == null || !b.propertyName.StartsWith(prefix)) return;

            var shape = b.propertyName.Substring(prefix.Length);
            if (!BlendShapeRefs.TryGetValue(b.path, out var set))
            {
                set = new HashSet<string>();
                BlendShapeRefs[b.path] = set;
            }
            set.Add(shape);
        }

        private void Add(string path, string clipName)
        {
            if (path == null) return;
            Paths.Add(path);

            if (!PathToClips.TryGetValue(path, out var set))
            {
                set = new HashSet<string>();
                PathToClips[path] = set;
            }
            // 表示用なので数が増えすぎないよう上限を設ける
            if (set.Count < 8) set.Add(clipName);
        }
    }
}
