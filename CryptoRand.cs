using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

internal static class CryptoRand
{
    private const uint UseSystemPreferredRng = 2;
    private const ulong UInt32Domain = 1UL << 32;

    [DllImport("bcrypt.dll", EntryPoint = "BCryptGenRandom", ExactSpelling = true)]
    private static extern int FillUInt32(IntPtr algorithm, out uint value, uint size, uint flags);

    [DllImport("bcrypt.dll", EntryPoint = "BCryptGenRandom", ExactSpelling = true)]
    private static extern int FillUInt64(IntPtr algorithm, out ulong value, uint size, uint flags);

    private static void CheckStatus(int status)
    {
        if (status != 0)
            throw new CryptographicException("BCryptGenRandom failed (NTSTATUS 0x" + status.ToString("X8") + ").");
    }

    private static uint NextUInt32()
    {
        CheckStatus(FillUInt32(IntPtr.Zero, out uint value, sizeof(uint), UseSystemPreferredRng));
        return value;
    }

    internal static double NextDouble()
    {
        CheckStatus(FillUInt64(IntPtr.Zero, out ulong value, sizeof(ulong), UseSystemPreferredRng));
        return (value >> 11) * (1.0 / (1UL << 53));
    }

    internal static int Range(int minInclusive, int maxExclusive)
    {
        return RangeWithLongUpperBound(minInclusive, maxExclusive);
    }

    internal static int RangeInclusive(int minInclusive, int maxInclusive)
    {
        return RangeWithLongUpperBound(minInclusive, (long)maxInclusive + 1);
    }

    private static int RangeWithLongUpperBound(long minInclusive, long maxExclusive)
    {
        if (minInclusive == maxExclusive)
            return (int)minInclusive;

        // A reversed Unity integer range keeps each argument's original inclusivity.
        long lower = minInclusive < maxExclusive ? minInclusive : maxExclusive + 1;
        long upper = minInclusive < maxExclusive ? maxExclusive : minInclusive + 1;
        ulong width = (ulong)(upper - lower);
        if (width == 1)
            return (int)lower;

        ulong limit = UInt32Domain - UInt32Domain % width;
        uint sample;
        do
        {
            sample = NextUInt32();
        }
        while (sample >= limit);

        return (int)(lower + (long)(sample % width));
    }

    internal static float Value => (float)((NextUInt32() >> 8) * (1.0 / ((1U << 24) - 1)));

    internal static float Range(float minInclusive, float maxInclusive)
    {
        if (minInclusive == maxInclusive)
            return minInclusive;

        double unit = (NextUInt32() >> 8) * (1.0 / ((1U << 24) - 1));
        return (float)(minInclusive + ((double)maxInclusive - minInclusive) * unit);
    }

    internal static Quaternion RotationUniform
    {
        get
        {
            // Shoemake's uniform unit-quaternion construction.
            double u = NextDouble();
            double angleA = 2.0 * Math.PI * NextDouble();
            double angleB = 2.0 * Math.PI * NextDouble();
            double a = Math.Sqrt(1.0 - u);
            double b = Math.Sqrt(u);
            return new Quaternion((float)(a * Math.Sin(angleA)), (float)(a * Math.Cos(angleA)),
                (float)(b * Math.Sin(angleB)), (float)(b * Math.Cos(angleB)));
        }
    }
}

// The game can rebuild its parser and copy dice results on every display update.
// A new roll gets new samples, while reads within the roll reuse the same draws.
internal sealed class RollRandom
{
    private List<double> expressionSamples;
    private ConditionalWeakTable<object, Dictionary<long, RangeSample>> replacements;

    internal static Reader CreateReader(RollRandom roll)
    {
        return new Reader(roll ?? new RollRandom());
    }

    internal sealed class Reader
    {
        private readonly RollRandom roll;
        private int position;

        internal Reader(RollRandom roll)
        {
            this.roll = roll;
        }

        internal double NextDouble()
        {
            if (roll.expressionSamples == null)
                roll.expressionSamples = new List<double>();
            if (position == roll.expressionSamples.Count)
                roll.expressionSamples.Add(CryptoRand.NextDouble());

            return roll.expressionSamples[position++];
        }
    }

    private struct RangeSample
    {
        internal int Min;
        internal int Max;
        internal int Value;
    }

    internal static object GetRollIdentity(ref object identity)
    {
        if (identity == null)
            identity = new object();
        return identity;
    }

    internal static void ApplyReplacement(DieResult die, object source, ExpValue replacement,
        ref RollRandom roll, int conditionIndex, int ruleIndex)
    {
        if (roll == null)
            roll = new RollRandom();

        int max = replacement.maxValue.GetInt(die.currentValue);
        int min = replacement.GetInt(die.currentValue);
        die.currentValue = roll.GetReplacement(source ?? die, conditionIndex, ruleIndex, min, max);
    }

    private int GetReplacement(object source, int conditionIndex, int ruleIndex, int min, int max)
    {
        if (replacements == null)
            replacements = new ConditionalWeakTable<object, Dictionary<long, RangeSample>>();

        Dictionary<long, RangeSample> samples = replacements.GetOrCreateValue(source);
        long slot = ((long)conditionIndex << 32) | (uint)ruleIndex;
        if (!samples.TryGetValue(slot, out RangeSample sample) || sample.Min != min || sample.Max != max)
        {
            sample = new RangeSample { Min = min, Max = max, Value = CryptoRand.RangeInclusive(min, max) };
            samples[slot] = sample;
        }

        return sample.Value;
    }
}
