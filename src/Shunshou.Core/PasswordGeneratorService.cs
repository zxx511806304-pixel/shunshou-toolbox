using System.Security.Cryptography;

namespace Shunshou.Core;

/// <summary>Generates passwords locally with RandomNumberGenerator only; nothing is stored or sent anywhere.</summary>
public static class PasswordGeneratorService
{
    private const string UpperChars = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string LowerChars = "abcdefghijkmnpqrstuvwxyz";
    private const string DigitChars = "23456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{};:,.<>?/";

    private const string UpperAmbiguous = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowerAmbiguous = "abcdefghijklmnopqrstuvwxyz";
    private const string DigitAmbiguous = "0123456789";

    public static string Generate(int length, bool upper, bool lower, bool digits, bool symbols, bool excludeAmbiguous)
    {
        if (length is < 4 or > 128) throw new ArgumentOutOfRangeException(nameof(length), "密码长度需在 4 到 128 之间。");
        if (!upper && !lower && !digits && !symbols) throw new ArgumentException("请至少选择一种字符类型");

        var pools = new List<string>();
        if (upper) pools.Add(excludeAmbiguous ? UpperChars : UpperAmbiguous);
        if (lower) pools.Add(excludeAmbiguous ? LowerChars : LowerAmbiguous);
        if (digits) pools.Add(excludeAmbiguous ? DigitChars : DigitAmbiguous);
        if (symbols) pools.Add(SymbolChars);

        var all = string.Concat(pools);
        var chars = new char[length];

        for (int i = 0; i < pools.Count && i < length; i++)
        {
            var pool = pools[i];
            chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        }
        for (int i = pools.Count; i < length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        for (int i = length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    public static (int Bits, string Label) EstimateStrength(int length, bool upper, bool lower, bool digits, bool symbols, bool excludeAmbiguous)
    {
        int poolSize = 0;
        if (upper) poolSize += (excludeAmbiguous ? UpperChars : UpperAmbiguous).Length;
        if (lower) poolSize += (excludeAmbiguous ? LowerChars : LowerAmbiguous).Length;
        if (digits) poolSize += (excludeAmbiguous ? DigitChars : DigitAmbiguous).Length;
        if (symbols) poolSize += SymbolChars.Length;
        if (poolSize == 0 || length <= 0) return (0, "无法估算");
        double bits = length * Math.Log2(poolSize);
        string label = bits < 45 ? "弱" : bits < 70 ? "中" : bits < 100 ? "强" : "很强";
        return ((int)Math.Round(bits), label);
    }
}
