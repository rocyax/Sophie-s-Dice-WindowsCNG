using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using System.Collections.Generic;

public static class CryptoRand
{
    private const uint BCRYPT_USE_SYSTEM_PREFERRED_RNG = 0x00000002;

    private const string BCRYPT_RNG_ALGORITHM = "RNG";

    private const int POOL_SIZE = 4096;

    [ThreadStatic] private static byte[] t_pool;
    [ThreadStatic] private static int t_pos;

    [DllImport("bcrypt.dll", ExactSpelling = true)]
    private static extern int BCryptGenRandom(
        IntPtr hAlgorithm,
        [Out] byte[] pbBuffer,
        int cbBuffer,
        uint dwFlags);

    [DllImport("bcrypt.dll", ExactSpelling = true)]
    private static extern int BCryptCloseAlgorithmProvider(
        IntPtr hAlgorithm,
        uint dwFlags);

    private static void EnsureBytes(int need)
    {
        if (t_pool == null)
        {
            t_pool = new byte[POOL_SIZE];
            t_pos = POOL_SIZE; // force refill on first use
        }

        if (t_pos + need <= POOL_SIZE)
            return;

        GetBytes(t_pool);
        t_pos = 0;
    }

    private static void GetBytes(byte[] buffer)
    {
        int status = BCryptGenRandom(IntPtr.Zero, buffer, buffer.Length, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
        if (status < 0)
            throw new CryptographicException("BCryptGenRandom failed, NTSTATUS=0x" + status.ToString("X8"));
    }

    // ====== int 版本：返回 [minInclusive, maxExclusive) ======
    public static int Range(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException();

        long diff = (long)maxExclusive - minInclusive;
        if (diff <= 0 || diff > uint.MaxValue)
            throw new ArgumentOutOfRangeException();

        uint range = (uint)diff;
        uint limit = uint.MaxValue - (uint.MaxValue % range);

        while (true)
        {
            uint x = NextUInt32();
            if (x < limit)
                return (int)(minInclusive + (x % range));
        }
    }

    // ====== float 版本：返回 [minInclusive, maxInclusive]（近似等价于 Unity 的 float Range 语义） ======
    public static float Range(float minInclusive, float maxInclusive)
    {
        if (float.IsNaN(minInclusive) || float.IsNaN(maxInclusive))
            throw new ArgumentOutOfRangeException();

        if (minInclusive > maxInclusive)
            throw new ArgumentOutOfRangeException();

        if (minInclusive == maxInclusive)
            return minInclusive;

        // t ∈ [0,1]（包含 1 的版本；极小概率正好取到 max）
        double t = NextDoubleInclusive01();
        double v = (double)minInclusive + ((double)maxInclusive - (double)minInclusive) * t;
        return (float)v;
    }

    // 如果你遇到 double 场景，也可以直接用这个
    public static double Range(double minInclusive, double maxInclusive)
    {
        if (double.IsNaN(minInclusive) || double.IsNaN(maxInclusive))
            throw new ArgumentOutOfRangeException();

        if (minInclusive > maxInclusive)
            throw new ArgumentOutOfRangeException();

        if (minInclusive == maxInclusive)
            return minInclusive;

        double t = NextDoubleInclusive01();
        return minInclusive + (maxInclusive - minInclusive) * t;
    }

    private static uint ReadUInt32LE(byte[] b, int i)
    {
        return (uint)b[i]
            | ((uint)b[i + 1] << 8)
            | ((uint)b[i + 2] << 16)
            | ((uint)b[i + 3] << 24);
    }

    private static ulong ReadUInt64LE(byte[] b, int i)
    {
        return (ulong)b[i]
            | ((ulong)b[i + 1] << 8)
            | ((ulong)b[i + 2] << 16)
            | ((ulong)b[i + 3] << 24)
            | ((ulong)b[i + 4] << 32)
            | ((ulong)b[i + 5] << 40)
            | ((ulong)b[i + 6] << 48)
            | ((ulong)b[i + 7] << 56);
    }

    

    // ====== 基础随机原语 ======
    private static uint NextUInt32()
    {
        EnsureBytes(4);
        uint v = ReadUInt32LE(t_pool, t_pos);
        t_pos += 4;
        return v;
    }

    private static ulong NextUInt64()
    {
        EnsureBytes(8);
        ulong v = ReadUInt64LE(t_pool, t_pos);
        t_pos += 8;
        return v;
    }

    // 生成 [0,1] 的 double（包含 1；概率约 1/(2^53)）
    // 用 53bit 精度匹配 double 的有效随机精度
    private static double NextDoubleInclusive01()
    {
        const ulong mask53 = (1UL << 53) - 1UL;
        ulong r = NextUInt64() & mask53;                 // 0 .. 2^53-1
        return (double)r / (double)mask53;              // 0 .. 1
    }

    public static Quaternion rotationUniform
    {
        get { return NextUniformQuaternion(); }
    }

    private static Quaternion NextUniformQuaternion()
    {
        // 经典的均匀四元数采样（Shoemake 方法）
        // u1,u2,u3 ~ U[0,1)
        float u1 = NextFloat01Exclusive();
        float u2 = NextFloat01Exclusive();
        float u3 = NextFloat01Exclusive();

        double sqrt1MinusU1 = Math.Sqrt(1.0 - u1);
        double sqrtU1 = Math.Sqrt(u1);

        double theta1 = 2.0 * Math.PI * u2;
        double theta2 = 2.0 * Math.PI * u3;

        float x = (float)(sqrt1MinusU1 * Math.Sin(theta1));
        float y = (float)(sqrt1MinusU1 * Math.Cos(theta1));
        float z = (float)(sqrtU1 * Math.Sin(theta2));
        float w = (float)(sqrtU1 * Math.Cos(theta2));

        return new Quaternion(x, y, z, w);
    }

    // 模拟 UnityEngine.Random.value：返回 [0,1) 的均匀 float
    public static float value
    {
        get { return NextFloat01Exclusive(); }
    }

    // 返回 [0,1) 的 float（排除 1.0f）
    private static float NextFloat01Exclusive()
    {
        // 取 24bit 随机数作为 float 尾数：r ∈ [0, 2^24-1]
        // r / 2^24 ∈ [0,1)
        uint r = NextUInt32() >> 8; // 取高 24 位
        return r * (1.0f / 16777216.0f); // 2^24
    }

    public static double NextDouble()
    {
        // 取 64bit，右移 11 得到 53bit 随机整数 r ∈ [0, 2^53)
        ulong r = NextUInt64() >> 11;
        return r * (1.0 / 9007199254740992.0); // 2^53
    }

    public static int RangeUnityCompat(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return Range(minInclusive, maxExclusive);
    }
    }

