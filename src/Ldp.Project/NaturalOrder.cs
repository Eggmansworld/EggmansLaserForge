using System;
using System.Collections.Generic;

namespace Ldp.Project;

/// <summary>
/// Compares names the way a person reads them: a run of digits compares as a
/// number, not character by character. Plain text sorting puts "Chapter 10"
/// between "Chapter 1" and "Chapter 2", which is exactly wrong for a list an
/// author scans to find scene 9 of 36.
///
/// This covers every naming habit, not just imported chapters - "extra 1" …
/// "extra 12" and "death 1" … "death 10" sort correctly without anyone having
/// to pad them by hand.
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int startX = i, startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                // Leading zeros are padding, not value: "007" and "7" are the
                // same number, so a padded and an unpadded list interleave
                // correctly instead of splitting into two groups.
                ReadOnlySpan<char> a = WithoutLeadingZeros(x.AsSpan(startX, i - startX));
                ReadOnlySpan<char> b = WithoutLeadingZeros(y.AsSpan(startY, j - startY));

                if (a.Length != b.Length) return a.Length - b.Length;
                int digits = a.SequenceCompareTo(b);
                if (digits != 0) return digits;
            }
            else
            {
                char ca = char.ToUpperInvariant(x[i]);
                char cb = char.ToUpperInvariant(y[j]);
                if (ca != cb) return ca - cb;
                i++;
                j++;
            }
        }

        // One ran out first: the shorter remaining tail sorts first.
        return (x.Length - i) - (y.Length - j);
    }

    private static ReadOnlySpan<char> WithoutLeadingZeros(ReadOnlySpan<char> digits)
    {
        int at = 0;
        while (at < digits.Length - 1 && digits[at] == '0') at++;
        return digits[at..];
    }
}
