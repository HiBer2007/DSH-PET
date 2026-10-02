using System;
using System.Globalization;

namespace DshPet.Core.Model
{
    /// <summary>
    /// Five significant digits with an SI-style suffix, for the numbers on the panel.
    ///
    /// The panel is read at a glance, and what a glance needs is the size of the
    /// number, not its exact tail: "1.2345K" is legible in one look where
    /// "1234.5678" has to be counted digit by digit. Five digits is the compromise
    /// between the two - enough to see a deduction land, few enough to read.
    ///
    /// Upward the suffixes are K/M/B (thousands, millions, billions); downward
    /// m/u/p (thousandths, millionths, billionths), which is what a single API call
    /// costs - a fraction of a cent, and without them it renders as a row of zeros
    /// with a digit hiding at the end.
    /// </summary>
    public static class Sig
    {
        /// <summary>Formats <paramref name="v"/> to five significant digits with a suffix.</summary>
        public static string Five(double v)
        {
            if (double.IsNaN(v)) return "--";
            if (v == 0) return "0.0000";

            string sign = v < 0 ? "-" : "";
            double a = Math.Abs(v);

            // The exponent is snapped to a multiple of three so the mantissa always
            // lands in [1, 1000), which is what makes one decimal rule work for every
            // suffix instead of a table of special cases.
            int e = (int)Math.Floor(Math.Log10(a) / 3.0) * 3;
            if (e > 9) e = 9;
            if (e < -9) e = -9;
            // No milli tier: "0.002411" says the same thing as "2.411m" and says it in
            // the form the number is actually spoken in. Below a thousandth the plain
            // form stops being readable at all (0.0000005), so u and p stay.

            // Rounding can push the mantissa over the boundary - 999.9999 formats as
            // "1000.00" - so the suffix steps up and the mantissa is recomputed rather
            // than printed as a four-digit mantissa.
            for (int guard = 0; guard < 3; guard++)
            {
                // Recomputed each pass, because stepping the exponent can move the
                // value into or out of the written-out range: 0.0009999999 starts in
                // the micro tier, rounds up to a mantissa of 1000.0, and lands on a
                // thousandth - which has no suffix at all.
                bool plainFraction = e == -3;
                double m = a / Math.Pow(10, e);
                // One digit fewer when a suffix is carried: the suffix is a character
                // on the panel in its own right, and five digits plus a letter crowds
                // the row it has to share with the currency symbol.
                bool suffixed = e != 0 && !plainFraction;
                if (plainFraction) {
                    // Four decimals below one, which is five digit characters once the
                    // leading zero is counted: "0.9362". Padding to five *significant*
                    // digits instead gave "0.93620" - six characters for a four-digit
                    // value, which is what the rule means on paper and not what it means
                    // on a panel. Above one the leading digit is significant, so the
                    // count runs the other way and the decimals shrink as the number
                    // grows.
                    return sign + a.ToString("F4", CultureInfo.InvariantCulture);
                }
                int dec = suffixed ? (m < 10 ? 3 : (m < 100 ? 2 : 1))
                                   : (m < 10 ? 4 : (m < 100 ? 3 : 2));
                string s = m.ToString("F" + dec.ToString(CultureInfo.InvariantCulture),
                                     CultureInfo.InvariantCulture);
                if (double.Parse(s, CultureInfo.InvariantCulture) < 1000 || e >= 9)
                    return sign + s + Suffix(e);
                if (e + 3 > 9) e = 9; else e += 3;
            }

            return sign + (a / Math.Pow(10, e)).ToString("F1", CultureInfo.InvariantCulture) + Suffix(e);
        }

        static string Suffix(int e)
        {
            switch (e)
            {
                case 9: return "B";
                case 6: return "M";
                case 3: return "K";
                case 0: return "";
                case -3: return "m";
                case -6: return "u";
                default: return "p";
            }
        }
    }
}
