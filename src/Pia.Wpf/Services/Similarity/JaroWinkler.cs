namespace Pia.Services.Similarity;

public static class JaroWinkler
{
    private const double PrefixScale = 0.1;
    private const double BoostThreshold = 0.7;

    public static double Similarity(string s1, string s2)
    {
        ArgumentNullException.ThrowIfNull(s1);
        ArgumentNullException.ThrowIfNull(s2);

        if (s1.Length == 0 && s2.Length == 0) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;
        if (s1 == s2) return 1.0;

        var jaro = JaroSimilarity(s1, s2);

        if (jaro < BoostThreshold) return jaro;

        var commonLimit = Math.Min(s1.Length, s2.Length);
        var prefix = 0;
        while (prefix < commonLimit && s1[prefix] == s2[prefix]) prefix++;

        var maxLength = Math.Max(s1.Length, s2.Length);
        var coefficient = Math.Min(PrefixScale, 1.0 / maxLength);

        return jaro + prefix * coefficient * (1.0 - jaro);
    }

    private static double JaroSimilarity(string s1, string s2)
    {
        var window = Math.Max(0, Math.Max(s1.Length, s2.Length) / 2 - 1);
        var s1Matched = new bool[s1.Length];
        var s2Matched = new bool[s2.Length];

        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - window);
            var end = Math.Min(i + window + 1, s2.Length);
            for (var j = start; j < end; j++)
            {
                if (s2Matched[j]) continue;
                if (s1[i] != s2[j]) continue;
                s1Matched[i] = true;
                s2Matched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matched[i]) continue;
            while (!s2Matched[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }
        transpositions /= 2;

        return ((double)matches / s1.Length
              + (double)matches / s2.Length
              + (double)(matches - transpositions) / matches) / 3.0;
    }
}
