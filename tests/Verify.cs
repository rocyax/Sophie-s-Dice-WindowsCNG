using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using B83.ExpressionParser;
using UnityEngine;

public static class Verify
{
    private const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InternalInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Main()
    {
        try
        {
            Console.WriteLine(typeof(DieResult).Assembly.Location);
            var random = typeof(DieResult).Assembly.GetType("CryptoRand", true);
            var range = (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>),
                random.GetMethod("Range", InternalStatic, null, new[] { typeof(int), typeof(int) }, null));
            var inclusive = (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>),
                random.GetMethod("RangeInclusive", InternalStatic));
            var unit = (Func<double>)Delegate.CreateDelegate(typeof(Func<double>), random.GetMethod("NextDouble", InternalStatic));
            var rotation = (Func<Quaternion>)Delegate.CreateDelegate(typeof(Func<Quaternion>),
                random.GetMethod("get_RotationUniform", InternalStatic));

            Check(range(0, 0) == 0 && range(4, 3) == 4, "equal and reversed ranges");
            Check(inclusive(int.MaxValue, int.MaxValue) == int.MaxValue, "Int32.MaxValue endpoint");
            Check(inclusive(int.MinValue, int.MinValue) == int.MinValue, "Int32.MinValue endpoint");
            var counts = new int[20];
            bool foundMax = false;
            bool foundMin = false;
            const int count = 200000;
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                counts[range(1, 21) - 1]++;
            timer.Stop();

            for (int i = 0; i < 10000; i++)
            {
                int reversed = range(10, 0);
                Check(reversed >= 1 && reversed <= 10, "reversed Unity bounds");
                foundMax |= inclusive(int.MaxValue - 1, int.MaxValue) == int.MaxValue;
                foundMin |= inclusive(int.MinValue, int.MinValue + 1) == int.MinValue;
                inclusive(int.MinValue, int.MaxValue);
                int almostFull = range(int.MinValue, 1);
                Check(almostFull < 1, "range with high rejection rate");
                double sample = unit();
                Check(sample >= 0 && sample < 1, "double bounds");
                Quaternion q = rotation();
                Check(Math.Abs(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w - 1) < 0.000001, "unit quaternion");
            }
            Check(foundMax && foundMin, "sampled both Int32 endpoints");

            double expected = count / 20.0;
            double chiSquare = 0;
            foreach (int bucket in counts)
                chiSquare += (bucket - expected) * (bucket - expected) / expected;
            Console.WriteLine("d20 ({0} draws, {1} ms): {2}", count, timer.ElapsedMilliseconds, string.Join(",", counts));
            Console.WriteLine("Chi-square (19 df): {0:F3}", chiSquare);
            Check(chiSquare < 70, "d20 gross distribution check");

            CheckExpression();
            CheckReplacement();
            Console.WriteLine("PASS: Windows CNG, ranges, quaternion norms, rnd(), and per-die replacement under Unity Mono.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckExpression()
    {
        var rollType = typeof(DieResult).Assembly.GetType("RollRandom", true);
        var parserCtor = typeof(ExpressionParser).GetConstructor(InternalInstance, null, new[] { rollType }, null);
        var roll = Activator.CreateInstance(rollType, true);
        var firstParser = (ExpressionParser)parserCtor.Invoke(new[] { roll });
        var secondParser = (ExpressionParser)parserCtor.Invoke(new[] { roll });
        var newRollParser = (ExpressionParser)parserCtor.Invoke(new[] { Activator.CreateInstance(rollType, true) });
        const string formula = "rnd(1)+rnd(1)+rnd(0,1)";
        double first = firstParser.Evaluate(formula);
        Check(first >= 0 && first < 3, "rnd overload bounds");
        Check(first == secondParser.Evaluate(formula), "rnd() stable across display refresh");
        Check(first != newRollParser.Evaluate(formula), "rnd() fresh after roll reset");
        Check(new ExpressionParser(0).Evaluate("2+3") == 5, "standalone constructor");
    }

    private static void CheckReplacement()
    {
        var rollType = typeof(DieResult).Assembly.GetType("RollRandom", true);
        var update = typeof(RollCondition_ValueReplace).GetMethod("ConditionUpdate", InternalInstance,
            null, new[] { typeof(List<DieResult>), rollType.MakeByRefType(), typeof(int) }, null);
        var modifier = new RollCondition_ValueReplace();
        modifier.replacementVals.Add(new ExpValue("test") {
            replacementVal = new ExpValue(1) { maxValue = new ExpValue(int.MaxValue) }
        });

        var first = new DieResult { madeWithDieObject = false, faceValueType = FaceValueType.String, currentStringValue = "test" };
        var second = new DieResult { madeWithDieObject = false, faceValueType = FaceValueType.String, currentStringValue = "test" };
        object[] args = { new List<DieResult> { first.MakeCopy(), second.MakeCopy() }, null, 0 };
        update.Invoke(modifier, args);
        var initial = (List<DieResult>)args[0];
        Check(initial[0].currentValue > 0 && initial[1].currentValue > 0, "inclusive replacement with large endpoint");
        Check(initial[0].currentValue != initial[1].currentValue, "independent samples for different dice");

        args[0] = new List<DieResult> { second.MakeCopy(), first.MakeCopy() };
        update.Invoke(modifier, args);
        var reordered = (List<DieResult>)args[0];
        Check(initial[0].currentValue == reordered[1].currentValue && initial[1].currentValue == reordered[0].currentValue,
            "same samples after reordering and copying");

        first.PrepareForNewRoll();
        args[0] = new List<DieResult> { first.MakeCopy(), second.MakeCopy() };
        update.Invoke(modifier, args);
        var rerolled = (List<DieResult>)args[0];
        Check(initial[0].currentValue != rerolled[0].currentValue && initial[1].currentValue == rerolled[1].currentValue,
            "individual reroll refreshes its replacement sample");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
