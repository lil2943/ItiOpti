using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Tests
{
    /// <summary>
    /// 保護判定のテスト。
    ///
    /// 一番大事なのは <see cref="Absolute_IsByInstance_NotByName"/>。
    /// ここが壊れると、服が持ち込んだ同名の複製ボーンまで保護され、
    /// このツールの中核機能（B-6 服アーマチュアの本体統合）が丸ごと死ぬ。
    /// </summary>
    public class ProtectionMapTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _spawned.Clear();
        }

        private GameObject NewRoot(string name = "Avatar")
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        private static Transform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// 【最重要】同じ名前のボーンが 2 本あっても、
        /// Animator が参照しているインスタンスだけが絶対保護になること。
        ///
        /// 実データでは、服 1 着ごとに Hips / Spine / Chest の複製が持ち込まれる。
        /// 名前で判定するとそれらが全部保護され、ボーンが 1 本も減らなくなる。
        /// </summary>
        [Test]
        public void Absolute_IsByInstance_NotByName()
        {
            var root = NewRoot();
            var armature = Child(root.transform, "Armature");

            var realHips = Child(armature, "Hips");        // 本体のヒューマノイドボーン
            var clothHips = Child(realHips, "Hips");       // 服が持ち込んだ複製（同じ名前）

            var index = ReferenceIndex.Build(root);

            // Animator を用意せずにヒューマノイド判定だけを再現する。
            // （テストで人型 Avatar を組むのは重いので、判定対象の集合を直接与える）
            index.HumanoidBones.Add(realHips);

            var map = ProtectionMap.Build(index);

            Assert.AreEqual(ProtectionLevel.Absolute, map.LevelOf(realHips),
                "Animator が参照している本体の Hips は絶対保護であるべき");

            Assert.AreNotEqual(ProtectionLevel.Absolute, map.LevelOf(clothHips),
                "服が持ち込んだ同名の複製ボーンを絶対保護にしてはいけない。" +
                "名前で判定していないか確認すること（仕様 1.2 / 6.1）");

            Assert.IsTrue(map.CanMerge(clothHips),
                "複製ボーンは統合の対象になれるべき");
        }

        /// <summary>ヒューマノイドボーンに至る経路も守られること（消すと階層が壊れるため）。</summary>
        [Test]
        public void PathToHumanoid_IsProtected()
        {
            var root = NewRoot();
            var armature = Child(root.transform, "Armature");
            var hips = Child(armature, "Hips");

            var index = ReferenceIndex.Build(root);
            index.HumanoidBones.Add(hips);
            index.HumanoidPath.Add(armature);
            index.HumanoidPath.Add(root.transform);

            var map = ProtectionMap.Build(index);

            Assert.AreEqual(ProtectionLevel.Absolute, map.LevelOf(armature),
                "ヒューマノイドボーンへの経路は絶対保護であるべき");
            Assert.IsFalse(map.CanDelete(armature));
        }

        /// <summary>どこからも参照されていないボーンは削除候補（Free）になること。</summary>
        [Test]
        public void UnreferencedBone_IsFree()
        {
            var root = NewRoot();
            var armature = Child(root.transform, "Armature");
            var junk = Child(armature, "UnusedBone");

            var index = ReferenceIndex.Build(root);
            var map = ProtectionMap.Build(index);

            Assert.AreEqual(ProtectionLevel.Free, map.LevelOf(junk),
                "誰にも使われていないボーンは削除候補になるべき");
            Assert.IsTrue(map.CanDelete(junk));
        }

        /// <summary>
        /// 保護された子孫を持つ親は、自分が未参照でも守られること。
        /// これが無いと、親を消した瞬間に子ごと消える。
        /// </summary>
        [Test]
        public void AncestorOfProtected_IsProtected()
        {
            var root = NewRoot();
            var middle = Child(root.transform, "Middle");   // 自身は誰からも参照されない
            var leaf = Child(middle, "Leaf");

            var index = ReferenceIndex.Build(root);
            index.HumanoidBones.Add(leaf);                  // 子だけが保護対象

            var map = ProtectionMap.Build(index);

            Assert.AreEqual(ProtectionLevel.Absolute, map.LevelOf(leaf));
            Assert.IsFalse(map.CanDelete(middle),
                "保護された子孫を持つ親は削除できないはず");
        }

        /// <summary>コンポーネントが乗っているだけで「参照あり」になること。</summary>
        [Test]
        public void BoneWithComponent_IsReferenced()
        {
            var root = NewRoot();
            var bone = Child(root.transform, "BoneWithLight");
            bone.gameObject.AddComponent<Light>();

            var index = ReferenceIndex.Build(root);
            var map = ProtectionMap.Build(index);

            Assert.AreEqual(ProtectionLevel.Referenced, map.LevelOf(bone));
            Assert.IsFalse(map.CanDelete(bone));
        }

        /// <summary>
        /// 未知のコンポーネントが Transform を握っている場合でも検出できること。
        /// 型を知らなくても SerializedObject の総なめで拾えるのが要点（仕様 5.2）。
        /// </summary>
        [Test]
        public void UnknownComponentReference_IsDetected()
        {
            var root = NewRoot();
            var holder = Child(root.transform, "Holder");
            var target = Child(root.transform, "SecretlyUsedBone");

            var probe = holder.gameObject.AddComponent<TestReferenceHolder>();
            probe.Target = target;

            var index = ReferenceIndex.Build(root);
            var map = ProtectionMap.Build(index);

            Assert.IsTrue(index.HasUsage(target, UsageKind.ComponentReference),
                "他コンポーネントからの Transform 参照を検出できていない");
            Assert.IsFalse(map.CanDelete(target),
                "参照されているボーンを削除候補にしてはいけない");
        }

        /// <summary>アバター外への参照を、誤ってインデックスに入れないこと。</summary>
        [Test]
        public void ReferenceToOutsideAvatar_IsIgnored()
        {
            var root = NewRoot();
            var holder = Child(root.transform, "Holder");

            var outside = NewRoot("OutsideObject");

            var probe = holder.gameObject.AddComponent<TestReferenceHolder>();
            probe.Target = outside.transform;

            var index = ReferenceIndex.Build(root);

            Assert.IsFalse(index.TransformUsage.ContainsKey(outside.transform),
                "アバター配下でない Transform をインデックスに入れてはいけない");
        }
    }

    /// <summary>テスト用。未知のコンポーネントが Transform を握っている状況を作る。</summary>
    public class TestReferenceHolder : MonoBehaviour
    {
        public Transform Target;
    }
}
