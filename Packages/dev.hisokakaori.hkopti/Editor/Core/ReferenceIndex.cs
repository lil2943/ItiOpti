using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Debug = UnityEngine.Debug;

namespace HisokaKaori.HKOpti.Editor.Core
{
    /// <summary>Transform が「誰に使われているか」の種類。仕様 5.2。</summary>
    public enum UsageKind
    {
        /// <summary>頂点がこのボーンにウェイト付けされている</summary>
        SkinWeight,

        /// <summary>SkinnedMeshRenderer.rootBone</summary>
        SkinnedMeshRootBone,

        /// <summary>Renderer.probeAnchor</summary>
        ProbeAnchor,

        /// <summary>PhysBone のチェーンに含まれる</summary>
        PhysBone,

        /// <summary>PhysBone の ignoreTransforms</summary>
        PhysBoneIgnore,

        /// <summary>PhysBoneCollider の rootTransform / 本体</summary>
        PhysBoneCollider,

        /// <summary>Contact Sender / Receiver</summary>
        Contact,

        /// <summary>Constraint のソースまたはターゲット</summary>
        Constraint,

        /// <summary>いずれかの AnimationClip がこのパスを参照している</summary>
        Animation,

        /// <summary>Transform 以外のコンポーネントが乗っている</summary>
        ComponentOwner,

        /// <summary>他のコンポーネントから参照されている（種類不明の総なめ検出）</summary>
        ComponentReference,

        /// <summary>Animator がヒューマノイドボーンとして参照している（インスタンス一致）</summary>
        Humanoid,

        /// <summary>ヒューマノイドボーンに至る経路上にある</summary>
        HumanoidPath,

        /// <summary>VRCAvatarDescriptor が参照している（視線・リップシンクなど）</summary>
        AvatarDescriptor,
    }

    public struct UsageRecord
    {
        public UsageKind Kind;

        /// <summary>参照元（コンポーネントやクリップ）。表示用。</summary>
        public UnityEngine.Object Source;

        /// <summary>補足説明。表示用。</summary>
        public string Detail;
    }

    /// <summary>
    /// 「このボーンを消したら何が壊れるか」を答えられるデータベース。仕様 5.2。
    ///
    /// HKOpti の全パスがこれを参照する。ここが不正確だと、
    /// 以降のすべての削除判断が信用できなくなる。
    /// </summary>
    public sealed class ReferenceIndex
    {
        public GameObject AvatarRoot { get; private set; }
        public Animator Animator { get; private set; }
        public VRCAvatarDescriptor Descriptor { get; private set; }
        public AnimationPathIndex Animations { get; private set; }

        /// <summary>Transform → 使用者のリスト。</summary>
        public readonly Dictionary<Transform, List<UsageRecord>> TransformUsage =
            new Dictionary<Transform, List<UsageRecord>>();

        /// <summary>アバター配下の全 Transform（順序はヒエラルキー順）。</summary>
        public readonly List<Transform> AllTransforms = new List<Transform>();

        /// <summary>Transform → アバタールートからの相対パス。</summary>
        public readonly Dictionary<Transform, string> PathOf = new Dictionary<Transform, string>();

        /// <summary>Animator がヒューマノイドとして参照している Transform（インスタンス一致）。</summary>
        public readonly HashSet<Transform> HumanoidBones = new HashSet<Transform>();

        /// <summary>ヒューマノイドボーンに至る経路上の Transform。</summary>
        public readonly HashSet<Transform> HumanoidPath = new HashSet<Transform>();

        public readonly List<SkinnedMeshRenderer> SkinnedMeshes = new List<SkinnedMeshRenderer>();
        public readonly List<MeshRenderer> MeshRenderers = new List<MeshRenderer>();
        public readonly List<VRCPhysBone> PhysBones = new List<VRCPhysBone>();
        public readonly List<VRCPhysBoneCollider> PhysBoneColliders = new List<VRCPhysBoneCollider>();

        /// <summary>
        /// PhysBone → そのチェーンに含まれる Transform（ルート自身を含む）。
        /// Performance Rank の「影響 Transform 数」の計算に使う。
        /// </summary>
        public readonly Dictionary<VRCPhysBone, List<Transform>> PhysBoneChains =
            new Dictionary<VRCPhysBone, List<Transform>>();

        /// <summary>マテリアル → それを使っている Renderer。</summary>
        public readonly Dictionary<Material, List<Renderer>> MaterialUsage =
            new Dictionary<Material, List<Renderer>>();

        /// <summary>テクスチャ → (マテリアル, プロパティ名)。</summary>
        public readonly Dictionary<Texture, List<(Material Mat, string Prop)>> TextureUsage =
            new Dictionary<Texture, List<(Material, string)>>();

