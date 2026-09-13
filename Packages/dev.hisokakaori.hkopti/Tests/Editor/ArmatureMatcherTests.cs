using HisokaKaori.HKOpti.Editor.Core;
using HisokaKaori.HKOpti.Editor.Passes;
using NUnit.Framework;
using UnityEngine;

namespace HisokaKaori.HKOpti.Tests
{
    public sealed class ArmatureMatcherTests
    {
        [Test]
        public void FindCandidates_UsesTargetInstanceAndAcceptsIdentityChild()
        {
            var avatar = new GameObject("Avatar");
            var target = NewChild(avatar.transform, "Hips");
            var source = NewChild(target, "Hips");

            try
            {
                var index = ReferenceIndex.Build(avatar);
                index.HumanoidBones.Add(target);

                var candidates = ArmatureMatcher.FindCandidates(index);

                Assert.That(candidates.Count, Is.EqualTo(1));
                Assert.That(candidates[0].Source, Is.SameAs(source));
                Assert.That(candidates[0].Target, Is.SameAs(target));
                Assert.That(candidates[0].CanRemapAutomatically, Is.True);
                Assert.That(candidates[0].CanDeleteAutomatically, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void FindCandidates_BlocksComponentOwner()
        {
            var avatar = new GameObject("Avatar");
            var target = NewChild(avatar.transform, "Chest");
            var source = NewChild(target, "Chest");
            source.gameObject.AddComponent<AudioSource>();

            try
            {
                var index = ReferenceIndex.Build(avatar);
                index.HumanoidBones.Add(target);

                var candidate = ArmatureMatcher.FindCandidates(index)[0];

                Assert.That(candidate.MotionEquivalent, Is.True);
                Assert.That(candidate.CanRemapAutomatically, Is.False);
                Assert.That(candidate.Blockers, Does.Contain(UsageKind.ComponentOwner.ToString()));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void ApplyWeightRemaps_ReplacesRendererReferencesWithoutDeletingSource()
        {
            var avatar = new GameObject("Avatar");
            var target = NewChild(avatar.transform, "Hips");
            var source = NewChild(target, "Hips");
            var rendererObject = new GameObject("Clothes");
            rendererObject.transform.SetParent(avatar.transform, false);
            var smr = rendererObject.AddComponent<SkinnedMeshRenderer>();
            smr.bones = new[] { source };
            smr.rootBone = source;
            smr.probeAnchor = source;

            try
            {
                var index = ReferenceIndex.Build(avatar);
                index.HumanoidBones.Add(target);
                var candidates = ArmatureMatcher.FindCandidates(index);

                int changed = ArmatureMatcher.ApplyWeightRemaps(index, candidates);

                Assert.That(changed, Is.EqualTo(1));
                Assert.That(smr.bones[0], Is.SameAs(target));
                Assert.That(smr.rootBone, Is.SameAs(target));
                Assert.That(smr.probeAnchor, Is.SameAs(target));
                Assert.That(source, Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void FindCandidates_RejectsPoseMismatch()
        {
            var avatar = new GameObject("Avatar");
            var target = NewChild(avatar.transform, "Head");
            var source = NewChild(target, "Head");
            source.localPosition = new Vector3(0.01f, 0f, 0f);

            try
            {
                var index = ReferenceIndex.Build(avatar);
                index.HumanoidBones.Add(target);

                Assert.That(ArmatureMatcher.FindCandidates(index), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }
    }
}
