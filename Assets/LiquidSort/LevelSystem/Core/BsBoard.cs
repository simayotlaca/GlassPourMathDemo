using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace BartenderSort.Core
{
    /// <summary>Runtime bardak. Dipten yukarı katmanlar.</summary>
    public class RtGlass
    {
        public GlassType Type;
        public List<Layer> Layers = new List<Layer>();
        /// <summary>Sahnedeki kimlik — view eşlemesi ve undo için sabit kalır.</summary>
        public int Id;
        /// <summary>Sayaçlı zincir kilidi; bu kadar teslim yapılana dek bardak kilitli.</summary>
        public int UnlockAfter;

        public int Capacity => BsRules.Capacity(Type);
        public int Free => Capacity - Layers.Count;
        public bool IsEmpty => Layers.Count == 0;
        public bool IsFull => Layers.Count >= Capacity;

        /// <summary>Zincir hâlâ takılı mı?</summary>
        public bool IsChained(int delivered) => UnlockAfter > 0 && delivered < UnlockAfter;

        /// <summary>Zincirin açılmasına kaç teslim kaldı.</summary>
        public int ChainRemaining(int delivered) => Math.Max(0, UnlockAfter - delivered);

        public RtGlass Clone() => new RtGlass
        { Type = Type, Id = Id, UnlockAfter = UnlockAfter, Layers = new List<Layer>(Layers) };

        /// <summary>
        /// En üstteki bitişik aynı-renk zincirin uzunluğu.
        /// Kilitli katman akmaz ve zinciri keser; en üst katman kilitliyse hiç akmaz.
        /// </summary>
        public int TopChainLength(int delivered)
        {
            if (Layers.Count == 0) return 0;
            int top = Layers.Count - 1;
            if (Layers[top].IsLocked(delivered)) return 0;   // kilitli katman çıkamaz
            int color = Layers[top].Color;
            int n = 1;
            for (int i = top - 1; i >= 0; i--)
            {
                // Gizli katman zinciri keser: içeriği bilinmeden akıtılamaz.
                if (Layers[i].Hidden || Layers[i].Color != color) break;
                if (Layers[i].IsLocked(delivered)) break;    // kilitli katman zinciri keser
                n++;
            }
            return n;
        }

        public bool HasLocked(int delivered)
        {
            for (int i = 0; i < Layers.Count; i++)
                if (Layers[i].IsLocked(delivered)) return true;
            return false;
        }

        public bool HasHidden()
        {
            for (int i = 0; i < Layers.Count; i++)
                if (Layers[i].Hidden) return true;
            return false;
        }

        /// <summary>Gizli renk kuralı: en üste çıkan katman açılır (Magic Sort birebir).</summary>
        public bool RevealTop()
        {
            if (Layers.Count == 0) return false;
            int top = Layers.Count - 1;
            if (!Layers[top].Hidden) return false;
            var l = Layers[top];
            l.Hidden = false;
            Layers[top] = l;
            return true;
        }
    }

    public struct PourResult
    {
        public bool Success;
        public int Amount;
        public int Color;
        public string Reason;

        public static PourResult Fail(string reason) => new PourResult { Success = false, Reason = reason };
    }

    /// <summary>
    /// Oyunun kural motoru. Görselden tamamen bağımsız — hem oynanış hem
    /// editördeki simülatör/solver aynı sınıfı kullanır.
    /// Kurallar one-pager §3 "Sistem kuralları (sıralı)" maddesinden gelir.
    /// </summary>
    public class BsBoard
    {
        sealed class OrderReferenceComparer : IEqualityComparer<OrderDef>
        {
            public static readonly OrderReferenceComparer Instance = new OrderReferenceComparer();

            OrderReferenceComparer() { }

            public bool Equals(OrderDef x, OrderDef y) => ReferenceEquals(x, y);
            public int GetHashCode(OrderDef obj) => RuntimeHelpers.GetHashCode(obj);
        }

        public List<RtGlass> Glasses = new List<RtGlass>();
        /// <summary>Açık sipariş slotları. null = boş slot.</summary>
        public OrderDef[] Slots;
        /// <summary>Desteden gelecek sıradaki siparişin indeksi.</summary>
        public int DeckIndex;
        public int Delivered;
        /// <summary>
        /// TimeLimit tek basina timer acmaz. Level feature bayragi runtime board'a
        /// kopyalanir ve gateway deadline kararinda authoritative kapidir.
        /// </summary>
        public bool TimedOrdersEnabled { get; private set; }

        readonly List<OrderDef> _deck = new List<OrderDef>();
        int _nextGlassId;

        public int TotalOrders => _deck.Count;
        public IReadOnlyList<OrderDef> Deck => _deck;

        public static BsBoard FromLevel(BsLevel level)
        {
            var b = new BsBoard { TimedOrdersEnabled = level.AllowTimedOrders };
            b.Slots = new OrderDef[Math.Max(1, level.OrderSlots)];
            foreach (var g in level.Glasses)
            {
                var rt = new RtGlass { Type = g.Type, Id = b._nextGlassId++, UnlockAfter = g.UnlockAfter };
                int cap = BsRules.Capacity(g.Type);
                for (int i = 0; i < g.Layers.Count && i < cap; i++)
                    rt.Layers.Add(g.Layers[i]);
                b.Glasses.Add(rt);
            }
            for (int orderIndex = 0; orderIndex < level.Orders.Count; orderIndex++)
            {
                OrderDef order = level.Orders[orderIndex].Clone();
                order.RuntimeOrderIndex = orderIndex;
                b._deck.Add(order);
            }
            b.RefillSlots();
            b.RevealAllTops();
            return b;
        }

        public BsBoard Clone()
        {
            var orderClones = new Dictionary<OrderDef, OrderDef>(OrderReferenceComparer.Instance);

            OrderDef CloneOrder(OrderDef source)
            {
                if (source == null) return null;
                if (orderClones.TryGetValue(source, out OrderDef clone)) return clone;

                clone = source.Clone();
                orderClones.Add(source, clone);
                return clone;
            }

            var b = new BsBoard
            {
                Slots = new OrderDef[Slots.Length],
                DeckIndex = DeckIndex,
                Delivered = Delivered,
                TimedOrdersEnabled = TimedOrdersEnabled,
                _nextGlassId = _nextGlassId
            };
            foreach (var g in Glasses) b.Glasses.Add(g.Clone());
            foreach (var order in _deck) b._deck.Add(CloneOrder(order));
            for (int i = 0; i < Slots.Length; i++) b.Slots[i] = CloneOrder(Slots[i]);
            return b;
        }

        public void RevealAllTops()
        {
            foreach (var g in Glasses) g.RevealTop();
        }

        /// <summary>
        /// Slotlari tazeler: kartlar SOLA cokur, yeni kart hep SAGDAN girer.
        ///
        /// Eskiden bosalan slot yerinde doldurulurdu; bir kart teslim edilince
        /// yenisi tam ayni yere gelirdi ve kuyruk hissi olusmazdi. Artik bir
        /// kart gidince soldaki komsulari kayiyor ve taze siparis kuyrugun
        /// sonuna ekleniyor.
        ///
        /// Cozulebilirlik etkilenmez: slotlardaki siparis KUMESI ayni, yalnizca
        /// dizilisi degisiyor. Yan fayda, StateKey'in artik kanonik slot
        /// sirasi gormesi -- ayni durum tek anahtar uretir.
        /// </summary>
        public void RefillSlots()
        {
            int write = 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                if (Slots[i] == null) continue;
                OrderDef card = Slots[i];
                Slots[i] = null;
                Slots[write++] = card;
            }
            for (int i = write; i < Slots.Length; i++)
            {
                if (DeckIndex >= _deck.Count) break;
                Slots[i] = _deck[DeckIndex++];
            }
        }

        public RtGlass GlassById(int id)
        {
            foreach (var g in Glasses) if (g.Id == id) return g;
            return null;
        }

        // ---------------------------------------------------------------
        //  DÖKME  (§3 kural 1 + 2)
        // ---------------------------------------------------------------

        /// <summary>
        /// Kural 1: kaynaktaki EN ÜST bitişik aynı-renk zincir, hedefte yer olduğu kadar akar.
        /// Kural 2: SERBEST DÖKME — hedefte yer varsa HER renk HER rengin üstüne
        /// dökülebilir. Aynı-renk-üstüne kısıtı YOKTUR; hedefin tamamlanmış (✓)
        /// olması da engel değildir (MVP kararı, bkz. gövdedeki not).
        /// </summary>
        public PourResult CanPour(RtGlass src, RtGlass dst)
        {
            if (src == null || dst == null) return PourResult.Fail("Geçersiz bardak");
            if (ReferenceEquals(src, dst)) return PourResult.Fail("Kaynak ve hedef aynı");
            if (src.IsChained(Delivered))
                return PourResult.Fail($"Bardak zincirli — {src.ChainRemaining(Delivered)} teslim kaldı");
            if (dst.IsChained(Delivered))
                return PourResult.Fail($"Hedef zincirli — {dst.ChainRemaining(Delivered)} teslim kaldı");
            if (src.IsEmpty) return PourResult.Fail("Kaynak boş");
            if (dst.Free <= 0) return PourResult.Fail("Hedefte yer yok");
            // Domain tamamlanmış (✓) bardağı ayrıca dondurmaz. Mevcut pointer sunumu
            // hazır bardağa dokunmayı doğrudan teslim niyeti olarak yorumlasa da solver,
            // test veya başka bir input adapter'ı aynı serbest-dökme kuralını kullanır.

            int chain = src.TopChainLength(Delivered);
            if (chain <= 0)
                return src.Layers[src.Layers.Count - 1].IsLocked(Delivered)
                    ? PourResult.Fail("Üst katman kilitli")
                    : PourResult.Fail("Akıtılacak katman yok");

            int amount = Math.Min(chain, dst.Free);
            return new PourResult
            {
                Success = true,
                Amount = amount,
                Color = src.Layers[src.Layers.Count - 1].Color
            };
        }

        public PourResult Pour(RtGlass src, RtGlass dst)
        {
            var r = CanPour(src, dst);
            if (!r.Success) return r;

            for (int i = 0; i < r.Amount; i++)
            {
                var l = src.Layers[src.Layers.Count - 1];
                src.Layers.RemoveAt(src.Layers.Count - 1);
                dst.Layers.Add(new Layer(l.Color, false));
            }
            src.RevealTop();   // gizli renk açılır
            return r;
        }

        public bool AnyPourAvailable()
        {
            for (int i = 0; i < Glasses.Count; i++)
                for (int j = 0; j < Glasses.Count; j++)
                {
                    if (i == j) continue;
                    if (CanPour(Glasses[i], Glasses[j]).Success) return true;
                }
            return false;
        }

        // ---------------------------------------------------------------
        //  EŞLEŞME & TESLİM  (§3 kural 3 + 4)
        // ---------------------------------------------------------------

        /// <summary>Bardak içeriği verilen kartı karşılıyor mu?</summary>
        public static bool Matches(RtGlass g, OrderDef o)
        {
            if (g == null) return false;
            // Zincir/kilit kontrolü MatchedSlot'ta yapılır (Delivered gerekiyor).
            // Tahsisatsız yol: CanPour bunu her aday hamlede çağırıyor, sıcak döngü.
            return MatchesLayers(g.Type, g.Layers, o);
        }

        /// <summary>
        /// Bardağın karşıladığı slot indeksi; yoksa -1.
        /// Kural 3: birden çok kartı karşılıyorsa SOLDAKİ kart önceliklidir.
        /// </summary>
        // [ThreadStatic]: bkz. BsAiTester'daki aynı gerekçe — ölçüm arka plan
        // iş parçacığında da koşuyor, paylaşılan tampon sessizce yanlış sonuç verir.
        [ThreadStatic] static List<int> _matchScratchTs;
        static List<int> MatchScratch => _matchScratchTs ?? (_matchScratchTs = new List<int>(8));

        /// <summary>
        /// Varsayımsal bir katman dizisi verilen kartı karşılıyor mu?
        /// Board klonlamadan "bu hamle siparişi tamamlar mı?" sorusuna cevap verir —
        /// AI tester'ın sıcak döngüsü bunu kullanır, tahsisat yapmaz.
        /// </summary>
        public static bool MatchesLayers(GlassType type, List<Layer> layers, OrderDef o)
        {
            if (o == null || layers == null) return false;
            if (type != o.Glass) return false;
            if (layers.Count != o.Contents.Count) return false;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Hidden) return false;

            if (o.Kind == OrderKind.Layer)
            {
                for (int i = 0; i < o.Contents.Count; i++)
                    if (layers[i].Color != o.Contents[i]) return false;
                return true;
            }

            // SET: sıra serbest — küçük listede eşleşenleri düşerek karşılaştır
            MatchScratch.Clear();
            MatchScratch.AddRange(o.Contents);
            for (int i = 0; i < layers.Count; i++)
            {
                int idx = MatchScratch.IndexOf(layers[i].Color);
                if (idx < 0) return false;
                MatchScratch.RemoveAt(idx);
            }
            return MatchScratch.Count == 0;
        }

        public int MatchedSlot(RtGlass g)
        {
            if (g == null) return -1;
            // Bardak ve katman kilitleri teslimi de engeller. Yalnız dökmeyi
            // engellemek yeterli değildir: içerik bir siparişle zaten eşleşiyorsa
            // kilit eşiği gelmeden bardak doğrudan servis edilebilirdi.
            if (g.IsChained(Delivered) || g.HasLocked(Delivered)) return -1;
            for (int i = 0; i < Slots.Length; i++)
                if (Slots[i] != null && Matches(g, Slots[i])) return i;
            return -1;
        }

        public bool AnyDeliverable()
        {
            foreach (var g in Glasses) if (MatchedSlot(g) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Teslim: bardak tezgâha uçar, kayar, SAHNEDEN ÇIKAR. Slot boşalır,
        /// destede kart varsa yerine gelir.
        /// </summary>
        public bool Deliver(RtGlass g, out int slotIndex)
        {
            slotIndex = MatchedSlot(g);
            if (slotIndex < 0) return false;
            Slots[slotIndex] = null;
            Glasses.Remove(g);          // bardak sahneden çıkar
            Delivered++;
            RefillSlots();
            return true;
        }

        // ---------------------------------------------------------------
        //  BİTİŞ DURUMLARI  (§3 kural 5 + 6)
        // ---------------------------------------------------------------

        /// <summary>WIN: sipariş listesi bitti (deste tükendi ve açık slot kalmadı).</summary>
        public bool IsWin()
        {
            if (DeckIndex < _deck.Count) return false;
            foreach (var s in Slots) if (s != null) return false;
            return true;
        }

        /// <summary>FAIL: hiçbir geçerli dökme mümkün değil VE hiçbir bardak teslim edilemez.</summary>
        public bool IsFail()
        {
            if (IsWin()) return false;
            return !AnyDeliverable() && !AnyPourAvailable();
        }

        // ---------------------------------------------------------------
        //  BOOSTER
        // ---------------------------------------------------------------

        /// <summary>+boş bardak (fail kurtarıcı). Miro fail-senaryosundaki "ekstra boş bardak".</summary>
        public RtGlass AddEmptyGlass(GlassType type)
        {
            var g = new RtGlass { Type = type, Id = _nextGlassId++ };
            Glasses.Add(g);
            return g;
        }

        // ---------------------------------------------------------------
        //  SOLVER DESTEĞİ
        // ---------------------------------------------------------------

        /// <summary>
        /// Kanonik durum anahtarı. Aynı tip + aynı içerikli bardaklar sıralanır ki
        /// permütasyonlar tek durum sayılsın (arama uzayını ciddi biçimde daraltır).
        /// </summary>
        public string StateKey()
        {
            var parts = new List<string>(Glasses.Count);
            var sb = new StringBuilder();
            foreach (var g in Glasses)
            {
                sb.Clear();
                sb.Append((int)g.Type).Append('/').Append(g.UnlockAfter).Append(':');
                foreach (var l in g.Layers)
                {
                    sb.Append(l.Hidden ? "?" : l.Color.ToString());
                    if (l.LockUntil > 0) sb.Append('L').Append(l.LockUntil);
                    sb.Append(',');
                }
                parts.Add(sb.ToString());
            }
            parts.Sort(StringComparer.Ordinal);

            sb.Clear();
            // Kilitler teslim sayısına bağlı olduğu için Delivered durumun parçasıdır.
            sb.Append(DeckIndex).Append('#').Append(Delivered).Append('|');
            foreach (var s in Slots)
                sb.Append(s == null ? "-" : (((int)s.Glass) + "/" + (int)s.Kind + "/" + string.Join(".", s.Contents))).Append(';');
            sb.Append('|');
            foreach (var p in parts) sb.Append(p).Append('#');
            return sb.ToString();
        }
    }
}
