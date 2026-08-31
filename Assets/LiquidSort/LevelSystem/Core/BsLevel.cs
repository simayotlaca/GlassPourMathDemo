using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;

namespace BartenderSort.Core
{
    /// <summary>
    /// Board'daki bir bardağın başlangıç tanımı: tip + dipten yukarı sıvı dizilimi.
    /// </summary>
    [Serializable]
    public class GlassDef
    {
        public GlassType Type = GlassType.Tumbler;
        /// <summary>Dipten yukarı. Uzunluk kapasiteyi aşamaz.</summary>
        public List<Layer> Layers = new List<Layer>();

        /// <summary>
        /// SAYAÇLI ZİNCİR KİLİDİ. Bardak, board'da bu sayıda teslim yapılana kadar
        /// tamamen kullanılamaz: ne dökülür, ne doldurulur, ne teslim edilir.
        /// 0 = zincirsiz.
        /// </summary>
        public int UnlockAfter;

        public int Capacity => BsRules.Capacity(Type);

        public GlassDef Clone()
        {
            return new GlassDef { Type = Type, Layers = new List<Layer>(Layers), UnlockAfter = UnlockAfter };
        }
    }

    /// <summary>
    /// Sipariş kartı — one-pager §4. Bardak tipi + içerik hedefi.
    /// SET: renk çoklu-kümesi, sıra serbest. LAYER: dipten yukarı kesin dizi.
    /// </summary>
    [Serializable]
    public class OrderDef
    {
        /// <summary>
        /// Runtime board kurulurken level destesindeki kalıcı sıra numarası atanır.
        /// Serialize edilmez; clone/undo arasında taşınarak süre bonusunun aynı mantıksal
        /// siparişi bulmasını sağlar.
        /// </summary>
        [NonSerialized] public int RuntimeOrderIndex = -1;

        public OrderKind Kind = OrderKind.Set;
        public GlassType Glass = GlassType.Kadeh;
        /// <summary>SET için sıra anlamsızdır; LAYER için dipten yukarı sıradır.</summary>
        public List<int> Contents = new List<int>();

        /// <summary>Süreli sipariş (L15+ feature). 0 = süresiz.</summary>
        public float TimeLimit = 0f;

        public int Capacity => BsRules.Capacity(Glass);

        public OrderDef Clone()
        {
            return new OrderDef
            {
                Kind = Kind,
                Glass = Glass,
                Contents = new List<int>(Contents),
                TimeLimit = TimeLimit,
                RuntimeOrderIndex = RuntimeOrderIndex
            };
        }

