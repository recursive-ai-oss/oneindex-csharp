namespace OneIndex.Models.Tokenization;

/// <summary>A batch of encoded texts padded to one length, in the row-major layout BERT-style models take.</summary>
public sealed class TokenizedBatch
{
    internal TokenizedBatch(int count, int length, int[] ids, int[] attentionMask, int[] lengths)
    {
        Count = count;
        Length = length;
        InputIds = ids;
        AttentionMask = attentionMask;
        Lengths = lengths;
    }

    /// <summary>The number of texts (rows).</summary>
    public int Count { get; }

    /// <summary>The padded length of every row: the longest row, or 0 for an empty batch.</summary>
    public int Length { get; }

    /// <summary>Token ids, <c>Count * Length</c> of them; padding positions hold the pad id.</summary>
    public int[] InputIds { get; }

    /// <summary>1 for a real token and 0 for padding, <c>Count * Length</c> of them.</summary>
    public int[] AttentionMask { get; }

    /// <summary>The unpadded length of each row.</summary>
    public int[] Lengths { get; }
}
