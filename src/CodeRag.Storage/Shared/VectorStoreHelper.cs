namespace CodeRag.Storage.Shared;

internal static class VectorStoreHelper
{
    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    internal static string? TruncateOpt(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);

    internal static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }
}
