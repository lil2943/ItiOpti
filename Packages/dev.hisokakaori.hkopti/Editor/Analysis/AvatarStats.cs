using System;
using System.Collections.Generic;
using UnityEngine;
using HisokaKaori.HKOpti.Editor.Core;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    /// <summary>1 項目ぶんの計測結果。</summary>
    public sealed class StatEntry
    {
        public string Label;
        public long Value;
        public PerfRank Rank;
        public string Unit;
        public Func<PerfLevel, int> Pick;

        public string ValueText => Unit == "MB"
            ? $"{Value / (1024.0 * 1024.0):F1} MB"
            : Value.ToString("N0");

        /// <summary>次のランクに上がるために必要な値。表示用。</summary>
        public int LimitFor(PerfRank rank, bool mobile)
        {
            var levels = PerformanceThresholds.Get(mobile);
            return levels.TryGetValue(rank, out var lv) ? Pick(lv) : 0;
        }
    }

    /// <summary>
    /// アバターの負荷を計測する。仕様 7.A-1。
    ///
    /// VRChat SDK 内部の計算 API に依存せず自前で数える。理由は 2 つ：
    /// ・「最適化したらどうなるか」の予測値も同じ計算で出したいため
    /// ・SDK の内部 API はバージョンで変わるため
    /// ただし閾値だけは SDK のアセットから読む（PerformanceThresholds）。
    /// </summary>
    public sealed class AvatarStats
    {
        public readonly List<StatEntry> Entries = new List<StatEntry>();
        public bool Mobile { get; private set; }

        /// <summary>全項目のうち最も悪いランク＝総合ランク。</summary>
        public PerfRank Overall
        {
            get
            {
                var worst = PerfRank.Excellent;
                foreach (var e in Entries)
                {
                    if (e.Rank > worst) worst = e.Rank;
                }
                return worst;
            }
        }

        /// <summary>総合ランクを決めている項目（＝一番の足枷）。</summary>
        public IEnumerable<StatEntry> Bottlenecks
        {
            get
            {
                var worst = Overall;
                foreach (var e in Entries)
                {
                    if (e.Rank == worst) yield return e;
                }
            }
        }

        public static AvatarStats Measure(ReferenceIndex index, bool mobile)
        {
            var stats = new AvatarStats { Mobile = mobile };
            if (index == null || index.AvatarRoot == null) return stats;

            long polys = 0;
            long materialSlots = 0;
            var meshes = new HashSet<Mesh>();

            foreach (var smr in index.SkinnedMeshes)
            {
                if (smr == null) continue;
                materialSlots += CountMaterials(smr);
                var m = smr.sharedMesh;
                if (m != null && meshes.Add(m)) polys += SafeTriangleCount(m);
            }

            foreach (var mr in index.MeshRenderers)
            {
                if (mr == null) continue;
                materialSlots += CountMaterials(mr);
                var mf = mr.GetComponent<MeshFilter>();
                var m = mf != null ? mf.sharedMesh : null;
                if (m != null && meshes.Add(m)) polys += SafeTriangleCount(m);
            }

            // ボーン数は VRChat の数え方に合わせ、SMR が実際に使っているボーンの総数（重複除去）
            var bones = new HashSet<Transform>();
            foreach (var smr in index.SkinnedMeshes)
            {
                if (smr == null || smr.bones == null) continue;
                foreach (var b in smr.bones)
                {
                    if (b != null) bones.Add(b);
                }
            }

            long pbTransforms = 0;
            long collisionChecks = 0;
            var pbColliders = new HashSet<UnityEngine.Object>();

            foreach (var pb in index.PhysBones)
            {
                if (pb == null) continue;
                long affected = CountAffected(index, pb);
                pbTransforms += affected;

                int colliderCount = 0;
                if (pb.colliders != null)
                {
                    foreach (var c in pb.colliders)
                    {
                        if (c == null) continue;
                        colliderCount++;
                        pbColliders.Add(c);
                    }
                }
                collisionChecks += affected * colliderCount;
            }

            long contacts = 0;
            long constraints = 0;
            long audio = 0;
            long particles = 0;
            long lights = 0;
            long cloths = 0;

            foreach (var comp in index.AvatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                switch (n)
                {
                    case "VRCContactSender":
                    case "VRCContactReceiver":
                        contacts++;
                        break;
                    case "AudioSource":
                        audio++;
                        break;
                    case "ParticleSystem":
                        particles++;
                        break;
                    case "Light":
                        lights++;
                        break;
                    case "Cloth":
                        cloths++;
                        break;
                }

                // Unity Constraint と VRC Constraint の両方を数える。
                // 型名で判定するのでアセンブリ参照が要らない。
                if (n.EndsWith("Constraint", StringComparison.Ordinal)) constraints++;
            }

            long textureBytes = 0;
            foreach (var kv in index.TextureUsage)
            {
                if (kv.Key == null) continue;
                textureBytes += EstimateTextureBytes(kv.Key);
            }

            stats.Add("ポリゴン数", polys, l => l.PolyCount, mobile);
            stats.Add("Skinned Mesh 数", index.SkinnedMeshes.Count, l => l.SkinnedMeshCount, mobile);
            stats.Add("Mesh 数", index.MeshRenderers.Count, l => l.MeshCount, mobile);
            stats.Add("マテリアルスロット数", materialSlots, l => l.MaterialCount, mobile);
            stats.Add("ボーン数", bones.Count, l => l.BoneCount, mobile);
            stats.Add("テクスチャメモリ", textureBytes, l => l.TextureMegabytes, mobile, "MB");
            stats.Add("PhysBone 数", index.PhysBones.Count, l => l.PhysBoneComponentCount, mobile);
            stats.Add("PhysBone 影響 Transform", pbTransforms, l => l.PhysBoneTransformCount, mobile);
            stats.Add("PhysBone コライダー数", pbColliders.Count, l => l.PhysBoneColliderCount, mobile);
            stats.Add("Collision Check 数", collisionChecks, l => l.PhysBoneCollisionCheckCount, mobile);
            stats.Add("Contact 数", contacts, l => l.ContactCount, mobile);
            stats.Add("Constraint 数", constraints, l => l.ConstraintsCount, mobile);
            stats.Add("Audio Source 数", audio, l => l.AudioSourceCount, mobile);
            stats.Add("Particle System 数", particles, l => l.ParticleSystemCount, mobile);
            stats.Add("Light 数", lights, l => l.LightCount, mobile);
            stats.Add("Cloth 数", cloths, l => l.ClothCount, mobile);

            return stats;
        }

        private void Add(string label, long value, Func<PerfLevel, int> pick, bool mobile,
            string unit = null)
        {
            // テクスチャだけバイト単位で持っているので、判定は MB に直してから行う
            long compare = unit == "MB" ? (long)Math.Ceiling(value / (1024.0 * 1024.0)) : value;

            Entries.Add(new StatEntry
            {
                Label = label,
                Value = value,
                Unit = unit,
                Pick = pick,
                Rank = PerformanceThresholds.RankFor(compare, pick, mobile),
            });
        }

        private static int CountMaterials(Renderer r)
        {
            var mats = r.sharedMaterials;
            return mats?.Length ?? 0;
        }

        private static long SafeTriangleCount(Mesh m)
        {
            try
            {
                long n = 0;
                for (int i = 0; i < m.subMeshCount; i++)
                {
                    n += m.GetIndexCount(i) / 3;
                }
                return n;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// この PhysBone 1 本が影響する Transform の数。
        ///
        /// VRChat はチェーンのルート自身を数に含めない（ルートは動かないため）ので、
        /// チェーン全体から 1 を引く。ignoreTransforms で切られた枝は
        /// ReferenceIndex 側で既に除外されている。
        /// </summary>
        private static long CountAffected(ReferenceIndex index,
            VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone pb)
        {
            if (!index.PhysBoneChains.TryGetValue(pb, out var chain)) return 0;
            return Math.Max(0, chain.Count - 1);
        }

        /// <summary>
        /// テクスチャの VRAM 使用量の概算。圧縮形式ごとの 1 ピクセルあたりビット数から求める。
        /// ミップマップぶんは約 1.33 倍で見積もる。
        /// </summary>
        public static long EstimateTextureBytes(Texture tex)
        {
            if (tex == null) return 0;

            int w = tex.width;
            int h = tex.height;
            double bpp = 8.0;

            if (tex is Texture2D t2d)
            {
                bpp = BitsPerPixel(t2d.format);
            }
            else if (tex is Cubemap cube)
            {
                bpp = BitsPerPixel(cube.format) * 6;
            }

            double bytes = w * (double)h * bpp / 8.0;
            if (tex is Texture2D tt && tt.mipmapCount > 1) bytes *= 1.3333;
            return (long)bytes;
        }

        private static double BitsPerPixel(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4:
                case TextureFormat.ETC_RGB4:
                case TextureFormat.ETC2_RGB:
                    return 4;

                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                    return 8;

                case TextureFormat.RGB24:
                    return 24;
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                    return 32;
                case TextureFormat.R8:
                case TextureFormat.Alpha8:
                    return 8;
                case TextureFormat.RGBAHalf:
                    return 64;
                case TextureFormat.RGBAFloat:
                    return 128;

                default:
                    // ASTC など、ここで拾えない形式は 8bpp とみなす（大きめに見積もる）
                    return 8;
            }
        }
    }
}
