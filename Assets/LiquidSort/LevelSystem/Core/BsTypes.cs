using System;
using System.Collections.Generic;
using UnityEngine;

namespace BartenderSort.Core
{
    /// <summary>
    /// Bardak evreni. Kapasiteler one-pager §3 "Kurulum" maddesinden gelir ve
    /// <see cref="BsRules.Capacity"/> üzerinden okunur (TUNABLE).
    /// </summary>
    public enum GlassType
    {
        Shot = 0,      // 1 birim
        Kadeh = 1,     // 2 birim  (kokteyl kadehi)
        Latte = 2,     // 3 birim  (latte kupası)
        Tumbler = 3,   // 4 birim
        Bira = 4,      // 5 birim
    }

    /// <summary>Sipariş kartı tipi — one-pager §4.</summary>
    public enum OrderKind
    {
        /// <summary>Renk seti; katman sırası SERBEST. Kolay tip.</summary>
        Set = 0,
        /// <summary>Dipten yukarı KESİN dizi. Zor tip; dökme sırası önem kazanır.</summary>
        Layer = 1,
    }

    /// <summary>
    /// Bir birim sıvı. 1 birim = sabit hacim (§3 birim disiplini).
    /// <paramref name="Hidden"/> gizli renk "?" mekaniği içindir (L10+): katman
    /// en üste çıkana kadar rengi oyuncuya gösterilmez.
    /// </summary>
    [Serializable]
    public struct Layer : IEquatable<Layer>
    {
        public int Color;
        public bool Hidden;

        /// <summary>
        /// KİLİTLİ KATMAN. Bu katman, board'da bu sayıda teslim yapılana kadar
        /// bardaktan çıkarılamaz — üstüne dökmek serbesttir, ama kendisi akmaz.
        /// 0 = kilitsiz.
        /// </summary>
        public int LockUntil;

        public Layer(int color, bool hidden = false, int lockUntil = 0)
        {
            Color = color;
            Hidden = hidden;
            LockUntil = lockUntil;
        }

        public bool IsLocked(int delivered) => LockUntil > 0 && delivered < LockUntil;

        public bool Equals(Layer other) =>
            Color == other.Color && Hidden == other.Hidden && LockUntil == other.LockUntil;
        public override bool Equals(object obj) => obj is Layer l && Equals(l);
        public override int GetHashCode() => (Color * 397) ^ (Hidden ? 1 : 0) ^ (LockUntil << 8);
        public override string ToString() =>
            (Hidden ? "?" : Color.ToString()) + (LockUntil > 0 ? "L" + LockUntil : "");
    }

    /// <summary>Kural sabitleri. Hepsi one-pager'da [TUNABLE] işaretli.</summary>
    public static class BsRules
    {
        /// <summary>Bardak tipi = kapasite (§3). Sıra <see cref="GlassType"/> ile aynı.</summary>
        public static readonly int[] CapacityTable = { 1, 2, 3, 4, 5 };

        public static int Capacity(GlassType t)
        {
            int i = (int)t;
            return i >= 0 && i < CapacityTable.Length ? CapacityTable[i] : 1;
        }

        /// <summary>
        /// Sipariş kartlarında oyuncuya görünen adlar.
        ///
        /// ENUM ADLARIYLA AYNI OLMAK ZORUNDA DEĞİL — enum adı serileştirmenin
        /// (level asset'leri, dosya adları) anahtarı, bu ise kopya. İkincisi
        /// sanat değiştikçe değişir, birincisi değişemez.
        ///
        /// `Tumbler` artık bir tumbler değil: yeni teslimatta o yuva saplı bir
        /// tulip/hurricane bardağı. `Bira` da kulplu bir kupa oldu. Enum adları
        /// olduğu gibi duruyor (30 level asset'i onlara bağlı), yalnızca
        /// oyuncunun gördüğü metin güncellendi.
        /// </summary>
        public static readonly string[] GlassDisplayName =
        {
            "Shot", "Kadeh", "Latte Kupası", "Tulip Bardak", "Bira Kupası"
        };

        public static string DisplayName(GlassType t)
        {
            int i = (int)t;
            return i >= 0 && i < GlassDisplayName.Length ? GlassDisplayName[i] : t.ToString();
        }

        public static IEnumerable<GlassType> AllGlassTypes
        {
            get
            {
                yield return GlassType.Shot;
                yield return GlassType.Kadeh;
                yield return GlassType.Latte;
                yield return GlassType.Tumbler;
                yield return GlassType.Bira;
            }
        }
    }

}
