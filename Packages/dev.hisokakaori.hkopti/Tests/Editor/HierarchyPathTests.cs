using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Tests
{
    /// <summary>
    /// 階層パスのテスト。
    ///
    /// ここが AnimationClip の path 形式とズレると、
    /// 「アニメーションが参照しているボーン」の照合が全部すり抜ける。
    /// アニメが無言で壊れる原因になるので、形式は厳密に合わせる。
    /// </summary>
    public class HierarchyPathTests
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

        /// <summary>ルート自身のパスは空文字列（AnimationClip の規則に合わせる）。</summary>
        [Test]
        public void Root_IsEmptyString()
        {
            var root = NewRoot();
            Assert.AreEqual(string.Empty, HierarchyPath.Relative(root.transform, root.transform));
        }

        [Test]
        public void Nested_UsesSlashSeparator()
        {
            var root = NewRoot();
            var a = Child(root.transform, "Armature");
            var b = Child(a, "Hips");
            var c = Child(b, "Spine");

            Assert.AreEqual("Armature", HierarchyPath.Relative(root.transform, a));
            Assert.AreEqual("Armature/Hips", HierarchyPath.Relative(root.transform, b));
            Assert.AreEqual("Armature/Hips/Spine", HierarchyPath.Relative(root.transform, c));
        }

        /// <summary>ルート配下でない Transform は null を返す（誤って空文字列にしない）。</summary>
        [Test]
        public void OutsideRoot_ReturnsNull()
        {
            var root = NewRoot();
            var other = NewRoot("Other");
            var t = Child(other.transform, "Something");

            Assert.IsNull(HierarchyPath.Relative(root.transform, t));
        }

        [Test]
        public void Null_IsHandled()
        {
            var root = NewRoot();
            Assert.IsNull(HierarchyPath.Relative(null, root.transform));
            Assert.IsNull(HierarchyPath.Relative(root.transform, null));
        }

        /// <summary>名前にスペースが入っていてもそのまま通ること（ラシューシャの Upper Leg.L 対策）。</summary>
        [Test]
        public void NamesWithSpaces_ArePreserved()
        {
            var root = NewRoot();
            var a = Child(root.transform, "Armature");
            var b = Child(a, "Upper Leg.L");

            Assert.AreEqual("Armature/Upper Leg.L", HierarchyPath.Relative(root.transform, b));
        }
    }
}
