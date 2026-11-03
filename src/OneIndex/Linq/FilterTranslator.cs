using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Linq;

/// <summary>Thrown when a <c>Where</c> expression uses something the index cannot filter on. The message shows the offending part.</summary>
public sealed class UnsupportedExpressionException : NotSupportedException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="expression">The sub-expression that cannot be translated.</param>
    /// <param name="reason">Why, and what to use instead.</param>
    public UnsupportedExpressionException(Expression expression, string reason)
        : base($"The expression '{expression}' cannot be used in Where: {reason}")
    {
        Expression = expression;
    }

    /// <summary>The sub-expression that cannot be translated.</summary>
    public Expression Expression { get; }
}

/// <summary>
/// Translates a <c>Where</c> predicate into query objects by walking the expression tree. Nothing is compiled or run except the
/// parts that do not mention the document (captured variables and constants), which are read directly. The supported forms are in
/// <c>docs/linq.md</c>.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class FilterTranslator<T>
    where T : class
{
    private readonly SearchMapping<T> _mapping;

    /// <summary>Creates a translator.</summary>
    /// <param name="mapping">The type's mapping, which says which members can be filtered.</param>
    public FilterTranslator(SearchMapping<T> mapping)
    {
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
    }

    /// <summary>Translates a predicate to a query to be used as a filter (its score does not matter).</summary>
    /// <param name="predicate">The predicate.</param>
    /// <returns>The query.</returns>
    /// <exception cref="UnsupportedExpressionException">Part of the predicate cannot be translated; the message shows it.</exception>
    public Search.Query Translate(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Visit(predicate.Body, predicate.Parameters[0]);
    }

    private static MatchAllQuery All() => new MatchAllQuery();

    private static BooleanQuery None() => new BooleanQuery([]);

    private Search.Query Visit(Expression e, ParameterExpression p)
    {
        if (!Mentions(e, p))
        {
            return Evaluate(e) is bool b ? (b ? All() : None()) : throw new UnsupportedExpressionException(e, "it does not produce a bool.");
        }

        switch (e)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.And } and:
                return new BooleanQuery([new BooleanClause(Visit(and.Left, p), Occur.Filter), new BooleanClause(Visit(and.Right, p), Occur.Filter)]);
            case BinaryExpression { NodeType: ExpressionType.OrElse or ExpressionType.Or } or:
                return new BooleanQuery([new BooleanClause(Visit(or.Left, p), Occur.Should), new BooleanClause(Visit(or.Right, p), Occur.Should)]) { MinimumShouldMatch = 1 };
            case UnaryExpression { NodeType: ExpressionType.Not } not when not.Operand.Type == typeof(bool):
                return Negate(Visit(not.Operand, p));
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual } cmp:
                return Compare(cmp, p);
            case MemberExpression m when m.Type == typeof(bool) && Member(m, p) is { } member:
                return Equal(member, true, e);
            case MethodCallExpression call:
                return Call(call, p);
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conv when conv.Type == typeof(bool) || conv.Type == typeof(bool?):
                return Visit(conv.Operand, p);
        }

        throw new UnsupportedExpressionException(e, "only comparisons of members with values, Contains on keyword collections, &&, || and ! can be filtered.");
    }

    // Not x, as a filter: everything that does not match x. Documents missing the member match, as they do for C# comparisons of lifted nulls.
    private static BooleanQuery Negate(Search.Query inner)
        => new BooleanQuery([new BooleanClause(All(), Occur.Filter), new BooleanClause(inner, Occur.MustNot)]);

    private Search.Query Compare(BinaryExpression cmp, ParameterExpression p)
    {
        if (StripConvert(cmp.Left) is MethodCallExpression pathCall && IsPathCall(pathCall))
        {
            return ComparePath(cmp, pathCall, p);
        }

        ExpressionType op = cmp.NodeType;
        Expression left = StripConvert(cmp.Left);
        Expression right = StripConvert(cmp.Right);
        MappedMember? member = Member(left, p);
        Expression valueSide = right;
        if (member is null)
        {
            member = Member(right, p);
            valueSide = left;
            op = Flip(op);
        }

        if (member is null)
        {
            throw new UnsupportedExpressionException(cmp, "one side must be a member of the document (a property of the lambda parameter, with no nested path or method call).");
        }

        if (Mentions(valueSide, p))
        {
            throw new UnsupportedExpressionException(cmp, "the other side may not use the document; compare a member with a constant or a captured variable.");
        }

        CheckFilterable(member, cmp);
        object? value = Evaluate(valueSide);
        if (value is null)
        {
            // A null comparison asks whether the document has a value: the index stores no explicit nulls, and an empty string or an empty
            // collection is not stored either, so those count as absent too (docs/linq.md).
            return op switch
            {
                ExpressionType.Equal => Negate(new RangeQuery(member.Name, null, null)),
                ExpressionType.NotEqual => new RangeQuery(member.Name, null, null),
                _ => throw new UnsupportedExpressionException(cmp, "null can only be compared with == and !=."),
            };
        }

        return op switch
        {
            ExpressionType.Equal => EqualsValue(member, value, cmp),
            ExpressionType.NotEqual => Negate(EqualsValue(member, value, cmp)),
            _ => Bounds(member, op, value, cmp),
        };
    }

    private Search.Query Call(MethodCallExpression call, ParameterExpression p)
    {
        if (call.Object is not null && call.Method.DeclaringType == typeof(DynamicValue) && call.Method.Name == nameof(DynamicValue.Exists) && StripConvert(call.Object) is MethodCallExpression existsPath && IsPathCall(existsPath))
        {
            return new RangeQuery(PathName(existsPath, p), null, null);
        }

        if (call.Method.DeclaringType == typeof(Geo))
        {
            return GeoCall(call, p);
        }

        if (call.Method.DeclaringType == typeof(Enumerable) && call.Method.Name == nameof(Enumerable.Any) && call.Object is null)
        {
            return AnyCall(call, p);
        }

        // collection.Contains(item): the member is the collection (any of its values equals item) or the item (it is one of a captured list).
        Expression? collection = null;
        Expression? item = null;
        if (call.Method.Name == "Contains")
        {
            // Arrays reach Contains as Enumerable.Contains or, with first-class spans, as MemoryExtensions.Contains over an implicit span conversion.
            if (call.Object is null && call.Arguments.Count == 2 && call.Method.DeclaringType == typeof(Enumerable) || call.Object is null && call.Method.DeclaringType == typeof(MemoryExtensions) && (call.Arguments.Count == 2 || call.Arguments.Count == 3 && call.Arguments[2] is ConstantExpression { Value: null }))
            {
                collection = call.Arguments[0];
                item = call.Arguments[1];
            }
            else if (call.Object is not null && call.Arguments.Count == 1 && call.Object.Type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(call.Object.Type))
            {
                collection = call.Object;
                item = call.Arguments[0];
            }
        }

        if (collection is null || item is null)
        {
            throw new UnsupportedExpressionException(call, "only Contains on a keyword collection or on a captured list is supported; string methods such as Contains, StartsWith and Equals are not filters.");
        }

        collection = StripConvert(collection);
        item = StripConvert(item);
        if (Member(collection, p) is { } list)
        {
            if (!list.Multi)
            {
                throw new UnsupportedExpressionException(call, $"'{list.Name}' is not a collection member.");
            }

            CheckFilterable(list, call);
            if (Mentions(item, p))
            {
                throw new UnsupportedExpressionException(call, "the searched value may not use the document.");
            }

            return EqualsValue(list, Evaluate(item) ?? throw new UnsupportedExpressionException(call, "searching for null is not supported."), call);
        }

        if (Member(item, p) is { } single && !Mentions(collection, p))
        {
            CheckFilterable(single, call);
            if (Evaluate(collection) is not IEnumerable values)
            {
                throw new UnsupportedExpressionException(call, "the list is null.");
            }

            var alternatives = new List<BooleanClause>();
            foreach (object? v in values)
            {
                // A null in the list matches documents without a value, as List.Contains(null) does for a null member.
                alternatives.Add(new BooleanClause(v is null ? Negate(new RangeQuery(single.Name, null, null)) : EqualsValue(single, v, call), Occur.Should));
            }

            return alternatives.Count == 0 ? None() : new BooleanQuery(alternatives) { MinimumShouldMatch = 1 };
        }

        throw new UnsupportedExpressionException(call, "Contains must be called on a member that is a collection, or with a member as its argument.");
    }

    // collection.Any() holds when the document has a value; collection.Any(t => ...) holds when some value satisfies the condition on t.
    private Search.Query AnyCall(MethodCallExpression call, ParameterExpression p)
    {
        if (Member(StripConvert(call.Arguments[0]), p) is not { Multi: true } list)
        {
            throw new UnsupportedExpressionException(call, "Any is supported on a collection property of the document.");
        }

        CheckFilterable(list, call);
        if (call.Arguments.Count == 1)
        {
            return new RangeQuery(list.Name, null, null);
        }

        if (call.Arguments[1] is not LambdaExpression { Parameters.Count: 1 } lambda)
        {
            throw new UnsupportedExpressionException(call, "Any needs a lambda such as t => t == \"a\".");
        }

        return AnyBody(lambda.Body, lambda.Parameters[0], list, p, call);
    }

    // The condition is on one element t. Only forms that mean "some element" are translated: == and ordering against a value, a captured
    // list's Contains(t), and || of those. != and && would need the same element to satisfy two tests, which a term index cannot answer.
    private static Search.Query AnyBody(Expression e, ParameterExpression t, MappedMember list, ParameterExpression p, Expression context)
    {
        switch (e)
        {
            case BinaryExpression { NodeType: ExpressionType.OrElse or ExpressionType.Or } or:
                return new BooleanQuery([new BooleanClause(AnyBody(or.Left, t, list, p, context), Occur.Should), new BooleanClause(AnyBody(or.Right, t, list, p, context), Occur.Should)]) { MinimumShouldMatch = 1 };
            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual } cmp:
            {
                ExpressionType op = cmp.NodeType;
                Expression left = StripConvert(cmp.Left);
                Expression right = StripConvert(cmp.Right);
                Expression valueSide = right;
                if (left != t)
                {
                    if (right != t)
                    {
                        break;
                    }

                    valueSide = left;
                    op = Flip(op);
                }

                if (Mentions(valueSide, t) || Mentions(valueSide, p))
                {
                    throw new UnsupportedExpressionException(cmp, "the other side may not use the document or the element.");
                }

                object value = Evaluate(valueSide) ?? throw new UnsupportedExpressionException(cmp, "comparing an element with null is not supported.");
                return op == ExpressionType.Equal ? EqualsValue(list, value, cmp) : Bounds(list, op, value, cmp);
            }

            case MethodCallExpression { Method.Name: "Contains" } contains:
            {
                Expression? collection = null;
                Expression? item = null;
                if (contains.Object is null && contains.Arguments.Count == 2)
                {
                    collection = contains.Arguments[0];
                    item = contains.Arguments[1];
                }
                else if (contains.Object is not null && contains.Arguments.Count == 1)
                {
                    collection = contains.Object;
                    item = contains.Arguments[0];
                }

                if (collection is null || item is null || StripConvert(item) != t || Mentions(collection, t) || Mentions(collection, p))
                {
                    break;
                }

                if (Evaluate(StripConvert(collection)) is not IEnumerable values)
                {
                    throw new UnsupportedExpressionException(contains, "the list is null.");
                }

                var alternatives = new List<BooleanClause>();
                foreach (object? v in values)
                {
                    alternatives.Add(new BooleanClause(EqualsValue(list, v ?? throw new UnsupportedExpressionException(contains, "the list holds a null."), contains), Occur.Should));
                }

                return alternatives.Count == 0 ? None() : new BooleanQuery(alternatives) { MinimumShouldMatch = 1 };
            }
        }

        throw new UnsupportedExpressionException(e, "inside Any only t == value, ordering comparisons of t with a value, a captured list's Contains(t) and || of those can be filtered.");
    }

    private static bool IsPathCall(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(JsonPathExtensions) && call.Method.Name == nameof(JsonPathExtensions.Path) && call.Arguments.Count == 2;

    // The logical name of x.Property.Path("a.b"): the property name, a dot, then the path (the form the index and the other query surfaces use).
    private string PathName(MethodCallExpression call, ParameterExpression p)
    {
        if (Member(StripConvert(call.Arguments[0]), p) is not { Type: FieldType.Json } member)
        {
            throw new UnsupportedExpressionException(call, "Path can only be taken from a property of the document marked [Dynamic].");
        }

        if (Mentions(call.Arguments[1], p))
        {
            throw new UnsupportedExpressionException(call, "the path may not use the document; give a constant or a captured variable.");
        }

        return Evaluate(call.Arguments[1]) is string path
            ? member.Name + "." + path
            : throw new UnsupportedExpressionException(call, "the path is null.");
    }

    private Search.Query ComparePath(BinaryExpression cmp, MethodCallExpression pathCall, ParameterExpression p)
    {
        string name = PathName(pathCall, p);
        if (Mentions(cmp.Right, p))
        {
            throw new UnsupportedExpressionException(cmp, "the other side may not use the document; compare a path with a constant or a captured variable.");
        }

        object? value = Evaluate(cmp.Right) ?? throw new UnsupportedExpressionException(cmp, "comparing with null is not supported; absent values are simply not matched.");
        FieldValue v = value switch
        {
            string s => FieldValue.FromText(s),
            bool b => FieldValue.FromBool(b),
            DateTimeOffset dto => FieldValue.FromDate(dto),
            DateTime dt => FieldValue.FromDate(SearchMapping.ToDate(dt)),
            IConvertible c => FieldValue.FromFloat64(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new UnsupportedExpressionException(cmp, "a path is compared with a string, number, bool or date."),
        };
        switch (cmp.NodeType)
        {
            case ExpressionType.Equal:
                return EqualPath(name, v);
            case ExpressionType.NotEqual:
                return Negate(EqualPath(name, v));
        }

        if (v.Kind == FieldValueKind.Bool)
        {
            throw new UnsupportedExpressionException(cmp, "ordering comparisons are not supported on booleans.");
        }

        bool lower = cmp.NodeType is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
        bool inclusive = cmp.NodeType is ExpressionType.GreaterThanOrEqual or ExpressionType.LessThanOrEqual;
        return lower
            ? new RangeQuery(name, v, null) { IncludeLower = inclusive }
            : new RangeQuery(name, null, v) { IncludeUpper = inclusive };
    }

    private static Search.Query EqualPath(string name, FieldValue v)
        => v.Kind == FieldValueKind.Text ? new TermQuery(name, v.AsText()) : double.IsNaN(v.Kind == FieldValueKind.Fractional ? v.AsFloat64() : 0) ? None() : new RangeQuery(name, v, v);

    // Geo.WithinRadius and Geo.WithinBoundingBox: the first argument is the geo property, the others are numbers that do not use the document.
    private Search.Query GeoCall(MethodCallExpression call, ParameterExpression p)
    {
        // A nullable point arrives as x.Location.Value: look through the Value.
        Expression first = StripConvert(call.Arguments[0]);
        if (first is MemberExpression { Member.Name: "Value", Expression: { } inner } && Nullable.GetUnderlyingType(inner.Type) is not null)
        {
            first = inner;
        }

        MappedMember member = Member(first, p)
            ?? throw new UnsupportedExpressionException(call, "the first argument must be a [Geo] property of the document.");
        if (member.Type != FieldType.GeoPoint || !member.Indexed || !member.Column)
        {
            throw new UnsupportedExpressionException(call, $"'{member.Name}' is not a geo property; mark a GeoPoint property [Geo].");
        }

        var numbers = new double[call.Arguments.Count - 1];
        for (int i = 0; i < numbers.Length; i++)
        {
            Expression arg = StripConvert(call.Arguments[i + 1]);
            if (Mentions(arg, p))
            {
                throw new UnsupportedExpressionException(call, "the coordinates and the radius may not use the document.");
            }

            numbers[i] = Evaluate(arg) is { } value ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : throw new UnsupportedExpressionException(call, "a coordinate is null.");
        }

        return call.Method.Name switch
        {
            nameof(Geo.WithinRadius) => new GeoRadiusQuery(member.Name, numbers[0], numbers[1], numbers[2]),
            nameof(Geo.WithinBoundingBox) => new GeoBoundingBoxQuery(member.Name, numbers[0], numbers[1], numbers[2], numbers[3]),
            _ => throw new UnsupportedExpressionException(call, $"Geo.{call.Method.Name} is not supported."),
        };
    }

    private static void CheckFilterable(MappedMember member, Expression context)
    {
        if (member.FullText)
        {
            throw new UnsupportedExpressionException(context, $"'{member.Name}' is full-text; match it with the search text, or filter on a separate [Keyword] property.");
        }

        if (!member.Indexed)
        {
            throw new UnsupportedExpressionException(context, $"'{member.Name}' is not filterable; mark it [Filterable] (or [Keyword] for text).");
        }
    }

    private static Search.Query Equal(MappedMember member, object value, Expression context)
    {
        CheckFilterable(member, context);
        return EqualsValue(member, value, context);
    }

    // Values the index keeps in another form: a Guid as its text, a DateOnly as its day number, a TimeOnly as its ticks.
    private static object Normalize(object value) => value switch
    {
        Guid g => g.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly day => (long)day.DayNumber,
        TimeOnly time => time.Ticks,
        _ => value,
    };

    private static Search.Query EqualsValue(MappedMember member, object value, Expression context)
    {
        value = Normalize(value);
        if (member.Type == FieldType.Keyword)
        {
            return value is string s ? new TermQuery(member.Name, s) : throw new UnsupportedExpressionException(context, $"'{member.Name}' holds text; compare it with a string.");
        }

        // A fractional bound against an integer field is exact: equal only if the number is whole.
        if (member.Type == FieldType.Integral && value is double or float or decimal)
        {
            double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (d != Math.Floor(d))
            {
                return None();
            }
        }

        FieldValue v = ToFieldValue(member, value, context, ExpressionType.Equal);
        return new RangeQuery(member.Name, v, v);
    }

    private static RangeQuery Bounds(MappedMember member, ExpressionType op, object value, Expression context)
    {
        if (member.Type == FieldType.Keyword)
        {
            throw new UnsupportedExpressionException(context, $"ordering comparisons are not supported on keyword '{member.Name}'; use ==, != or Contains.");
        }

        if (member.Type == FieldType.Bool)
        {
            throw new UnsupportedExpressionException(context, $"ordering comparisons are not supported on bool '{member.Name}'.");
        }

        FieldValue v = ToFieldValue(member, value, context, op);
        // For an integer field and a fractional bound the value was rounded inward (> 2.5 became >= 3), so the bound is inclusive.
        bool rounded = member.Type == FieldType.Integral && value is double or float or decimal && IsFractional(value);
        bool inclusive = rounded || op is ExpressionType.GreaterThanOrEqual or ExpressionType.LessThanOrEqual;
        if (op is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual)
        {
            return new RangeQuery(member.Name, v, null) { IncludeLower = inclusive };
        }

        return new RangeQuery(member.Name, null, v) { IncludeUpper = inclusive };
    }

    private static bool IsFractional(object value)
    {
        double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        return d != Math.Floor(d);
    }

    private static FieldValue ToFieldValue(MappedMember member, object value, Expression context, ExpressionType op)
    {
        value = Normalize(value);
        try
        {
            switch (member.Type)
            {
                case FieldType.Integral:
                    if (value is double or float or decimal)
                    {
                        double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (double.IsNaN(d))
                        {
                            throw new UnsupportedExpressionException(context, "NaN cannot be compared.");
                        }

                        // > 2.5 and >= 2.5 both mean >= 3; < 2.5 and <= 2.5 both mean <= 2.
                        double rounded = op is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual ? Math.Ceiling(d) : Math.Floor(d);
                        return FieldValue.FromInt64(rounded >= 9.2233720368547758E+18 ? long.MaxValue : rounded <= -9.2233720368547758E+18 ? long.MinValue : (long)rounded);
                    }

                    return FieldValue.FromInt64(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                case FieldType.Fractional:
                    return FieldValue.FromFloat64(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                case FieldType.Bool:
                    return FieldValue.FromBool((bool)value);
                case FieldType.Date:
                    return value switch
                    {
                        DateTime dt => FieldValue.FromDate(SearchMapping.ToDate(dt)),
                        DateTimeOffset dto => FieldValue.FromDate(dto),
                        _ => throw new UnsupportedExpressionException(context, $"'{member.Name}' is a date; compare it with a DateTime or DateTimeOffset."),
                    };
                default:
                    throw new UnsupportedExpressionException(context, $"'{member.Name}' cannot be compared.");
            }
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            throw new UnsupportedExpressionException(context, $"the value {value} does not fit '{member.Name}' ({member.Type}).");
        }
    }

    private MappedMember? Member(Expression e, ParameterExpression p)
        => e is MemberExpression { Expression: { } owner, Member: PropertyInfo or FieldInfo } m && owner == p && _mapping.TryGetMember(m.Member.Name, out MappedMember? member)
            ? member
            : null;

    private static ExpressionType Flip(ExpressionType op) => op switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => op,
    };

    // The compiler adds conversions for widening and for lifting to nullable; they do not change which member or value is meant.
    private static Expression StripConvert(Expression e)
    {
        while (true)
        {
            if (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs, Method: null } u)
            {
                e = u.Operand;
            }
            else if (e is MethodCallExpression { Method: { Name: "op_Implicit", DeclaringType: { IsGenericType: true } owner }, Arguments.Count: 1 } call
                && (owner.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) || owner.GetGenericTypeDefinition() == typeof(Span<>)))
            {
                // C# 14 turns an array into a span before calling MemoryExtensions.Contains; the array is what is meant.
                e = call.Arguments[0];
            }
            else
            {
                return e;
            }
        }
    }

    private static bool Mentions(Expression e, ParameterExpression p)
    {
        var finder = new ParameterFinder(p);
        finder.Visit(e);
        return finder.Found;
    }

    private sealed class ParameterFinder(ParameterExpression target) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == target)
            {
                Found = true;
            }

            return node;
        }
    }

    // Reads a part of the expression that does not use the document: constants, captured variables, static members, and the calls and
    // constructors that build values from them (new DateTime(2024, 1, 1), now.AddDays(-7)). Nothing is compiled.
    private static object? Evaluate(Expression e)
    {
        try
        {
            switch (e)
            {
                case ConstantExpression c:
                    return c.Value;
                case MemberExpression m:
                    object? owner = m.Expression is null ? null : Evaluate(m.Expression);
                    return m.Member switch
                    {
                        FieldInfo f => f.GetValue(owner),
                        PropertyInfo prop => prop.GetValue(owner),
                        _ => throw new UnsupportedExpressionException(e, "this member cannot be read."),
                    };
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote or ExpressionType.TypeAs } u:
                    object? operand = Evaluate(u.Operand);
                    if (operand is null)
                    {
                        return null;
                    }

                    if (u.Method is not null)
                    {
                        return u.Method.Invoke(null, [operand]);
                    }

                    Type target = Nullable.GetUnderlyingType(u.Type) ?? u.Type;
                    return target.IsInstanceOfType(operand) || !typeof(IConvertible).IsAssignableFrom(target) || operand is not IConvertible
                        ? operand
                        : Convert.ChangeType(operand, target, System.Globalization.CultureInfo.InvariantCulture);
                case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply or ExpressionType.AddChecked or ExpressionType.SubtractChecked or ExpressionType.MultiplyChecked } arithmetic:
                    return Arithmetic(arithmetic);
                case BinaryExpression { NodeType: ExpressionType.ArrayIndex } index when Evaluate(index.Left) is Array array && Evaluate(index.Right) is int at:
                    return array.GetValue(at);
                case UnaryExpression { NodeType: ExpressionType.ArrayLength } length when Evaluate(length.Operand) is Array counted:
                    return counted.Length;
                case UnaryExpression { NodeType: ExpressionType.Not } not when Evaluate(not.Operand) is bool nb:
                    return !nb;
                case MethodCallExpression call:
                    object? instance = call.Object is null ? null : Evaluate(call.Object);
                    var args = new object?[call.Arguments.Count];
                    for (int i = 0; i < args.Length; i++)
                    {
                        args[i] = Evaluate(call.Arguments[i]);
                    }

                    return call.Method.Invoke(instance, args);
                case NewExpression { Constructor: { } ctor } created:
                    var ctorArgs = new object?[created.Arguments.Count];
                    for (int i = 0; i < ctorArgs.Length; i++)
                    {
                        ctorArgs[i] = Evaluate(created.Arguments[i]);
                    }

                    return ctor.Invoke(ctorArgs);
                case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                    var items = new List<object?>(array.Expressions.Count);
                    foreach (Expression item in array.Expressions)
                    {
                        items.Add(Evaluate(item));
                    }

                    return items.ToArray();
                case ListInitExpression:
                case DefaultExpression:
                    break;
            }
        }
        catch (TargetInvocationException inner)
        {
            throw new UnsupportedExpressionException(e, $"evaluating it threw {inner.InnerException?.GetType().Name}: {inner.InnerException?.Message}");
        }
        catch (TargetException)
        {
            throw new UnsupportedExpressionException(e, "a value it needs is null.");
        }

        throw new UnsupportedExpressionException(e, "its value cannot be read without the document; use a constant, a captured variable or a simple member access.");
    }

    // Plus, minus and times on captured numbers and on values with operators (DateTime minus TimeSpan).
    private static object? Arithmetic(BinaryExpression b)
    {
        object? left = Evaluate(b.Left);
        object? right = Evaluate(b.Right);
        if (left is null || right is null)
        {
            return null;
        }

        if (b.Method is not null)
        {
            return b.Method.Invoke(null, [left, right]);
        }

        bool plus = b.NodeType is ExpressionType.Add or ExpressionType.AddChecked;
        bool minus = b.NodeType is ExpressionType.Subtract or ExpressionType.SubtractChecked;
        return (left, right) switch
        {
            (int l, int r) => plus ? checked(l + r) : minus ? checked(l - r) : checked(l * r),
            (long l, long r) => plus ? checked(l + r) : minus ? checked(l - r) : checked(l * r),
            (double l, double r) => plus ? l + r : minus ? l - r : l * r,
            (float l, float r) => plus ? l + r : minus ? l - r : l * r,
            _ => throw new UnsupportedExpressionException(b, "arithmetic is supported on two ints, longs, floats or doubles of the same type."),
        };
    }
}
