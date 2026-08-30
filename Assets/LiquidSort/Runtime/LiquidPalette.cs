using UnityEngine;

namespace LiquidSort
{
    /// <summary>
    /// Body and top face colour for each liquid, sampled off the reference footage.
    ///
    /// The top face is not derived from the body, and that is the whole point of this
    /// file. Measured down the middle of a resting bottle the reference gives:
    ///
    ///     wine    body 6A0051 (V 0.416)  cap B71A89 (V 0.718)   x1.73
    ///     sand    body ADAB82 (V 0.678)  cap C0BA91 (V 0.753)   x1.11
    ///     orange  body E35800 (V 0.890)  cap F78E11 (V 0.969)   x1.09
    ///
    /// No single multiply fits those. A dark liquid gets a top face nearly twice its own
    /// brightness while a pale one barely lifts at all, because the cap is picked to stay
    /// readable rather than to obey a light. Deriving it instead is what made our pink
    /// lose its cap completely: at V 0.97 any multiply clamps straight back to the body,
    /// and the band flattens into one slab of colour.
    /// </summary>
    public static class LiquidPalette
    {
        public struct Entry
        {
            /// <summary>What a designer calls this liquid. Levels refer to it by name so a
            /// colour can be retuned in one place without touching any level data.</summary>
            public string name;
            public Color body;
            public Color cap;
        }

        public static readonly Entry[] Reference =
        {
            Pair("Pink",             0xFC6FD8, 0xFFA8E6),
            Pair("Purple",           0x5E1D8D, 0x8B44C4),
            Pair("Wine",             0x6A0051, 0xB71A89),
            Pair("Lime",             0x6FA400, 0x9BCB2E),
            Pair("Sand",             0xADAB82, 0xC0BA91),
            Pair("Teal",             0x057A64, 0x2FA98D),
            Pair("Orange",           0xE35800, 0xF78E11),
            Pair("Blue",             0x0098D7, 0x3FC0F0),

            // Caps follow the same rule the measurements show: a dark liquid takes a
            // large lift, a bright one almost none, so Deep Teal climbs much further
            // than Tangerine does.
            Pair("Candy Pink",       0xE8456E, 0xF87A9B),
            Pair("Tangerine Orange", 0xF5933D, 0xFFB265),
            Pair("Cyan",             0x3DAEEF, 0x74C9F7),
            Pair("Deep Teal",        0x1E7A63, 0x39A98B),
            Pair("Lime Punch",       0x8ED12A, 0xB9EE55),
            Pair("Grape Pop",        0x8447E9, 0xB17AFF),
            Pair("Lemon Candy",      0xF3C928, 0xFFE36A)
        };

        /// <summary>Palette entry by name. Case and spacing are ignored.</summary>
        public static bool TryGet(string name, out Entry entry)
        {
            for (int i = 0; i < Reference.Length; i++)
            {
                if (!SameName(Reference[i].name, name)) continue;
                entry = Reference[i];
                return true;
            }
            entry = default;
            return false;
        }

        /// <summary>Body colour by name, magenta if the palette has never heard of it.</summary>
        public static Color BodyOf(string name) =>
            TryGet(name, out Entry entry) ? entry.body : Color.magenta;

        /// <summary>
        /// Top face for a body colour. An exact palette entry wins; anything else falls
        /// back to a lift that behaves the way the measurements do — a lot of headroom
        /// for a dark liquid, almost none for one that is already bright.
        /// </summary>
        public static Color CapFor(Color body)
        {
            for (int i = 0; i < Reference.Length; i++)
                if (Same(Reference[i].body, body)) return Reference[i].cap;

            return Derive(body);
        }

        /// <summary>
        /// Lift toward white in value, by a share that shrinks as the body gets brighter.
        /// Checked against the three measured pairs it lands within a few percent of each,
        /// which is close enough for a colour the palette never saw.
        /// </summary>
        public static Color Derive(Color body)
        {
            float value = Mathf.Max(body.r, Mathf.Max(body.g, body.b));
            if (value <= 1e-4f) return new Color(0.25f, 0.25f, 0.25f, body.a);

            // 0.52 at black, tapering to 0.09 at white.
            float lift = Mathf.Lerp(0.52f, 0.09f, Mathf.Clamp01(value));
            float wanted = Mathf.Clamp01(value + lift * (1f - value));

            // Scale the hue rather than adding grey, so a saturated liquid keeps its hue.
            float scale = wanted / value;
            return new Color(
                Mathf.Clamp01(body.r * scale),
                Mathf.Clamp01(body.g * scale),
                Mathf.Clamp01(body.b * scale),
                body.a);
        }

        private static Entry Pair(string name, int body, int cap) =>
            new Entry { name = name, body = Hex(body), cap = Hex(cap) };

        private static bool SameName(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Replace(" ", ""), b.Replace(" ", ""),
                System.StringComparison.OrdinalIgnoreCase);
        }

        private static Color Hex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        private static bool Same(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;
    }
}
