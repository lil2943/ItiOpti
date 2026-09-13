using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using HisokaKaori.HKOpti.Editor.Atlas;

namespace HisokaKaori.HKOpti.Tests
{
    public class IslandPackerTests
    {
        private static List<PackRequest> Req(params (int id, int w, int h)[] items)
            => items.Select(i => new PackRequest { Id = i.id, Width = i.w, Height = i.h }).ToList();

        [Test]
        public void FourQuarters_FillAtlasExactly()
        {
            var r = IslandPacker.Pack(
                Req((0, 128, 128), (1, 128, 128), (2, 128, 128), (3, 128, 128)), 256, 256);

            Assert.IsTrue(r.Success);
            Assert.AreEqual(4, r.Placements.Count);
            Assert.AreEqual(1f, r.Occupancy, 0.001f);
            AssertNoOverlap(r);
        }

        [Test]
        public void PlacementsNeverOverlapAndStayInside()
        {
            var items = new List<PackRequest>();
            for (int i = 0; i < 40; i++)
                items.Add(new PackRequest { Id = i, Width = 17 + (i * 13) % 90, Height = 11 + (i * 7) % 70 });

            var r = IslandPacker.Pack(items, 512, 512);

            Assert.IsTrue(r.Success, "512x512 に 40 個は入るはず");
            AssertNoOverlap(r);
            foreach (var p in r.Placements)
            {
                Assert.GreaterOrEqual(p.X, 0);
                Assert.GreaterOrEqual(p.Y, 0);
                Assert.LessOrEqual(p.X + p.Width, r.AtlasWidth);
                Assert.LessOrEqual(p.Y + p.Height, r.AtlasHeight);
            }
        }

        [Test]
        public void TooBig_IsReportedAsFailed_NotSilentlyDropped()
        {
            var r = IslandPacker.Pack(Req((0, 300, 300)), 256, 256);

            Assert.IsFalse(r.Success);
            CollectionAssert.Contains(r.Failed, 0);
        }

        [Test]
        public void PackToSmallestSquare_PicksTheSmallestThatFits()
        {
            // 128x128 が 4 個 → 256x256 にちょうど収まる。512 を選んではいけない。
            var r = IslandPacker.PackToSmallestSquare(
                Req((0, 128, 128), (1, 128, 128), (2, 128, 128), (3, 128, 128)));

            Assert.IsTrue(r.Success);
            Assert.AreEqual(256, r.AtlasWidth);
        }

        [Test]
        public void SameInput_GivesSameResult()
        {
            // 実行するたびに配置が変わると、ビルドのたびにテクスチャが変わってしまう。
            var items = Req((5, 60, 40), (2, 60, 40), (9, 30, 90), (1, 100, 20));
            var a = IslandPacker.Pack(items, 256, 256);
            var b = IslandPacker.Pack(items.OrderBy(i => i.Id).ToList(), 256, 256);

            CollectionAssert.AreEqual(
                a.Placements.Select(p => (p.Id, p.X, p.Y)).ToList(),
                b.Placements.Select(p => (p.Id, p.X, p.Y)).ToList());
        }

        [Test]
        public void ZeroSized_IsAcceptedWithoutFailing()
        {
            var r = IslandPacker.Pack(Req((0, 0, 0), (1, 10, 10)), 64, 64);
            Assert.IsTrue(r.Success);
            Assert.AreEqual(2, r.Placements.Count);
        }

        private static void AssertNoOverlap(PackResult r)
        {
            var list = r.Placements.Where(p => p.Width > 0 && p.Height > 0).ToList();
            for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                var a = list[i]; var b = list[j];
                bool overlap = a.X < b.X + b.Width && a.X + a.Width > b.X
                            && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
                Assert.IsFalse(overlap, $"Id {a.Id} と {b.Id} が重なっている");
            }
        }
    }
}
