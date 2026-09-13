using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using HisokaKaori.HKOpti.Editor.Atlas;

namespace HisokaKaori.HKOpti.Tests
{
    public class AtlasLayoutTests
    {
        private static IslandPlacement P(Rect source, Rect target)
            => new IslandPlacement { Source = source, Target = target };

        [Test]
        public void FullUvToQuarter_MapsCornersCorrectly()
        {
            // 0-1 全体を使っていた島が、アトラスの左下 1/4 に入る場合
            var p = P(new Rect(0f, 0f, 1f, 1f), new Rect(0f, 0f, 0.5f, 0.5f));

            Assert.AreEqual(new Vector2(0.5f, 0.5f), p.Scale);
            Assert.AreEqual(new Vector2(0f, 0f), p.Offset);
            AssertMaps(p, new Vector2(0f, 0f), new Vector2(0f, 0f));
            AssertMaps(p, new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            AssertMaps(p, new Vector2(0.5f, 0.5f), new Vector2(0.25f, 0.25f));
        }

        [Test]
        public void OffsetIsland_MapsToItsOwnSlot()
        {
            // 元 UV の (0.2,0.4)-(0.6,0.8) にあった島が、アトラスの (0.5,0.5)-(0.75,0.75) へ
            var p = P(new Rect(0.2f, 0.4f, 0.4f, 0.4f), new Rect(0.5f, 0.5f, 0.25f, 0.25f));

            AssertMaps(p, new Vector2(0.2f, 0.4f), new Vector2(0.5f, 0.5f));
            AssertMaps(p, new Vector2(0.6f, 0.8f), new Vector2(0.75f, 0.75f));
            AssertMaps(p, new Vector2(0.4f, 0.6f), new Vector2(0.625f, 0.625f));
        }

        [Test]
        public void NonSquareIsland_KeepsEachAxisIndependent()
        {
            var p = P(new Rect(0f, 0f, 1f, 0.5f), new Rect(0f, 0f, 0.25f, 0.5f));

            Assert.AreEqual(0.25f, p.Scale.x, 1e-5f);
            Assert.AreEqual(1.0f, p.Scale.y, 1e-5f);
            AssertMaps(p, new Vector2(1f, 0.5f), new Vector2(0.25f, 0.5f));
        }

        [Test]
        public void ZeroSizedSource_DoesNotProduceNaN()
        {
            var p = P(new Rect(0.3f, 0.3f, 0f, 0f), new Rect(0f, 0f, 0.1f, 0.1f));

            Assert.AreEqual(0f, p.Scale.x);
            Assert.AreEqual(0f, p.Scale.y);
            Assert.IsFalse(float.IsNaN(p.Offset.x));
            Assert.IsFalse(float.IsNaN(p.Offset.y));
        }

        [Test]
        public void Rewrite_WhenVerticesAreDuplicated_PreservesUv7WithOriginalDimension()
        {
            var mesh = new Mesh { name = "UV channel preservation" };
            Mesh rewritten = null;
            try
            {
                mesh.vertices = new[]
                {
                    Vector3.zero, Vector3.right, Vector3.up,
                };
                mesh.uv = new[]
                {
                    Vector2.zero, Vector2.right, Vector2.up,
                };
                var uv7 = new List<Vector4>
                {
                    new Vector4(1, 2, 3, 4),
                    new Vector4(5, 6, 7, 8),
                    new Vector4(9, 10, 11, 12),
                };
                mesh.SetUVs(7, uv7);
                mesh.subMeshCount = 2;
                mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                mesh.SetTriangles(new[] { 0, 1, 2 }, 1);

                var first = new UvIsland();
                first.TriangleStarts.Add(0);
                var second = new UvIsland();
                second.TriangleStarts.Add(0);
                var placements = new[]
                {
                    new IslandPlacement
                    {
                        Mesh = mesh, SubMeshIndex = 0, Island = first,
                        Source = new Rect(0, 0, 1, 1), Target = new Rect(0, 0, 0.5f, 0.5f),
                    },
                    new IslandPlacement
                    {
                        Mesh = mesh, SubMeshIndex = 1, Island = second,
                        Source = new Rect(0, 0, 1, 1), Target = new Rect(0.5f, 0.5f, 0.5f, 0.5f),
                    },
                };

                rewritten = MeshUvRewriter.Rewrite(mesh, placements);

                Assert.NotNull(rewritten);
                Assert.AreEqual(6, rewritten.vertexCount);
                Assert.IsTrue(rewritten.HasVertexAttribute(VertexAttribute.TexCoord7));
                Assert.AreEqual(4,
                    rewritten.GetVertexAttributeDimension(VertexAttribute.TexCoord7));
                var actual = new List<Vector4>();
                rewritten.GetUVs(7, actual);
                Assert.AreEqual(6, actual.Count);
                for (int i = 0; i < 3; i++) Assert.AreEqual(uv7[i], actual[i]);
                for (int i = 0; i < 3; i++) Assert.AreEqual(uv7[i], actual[i + 3]);
            }
            finally
            {
                if (rewritten != null) Object.DestroyImmediate(rewritten);
                Object.DestroyImmediate(mesh);
            }
        }

        private static void AssertMaps(IslandPlacement p, Vector2 uvIn, Vector2 expected)
        {
            var s = p.Scale;
            var o = p.Offset;
            var actual = new Vector2(uvIn.x * s.x + o.x, uvIn.y * s.y + o.y);
            Assert.AreEqual(expected.x, actual.x, 1e-4f, $"x: {uvIn} → {actual}");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, $"y: {uvIn} → {actual}");
        }
    }
}
