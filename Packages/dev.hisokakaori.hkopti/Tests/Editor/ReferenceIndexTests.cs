using System.Collections.Generic;
using HisokaKaori.HKOpti.Editor.Core;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace HisokaKaori.HKOpti.Tests
{
    public sealed class ReferenceIndexTests
    {
        [Test]
        public void ExpressionMenuTextures_CollectsControlsLabelsAndSubmenusWithoutLooping()
        {
            var avatar = new GameObject("Avatar");
            var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
            var rootMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            var subMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            var rootIcon = new Texture2D(4, 4);
            var labelIcon = new Texture2D(4, 4);
            var subIcon = new Texture2D(4, 4);

            try
            {
                rootMenu.controls = new List<VRCExpressionsMenu.Control>
                {
                    new VRCExpressionsMenu.Control
                    {
                        icon = rootIcon,
                        labels = new[]
                        {
                            new VRCExpressionsMenu.Control.Label { icon = labelIcon },
                        },
                        subMenu = subMenu,
                    },
                };
                subMenu.controls = new List<VRCExpressionsMenu.Control>
                {
                    new VRCExpressionsMenu.Control { icon = subIcon, subMenu = rootMenu },
                };
                descriptor.expressionsMenu = rootMenu;

                var index = ReferenceIndex.Build(avatar);

                Assert.That(index.ExpressionMenuTextures.Count, Is.EqualTo(3));
                Assert.That(index.ExpressionMenuTextures, Does.Contain(rootIcon));
                Assert.That(index.ExpressionMenuTextures, Does.Contain(labelIcon));
                Assert.That(index.ExpressionMenuTextures, Does.Contain(subIcon));
            }
            finally
            {
                Object.DestroyImmediate(rootIcon);
                Object.DestroyImmediate(labelIcon);
                Object.DestroyImmediate(subIcon);
                Object.DestroyImmediate(rootMenu);
                Object.DestroyImmediate(subMenu);
                Object.DestroyImmediate(avatar);
            }
        }
    }
}
