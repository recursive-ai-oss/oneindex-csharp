using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OneIndex.Generators;

/// <summary>
/// For every class whose members carry OneIndex attributes, emits a <c>SearchMapping&lt;T&gt;</c>: the schema and the conversion between the
/// class and an untyped document, written as plain C# so that nothing is discovered by reflection at run time (AOT and trimming keep
/// exactly what the code names). The mapping registers itself from a module initializer.
/// </summary>
[Generator]
public sealed class MappingGenerator : IIncrementalGenerator
{
    private const string AttributeNamespace = "OneIndex";

    private static readonly DiagnosticDescriptor UnsupportedType = Error("OI0001", "Unsupported member type", "Member '{0}' has type '{1}', which {2} cannot map");
    private static readonly DiagnosticDescriptor NotYetSupported = Error("OI0002", "Attribute not supported yet", "[{1}] on '{0}' is declared but not supported yet");
    private static readonly DiagnosticDescriptor Conflict = Error("OI0003", "Conflicting attributes", "Member '{0}': {1}");
    private static readonly DiagnosticDescriptor BadId = Error("OI0004", "Invalid identifier member", "{0}");
    private static readonly DiagnosticDescriptor BadType = Error("OI0005", "Type cannot be mapped", "Type '{0}' {1}");
    private static readonly DiagnosticDescriptor BadMember = Error("OI0006", "Member cannot be mapped", "Member '{0}' {1}");
    private static readonly DiagnosticDescriptor BadAttributeValue = Error("OI0007", "Invalid attribute value", "Member '{0}': {1}");

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Result> classes = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax c && c.Members.Any(m => (m is PropertyDeclarationSyntax || m is FieldDeclarationSyntax) && m.AttributeLists.Count > 0),
                static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is not null)!;

        context.RegisterSourceOutput(classes, static (spc, item) =>
        {
            foreach (Diagnostic d in item.Diagnostics)
            {
                spc.ReportDiagnostic(d);
            }

            if (item.Source is not null)
            {
                spc.AddSource(item.HintName, item.Source);
            }
        });
    }

    private static DiagnosticDescriptor Error(string id, string title, string message)
        => new(id, title, message, "OneIndex", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private enum Shape
    {
        Text,
        TextArray,
        TextList,
        TextInterface,
        Integral,
        Fractional,
        Bool,
        DateTime,
        DateTimeOffset,
        Geo,
    }

    private sealed class Result
    {
        public string HintName = string.Empty;
        public string? Source;
        public List<Diagnostic> Diagnostics = new List<Diagnostic>();
    }

    private sealed class Member
    {
        public string Name = string.Empty;
        public Shape Shape;
        public bool Nullable;
        public string TypeName = string.Empty;
        public bool FullText;
        public bool Keyword;
        public bool IsId;
        public bool Filterable;
        public bool Sortable;
        public bool Facet;
        public bool GeoAttr;
        public double Boost = 1;
        public string? Language;
        public string? Analyzer;

        public bool Multi => Shape is Shape.TextArray or Shape.TextList or Shape.TextInterface;
    }

    private static Result? Describe(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type)
        {
            return null;
        }

        var result = new Result();
        var members = new List<Member>();
        bool any = false;
        foreach (ISymbol symbol in type.GetMembers())
        {
            if (symbol is IFieldSymbol field && !field.IsImplicitlyDeclared && field.GetAttributes().Any(IsOurs))
            {
                result.Diagnostics.Add(Diagnostic.Create(BadMember, field.Locations.FirstOrDefault(), field.Name, "is a field; put the attributes on a property"));
                any = true;
                continue;
            }

            if (symbol is not IPropertySymbol property || property.IsStatic || property.IsIndexer)
            {
                continue;
            }

            if (property.GetAttributes().Any(IsOurs))
            {
                any = true;
            }

            Member? m = DescribeMember(property, result.Diagnostics);
            if (m is not null)
            {
                members.Add(m);
            }
        }

        if (!any)
        {
            return null;
        }

        Location typeLocation = type.Locations.FirstOrDefault() ?? Location.None;
        string typeName = type.ToDisplayString();
        if (type.IsGenericType)
        {
            result.Diagnostics.Add(Diagnostic.Create(BadType, typeLocation, typeName, "is generic, which cannot be mapped"));
        }

        if (type.IsAbstract || type.IsStatic)
        {
            result.Diagnostics.Add(Diagnostic.Create(BadType, typeLocation, typeName, "is abstract or static; a mapped type must be a class that can be created"));
        }

        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            {
                result.Diagnostics.Add(Diagnostic.Create(BadType, typeLocation, typeName, "is not accessible to generated code in its assembly (it or a containing type is private or protected)"));
                break;
            }
        }

        if (!type.IsAbstract && !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            result.Diagnostics.Add(Diagnostic.Create(BadType, typeLocation, typeName, "needs a public or internal parameterless constructor"));
        }

        Member[] ids = members.Where(m => m.IsId).ToArray();
        if (ids.Length > 1)
        {
            result.Diagnostics.Add(Diagnostic.Create(BadId, typeLocation, "Type '" + typeName + "' has several [SearchId] members: " + string.Join(", ", ids.Select(i => i.Name)) + "."));
        }

        string ns = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();
        string safe = (ns.Length == 0 ? string.Empty : ns.Replace('.', '_') + "_") + string.Join("_", ContainingChain(type));
        result.HintName = safe + ".Mapping.g.cs";
        if (result.Diagnostics.Count == 0)
        {
            result.Source = Emit(type, safe, members);
        }

        return result;
    }

    private static List<string> ContainingChain(INamedTypeSymbol type)
    {
        var names = new List<string>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            names.Insert(0, t.Name);
        }

        return names;
    }

    private static bool IsOurs(AttributeData a) => a.AttributeClass is { } c && c.ContainingNamespace.ToDisplayString() == AttributeNamespace && c.Name.EndsWith("Attribute", StringComparison.Ordinal);

    private static Member? DescribeMember(IPropertySymbol property, List<Diagnostic> diagnostics)
    {
        Location location = property.Locations.FirstOrDefault() ?? Location.None;
        var m = new Member { Name = property.Name };
        bool declared = false;
        foreach (AttributeData attribute in property.GetAttributes())
        {
            if (!IsOurs(attribute))
            {
                continue;
            }

            declared = true;
            switch (attribute.AttributeClass!.Name)
            {
                case "SearchIdAttribute":
                    m.IsId = true;
                    break;
                case "FullTextAttribute":
                    m.FullText = true;
                    foreach (KeyValuePair<string, TypedConstant> arg in attribute.NamedArguments)
                    {
                        if (arg.Key == "Boost" && arg.Value.Value is float boost)
                        {
                            m.Boost = boost;
                        }
                        else if (arg.Key == "Language")
                        {
                            m.Language = arg.Value.Value as string;
                        }
                        else if (arg.Key == "Analyzer")
                        {
                            m.Analyzer = arg.Value.Value as string;
                        }
                    }

                    break;
                case "KeywordAttribute":
                    m.Keyword = true;
                    break;
                case "FilterableAttribute":
                    m.Filterable = true;
                    break;
                case "SortableAttribute":
                    m.Sortable = true;
                    break;
                case "FacetAttribute":
                    m.Facet = true;
                    break;
                case "GeoAttribute":
                    m.GeoAttr = true;
                    break;
                case "DynamicAttribute":
                    diagnostics.Add(Diagnostic.Create(NotYetSupported, location, property.Name, "Dynamic"));
                    return null;
                case "VectorAttribute":
                    diagnostics.Add(Diagnostic.Create(NotYetSupported, location, property.Name, "Vector"));
                    return null;
            }
        }

        if (!declared && property.DeclaredAccessibility != Accessibility.Public)
        {
            return null;
        }

        if (!TryClassify(property.Type, m))
        {
            if (declared)
            {
                diagnostics.Add(Diagnostic.Create(UnsupportedType, location, property.Name, property.Type.ToDisplayString(), "OneIndex"));
            }

            return null;
        }

        m.TypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        bool accessible = property.GetMethod is { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal }
            && property.SetMethod is { IsInitOnly: false, DeclaredAccessibility: Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal };
        if (!accessible || property.IsRequired)
        {
            if (declared)
            {
                diagnostics.Add(Diagnostic.Create(BadMember, location, property.Name, "needs a public or internal getter and a plain setter (init-only and required members are not supported yet)"));
            }

            return null;
        }

        if (m.FullText && m.Keyword)
        {
            diagnostics.Add(Diagnostic.Create(Conflict, location, property.Name, "[FullText] and [Keyword] cannot be combined; use two properties for both behaviours"));
        }

        if (m.IsId && (m.FullText || m.Shape != Shape.Text))
        {
            diagnostics.Add(Diagnostic.Create(BadId, location, "[SearchId] member '" + property.Name + "' must be a single string and not [FullText]."));
        }

        bool textual = m.Shape is Shape.Text or Shape.TextArray or Shape.TextList or Shape.TextInterface;
        if (m.FullText && !textual)
        {
            diagnostics.Add(Diagnostic.Create(UnsupportedType, location, property.Name, property.Type.ToDisplayString(), "[FullText]"));
        }

        if (m.GeoAttr && m.Shape != Shape.Geo)
        {
            diagnostics.Add(Diagnostic.Create(UnsupportedType, location, property.Name, property.Type.ToDisplayString(), "[Geo]"));
        }

        if (m.Facet && m.Shape == Shape.Geo)
        {
            diagnostics.Add(Diagnostic.Create(Conflict, location, property.Name, "a geo point cannot be a [Facet]"));
        }

        if (m.Keyword && !textual)
        {
            diagnostics.Add(Diagnostic.Create(UnsupportedType, location, property.Name, property.Type.ToDisplayString(), "[Keyword]"));
        }

        if (m.FullText && (m.Filterable || m.Facet || m.Sortable))
        {
            diagnostics.Add(Diagnostic.Create(Conflict, location, property.Name, "a [FullText] member cannot be [Filterable], [Sortable] or [Facet]; add a separate [Keyword] property for that"));
        }

        if (m.Multi && m.Sortable)
        {
            diagnostics.Add(Diagnostic.Create(Conflict, location, property.Name, "a collection cannot be [Sortable]"));
        }

        if (textual && !m.FullText && !m.Keyword && !m.IsId && (m.Filterable || m.Sortable || m.Facet))
        {
            // A string with only a filtering attribute is a keyword: the whole value is matched exactly.
            m.Keyword = true;
        }

        if (m.FullText && !(m.Boost > 0 && m.Boost < float.PositiveInfinity))
        {
            diagnostics.Add(Diagnostic.Create(BadAttributeValue, location, property.Name, "Boost must be finite and above 0"));
        }

        return m;
    }

    private static bool TryClassify(ITypeSymbol type, Member m)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            m.Nullable = true;
            type = nullable.TypeArguments[0];
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                m.Shape = Shape.Text;
                return !m.Nullable;
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
                m.Shape = Shape.Integral;
                return true;
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                m.Shape = Shape.Fractional;
                return true;
            case SpecialType.System_Boolean:
                m.Shape = Shape.Bool;
                return true;
            case SpecialType.System_DateTime:
                m.Shape = Shape.DateTime;
                return true;
        }

        string display = type.ToDisplayString();
        if (display == "System.DateTimeOffset")
        {
            m.Shape = Shape.DateTimeOffset;
            return true;
        }

        if (display == "OneIndex.GeoPoint")
        {
            m.Shape = Shape.Geo;
            return true;
        }

        if (m.Nullable)
        {
            return false;
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array && array.ElementType.SpecialType == SpecialType.System_String)
        {
            m.Shape = Shape.TextArray;
            return true;
        }

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic && generic.TypeArguments[0].SpecialType == SpecialType.System_String)
        {
            switch (generic.OriginalDefinition.ToDisplayString())
            {
                case "System.Collections.Generic.List<T>":
                    m.Shape = Shape.TextList;
                    return true;
                case "System.Collections.Generic.IEnumerable<T>":
                case "System.Collections.Generic.IReadOnlyList<T>":
                case "System.Collections.Generic.IReadOnlyCollection<T>":
                case "System.Collections.Generic.IList<T>":
                case "System.Collections.Generic.ICollection<T>":
                    m.Shape = Shape.TextInterface;
                    return true;
            }
        }

        return false;
    }

    private static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < ' ' || c > '~')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string Emit(INamedTypeSymbol type, string safe, List<Member> members)
    {
        string t = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace OneIndex.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>The mapping of <c>" + type.ToDisplayString().Replace("<", "&lt;") + "</c> to an index document, from its OneIndex attributes.</summary>");
        sb.AppendLine("    internal sealed class " + safe + "Mapping : global::OneIndex.SearchMapping<" + t + ">");
        sb.AppendLine("    {");
        sb.AppendLine("        private static readonly global::OneIndex.MappedMember[] MemberTable =");
        sb.AppendLine("        {");
        foreach (Member m in members)
        {
            sb.AppendLine("            " + MemberExpression(m) + ",");
        }

        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        public " + safe + "Mapping() : base(MemberTable)");
        sb.AppendLine("        {");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override string? GetId(" + t + " value) => " + (members.FirstOrDefault(m => m.IsId) is { } id ? "value." + id.Name : "null") + ";");
        sb.AppendLine();
        sb.AppendLine("        public override global::OneIndex.Index.Document ToDocument(" + t + " value)");
        sb.AppendLine("        {");
        sb.AppendLine("            var document = new global::OneIndex.Index.Document();");
        foreach (Member m in members)
        {
            EmitWrite(sb, m);
        }

        sb.AppendLine("            return document;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override " + t + " FromDocument(global::OneIndex.Index.Document document)");
        sb.AppendLine("        {");
        sb.AppendLine("            var result = new " + t + "();");
        foreach (Member m in members.Where(m => m.Multi))
        {
            sb.AppendLine("            global::System.Collections.Generic.List<string>? l_" + m.Name + " = null;");
        }

        sb.AppendLine("            foreach (global::OneIndex.Index.DocumentField field in document.Fields)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (field.Name)");
        sb.AppendLine("                {");
        foreach (Member m in members)
        {
            sb.AppendLine("                    case " + Lit(m.Name) + ":");
            sb.AppendLine("                        " + ReadStatement(m));
            sb.AppendLine("                        break;");
        }

        sb.AppendLine("                }");
        sb.AppendLine("            }");
        foreach (Member m in members.Where(m => m.Multi))
        {
            string value = m.Shape == Shape.TextList ? "l_" + m.Name : "l_" + m.Name + ".ToArray()";
            sb.AppendLine("            if (l_" + m.Name + " is not null) { result." + m.Name + " = " + value + "; }");
        }

        sb.AppendLine("            return result;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static class " + safe + "Registration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register() => global::OneIndex.SearchMappings.Register(new " + safe + "Mapping());");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string MemberExpression(Member m)
    {
        string kind = m.Shape switch
        {
            Shape.Text or Shape.TextArray or Shape.TextList or Shape.TextInterface => m.FullText ? "Text" : "Keyword",
            Shape.Integral => "Integral",
            Shape.Fractional => "Fractional",
            Shape.Bool => "Bool",
            Shape.Geo => "GeoPoint",
            _ => "Date",
        };
        // Text fields are searched when [FullText]; everything else is searchable by value only when marked for filtering.
        bool indexed = m.FullText || m.IsId || m.Keyword || m.Filterable || m.Facet || m.GeoAttr;
        bool column = m.Filterable || m.Sortable || m.Facet || m.IsId || m.GeoAttr;
        string boost = m.Boost.ToString("R", CultureInfo.InvariantCulture) + "f";
        string? analyzerName = m.FullText ? (m.Analyzer ?? m.Language) : null;
        string analyzer = analyzerName is null ? "null" : Lit(analyzerName);

        return "new global::OneIndex.MappedMember(" + Lit(m.Name) + ", global::OneIndex.Index.FieldType." + kind + ", multi: " + Bool(m.Multi) + ", indexed: " + Bool(indexed) + ", column: " + Bool(column)
            + ", facet: " + Bool(m.Facet) + ", fullText: " + Bool(m.FullText) + ", isId: " + Bool(m.IsId) + ", boost: " + boost + ", analyzer: " + analyzer + ")";
    }

    private static string Bool(bool b) => b ? "true" : "false";

    private static void EmitWrite(StringBuilder sb, Member m)
    {
        string n = Lit(m.Name);
        string p = "value." + m.Name;
        switch (m.Shape)
        {
            case Shape.Text:
                sb.AppendLine("            if (" + p + " is { } s_" + m.Name + ") { document.Add(" + n + ", s_" + m.Name + "); }");
                break;
            case Shape.TextArray:
            case Shape.TextList:
            case Shape.TextInterface:
                sb.AppendLine("            if (" + p + " is { } c_" + m.Name + ") { foreach (string? e in c_" + m.Name + ") { if (e is not null) { document.Add(" + n + ", e); } } }");
                break;
            default:
                string v = m.Nullable ? "n_" + m.Name : p;
                string add = m.Shape switch
                {
                    Shape.Integral => "document.Add(" + n + ", (long)" + v + ");",
                    Shape.Fractional => "document.Add(" + n + ", (double)" + v + ");",
                    Shape.Bool => "document.Add(" + n + ", " + v + ");",
                    Shape.DateTime => "document.Add(" + n + ", global::OneIndex.SearchMapping.ToDate(" + v + "));",
                    Shape.Geo => "document.Add(" + n + ", global::OneIndex.Index.FieldValue.FromGeoPoint(" + v + ".Latitude, " + v + ".Longitude));",
                    _ => "document.Add(" + n + ", " + v + ");",
                };
                sb.AppendLine(m.Nullable ? "            if (" + p + " is { } n_" + m.Name + ") { " + add + " }" : "            " + add);
                break;
        }
    }

    private static string ReadStatement(Member m)
    {
        string name = m.Name;
        return m.Shape switch
        {
            Shape.Text => "result." + name + " = field.Value.AsText();",
            Shape.TextArray or Shape.TextList or Shape.TextInterface => "(l_" + name + " ??= new global::System.Collections.Generic.List<string>()).Add(field.Value.AsText());",
            Shape.Integral => "result." + name + " = unchecked((" + IntegralType(m) + ")field.Value.AsInt64());",
            Shape.Fractional => "result." + name + " = (" + FractionalType(m) + ")field.Value.AsFloat64();",
            Shape.Bool => "result." + name + " = field.Value.AsBool();",
            Shape.DateTime => "result." + name + " = global::OneIndex.SearchMapping.FromDate(field.Value);",
            Shape.Geo => "{ (double la, double lo) = field.Value.AsGeoPoint(); result." + name + " = new global::OneIndex.GeoPoint(la, lo); }",
            _ => "result." + name + " = global::System.DateTimeOffset.FromUnixTimeMilliseconds(field.Value.AsInt64());",
        };
    }

    // The member's own numeric type, without the nullable wrapper (the assignment converts implicitly).
    private static string IntegralType(Member m) => Unwrap(m.TypeName);

    private static string FractionalType(Member m) => Unwrap(m.TypeName);

    private static string Unwrap(string typeName)
        => typeName.StartsWith("global::System.Nullable<", StringComparison.Ordinal) ? typeName.Substring(24, typeName.Length - 25) : typeName.EndsWith("?", StringComparison.Ordinal) ? typeName.Substring(0, typeName.Length - 1) : typeName;
}
