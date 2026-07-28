namespace AiKnowledgeAssistant.UnitTests.Fakes;

/// <summary>Cosine similarity, so the in-memory store ranks the way the configured Qdrant collection does.</summary>
internal static class VectorMath
{
    public static float CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0)
        {
            return 0f;
        }

        double dot = 0, leftNorm = 0, rightNorm = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * (double)right[i];
            leftNorm += left[i] * (double)left[i];
            rightNorm += right[i] * (double)right[i];
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm)));
    }
}
