using System.Buffers.Binary;
using System.Text;

namespace OneIndex.Models.Tokenization;

/// <summary>Thrown when a SentencePiece model file is malformed, exceeds <see cref="SentencePieceLimits"/> or uses a feature this reader does not support.</summary>
public sealed class SentencePieceFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong with the model.</param>
    public SentencePieceFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>Ceilings checked while reading a SentencePiece model, before anything large is allocated.</summary>
/// <param name="MaxFileBytes">The largest model file. Default 64 MiB.</param>
/// <param name="MaxPieces">The most pieces. Default 4 million.</param>
/// <param name="MaxPieceChars">The longest piece, in UTF-16 units. Default 64.</param>
public sealed record SentencePieceLimits(int MaxFileBytes = 64 << 20, int MaxPieces = 4_000_000, int MaxPieceChars = 64)
{
    /// <summary>The default limits.</summary>
    public static SentencePieceLimits Default { get; } = new();
}

/// <summary>The kinds of piece a model lists (the <c>Type</c> enum of the published model schema).</summary>
public enum SentencePieceType
{
    /// <summary>An ordinary piece.</summary>
    Normal = 1,

    /// <summary>The unknown symbol.</summary>
    Unknown = 2,

    /// <summary>A control symbol such as the sentence boundaries; never produced by segmentation.</summary>
    Control = 3,

    /// <summary>A user-defined symbol.</summary>
    UserDefined = 4,

    /// <summary>A piece that is not used.</summary>
    Unused = 5,

    /// <summary>A byte piece (byte fallback).</summary>
    Byte = 6,
}

/// <summary>
/// A SentencePiece model read from its serialized file (Kudo and Richardson 2018). The file is a protocol-buffer message; this reader
/// follows the published schema (<c>sentencepiece_model.proto</c>: pieces with scores and types, the trainer's model type, the
/// normalizer's flags) and ignores fields it does not need. Only the unigram model type is supported; a BPE or character model is
/// refused with a <see cref="SentencePieceFormatException"/>.
/// </summary>
public sealed class SentencePieceModel
{
    private SentencePieceModel(string[] pieces, float[] scores, SentencePieceType[] types, string normalizerName, bool addDummyPrefix, bool removeExtraWhitespaces, bool escapeWhitespaces, bool byteFallback, int unkId)
    {
        Pieces = pieces;
        Scores = scores;
        Types = types;
        NormalizerName = normalizerName;
        AddDummyPrefix = addDummyPrefix;
        RemoveExtraWhitespaces = removeExtraWhitespaces;
        EscapeWhitespaces = escapeWhitespaces;
        ByteFallback = byteFallback;
        UnkId = unkId;
    }

    /// <summary>The pieces, by id.</summary>
    public IReadOnlyList<string> Pieces { get; }

    /// <summary>The log-probability score of each piece.</summary>
    public IReadOnlyList<float> Scores { get; }

    /// <summary>The type of each piece.</summary>
    public IReadOnlyList<SentencePieceType> Types { get; }

    /// <summary>The normalizer's name, for example <c>nmt_nfkc</c> or <c>identity</c>.</summary>
    public string NormalizerName { get; }

    /// <summary>Whether a space is put in front of the text, so the first word is treated like the others.</summary>
    public bool AddDummyPrefix { get; }

    /// <summary>Whether leading and trailing spaces are removed and runs of spaces collapsed.</summary>
    public bool RemoveExtraWhitespaces { get; }

    /// <summary>Whether a space is written as U+2581 inside pieces.</summary>
    public bool EscapeWhitespaces { get; }

    /// <summary>Whether unknown characters fall back to byte pieces (not supported by this reader).</summary>
    public bool ByteFallback { get; }

    /// <summary>The id of the unknown symbol.</summary>
    public int UnkId { get; }

