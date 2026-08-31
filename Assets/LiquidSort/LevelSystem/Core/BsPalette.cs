using System;
using System.Collections.Generic;
using UnityEngine;

namespace BartenderSort.Core
{
    /// <summary>
    /// Renk paleti — içecek evreni (kokteyl renkleri + bira tonu + kahve ailesi).
    /// Editörde swatch olarak gösterilir, oyunda sıvı rengine çevrilir.
    /// One-pager §3: renk sayısı 4–7 [TUNABLE]; latte/americano gibi yakın tonlar
    /// yalnız geç level çeşitliliği için.
    /// </summary>
    [CreateAssetMenu(menuName = "Bartender Sort/Palette", fileName = "BsPalette")]
    public class BsPalette : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string Name;
            public Color Color;
        }

        public List<Entry> Colors = new List<Entry>();

        public int Count => Colors.Count;

        public Color ColorAt(int index)
        {
            if (index < 0 || index >= Colors.Count) return Color.magenta;
            return Colors[index].Color;
        }

        public string NameAt(int index)
        {
            if (index < 0 || index >= Colors.Count) return "?";
            var n = Colors[index].Name;
            return string.IsNullOrEmpty(n) ? ("Renk " + index) : n;
        }

        /// <summary>Palet boşsa varsayılan içecek evrenini kurar.</summary>
        public void EnsureDefaults()
        {
            if (Colors.Count > 0) return;
            void Add(string n, string hex)
            {
                ColorUtility.TryParseHtmlString(hex, out var c);
                Colors.Add(new Entry { Name = n, Color = c });
            }
            // Kokteyl renkleri
            Add("Kırmızı", "#E8453C");
            Add("Turuncu", "#F5893B");
            Add("Sarı", "#F7CE46");
            Add("Yeşil", "#5BC24B");
            Add("Mavi", "#3E8EDE");
            Add("Mor", "#7D32BF");
            Add("Pembe", "#EE6FA8");

            // 12'ydi, 7'ye indirildi — ÖLÇÜLDÜ.
            //
            // 30 level tarandı: indeks 0-6 kullanılıyor (Pembe 4 levelde, 32 kez).
            // Bira / Süt / Kahve / Karamel / Şurup ise SIFIR kez geçiyor. Sebebi
            // üreteç: BsSetup.CurveFor renk sayısını 3-7 arasında tutuyor ve
            // BsCalibrator.Harder 7'de duruyor, yani 8+ indeks hiç üretilemiyor.
            // Kahve ailesi one-pager'ın "geç level çeşitliliği" fikri için konmuştu
            // ama üretime hiç bağlanmadı.
            //
            // 7'ye kırpmak GÜVENLİ çünkü kullanılan indeksler 0-6 aralığında
            // bitişik — hiçbir Layer.Color kaymıyor, 30 level olduğu gibi çalışıyor.
            // İleride kahve ailesi istenirse SONA EKLEMEK de güvenli (indeks 7'den
            // itibaren). Tehlikeli olan araya sokmak veya sırayı değiştirmek.
        }
    }
}
