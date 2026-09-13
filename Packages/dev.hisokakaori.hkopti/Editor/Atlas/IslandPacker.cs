using System;
using System.Collections.Generic;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Atlas
{
    /// <summary>詰め込みたい 1 個の長方形。単位はピクセル。</summary>
    public struct PackRequest
    {
        public int Id;      // 呼び出し側が島を特定するための番号
        public int Width;
        public int Height;
    }

    /// <summary>詰め込んだ結果の位置。単位はピクセル。左下原点。</summary>
    public struct PackPlacement
    {
        public int Id;
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    public sealed class PackResult
    {
        public int AtlasWidth;
        public int AtlasHeight;
        public readonly List<PackPlacement> Placements = new List<PackPlacement>();
        public readonly List<int> Failed = new List<int>();   // 入りきらなかった Id
        public bool Success => Failed.Count == 0;

        /// <summary>実際に使われた面積の割合。詰め込み効率の実測値。</summary>
        public float Occupancy
        {
            get
            {
                long area = 0;
                foreach (var p in Placements) area += (long)p.Width * p.Height;
                long total = (long)AtlasWidth * AtlasHeight;
                return total > 0 ? (float)(area / (double)total) : 0f;
            }
        }
    }

    /// <summary>
    /// 長方形の詰め込み（MaxRects 法、Best Short Side Fit）。
    ///
    /// 回転は使わない。回転すると UV の書き換えに 90 度回転が入り、
    /// 法線マップの向きまで直す必要が出てくる（間違えると陰影が反転する）。
    /// 詰め込み効率より、壊れないことを優先する。
    /// </summary>
    public static class IslandPacker
    {
        /// <summary>
        /// 指定サイズのアトラスに詰める。入りきらなければ Failed に入る。
        /// </summary>
        public static PackResult Pack(IEnumerable<PackRequest> requests, int atlasWidth, int atlasHeight)
        {
            var result = new PackResult { AtlasWidth = atlasWidth, AtlasHeight = atlasHeight };

            var items = new List<PackRequest>(requests ?? Array.Empty<PackRequest>());
            // 大きいものから置く。小さいものを先に置くと大きな空きが分断されて入らなくなる。
            items.Sort((a, b) =>
            {
                int byArea = ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height);
                if (byArea != 0) return byArea;
                return a.Id.CompareTo(b.Id);   // 実行ごとに結果が変わらないように
            });

            var free = new List<RectInt> { new RectInt(0, 0, atlasWidth, atlasHeight) };

            foreach (var item in items)
            {
                if (item.Width <= 0 || item.Height <= 0)
                {
                    result.Placements.Add(new PackPlacement
                    { Id = item.Id, X = 0, Y = 0, Width = 0, Height = 0 });
                    continue;
                }

                int bestIndex = -1, bestShort = int.MaxValue, bestLong = int.MaxValue;
                for (int i = 0; i < free.Count; i++)
                {
                    var f = free[i];
                    if (f.width < item.Width || f.height < item.Height) continue;
                    int leftoverH = f.width - item.Width;
                    int leftoverV = f.height - item.Height;
                    int shortSide = Mathf.Min(leftoverH, leftoverV);
                    int longSide = Mathf.Max(leftoverH, leftoverV);
                    if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
                    {
                        bestIndex = i; bestShort = shortSide; bestLong = longSide;
                    }
                }

                if (bestIndex < 0) { result.Failed.Add(item.Id); continue; }

                var target = free[bestIndex];
                var placed = new RectInt(target.x, target.y, item.Width, item.Height);
                result.Placements.Add(new PackPlacement
                {
                    Id = item.Id,
                    X = placed.x,
                    Y = placed.y,
                    Width = placed.width,
                    Height = placed.height
                });

                SplitFreeRects(free, placed);
                PruneContained(free);
            }

            result.Placements.Sort((a, b) => a.Id.CompareTo(b.Id));
            return result;
        }

        /// <summary>
        /// 全部入る中でいちばん小さい 2 の冪の正方形アトラスを探す。
        /// 小さい方から試すので、無駄に大きなテクスチャを作らない。
        /// </summary>
        public static PackResult PackToSmallestSquare(
            IEnumerable<PackRequest> requests, int minSize = 32, int maxSize = 4096)
        {
            var list = new List<PackRequest>(requests ?? Array.Empty<PackRequest>());
            PackResult last = null;
            for (int size = Mathf.Max(1, minSize); size <= maxSize; size *= 2)
            {
                last = Pack(list, size, size);
                if (last.Success) return last;
            }
            return last ?? new PackResult { AtlasWidth = maxSize, AtlasHeight = maxSize };
        }

        private static void SplitFreeRects(List<RectInt> free, RectInt used)
        {
            for (int i = free.Count - 1; i >= 0; i--)
            {
                var f = free[i];
                if (!Overlaps(f, used)) continue;
                free.RemoveAt(i);

                // 使った矩形の上下左右に残る帯を、新しい空き矩形として登録する
                if (used.x > f.x)
                    free.Add(new RectInt(f.x, f.y, used.x - f.x, f.height));
                if (used.xMax < f.xMax)
                    free.Add(new RectInt(used.xMax, f.y, f.xMax - used.xMax, f.height));
                if (used.y > f.y)
                    free.Add(new RectInt(f.x, f.y, f.width, used.y - f.y));
                if (used.yMax < f.yMax)
                    free.Add(new RectInt(f.x, used.yMax, f.width, f.yMax - used.yMax));
            }
        }

        /// <summary>他の空き矩形に完全に含まれる空き矩形は要らないので消す。</summary>
        private static void PruneContained(List<RectInt> free)
        {
            for (int i = free.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < free.Count; j++)
                {
                    if (i == j) continue;
                    if (Contains(free[j], free[i])) { free.RemoveAt(i); break; }
                }
            }
        }

        private static bool Overlaps(RectInt a, RectInt b)
            => a.x < b.xMax && a.xMax > b.x && a.y < b.yMax && a.yMax > b.y;

        private static bool Contains(RectInt outer, RectInt inner)
            => inner.x >= outer.x && inner.y >= outer.y
            && inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;
    }
}
