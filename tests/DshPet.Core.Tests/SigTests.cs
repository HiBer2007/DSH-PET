using DshPet.Core.Model;
using Xunit;

namespace DshPet.Core.Tests
{
    public class SigTests
    {
        [Fact]
        public void Five_significant_digits_at_every_size()
        {
            // The ordinary case is unchanged: a quota of a few dollars still reads as
            // it always did, so nothing about the panel moves for the usual numbers.
            Assert.Equal("3.5258", Sig.Five(3.5258));
            Assert.Equal("2.0650", Sig.Five(2.065));
            Assert.Equal("30.000", Sig.Five(30));
            Assert.Equal("12.345", Sig.Five(12.3454));
            Assert.Equal("123.45", Sig.Five(123.4543));
        }

        [Fact]
        public void Large_numbers_gain_a_suffix_instead_of_digits()
        {
            // One digit fewer once a suffix is carried: the suffix is a character on
            // the panel in its own right.
            Assert.Equal("1.234K", Sig.Five(1234.4));
            Assert.Equal("12.34K", Sig.Five(12344));
            Assert.Equal("123.4K", Sig.Five(123440));
            Assert.Equal("1.234M", Sig.Five(1234400));
            Assert.Equal("1.234B", Sig.Five(1234400000));
        }

        [Fact]
        public void Small_numbers_gain_a_suffix_too()
        {
            // One API call costs a fraction of a cent. Below a thousandth the plain
            // form stops being readable at all, so u and p carry the scale.
            Assert.Equal("123.4u", Sig.Five(0.00012344));
            Assert.Equal("5.000u", Sig.Five(0.000005));
            Assert.Equal("1.234p", Sig.Five(1.2344e-9));
        }

        [Fact]
        public void Below_one_the_leading_zero_counts_as_one_of_the_five_digits()
        {
            // No milli tier: "0.0024" says the same thing as "2.411m" and says it in the
            // form the number is actually spoken in.
            //
            // Four decimals, not five significant digits: 0.9362 padded to five
            // significant digits is "0.93620", six digit characters for a four-digit
            // value. Counting the leading zero makes "0.9362" the five-digit form, and
            // it is also the form the board used before any of this.
            Assert.Equal("0.9362", Sig.Five(0.9362));
            Assert.Equal("0.1500", Sig.Five(0.15));
            Assert.Equal("0.0024", Sig.Five(0.0024114));
            Assert.Equal("0.0010", Sig.Five(0.001));
            Assert.Equal("-0.0024", Sig.Five(-0.0024114));
        }

        [Fact]
        public void Rounding_up_into_the_next_suffix_steps_the_suffix()
        {
            // 999.9999 at two decimals is "1000.00", which is not a five-significant
            // -digit mantissa. It has to become 1.000K, not 1000.00.
            Assert.Equal("1.000K", Sig.Five(999.9999));
            // Crossing *into* the written-out range: 0.0009999999 starts in the micro
            // tier, rounds up to a mantissa of 1000.0, and lands on a thousandth - so
            // the answer is the plain form, not "1.000m".
            Assert.Equal("0.0010", Sig.Five(0.0009999999));
        }

        [Fact]
        public void A_mantissa_sitting_on_the_half_rounds_away_from_zero()
        {
            // Pinned deliberately: 1234.5 scales to a mantissa of exactly 1.2345, and
            // "F3" rounds that up rather than to even. Worth a test because the whole
            // point of the format is that a deduction visibly moves the last digit,
            // and a rounding mode that sometimes rounds down would hide small ones.
            Assert.Equal("1.235K", Sig.Five(1234.5));
            Assert.Equal("1.235u", Sig.Five(1.2345e-6));
        }

        [Fact]
        public void Sign_and_zero_are_handled()
        {
            // The floating cost readouts are negative, though they no longer use this
            // format - the sign still has to survive.
            Assert.Equal("-1.234K", Sig.Five(-1234.4));
            Assert.Equal("0.0000", Sig.Five(0));
            Assert.Equal("--", Sig.Five(double.NaN));
        }
    }
}
