namespace DocuMind.Data;

/// <summary>Defines the single supported search space; changing it requires migration and re-embedding.</summary>
public static class EmbeddingContract
{
    /// <summary>Provider-qualified model identity; equal dimensions alone do not imply compatible vectors.</summary>
    public const string Model = "openai/text-embedding-3-small";

    /// <summary>The selected model's default output size; future API calls must request exactly this size.</summary>
    public const int Dimensions = 1536;
}
