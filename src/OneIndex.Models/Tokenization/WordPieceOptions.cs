namespace OneIndex.Models.Tokenization;

/// <summary>Settings of a <see cref="WordPieceTokenizer"/>. The defaults are those of the uncased English BERT family
/// (<c>bert-base-uncased</c>, <c>bge-small-en-v1.5</c>).</summary>
public sealed class WordPieceOptions
{
    /// <summary>Lower-cases the text. <see langword="true"/> by default.</summary>
    public bool LowerCase { get; init; } = true;

    /// <summary>Removes accents (canonical decomposition, then dropping nonspacing marks). <see langword="null"/>, the default,
    /// follows <see cref="LowerCase"/>, as BERT's published tokenizer does.</summary>
    public bool? StripAccents { get; init; }

    /// <summary>Puts spaces around each CJK ideograph so that it becomes a word of its own. <see langword="true"/> by default.</summary>
    public bool TokenizeChineseChars { get; init; } = true;

    /// <summary>Recognizes the literal text of special tokens (<c>[CLS]</c>, <c>[SEP]</c>, <c>[PAD]</c>, <c>[UNK]</c>, <c>[MASK]</c>)
    /// in the input and maps it to the token's id instead of tokenizing the brackets. <see langword="true"/> by default, which is
    /// what the published tokenizer does; turn it off when the text is untrusted and must never contain control tokens.</summary>
    public bool SplitOnSpecialTokens { get; init; } = true;

    /// <summary>A word with more characters than this becomes the unknown token without matching. 100 by default (BERT).</summary>
    public int MaxInputCharsPerWord { get; init; } = 100;

    /// <summary>The unknown token's text. <c>[UNK]</c> by default.</summary>
    public string UnknownToken { get; init; } = "[UNK]";

    /// <summary>The token that starts a sequence. <c>[CLS]</c> by default.</summary>
    public string ClassToken { get; init; } = "[CLS]";

    /// <summary>The token that ends a sequence. <c>[SEP]</c> by default.</summary>
    public string SeparatorToken { get; init; } = "[SEP]";

    /// <summary>The padding token. <c>[PAD]</c> by default.</summary>
    public string PadToken { get; init; } = "[PAD]";

    /// <summary>The mask token. <c>[MASK]</c> by default.</summary>
    public string MaskToken { get; init; } = "[MASK]";
}