        /// <summary>
        /// Expression Menu の Control / Puppet ラベルが使う画像。
        /// Renderer からは到達できないため、通常の TextureUsage とは分けて保持する。
        /// </summary>
        public readonly HashSet<Texture2D> ExpressionMenuTextures = new HashSet<Texture2D>();

        /// <summary>読み取れなかったメッシュ（Read/Write 無効など）。解析の穴として報告する。</summary>
        public readonly List<string> UnreadableMeshes = new List<string>();

        /// <summary>構築にかかった時間（ミリ秒）。</summary>
        public long BuildMilliseconds { get; private set; }

        // ------------------------------------------------------------------

        public bool IsUsed(Transform t) =>
            t != null && TransformUsage.TryGetValue(t, out var l) && l.Count > 0;

        public IReadOnlyList<UsageRecord> UsageOf(Transform t) =>
            t != null && TransformUsage.TryGetValue(t, out var l)
                ? (IReadOnlyList<UsageRecord>)l
                : Array.Empty<UsageRecord>();

        public bool HasUsage(Transform t, UsageKind kind)
        {
            if (t == null || !TransformUsage.TryGetValue(t, out var list)) return false;
            foreach (var r in list)
            {
                if (r.Kind == kind) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------

        public static ReferenceIndex Build(GameObject avatarRoot)
        {
            var sw = Stopwatch.StartNew();
            var idx = new ReferenceIndex { AvatarRoot = avatarRoot };
            if (avatarRoot == null) return idx;

            idx.Animator = avatarRoot.GetComponent<Animator>();
            idx.Descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();

            idx.CollectTransforms();
            idx.CollectHumanoid();
            idx.Animations = AnimationPathIndex.Build(avatarRoot);
            idx.CollectAnimationUsage();
            idx.CollectRenderers();
            idx.CollectExpressionMenuTextures();
            idx.CollectPhysBones();
            idx.CollectContacts();
            idx.CollectDescriptorRefs();
            idx.CollectGenericComponentRefs();

            sw.Stop();
            idx.BuildMilliseconds = sw.ElapsedMilliseconds;
            return idx;
        }

        // ------------------------------------------------------------------

        private void Add(Transform t, UsageKind kind, UnityEngine.Object source, string detail = null)
        {
            if (t == null) return;
            // アバター配下でないものは無視する（外部オブジェクトへの参照）
            if (!PathOf.ContainsKey(t)) return;

            if (!TransformUsage.TryGetValue(t, out var list))
            {
                list = new List<UsageRecord>();
                TransformUsage[t] = list;
            }

            // 同じ種類・同じ参照元の重複は積まない（数万件になるのを防ぐ）
            foreach (var r in list)
            {
                if (r.Kind == kind && ReferenceEquals(r.Source, source)) return;
            }

            list.Add(new UsageRecord { Kind = kind, Source = source, Detail = detail });
        }

        private void CollectTransforms()
        {
            var root = AvatarRoot.transform;
            foreach (var t in AvatarRoot.GetComponentsInChildren<Transform>(true))
            {
                AllTransforms.Add(t);
                PathOf[t] = HierarchyPath.Relative(root, t) ?? string.Empty;
                if (!TransformUsage.ContainsKey(t)) TransformUsage[t] = new List<UsageRecord>();
            }
        }

        private void CollectHumanoid()
        {
            if (Animator == null || !Animator.isHuman) return;

            foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (hb == HumanBodyBones.LastBone) continue;

                Transform t;
                try
                {
                    t = Animator.GetBoneTransform(hb);
                }
                catch (Exception)
                {
                    continue;
                }
                if (t == null) continue;

                // ★ インスタンス一致で記録する。名前で判定してはいけない。
                //    服が持ち込んだ同名の複製ボーンをここに入れてしまうと、
                //    B-6（服アーマチュアの本体統合）が丸ごと機能しなくなる。仕様 1.2 / 6.1。
                HumanoidBones.Add(t);
                Add(t, UsageKind.Humanoid, Animator, hb.ToString());
            }

            // ヒューマノイドボーンに至る経路も守る（消すと階層が壊れる）
            var root = AvatarRoot.transform;
            foreach (var bone in HumanoidBones)
            {
                for (var p = bone.parent; p != null && p != root.parent; p = p.parent)
                {
                    if (HumanoidBones.Contains(p)) continue;
                    if (HumanoidPath.Add(p))
                        Add(p, UsageKind.HumanoidPath, Animator, "ヒューマノイドボーンへの経路");
                    if (p == root) break;
                }
            }
        }

        private void CollectAnimationUsage()
        {
            foreach (var t in AllTransforms)
            {
                var path = PathOf[t];
                if (!Animations.IsReferenced(path)) continue;

                var clips = Animations.PathToClips.TryGetValue(path, out var set)
                    ? string.Join(", ", set)
                    : null;
                Add(t, UsageKind.Animation, null, clips);
            }
        }

        private void CollectRenderers()
        {
            foreach (var r in AvatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;

                if (r.probeAnchor != null)
                    Add(r.probeAnchor, UsageKind.ProbeAnchor, r);

                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) continue;
                    if (!MaterialUsage.TryGetValue(mat, out var users))
                    {
                        users = new List<Renderer>();
                        MaterialUsage[mat] = users;
                    }
                    if (!users.Contains(r)) users.Add(r);
                    CollectTexturesOf(mat);
                }

                switch (r)
                {
                    case SkinnedMeshRenderer smr:
                        SkinnedMeshes.Add(smr);
                        CollectSkinnedMesh(smr);
                        break;
                    case MeshRenderer mr:
                        MeshRenderers.Add(mr);
                        break;
                }
            }
        }

