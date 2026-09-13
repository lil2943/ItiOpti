using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Tools;

namespace HisokaKaori.HKOpti.Tests
{
    public class BackupManifestTests
    {
        [Test]
        public void ReadManifest_WhenPrimaryIsBroken_UsesRedundantCopy()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "ItiOptimiser_manifest_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "broken json");
                var expected = new BackupManifest { CreatedAt = "test" };
                expected.Avatars.Add(new BackupAvatar
                {
                    Name = "Avatar",
                    GlobalObjectId = "stable-id",
                    ScenePath = "Assets/test.unity",
                    HierarchyPath = "Avatar",
                });
                expected.Entries.Add(new BackupEntry
                {
                    AssetPath = "Assets/test.png",
                    MetaBackup = "test.meta",
                });
                File.WriteAllText(Path.Combine(directory, "manifest.backup.json"),
                    JsonUtility.ToJson(expected, true));

                var actual = AssetOptimizer.ReadManifest(directory);

                Assert.NotNull(actual);
                Assert.AreEqual("test", actual.CreatedAt);
                Assert.AreEqual("stable-id", actual.Avatars[0].GlobalObjectId);
                Assert.AreEqual("Assets/test.png", actual.Entries[0].AssetPath);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ReadManifest_WhenBothCopiesAreInvalid_ReturnsNull()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "ItiOptimiser_manifest_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "broken");
                File.WriteAllText(Path.Combine(directory, "manifest.backup.json"), "also broken");

                Assert.IsNull(AssetOptimizer.ReadManifest(directory));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ResolveRestorePaths_RejectsBackupTraversal()
        {
            var entry = new BackupEntry
            {
                AssetPath = "Assets/test.png",
                MetaBackup = "../outside.meta",
            };

            Assert.IsFalse(ResolveRestorePaths(entry));
        }

        [Test]
        public void ResolveRestorePaths_RejectsProjectTraversal()
        {
            var entry = new BackupEntry
            {
                AssetPath = "Assets/../../outside.png",
                MetaBackup = "safe.meta",
            };

            Assert.IsFalse(ResolveRestorePaths(entry));
        }

        private static bool ResolveRestorePaths(BackupEntry entry)
        {
            var method = typeof(AssetOptimizer).GetMethod("TryResolveRestorePaths",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method, "Restore path validator is missing");
            var arguments = new object[]
            {
                Path.Combine(Path.GetTempPath(), "ItiOptimiser_backup"),
                entry,
                null,
                null,
            };
            return (bool)method.Invoke(null, arguments);
        }
    }
}
