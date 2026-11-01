using System.Text;

namespace OneIndex.Models.Tokenization;

/// <summary>
/// A WordPiece vocabulary: one token per line, the line number (from 0) being the token id, as in the <c>vocab.txt</c> file that
/// BERT-family models publish (Devlin et al. 2019, section 3.1 and the BERT README).
/// </summary>
/// <remarks>
/// Tokens that start with the continuation prefix (<c>##</c>) are stored without it in a second table, so that matching a piece
/// of a word never has to build the prefixed string. Immutable and thread-safe once built.
/// </remarks>
public sealed class WordPieceVocabulary
{
    /// <summary>The default most lines a vocabulary may have.</summary>
    public const int DefaultMaxTokens = 4_000_000;

    private readonly Dictionary<string, int> _initial;
    private readonly Dictionary<string, int> _continuation;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _initialLookup;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _continuationLookup;
    private readonly string[] _tokens;

    private WordPieceVocabulary(string[] tokens, string continuingPrefix)
    {
        _tokens = tokens;
        ContinuingPrefix = continuingPrefix;
        _initial = new Dictionary<string, int>(tokens.Length, StringComparer.Ordinal);
        _continuation = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int id = 0; id < tokens.Length; id++)
        {
            string token = tokens[id];
            if (token.Length > continuingPrefix.Length && token.StartsWith(continuingPrefix, StringComparison.Ordinal))
            {
                if (!_continuation.TryAdd(token[continuingPrefix.Length..], id))
                {
                    throw new FormatException($"The vocabulary lists the token '{token}' twice.");
                }
            }
            else if (!_initial.TryAdd(token, id))
            {
                throw new FormatException($"The vocabulary lists the token '{token}' twice.");
            }
        }

        _initialLookup = _initial.GetAlternateLookup<ReadOnlySpan<char>>();
        _continuationLookup = _continuation.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The number of tokens; ids run from 0 to <c>Count - 1</c>.</summary>
    public int Count => _tokens.Length;

    /// <summary>The prefix that marks a piece that continues a word (<c>##</c> unless set otherwise).</summary>
    public string ContinuingPrefix { get; }

    /// <summary>The token with the given id.</summary>
    /// <param name="id">An id from 0 to <see cref="Count"/> - 1.</param>
    /// <returns>The token text, prefix included for continuation pieces.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The id is outside the vocabulary.</exception>
    public string this[int id] => (uint)id < (uint)_tokens.Length ? _tokens[id] : throw new ArgumentOutOfRangeException(nameof(id));

    /// <summary>Finds a token by its exact text (a special token such as <c>[CLS]</c>, or a whole-word token; a continuation piece
    /// is looked up with its prefix).</summary>
    /// <param name="token">The token text.</param>
    /// <param name="id">The token's id when found.</param>
    /// <returns><see langword="true"/> if the vocabulary has the token.</returns>
    public bool TryGetId(ReadOnlySpan<char> token, out int id)
    {
        if (token.StartsWith(ContinuingPrefix, StringComparison.Ordinal) && token.Length > ContinuingPrefix.Length)
        {
            return _continuationLookup.TryGetValue(token[ContinuingPrefix.Length..], out id);
        }

        return _initialLookup.TryGetValue(token, out id);
    }

    /// <summary>Looks up a piece that starts a word.</summary>
    internal bool TryGetInitial(ReadOnlySpan<char> piece, out int id) => _initialLookup.TryGetValue(piece, out id);

    /// <summary>Looks up a piece that continues a word, given without the prefix.</summary>
    internal bool TryGetContinuation(ReadOnlySpan<char> piece, out int id) => _continuationLookup.TryGetValue(piece, out id);

    /// <summary>Reads a vocabulary from UTF-8 text with one token per line (<c>\n</c> or <c>\r\n</c>).</summary>
    /// <param name="utf8">The file's bytes. A final line break is allowed.</param>
    /// <param name="continuingPrefix">The continuation marker; <c>##</c> for BERT vocabularies.</param>
    /// <param name="maxTokens">The most lines accepted, so that hostile input cannot make the table arbitrarily large.</param>
    /// <returns>The vocabulary.</returns>
    /// <exception cref="FormatException">The text is not valid UTF-8, has more than <paramref name="maxTokens"/> lines, is empty,
    /// or lists a token twice.</exception>
    public static WordPieceVocabulary Parse(ReadOnlySpan<byte> utf8, string continuingPrefix = "##", int maxTokens = DefaultMaxTokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(continuingPrefix);
        var tokens = new List<string>();
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        while (!utf8.IsEmpty)
        {
            int newline = utf8.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = newline < 0 ? utf8 : utf8[..newline];
            utf8 = newline < 0 ? default : utf8[(newline + 1)..];
            if (!line.IsEmpty && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (tokens.Count >= maxTokens)
            {
                throw new FormatException($"The vocabulary has more than {maxTokens} tokens.");
            }

            try
            {
                tokens.Add(strict.GetString(line));
            }
            catch (ArgumentException e)
            {
                throw new FormatException("The vocabulary is not valid UTF-8.", e);
            }
        }

        if (tokens.Count == 0)
        {
            throw new FormatException("The vocabulary is empty.");
        }

        return new WordPieceVocabulary([.. tokens], continuingPrefix);
    }

    /// <summary>Reads a vocabulary from a file.</summary>
    /// <param name="path">The <c>vocab.txt</c> path.</param>
    /// <param name="continuingPrefix">The continuation marker.</param>
    /// <param name="maxTokens">The most lines accepted.</param>
    /// <returns>The vocabulary.</returns>
    /// <exception cref="FormatException">See <see cref="Parse"/>.</exception>
    public static WordPieceVocabulary Load(string path, string continuingPrefix = "##", int maxTokens = DefaultMaxTokens) =>
        Parse(File.ReadAllBytes(path), continuingPrefix, maxTokens);

    /// <summary>Reads a vocabulary from a stream, to its end.</summary>
    /// <param name="stream">A readable stream.</param>
    /// <param name="continuingPrefix">The continuation marker.</param>
    /// <param name="maxTokens">The most lines accepted.</param>
    /// <returns>The vocabulary.</returns>
    /// <exception cref="FormatException">See <see cref="Parse"/>.</exception>
    public static WordPieceVocabulary Load(Stream stream, string continuingPrefix = "##", int maxTokens = DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), continuingPrefix, maxTokens);
    }
}