        private void CollectSkinnedMesh(SkinnedMeshRenderer smr)
        {
            if (smr.rootBone != null)
                Add(smr.rootBone, UsageKind.SkinnedMeshRootBone, smr);

            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            if (mesh == null || bones == null || bones.Length == 0) return;

            BoneWeight[] weights;
            try
            {
                weights = mesh.boneWeights;
            }
            catch (Exception e)
            {
                UnreadableMeshes.Add($"{HierarchyPath.Of(smr.transform)} / {mesh.name}: {e.GetType().Name}");
                return;
            }

            if (weights == null || weights.Length == 0)
            {
                // ウェイトが読めない＝どのボーンが要るか判断できない。
                // このメッシュが使う全ボーンを「使用中」として安全側に倒す。
                UnreadableMeshes.Add($"{HierarchyPath.Of(smr.transform)} / {mesh.name}: ウェイトを取得できず");
                foreach (var b in bones)
                    Add(b, UsageKind.SkinWeight, smr, "ウェイト不明のため安全側で保持");
                return;
            }

            const float threshold = 0.0001f;
            var used = new HashSet<int>();
            foreach (var w in weights)
            {
                if (w.weight0 > threshold) used.Add(w.boneIndex0);
                if (w.weight1 > threshold) used.Add(w.boneIndex1);
                if (w.weight2 > threshold) used.Add(w.boneIndex2);
                if (w.weight3 > threshold) used.Add(w.boneIndex3);
            }

            foreach (var i in used)
            {
                if (i < 0 || i >= bones.Length) continue;
                Add(bones[i], UsageKind.SkinWeight, smr);
            }
        }

