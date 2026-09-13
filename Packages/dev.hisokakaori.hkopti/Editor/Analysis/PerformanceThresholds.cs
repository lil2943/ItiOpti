using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HisokaKaori.HKOpti.Editor.Analysis
{
    public enum PerfRank
    {
        Excellent = 0,
        Good = 1,
        Medium = 2,
        Poor = 3,
        VeryPoor = 4,
    }

    /// <summary>1 ランクぶんの閾値。SDK のアセットのフィールド名に合わせている。</summary>
    public sealed class PerfLevel
    {
        public int PolyCount;
        public int SkinnedMeshCount;
        public int MeshCount;
        public int MaterialCount;
        public int BoneCount;
        public int TextureMegabytes;
        public int PhysBoneComponentCount;
        public int PhysBoneTransformCount;
        public int PhysBoneColliderCount;
        public int PhysBoneCollisionCheckCount;
        public int ContactCount;
        public int ConstraintsCount;
        public int AudioSourceCount;
        public int ParticleSystemCount;
        public int LightCount;
        public int ClothCount;
    }

    /// <summary>
    /// VRChat SDK が同梱している公式の閾値アセットを読む。仕様 11.1。
    ///
    /// 【重要】閾値をコードに書かない。SDK を更新すれば自動で追従させるため。
    /// 実際、記憶で書いた値は複数間違っていた（Constraints は 1 桁違っていた）。
    ///
    /// 型を参照せず SerializedObject で読むので、SDK 側のクラス名が変わっても壊れない。
    /// </summary>
    public static class PerformanceThresholds
    {
        private const string WindowsPath =
            "Validation/Performance/StatsLevels/Windows/AvatarPerformanceStatLevels_Windows";

        private const string QuestPath =
            "Validation/Performance/StatsLevels/Quest/AvatarPerformanceStatLevels_Quest";

        private static Dictionary<PerfRank, PerfLevel> _windows;
        private static Dictionary<PerfRank, PerfLevel> _quest;

        /// <summary>読み込みに失敗した場合の理由。UI に出して原因を分かるようにする。</summary>
        public static string LoadError { get; private set; }

        public static Dictionary<PerfRank, PerfLevel> Get(bool mobile)
        {
            if (mobile)
                return _quest ?? (_quest = Load(QuestPath));
            return _windows ?? (_windows = Load(WindowsPath));
        }

        /// <summary>キャッシュを捨てる。SDK を更新したときに使う。</summary>
        public static void Reload()
        {
            _windows = null;
            _quest = null;
            LoadError = null;
            MissingFields.Clear();
        }

        private static Dictionary<PerfRank, PerfLevel> Load(string resourcePath)
        {
            var result = new Dictionary<PerfRank, PerfLevel>();

            var set = Resources.Load<ScriptableObject>(resourcePath);
            if (set == null)
            {
                LoadError =
                    $"SDK の閾値アセットが見つかりません: Resources/{resourcePath}\n" +
                    "VRChat SDK が入っているか、パスが変わっていないか確認してください。";
                Debug.LogWarning("[ItiOptimiser] " + LoadError);
                return result;
            }

            using (var so = new SerializedObject(set))
            {
                TryLoadLevel(so, "excellent", PerfRank.Excellent, result);
                TryLoadLevel(so, "good", PerfRank.Good, result);
                TryLoadLevel(so, "medium", PerfRank.Medium, result);
                TryLoadLevel(so, "poor", PerfRank.Poor, result);
            }

            return result;
        }

        private static void TryLoadLevel(SerializedObject parent, string fieldName,
            PerfRank rank, Dictionary<PerfRank, PerfLevel> into)
        {
            var prop = parent.FindProperty(fieldName);
            if (prop == null || prop.objectReferenceValue == null)
            {
                Debug.LogWarning($"[ItiOptimiser] 閾値アセットに '{fieldName}' がありません。");
                return;
            }

            using (var so = new SerializedObject(prop.objectReferenceValue))
            {
                into[rank] = new PerfLevel
                {
                    PolyCount = Int(so, "polyCount"),
                    SkinnedMeshCount = Int(so, "skinnedMeshCount"),
                    MeshCount = Int(so, "meshCount"),
                    MaterialCount = Int(so, "materialCount"),
                    BoneCount = Int(so, "boneCount"),
                    TextureMegabytes = Int(so, "textureMegabytes"),
                    PhysBoneComponentCount = Int(so, "physBone.componentCount"),
                    PhysBoneTransformCount = Int(so, "physBone.transformCount"),
                    PhysBoneColliderCount = Int(so, "physBone.colliderCount"),
                    PhysBoneCollisionCheckCount = Int(so, "physBone.collisionCheckCount"),
                    ContactCount = Int(so, "contactCount"),
                    ConstraintsCount = Int(so, "constraintsCount"),
                    AudioSourceCount = Int(so, "audioSourceCount"),
                    ParticleSystemCount = Int(so, "particleSystemCount"),
                    LightCount = Int(so, "lightCount"),
                    ClothCount = Int(so, "clothCount"),
                };
            }
        }

        /// <summary>読み取れなかったフィールド。黙って誤判定しないよう UI に出す。</summary>
        public static readonly List<string> MissingFields = new List<string>();

        /// <summary>
        /// 閾値のフィールドを 1 つ読む。
        ///
        /// 【この関数で一度バグを出している】
        /// 当初 Integer 型しか受け付けず、float のフィールド（textureMegabytes）で
        /// int.MaxValue を返していた。その結果「127.7 MB なのに Excellent」という
        /// 嘘の判定が出た。読めない項目を「上限なし」に倒すと、
        /// 誤りが必ず「実際より良いランク」の方向に出るので危険。
        /// 読めなかったことを必ず記録して表に出すこと。
        /// </summary>
        private static int Int(SerializedObject so, string path)
        {
            var p = so.FindProperty(path);
            if (p == null)
            {
                Record(so, path, "フィールドが存在しない");
                return int.MaxValue;
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    // long のフィールドでも propertyType は Integer になる
                    return p.longValue > int.MaxValue ? int.MaxValue : (int)p.longValue;

                case SerializedPropertyType.Float:
                    // float / double。切り上げると閾値が緩くなるので切り捨てる
                    return (int)System.Math.Floor(p.doubleValue);

                default:
                    Record(so, path, $"想定外の型 {p.propertyType}");
                    return int.MaxValue;
            }
        }

        private static void Record(SerializedObject so, string path, string reason)
        {
            var name = so.targetObject != null ? so.targetObject.name : "(不明)";
            var msg = $"{name}.{path}: {reason}";
            if (!MissingFields.Contains(msg)) MissingFields.Add(msg);
            Debug.LogWarning($"[ItiOptimiser] 閾値を読めませんでした → {msg}");
        }

        /// <summary>
        /// 値が閾値以下かで、その項目のランクを求める。
        /// SDK と同じく「以下なら合格」で判定する。
        /// </summary>
        public static PerfRank RankFor(long value, System.Func<PerfLevel, int> pick, bool mobile)
        {
            var levels = Get(mobile);
            foreach (var rank in new[] { PerfRank.Excellent, PerfRank.Good, PerfRank.Medium, PerfRank.Poor })
            {
                if (!levels.TryGetValue(rank, out var lv)) continue;
                if (value <= pick(lv)) return rank;
            }
            return PerfRank.VeryPoor;
        }

        public static string DisplayName(PerfRank r)
        {
            switch (r)
            {
                case PerfRank.Excellent: return "Excellent";
                case PerfRank.Good: return "Good";
                case PerfRank.Medium: return "Medium";
                case PerfRank.Poor: return "Poor";
                default: return "Very Poor";
            }
        }

        public static Color ColorOf(PerfRank r)
        {
            switch (r)
            {
                case PerfRank.Excellent: return new Color(0.20f, 0.75f, 0.95f);
                case PerfRank.Good: return new Color(0.30f, 0.80f, 0.35f);
                case PerfRank.Medium: return new Color(0.95f, 0.80f, 0.20f);
                case PerfRank.Poor: return new Color(0.95f, 0.55f, 0.15f);
                default: return new Color(0.95f, 0.30f, 0.30f);
            }
        }
    }
}
