using System.Linq;
using NUnit.Framework;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Atlas;

namespace HisokaKaori.HKOpti.Tests
{
    public class UvIslandTests
    {
        [Test]
        public void TwoSeparateQuads_AreTwoIslands()
        {
            // 左下の四角と、右上の四角。UV 上でつながっていない。
            var uv = new[]
            {
                new Vector2(0.0f, 0.0f), new Vector2(0.4f, 0.0f),
                new Vector2(0.4f, 0.4f), new Vector2(0.0f, 0.4f),

                new Vector2(0.6f, 0.6f), new Vector2(1.0f, 0.6f),
                new Vector2(1.0f, 1.0f), new Vector2(0.6f, 1.0f),
            };
            var tris = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

            var islands = UvIslandFinder.Find(tris, uv);

            Assert.AreEqual(2, islands.Count);
            var sorted = islands.OrderBy(i => i.Min.x).ToList();
            Assert.AreEqual(new Vector2(0.0f, 0.0f), sorted[0].Min);
            Assert.AreEqual(new Vector2(0.4f, 0.4f), sorted[0].Max);
            Assert.AreEqual(new Vector2(0.6f, 0.6f), sorted[1].Min);
            Assert.AreEqual(new Vector2(1.0f, 1.0f), sorted[1].Max);
        }

        [Test]
        public void ConnectedQuad_IsOneIsland()
        {
            var uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            var tris = new[] { 0, 1, 2, 0, 2, 3 };

            var islands = UvIslandFinder.Find(tris, uv);

            Assert.AreEqual(1, islands.Count);
            Assert.AreEqual(2, islands[0].TriangleStarts.Count);
        }

        [Test]
        public void DuplicatedVerticesAtSameUv_AreWeldedIntoOneIsland()
        {
            // 法線の切れ目で頂点が複製されている状況。UV は同じ位置。
            // ここを別の島にすると、詰め直したとき継ぎ目に線が出る。
            var uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            var tris = new[] { 0, 1, 2, 3, 4, 5 };

            var islands = UvIslandFinder.Find(tris, uv);

            Assert.AreEqual(1, islands.Count, "UV が同じ頂点は同じ島に融合されるべき");
        }

        [Test]
        public void TiledUv_IsDetected()
        {
            var uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(3f, 0f),
                new Vector2(3f, 3f), new Vector2(0f, 3f),
            };
            var tris = new[] { 0, 1, 2, 0, 2, 3 };

            var islands = UvIslandFinder.Find(tris, uv);

            Assert.AreEqual(1, islands.Count);
            Assert.IsTrue(UvIslandFinder.IsTiled(islands[0]));
        }

        [Test]
        public void EmptyInput_ReturnsEmpty()
        {
            Assert.AreEqual(0, UvIslandFinder.Find(new int[0], new Vector2[0]).Count);
            Assert.AreEqual(0, UvIslandFinder.Find(null, null).Count);
        }
    }
}
