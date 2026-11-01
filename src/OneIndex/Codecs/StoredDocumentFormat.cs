using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// How a document's stored fields are written inside a stored-source block (docs/stored-source.md): a count, then for each value
/// the index of its field in the file's field table, a kind byte and a payload. Values keep the order of the document, so
/// multi-valued fields read back as they went in.
/// </summary>
internal static class StoredDocumentFormat
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Encode(Document document, Dictionary<string, int> fieldIndex, ByteBuilder destination)
    {
        int count = 0;
        foreach (DocumentField f in document.Fields)
        {
            if (fieldIndex.ContainsKey(f.Name))
            {
                count++;
            }
        }

        destination.WriteVarUInt((uint)count);
        foreach (DocumentField f in document.Fields)
        {
            if (!fieldIndex.TryGetValue(f.Name, out int index))
            {
                continue;
            }

            destination.WriteVarUInt((uint)index);
            destination.WriteByte((byte)f.Value.Kind);
            switch (f.Value.Kind)
            {
                case FieldValueKind.Text:
                    WriteBytes(destination, Encoding.UTF8.GetBytes(f.Value.AsText()));
                    break;
                case FieldValueKind.Integral:
                case FieldValueKind.Date:
                    long v = f.Value.AsInt64();
                    destination.WriteVarUInt(unchecked((ulong)((v << 1) ^ (v >> 63))));
                    break;
                case FieldValueKind.Fractional:
                    destination.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(f.Value.AsFloat64()));
                    break;
                case FieldValueKind.Bool:
                    destination.WriteByte(f.Value.AsBool() ? (byte)1 : (byte)0);
                    break;
                case FieldValueKind.GeoPoint:
                    (double lat, double lon) = f.Value.AsGeoPoint();
                    destination.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(lat));
                    destination.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(lon));
                    break;
                case FieldValueKind.Bytes:
                    WriteBytes(destination, f.Value.AsBytes().Span);
                    break;
                case FieldValueKind.Vector:
                    ReadOnlySpan<float> vector = f.Value.AsVector().Span;
                    destination.WriteVarUInt((uint)vector.Length);
                    foreach (float x in vector)
                    {
                        destination.WriteUInt32(BitConverter.SingleToUInt32Bits(x));
                    }

                    break;
                case FieldValueKind.Sparse:
                    SparseVector sparse = f.Value.AsSparse();
                    destination.WriteVarUInt((uint)sparse.Count);
                    for (int k = 0; k < sparse.Count; k++)
                    {
                        WriteBytes(destination, Encoding.UTF8.GetBytes(sparse.Tokens[k]));
                        destination.WriteUInt32(BitConverter.SingleToUInt32Bits(sparse.Weights[k]));
                    }

                    break;
                case FieldValueKind.Json:
                    WriteBytes(destination, Encoding.UTF8.GetBytes(f.Value.AsJson().GetRawText()));
                    break;
                default:
                    throw new ArgumentException($"Field '{f.Name}' has a value of an unknown kind.");
            }
        }
    }

    /// <summary>Rebuilds a document from its source bytes. Raises <see cref="CorruptIndexException"/> for malformed bytes.</summary>
    public static Document Decode(string fileName, ReadOnlySpan<byte> source, string[] names, IndexLimits limits)
    {
        var document = new Document();
        var cursor = new Reader(fileName, source);
        int count = cursor.Count(source.Length, "field value count");
        for (int i = 0; i < count; i++)
        {
            int index = cursor.Count(names.Length - 1, "field index");
            if (names.Length == 0)
            {
                throw new CorruptIndexException(fileName, "a document has values but the file lists no fields");
            }

            var kind = (FieldValueKind)cursor.Byte();
            FieldValue value;
            switch (kind)
            {
                case FieldValueKind.Text:
                    value = FieldValue.FromText(cursor.Text(limits));
                    break;
                case FieldValueKind.Integral:
                    value = FieldValue.FromInt64(cursor.ZigZag());
                    break;
                case FieldValueKind.Date:
                    value = FieldValue.FromDate(DateTimeOffsetFromMilliseconds(fileName, cursor.ZigZag()));
                    break;
                case FieldValueKind.Fractional:
                    value = FieldValue.FromFloat64(BitConverter.Int64BitsToDouble(cursor.Int64()));
                    break;
                case FieldValueKind.Bool:
                    value = FieldValue.FromBool(cursor.Byte() != 0);
                    break;
                case FieldValueKind.GeoPoint:
                    try
                    {
                        value = FieldValue.FromGeoPoint(BitConverter.Int64BitsToDouble(cursor.Int64()), BitConverter.Int64BitsToDouble(cursor.Int64()));
                    }
                    catch (ArgumentOutOfRangeException e)
                    {
                        throw new CorruptIndexException(fileName, "a stored geo point is outside the valid range", e);
                    }

                    break;
                case FieldValueKind.Bytes:
                    value = FieldValue.FromBytes(cursor.Bytes().ToArray());
                    break;
                case FieldValueKind.Vector:
                    {
                        int dims = cursor.Count(limits.MaxVectorDimensions, "vector dimensions");
                        var vector = new float[dims];
                        for (int k = 0; k < dims; k++)
                        {
                            vector[k] = BitConverter.UInt32BitsToSingle(cursor.UInt32());
                        }

                        value = FieldValue.FromVector(vector);
                        break;
                    }

                case FieldValueKind.Sparse:
                    {
                        int n = cursor.Count(SparseVector.MaxEntries, "sparse entries");
                        var entries = new List<KeyValuePair<string, float>>(n);
                        for (int k = 0; k < n; k++)
                        {
                            string token = cursor.Text(limits);
                            entries.Add(new(token, BitConverter.UInt32BitsToSingle(cursor.UInt32())));
                        }

                        try
                        {
                            value = FieldValue.FromSparse(new SparseVector(entries));
                        }
                        catch (ArgumentException e)
                        {
                            throw new CorruptIndexException(fileName, "a stored sparse vector is invalid", e);
                        }

                        break;
                    }

                case FieldValueKind.Json:
                    try
                    {
                        value = FieldValue.FromJson(JsonDocument.Parse(cursor.Bytes().ToArray()).RootElement);
                    }
                    catch (JsonException e)
                    {
                        throw new CorruptIndexException(fileName, "a stored JSON value is not valid JSON", e);
                    }

                    break;
                default:
                    throw new CorruptIndexException(fileName, $"a stored value has unknown kind {(int)kind}");
            }

            document.Add(names[index], value);
        }

        if (cursor.Remaining != 0)
        {
            throw new CorruptIndexException(fileName, "a stored document has trailing bytes");
        }

        return document;
    }

    /// <summary>Copies a source to a new field table, dropping values whose field is not in the new table (<paramref name="map"/> is -1 for those).</summary>
    public static void Transcode(string fileName, ReadOnlySpan<byte> source, int[] map, ByteBuilder destination, IndexLimits limits)
    {
        var cursor = new Reader(fileName, source);
        int count = cursor.Count(source.Length, "field value count");
        var kept = new ByteBuilder(source.Length + 16);
        int keptCount = 0;
        for (int i = 0; i < count; i++)
        {
            int index = cursor.Count(map.Length - 1, "field index");
            if (map.Length == 0)
            {
                throw new CorruptIndexException(fileName, "a document has values but the file lists no fields");
            }

            int start = cursor.Position;
            byte kind = cursor.Byte();
            cursor.Skip((FieldValueKind)kind, limits);
            if (map[index] >= 0)
            {
                kept.WriteVarUInt((uint)map[index]);
                kept.Write(source[start..cursor.Position]);
                keptCount++;
            }
        }

        destination.WriteVarUInt((uint)keptCount);
        destination.Write(kept.Span);
    }

    private static DateTimeOffset DateTimeOffsetFromMilliseconds(string fileName, long ms)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new CorruptIndexException(fileName, "a stored date is outside the representable range", e);
        }
    }

    private static void WriteBytes(ByteBuilder destination, ReadOnlySpan<byte> bytes)
    {
        destination.WriteVarUInt((uint)bytes.Length);
        destination.Write(bytes);
    }

    private ref struct Reader(string fileName, ReadOnlySpan<byte> data)
    {
        private readonly string _name = fileName;
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public readonly int Remaining => _data.Length - Position;

        public byte Byte()
        {
            if (Position >= _data.Length)
            {
                throw new CorruptIndexException(_name, "a stored document ends early");
            }

            return _data[Position++];
        }

        public ulong VarUInt()
        {
            ulong result = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                byte b = Byte();
                if (shift == 63 && b > 1)
                {
                    throw new CorruptIndexException(_name, "a stored varint overflows");
                }

                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }
            }

            throw new CorruptIndexException(_name, "a stored varint is too long");
        }

        public int Count(long max, string what)
        {
            ulong v = VarUInt();
            if (v > (ulong)Math.Max(max, 0))
            {
                throw new CorruptIndexException(_name, $"{what} {v} exceeds {max}");
            }

            return (int)v;
        }

        public long ZigZag()
        {
            ulong v = VarUInt();
            return unchecked((long)(v >> 1) ^ -(long)(v & 1));
        }

        public long Int64()
        {
            if (Remaining < 8)
            {
                throw new CorruptIndexException(_name, "a stored document ends early");
            }

            long v = BinaryPrimitives.ReadInt64LittleEndian(_data[Position..]);
            Position += 8;
            return v;
        }

        public uint UInt32()
        {
            if (Remaining < 4)
            {
                throw new CorruptIndexException(_name, "a stored document ends early");
            }

            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
            Position += 4;
            return v;
        }

        public ReadOnlySpan<byte> Bytes()
        {
            ulong length = VarUInt();
            if (length > (ulong)Remaining)
            {
                throw new CorruptIndexException(_name, "a stored length runs past the end of its document");
            }

            ReadOnlySpan<byte> v = _data.Slice(Position, (int)length);
            Position += (int)length;
            return v;
        }

        public string Text(IndexLimits limits)
        {
            ReadOnlySpan<byte> bytes = Bytes();
            _ = limits;
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException e)
            {
                throw new CorruptIndexException(_name, "a stored text value is not valid UTF-8", e);
            }
        }

        public void Skip(FieldValueKind kind, IndexLimits limits)
        {
            switch (kind)
            {
                case FieldValueKind.Text:
                case FieldValueKind.Bytes:
                case FieldValueKind.Json:
                    _ = Bytes();
                    break;
                case FieldValueKind.Integral:
                case FieldValueKind.Date:
                    _ = VarUInt();
                    break;
                case FieldValueKind.Fractional:
                    _ = Int64();
                    break;
                case FieldValueKind.Bool:
                    _ = Byte();
                    break;
                case FieldValueKind.GeoPoint:
                    _ = Int64();
                    _ = Int64();
                    break;
                case FieldValueKind.Vector:
                    {
                        int dims = Count(limits.MaxVectorDimensions, "vector dimensions");
                        if (Remaining < (long)dims * 4)
                        {
                            throw new CorruptIndexException(_name, "a stored vector ends early");
                        }

                        Position += dims * 4;
                        break;
                    }

                case FieldValueKind.Sparse:
                    {
                        int n = Count(SparseVector.MaxEntries, "sparse entries");
                        for (int k = 0; k < n; k++)
                        {
                            _ = Bytes();
                            _ = UInt32();
                        }

                        break;
                    }

                default:
                    throw new CorruptIndexException(_name, $"a stored value has unknown kind {(int)kind}");
            }
        }
    }
}
