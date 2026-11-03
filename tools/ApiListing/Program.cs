using System.Reflection;
using System.Runtime.Loader;
using System.Text;

// Usage: ApiListing <assembly.dll> <output.txt>
// Dependencies are resolved from the directory of the assembly. The listing is one line per public type and member, sorted by ordinal
// comparison, so two runs over the same assembly are byte-identical.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ApiListing <assembly.dll> <output.txt>");
    return 2;
}

string path = Path.GetFullPath(args[0]);
string dir = Path.GetDirectoryName(path)!;
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string candidate = Path.Combine(dir, name.Name + ".dll");
    if (File.Exists(candidate))
    {
        return context.LoadFromAssemblyPath(candidate);
    }

    // Dependencies of the packages come from the NuGet cache (NUGET_PACKAGES): <id>/<version>/lib/<tfm>/<name>.dll, the newest version.
    string? cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (cache is not null && Directory.Exists(cache))
    {
        foreach (string package in Directory.EnumerateDirectories(cache))
        {
            foreach (string version in Directory.EnumerateDirectories(package).Order(StringComparer.Ordinal).Reverse())
            {
                foreach (string tfm in (Directory.Exists(Path.Combine(version, "lib")) ? Directory.EnumerateDirectories(Path.Combine(version, "lib")) : []).Order(StringComparer.Ordinal).Reverse())
                {
                    string dll = Path.Combine(tfm, name.Name + ".dll");
                    if (File.Exists(dll))
                    {
                        return context.LoadFromAssemblyPath(dll);
                    }
                }
            }
        }
    }

    return null;
};

Assembly assembly = Assembly.LoadFrom(path);
var lines = new List<string>();
foreach (Type type in assembly.GetTypes())
{
    if (!IsExposed(type))
    {
        continue;
    }

    string name = Name(type);
    lines.Add(Describe(type));
    BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (ConstructorInfo c in type.GetConstructors(all))
    {
        if (c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly)
        {
            lines.Add($"{name}.{Access(c.IsPublic)}.ctor({Parameters(c.GetParameters())})");
        }
    }

    foreach (MethodInfo m in type.GetMethods(all))
    {
        if (m.IsSpecialName || !(m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) || m.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null || m.Name.Contains('<', StringComparison.Ordinal))
        {
            continue;
        }

        string generic = m.IsGenericMethodDefinition ? "<" + string.Join(",", m.GetGenericArguments().Select(static a => a.Name)) + ">" : "";
        lines.Add($"{name}.{Access(m.IsPublic)}{(m.IsStatic ? "static " : "")}{(m.IsAbstract ? "abstract " : m.IsVirtual && !m.IsFinal ? "virtual " : "")}{m.Name}{generic}({Parameters(m.GetParameters())}) : {Name(m.ReturnType)}");
    }

    foreach (PropertyInfo p in type.GetProperties(all))
    {
        MethodInfo? g = p.GetMethod;
        MethodInfo? s = p.SetMethod;
        bool gv = g is not null && (g.IsPublic || g.IsFamily || g.IsFamilyOrAssembly);
        bool sv = s is not null && (s.IsPublic || s.IsFamily || s.IsFamilyOrAssembly);
        if (!gv && !sv || p.Name == "EqualityContract")
        {
            continue;
        }

        string accessors = (gv ? " get;" : "") + (sv ? (s!.ReturnParameter.GetRequiredCustomModifiers().Any(static t => t.Name == "IsExternalInit") ? " init;" : " set;") : "");
        string index = p.GetIndexParameters().Length > 0 ? "[" + Parameters(p.GetIndexParameters()) + "]" : "";
        lines.Add($"{name}.{Access((g ?? s)!.IsPublic)}{((g ?? s)!.IsStatic ? "static " : "")}{p.Name}{index} : {Name(p.PropertyType)} {{{accessors} }}");
    }

    foreach (FieldInfo f in type.GetFields(all))
    {
        if (!(f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly) || f.IsSpecialName)
        {
            continue;
        }

        string value = f.IsLiteral && !type.IsEnum ? " = " + Convert.ToString(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) : type.IsEnum ? " = " + Convert.ToString(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) : "";
        lines.Add($"{name}.{Access(f.IsPublic)}{(f.IsStatic && !f.IsLiteral ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{f.Name} : {Name(f.FieldType)}{value}");
    }

    foreach (EventInfo e in type.GetEvents(all))
    {
        lines.Add($"{name}.event {e.Name} : {Name(e.EventHandlerType!)}");
    }
}

lines.Sort(StringComparer.Ordinal);
File.WriteAllText(args[1], string.Join('\n', lines) + "\n", new UTF8Encoding(false));
Console.WriteLine($"{lines.Count} lines -> {args[1]}");
return 0;

static bool IsExposed(Type t)
{
    if (t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null)
    {
        return false;
    }

    return t.IsNested ? (t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem) && IsExposed(t.DeclaringType!) : t.IsPublic;
}

static string Access(bool isPublic) => isPublic ? "" : "protected ";

static string Describe(Type t)
{
    string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : typeof(Delegate).IsAssignableFrom(t) ? "delegate" : "class";
    string modifiers = t.IsClass && t.IsAbstract && t.IsSealed ? "static " : t.IsClass && t.IsAbstract ? "abstract " : t.IsClass && t.IsSealed && !typeof(Delegate).IsAssignableFrom(t) ? "sealed " : "";
    var bases = new List<string>();
    if (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum) && b != typeof(MulticastDelegate))
    {
        bases.Add(Name(b));
    }

    bases.AddRange(t.GetInterfaces().Where(i => IsPublicType(i)).Select(Name).Order(StringComparer.Ordinal));
    return $"{modifiers}{kind} {Name(t)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}";
}

static bool IsPublicType(Type t) => t.IsPublic || t.IsNestedPublic;

static string Parameters(ParameterInfo[] ps)
    => string.Join(", ", ps.Select(static p =>
        (p.ParameterType.IsByRef ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ") : "") + Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name
        + (p.HasDefaultValue ? " = " + (p.DefaultValue is null ? "null" : Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture)) : "")));

static string Name(Type t)
{
    if (t.IsByRef)
    {
        return Name(t.GetElementType()!);
    }

    if (t.IsArray)
    {
        return Name(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
    }

    if (t.IsGenericParameter)
    {
        return t.Name;
    }

    if (Nullable.GetUnderlyingType(t) is { } u)
    {
        return Name(u) + "?";
    }

    string baseName = t.IsNested ? Name(t.DeclaringType!) + "." + Strip(t.Name) : (t.Namespace is { Length: > 0 } ns ? ns + "." : "") + Strip(t.Name);
    if (t.IsGenericType)
    {
        Type[] args = t.GetGenericArguments();
        int own = t.IsNested ? args.Length - (t.DeclaringType!.IsGenericType ? t.DeclaringType.GetGenericArguments().Length : 0) : args.Length;
        if (own > 0)
        {
            baseName += "<" + string.Join(",", args.Skip(args.Length - own).Select(Name)) + ">";
        }
    }

    return baseName;
}

static string Strip(string n)
{
    int tick = n.IndexOf('`', StringComparison.Ordinal);
    return tick < 0 ? n : n[..tick];
}