        public string Describe(BsPalette pal)
        {
            var sb = new StringBuilder();
            sb.Append(BsRules.DisplayName(Glass)).Append(": ");
            if (Kind == OrderKind.Set)
            {
                // Renkleri say ve "2× Kırmızı" gibi yaz
                var counts = new Dictionary<int, int>();
                foreach (var c in Contents)
                    counts[c] = counts.TryGetValue(c, out var v) ? v + 1 : 1;
                bool first = true;
                foreach (var kv in counts)
                {
                    if (!first) sb.Append(" + ");
                    sb.Append(kv.Value).Append("x ").Append(pal ? pal.NameAt(kv.Key) : kv.Key.ToString());
                    first = false;
                }
            }
            else
            {
                for (int i = 0; i < Contents.Count; i++)
                {
                    if (i > 0) sb.Append(" > ");
                    sb.Append(pal ? pal.NameAt(Contents[i]) : Contents[i].ToString());
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Bir level'ın tüm verisi (one-pager §5).
    /// Bardak seti + başlangıç sıvı dağılımı + sipariş destesi.
    /// </summary>
    [CreateAssetMenu(menuName = "Bartender Sort/Level", fileName = "Level_000")]
    public class BsLevel : ScriptableObject
    {
        [Header("Kimlik")]
        public int Index = 1;
        public string DisplayName = "";

        [Header("Board")]
        public List<GlassDef> Glasses = new List<GlassDef>();
        /// <summary>Board'un kaç sütun halinde dizileceği (sunum).</summary>
        public int ColumnsPerRow = 4;

        [Header("Sipariş destesi (sırayla gelir)")]
        public List<OrderDef> Orders = new List<OrderDef>();
        /// <summary>Aynı anda açık slot sayısı (§3, TUNABLE = 3).</summary>
        public int OrderSlots = 3;

        [Header("Level özellikleri")]
        public bool AllowHiddenColors = false;   // L10+
        /// <summary>
        /// Sureli siparis mekaniginin level kapisi. Yalniz bu bayrak acikken ve
        /// ilgili OrderDef.TimeLimit sifirdan buyukken controller geri sayar.
        /// Kampanyanin L15+ assetleri bu mekanigi bilerek kullanir.
        /// </summary>
        public bool AllowTimedOrders = false;

        /// <summary>
        /// Bayrak kapaliyken siparis verisinde gizli bir sure birakilmasini engeller.
        /// Runtime'da bayrak yine asil guvenlik kapisidir; bu normalizasyon asset ve
        /// editor verisini de ayni invariantta tutar.
        /// </summary>
        public bool NormalizeTimedOrders()
        {
            if (AllowTimedOrders || Orders == null) return false;

            bool changed = false;
            for (int i = 0; i < Orders.Count; i++)
            {
                OrderDef order = Orders[i];
                if (order == null || order.TimeLimit == 0f) continue;
                order.TimeLimit = 0f;
                changed = true;
            }
            return changed;
        }

        [Header("Booster stokları")]
        public int UndoCount = 99;
        [FormerlySerializedAs("ExtraGlassCount")]
        public int TimeBoostCount = 99;
        public int ShuffleCount = 99;

        [Header("Doğrulama durumu (editör yazar)")]
        public bool ValidatedSolvable = false;
        public string ValidationNote = "";

        public string Title => string.IsNullOrEmpty(DisplayName) ? name : DisplayName;

        /// <summary>
        /// İçeriği kopyalar. Kimlik (Index/DisplayName) VARSAYILAN OLARAK kopyalanmaz.
        /// Sebep: kalibratör geçici bir "work" level'ından sonucu asıl asset'e
        /// kopyalıyor; kimlik de kopyalanınca work'ün varsayılan Index=1'i asıl
        /// asset'in doğru Index'ini eziyordu ve 32 level'ın 28'i "Level 1" olmuştu.
        /// </summary>
        public BsLevel DeepCopyInto(BsLevel target, bool includeIdentity = false)
        {
            if (includeIdentity)
            {
                target.Index = Index;
                target.DisplayName = DisplayName;
            }
            target.ColumnsPerRow = ColumnsPerRow;
            target.OrderSlots = OrderSlots;
            target.AllowHiddenColors = AllowHiddenColors;
            target.AllowTimedOrders = AllowTimedOrders;
            target.UndoCount = UndoCount;
            target.TimeBoostCount = TimeBoostCount;
            target.ShuffleCount = ShuffleCount;
            target.ValidatedSolvable = ValidatedSolvable;
            target.ValidationNote = ValidationNote;
            target.Glasses = new List<GlassDef>();
            foreach (var g in Glasses) target.Glasses.Add(g.Clone());
            target.Orders = new List<OrderDef>();
            foreach (var o in Orders) target.Orders.Add(o.Clone());
            target.NormalizeTimedOrders();
            return target;
        }

        /// <summary>Board'daki toplam sıvı birimi (renk bazında).</summary>
        public Dictionary<int, int> BoardColorTotals()
        {
            var d = new Dictionary<int, int>();
            foreach (var g in Glasses)
                foreach (var l in g.Layers)
                    d[l.Color] = d.TryGetValue(l.Color, out var v) ? v + 1 : 1;
            return d;
        }

        /// <summary>Sipariş destesinin toplam içerik ihtiyacı (renk bazında).</summary>
        public Dictionary<int, int> OrderColorTotals()
        {
            var d = new Dictionary<int, int>();
            foreach (var o in Orders)
                foreach (var c in o.Contents)
                    d[c] = d.TryGetValue(c, out var v) ? v + 1 : 1;
            return d;
        }

        public int TotalBoardUnits()
        {
            int n = 0;
            foreach (var g in Glasses) n += g.Layers.Count;
            return n;
        }

        public int TotalOrderUnits()
        {
            int n = 0;
            foreach (var o in Orders) n += o.Contents.Count;
            return n;
        }
    }
}