        private void CollectTexturesOf(Material mat)
        {
            var shader = mat.shader;
            if (shader == null) return;

            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    continue;

                var prop = ShaderUtil.GetPropertyName(shader, i);
                var tex = mat.GetTexture(prop);
                if (tex == null) continue;

                if (!TextureUsage.TryGetValue(tex, out var list))
                {
                    list = new List<(Material, string)>();
                    TextureUsage[tex] = list;
                }
                list.Add((mat, prop));
            }
        }

        private void CollectExpressionMenuTextures()
        {
            if (Descriptor == null || Descriptor.expressionsMenu == null) return;

            var visited = new HashSet<VRCExpressionsMenu>();
            var stack = new Stack<VRCExpressionsMenu>();
            stack.Push(Descriptor.expressionsMenu);

            while (stack.Count > 0)
            {
                var menu = stack.Pop();
                if (menu == null || !visited.Add(menu) || menu.controls == null) continue;

                foreach (var control in menu.controls)
                {
                    if (control == null) continue;
                    if (control.icon != null) ExpressionMenuTextures.Add(control.icon);

                    if (control.labels != null)
                    {
                        foreach (var label in control.labels)
                        {
                            if (label.icon != null) ExpressionMenuTextures.Add(label.icon);
                        }
                    }

                    if (control.subMenu != null) stack.Push(control.subMenu);
                }
            }
        }

        private void CollectPhysBones()
        {
            foreach (var pb in AvatarRoot.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (pb == null) continue;
                PhysBones.Add(pb);

                var chainRoot = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                Add(chainRoot, UsageKind.PhysBone, pb, "チェーンのルート");

                var ignored = new HashSet<Transform>();
                if (pb.ignoreTransforms != null)
                {
                    foreach (var ig in pb.ignoreTransforms)
                    {
                        if (ig == null) continue;
                        ignored.Add(ig);
                        Add(ig, UsageKind.PhysBoneIgnore, pb);
                    }
                }

                var chain = new List<Transform>();
                foreach (var t in ChainOf(chainRoot, ignored))
                {
                    chain.Add(t);
                    Add(t, UsageKind.PhysBone, pb);
                }
                PhysBoneChains[pb] = chain;

                if (pb.colliders != null)
                {
                    foreach (var col in pb.colliders)
                    {
                        if (col == null) continue;
                        Add(col.transform, UsageKind.PhysBoneCollider, pb);
                        if (col.rootTransform != null)
                            Add(col.rootTransform, UsageKind.PhysBoneCollider, pb);
                    }
                }
            }

            foreach (var col in AvatarRoot.GetComponentsInChildren<VRCPhysBoneCollider>(true))
            {
                if (col == null) continue;
                PhysBoneColliders.Add(col);
                Add(col.transform, UsageKind.PhysBoneCollider, col);
                if (col.rootTransform != null)
                    Add(col.rootTransform, UsageKind.PhysBoneCollider, col);
            }
        }

        /// <summary>
        /// PhysBone チェーンに含まれる Transform を列挙する。
        /// ignoreTransforms に指定されたものは、そこで枝ごと打ち切られる。
        /// </summary>
        private static IEnumerable<Transform> ChainOf(Transform root, HashSet<Transform> ignored)
        {
            if (root == null) yield break;

            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                yield return t;

                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (ignored != null && ignored.Contains(c)) continue;
                    stack.Push(c);
                }
            }
        }

        private void CollectContacts()
        {
            foreach (var c in AvatarRoot.GetComponentsInChildren<VRCContactSender>(true))
            {
                if (c == null) continue;
                Add(c.transform, UsageKind.Contact, c);
                if (c.rootTransform != null) Add(c.rootTransform, UsageKind.Contact, c);
            }

            foreach (var c in AvatarRoot.GetComponentsInChildren<VRCContactReceiver>(true))
            {
                if (c == null) continue;
                Add(c.transform, UsageKind.Contact, c);
                if (c.rootTransform != null) Add(c.rootTransform, UsageKind.Contact, c);
            }
        }

        private void CollectDescriptorRefs()
        {
            if (Descriptor == null) return;

            if (Descriptor.VisemeSkinnedMesh != null)
                Add(Descriptor.VisemeSkinnedMesh.transform, UsageKind.AvatarDescriptor,
                    Descriptor, "リップシンク用メッシュ");

            var eye = Descriptor.customEyeLookSettings;
            if (eye.eyelidsSkinnedMesh != null)
                Add(eye.eyelidsSkinnedMesh.transform, UsageKind.AvatarDescriptor,
                    Descriptor, "まばたき用メッシュ");
            if (eye.leftEye != null)
                Add(eye.leftEye, UsageKind.AvatarDescriptor, Descriptor, "左目");
            if (eye.rightEye != null)
                Add(eye.rightEye, UsageKind.AvatarDescriptor, Descriptor, "右目");
        }

        /// <summary>
        /// 全コンポーネントの SerializedObject を総なめして、Transform / GameObject への
        /// 参照を拾う。仕様 5.2 の「その他コンポーネント」。
        ///
        /// これが無いと、未知のツールのコンポーネントが握っているボーンを見落として、
        /// 消してはいけないボーンを消す。型を知らなくても検出できるのが要点。
        /// </summary>
        private void CollectGenericComponentRefs()
        {
            foreach (var comp in AvatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue; // Missing Script

                // Transform 自体は階層構造そのものなので、参照として数えない
                if (comp is Transform) continue;

                // コンポーネントが乗っていること自体が「使われている」根拠になる
                Add(comp.transform, UsageKind.ComponentOwner, comp, comp.GetType().Name);

                SerializedObject so;
                try
                {
                    so = new SerializedObject(comp);
                }
                catch (Exception)
                {
                    continue;
                }

                using (so)
                {
                    var it = so.GetIterator();
                    // NextVisible は Unity の標準的な走査方法。
                    // enterChildren: true で配列やネストした構造体の中まで入る。
                    while (it.NextVisible(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                        var obj = it.objectReferenceValue;
                        if (obj == null) continue;

                        Transform target = null;
                        switch (obj)
                        {
                            case Transform tr: target = tr; break;
                            case GameObject go: target = go.transform; break;
                            case Component c when c != null: target = c.transform; break;
                        }
                        if (target == null) continue;

                        // 自分自身への参照は情報量が無いので飛ばす
                        if (target == comp.transform) continue;

                        Add(target, UsageKind.ComponentReference, comp,
                            $"{comp.GetType().Name}.{it.propertyPath}");
                    }
                }
            }
        }
    }
}