    /// <summary>Reads a model file.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="limits">Ceilings; <see cref="SentencePieceLimits.Default"/> when <see langword="null"/>.</param>
    /// <returns>The model.</returns>
    /// <exception cref="SentencePieceFormatException">The file is malformed, over a limit, not a unigram model, or uses byte fallback.</exception>
    public static SentencePieceModel Load(ReadOnlySpan<byte> file, SentencePieceLimits? limits = null)
    {
        limits ??= SentencePieceLimits.Default;
        if (file.Length > limits.MaxFileBytes)
        {
            throw new SentencePieceFormatException("The model file is larger than the limit.");
        }

        var pieces = new List<string>();
        var scores = new List<float>();
        var types = new List<SentencePieceType>();
        string normalizerName = string.Empty;
        bool dummy = true, strip = true, escape = true, byteFallback = false;
        int modelType = 1, unkId = 0;
        int pos = 0;
        while (pos < file.Length)
        {
            (int field, int wire) = ReadKey(file, ref pos);
            if (field == 1 && wire == 2)
            {
                if (pieces.Count >= limits.MaxPieces)
                {
                    throw new SentencePieceFormatException("The model lists more pieces than the limit.");
                }

                ReadOnlySpan<byte> body = ReadBytes(file, ref pos);
                string piece = string.Empty;
                float score = 0;
                var type = SentencePieceType.Normal;
                int p = 0;
                while (p < body.Length)
                {
                    (int f, int w) = ReadKey(body, ref p);
                    if (f == 1 && w == 2)
                    {
                        ReadOnlySpan<byte> s = ReadBytes(body, ref p);
                        try
                        {
                            piece = new UTF8Encoding(false, true).GetString(s);
                        }
                        catch (ArgumentException)
                        {
                            throw new SentencePieceFormatException("A piece is not valid UTF-8.");
                        }
                    }
                    else if (f == 2 && w == 5)
                    {
                        if (body.Length - p < 4)
                        {
                            throw new SentencePieceFormatException("A score is truncated.");
                        }

                        score = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(body[p..]));
                        p += 4;
                    }
                    else if (f == 3 && w == 0)
                    {
                        long t = ReadVarint(body, ref p);
                        type = t is >= 1 and <= 6 ? (SentencePieceType)t : throw new SentencePieceFormatException("A piece has an unknown type.");
                    }
                    else
                    {
                        Skip(body, ref p, w);
                    }
                }

                if (piece.Length == 0 || piece.Length > limits.MaxPieceChars)
                {
                    throw new SentencePieceFormatException("A piece is empty or longer than the limit.");
                }

                pieces.Add(piece);
                scores.Add(score);
                types.Add(type);
            }
            else if (field == 2 && wire == 2)
            {
                ReadOnlySpan<byte> body = ReadBytes(file, ref pos);
                int p = 0;
                while (p < body.Length)
                {
                    (int f, int w) = ReadKey(body, ref p);
                    if (f == 3 && w == 0)
                    {
                        modelType = (int)ReadVarint(body, ref p);
                    }
                    else if (f == 35 && w == 0)
                    {
                        byteFallback = ReadVarint(body, ref p) != 0;
                    }
                    else if (f == 40 && w == 0)
                    {
                        unkId = (int)ReadVarint(body, ref p);
                    }
                    else
                    {
                        Skip(body, ref p, w);
                    }
                }
            }
            else if (field == 3 && wire == 2)
            {
                ReadOnlySpan<byte> body = ReadBytes(file, ref pos);
                int p = 0;
                while (p < body.Length)
                {
                    (int f, int w) = ReadKey(body, ref p);
                    if (f == 1 && w == 2)
                    {
                        normalizerName = Encoding.UTF8.GetString(ReadBytes(body, ref p));
                    }
                    else if (f == 3 && w == 0)
                    {
                        dummy = ReadVarint(body, ref p) != 0;
                    }
                    else if (f == 4 && w == 0)
                    {
                        strip = ReadVarint(body, ref p) != 0;
                    }
                    else if (f == 5 && w == 0)
                    {
                        escape = ReadVarint(body, ref p) != 0;
                    }
                    else
                    {
                        Skip(body, ref p, w);
                    }
                }
            }
            else
            {
                Skip(file, ref pos, wire);
            }
        }

        if (modelType != 1)
        {
            throw new SentencePieceFormatException("Only unigram SentencePiece models are supported (this model is type " + modelType + ").");
        }

        if (byteFallback)
        {
            throw new SentencePieceFormatException("SentencePiece models with byte fallback are not supported.");
        }

        if (pieces.Count == 0 || unkId < 0 || unkId >= pieces.Count)
        {
            throw new SentencePieceFormatException("The model has no pieces or names an unknown-symbol id outside them.");
        }

        return new SentencePieceModel([.. pieces], [.. scores], [.. types], normalizerName, dummy, strip, escape, byteFallback, unkId);
    }

    private static (int Field, int Wire) ReadKey(ReadOnlySpan<byte> data, ref int pos)
    {
        long key = ReadVarint(data, ref pos);
        if (key < 8 || key > int.MaxValue)
        {
            throw new SentencePieceFormatException("The model has an invalid field key.");
        }

        return ((int)(key >> 3), (int)(key & 7));
    }

    private static long ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        long result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (pos >= data.Length)
            {
                throw new SentencePieceFormatException("The model is truncated.");
            }

            byte b = data[pos++];
            if (shift < 63)
            {
                result |= (long)(b & 0x7F) << shift;
            }

            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new SentencePieceFormatException("The model has an over-long integer.");
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int pos)
    {
        long length = ReadVarint(data, ref pos);
        if (length < 0 || length > data.Length - pos)
        {
            throw new SentencePieceFormatException("A length runs past the end of the model.");
        }

        ReadOnlySpan<byte> r = data.Slice(pos, (int)length);
        pos += (int)length;
        return r;
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0:
                _ = ReadVarint(data, ref pos);
                break;
            case 1:
                Advance(data, ref pos, 8);
                break;
            case 2:
                _ = ReadBytes(data, ref pos);
                break;
            case 5:
                Advance(data, ref pos, 4);
                break;
            default:
                throw new SentencePieceFormatException("The model uses an unsupported field encoding.");
        }
    }

    private static void Advance(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (data.Length - pos < count)
        {
            throw new SentencePieceFormatException("The model is truncated.");
        }

        pos += count;
    }
}
