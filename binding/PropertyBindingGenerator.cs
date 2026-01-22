using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace cfGodotEngine.Binding;

[Generator(LanguageNames.CSharp)]
public class PropertyBindingGenerator : IIncrementalGenerator
{
    private const string AttrMeta = "cfGodotEngine.Binding.PropertyBindingAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var matched = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttrMeta,
            static (node, _) => node is VariableDeclaratorSyntax,
            static (ctx, _) =>
            {
                var field = (IFieldSymbol)ctx.TargetSymbol;
                var attribute = field.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttrMeta);
                return (field, attribute);
            });

        var grouped = matched.Collect();

        context.RegisterSourceOutput(grouped, (spc, fields) =>
        {
            if (fields.IsDefaultOrEmpty) return;

            foreach (var group in fields.GroupBy(f => f.field.ContainingType, SymbolEqualityComparer.Default))
            {
                var type = group.Key as INamedTypeSymbol;
                if (type == null) continue;
                
                var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
                var className = type.Name;
                var typeMods = GetTypeModifiers(type);

                // Group fields by type for efficient switch generation
                var fieldsByType = new Dictionary<string, List<(IFieldSymbol field, AttributeData attribute, string propName)>>();
                var validFields = new List<(IFieldSymbol field, AttributeData attribute, string propName)>();

                foreach (var (field, attribute) in group)
                {
                    var fieldName = field.Name;
                    
                    // Enforce naming convention - field must start with underscore
                    if (!fieldName.StartsWith("_"))
                    {
                        var descriptor = new DiagnosticDescriptor(
                            id: "BIND001",
                            title: "PropertyBinding field must start with underscore",
                            messageFormat: "Field '{0}' with [PropertyBinding] attribute must use '_camelCase' naming (e.g., '_{1}')",
                            category: "Binding",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true,
                            description: "Fields marked with [PropertyBinding] must follow C# naming conventions with underscore prefix to avoid conflicts with generated setter methods."
                        );
                        
                        var diagnostic = Diagnostic.Create(
                            descriptor,
                            field.Locations.FirstOrDefault(),
                            fieldName,
                            fieldName.Length > 0 ? char.ToLower(fieldName[0]) + fieldName.Substring(1) : fieldName
                        );
                        
                        spc.ReportDiagnostic(diagnostic);
                        continue;
                    }

                    var propName = ToPascal(fieldName);
                    var typeKey = GetTypeKey(field.Type);
                    
                    if (!fieldsByType.ContainsKey(typeKey))
                        fieldsByType[typeKey] = new List<(IFieldSymbol, AttributeData, string)>();
                    
                    fieldsByType[typeKey].Add((field, attribute, propName));
                    validFields.Add((field, attribute, propName));
                }

                if (validFields.Count == 0) continue;

                var sb = new StringBuilder();
                
                // Generate Properties class
                GeneratePropertiesClass(sb, ns, className, type, validFields, fieldsByType);
                spc.AddSource($"{className}.Properties.generated.cs", sb.ToString());

                // Generate main class partial
                sb.Clear();
                GenerateMainClassPartial(sb, ns, className, typeMods, validFields);
                spc.AddSource($"{className}.Binding.generated.cs", sb.ToString());

                // Generate BindingKey class
                sb.Clear();
                GenerateBindingKeyClass(sb, ns, className, typeMods, validFields);
                spc.AddSource($"{className}.BindingKey.generated.cs", sb.ToString());
            }
        });
    }

    private static void GeneratePropertiesClass(
        StringBuilder sb,
        string ns,
        string className,
        INamedTypeSymbol type,
        List<(IFieldSymbol field, AttributeData attribute, string propName)> validFields,
        Dictionary<string, List<(IFieldSymbol field, AttributeData attribute, string propName)>> fieldsByType)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"partial class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    partial class {className}_Properties : global::cfEngine.DataStructure.IPropertyMap");
        sb.AppendLine("    {");
        sb.AppendLine($"        private {className} _owner;");
        sb.AppendLine("        internal global::cfEngine.Rx.Relay<string> _propertyChangedRelay;");
        sb.AppendLine();
        sb.AppendLine("        public global::cfEngine.Rx.IRelay<string> propertyChangedRelay");
        sb.AppendLine("        {");
        sb.AppendLine("            get");
        sb.AppendLine("            {");
        sb.AppendLine("                if (_propertyChangedRelay == null)");
        sb.AppendLine("                    _propertyChangedRelay = new global::cfEngine.Rx.Relay<string>(_owner);");
        sb.AppendLine("                return _propertyChangedRelay;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        public {className}_Properties({className} owner) => _owner = owner;");
        sb.AppendLine();

        // Generate type-specific getter methods
        GenerateTypeSpecificGetters(sb, fieldsByType, type);

        // Generate generic Get<T> method
        GenerateGenericGet(sb, fieldsByType);

        // Generate RegisterPropertyChange and UnregisterPropertyChange
        sb.AppendLine("        public void RegisterPropertyChange(global::System.Action<string> callback)");
        sb.AppendLine("        {");
        sb.AppendLine("            propertyChangedRelay.AddListener(callback);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public void UnregisterPropertyChange(global::System.Action<string> callback)");
        sb.AppendLine("        {");
        sb.AppendLine("            propertyChangedRelay.RemoveListener(callback);");
        sb.AppendLine("        }");
        
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void GenerateTypeSpecificGetters(
        StringBuilder sb,
        Dictionary<string, List<(IFieldSymbol field, AttributeData attribute, string propName)>> fieldsByType,
        INamedTypeSymbol type)
    {
        var typeMap = new Dictionary<string, string>
        {
            { "int", "int" },
            { "string", "string" },
            { "bool", "bool" },
            { "float", "float" },
            { "double", "double" }
        };

        foreach (var kvp in typeMap)
        {
            var typeKey = kvp.Key;
            var typeName = kvp.Value;
            
            sb.AppendLine($"        private bool _Get{char.ToUpper(typeKey[0]) + typeKey.Substring(1)}(string key, out {typeName} value)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (key)");
            sb.AppendLine("            {");

            if (fieldsByType.TryGetValue(typeKey, out var fields))
            {
                foreach (var (field, _, propName) in fields)
                {
                    sb.AppendLine($"                case \"{propName}\":");
                    sb.AppendLine($"                    value = _owner.{field.Name};");
                    sb.AppendLine("                    return true;");
                }
            }

            sb.AppendLine("                default:");
            sb.AppendLine($"                    value = default({typeName});");
            sb.AppendLine("                    return false;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Generate _GetObject for other types
        sb.AppendLine("        private bool _GetObject<T>(string key, out T value)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (key)");
        sb.AppendLine("            {");

        if (fieldsByType.TryGetValue("object", out var objectFields))
        {
            foreach (var (field, _, propName) in objectFields)
            {
                var fieldType = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sb.AppendLine($"                case \"{propName}\":");
                sb.AppendLine($"                    if (typeof(T) == typeof({fieldType}))");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        value = (T)(object)_owner.{field.Name};");
                sb.AppendLine("                        return true;");
                sb.AppendLine("                    }");
                sb.AppendLine("                    break;");
            }
        }

        sb.AppendLine("                default:");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("            value = default(T);");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void GenerateGenericGet(StringBuilder sb, Dictionary<string, List<(IFieldSymbol field, AttributeData attribute, string propName)>> fieldsByType)
    {
        sb.AppendLine("        public bool Get<T>(string key, out T? value)");
        sb.AppendLine("        {");
        
        var typeChecks = new[]
        {
            ("int", "Int"),
            ("string", "String"),
            ("bool", "Bool"),
            ("float", "Float"),
            ("double", "Double")
        };

        bool first = true;
        foreach (var (type, methodSuffix) in typeChecks)
        {
            var keyword = first ? "if" : "else if";
            sb.AppendLine($"            {keyword} (typeof(T) == typeof({type}))");
            sb.AppendLine("            {");
            sb.AppendLine($"                if (_Get{methodSuffix}(key, out {type} typedValue))");
            sb.AppendLine("                {");
            sb.AppendLine("                    value = (T)(object)typedValue;");
            sb.AppendLine("                    return true;");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            first = false;
        }

        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                if (_GetObject<T>(key, out T objValue))");
        sb.AppendLine("                {");
        sb.AppendLine("                    value = objValue;");
        sb.AppendLine("                    return true;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            value = default;");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
    }

    private static void GenerateMainClassPartial(
        StringBuilder sb,
        string ns,
        string className,
        string typeMods,
        List<(IFieldSymbol field, AttributeData attribute, string propName)> validFields)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"{typeMods} {className} : global::cfGodotEngine.Binding.IBindingSource");
        sb.AppendLine("{");
        sb.AppendLine($"    private {className}_Properties _properties;");
        sb.AppendLine();
        sb.AppendLine("    public global::cfEngine.DataStructure.IPropertyMap GetBindings");
        sb.AppendLine("    {");
        sb.AppendLine("        get");
        sb.AppendLine("        {");
        sb.AppendLine("            if (_properties == null)");
        sb.AppendLine($"                _properties = new {className}_Properties(this);");
        sb.AppendLine("            return _properties;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Generate setter methods
        foreach (var (field, attribute, propName) in validFields)
        {
            var typeName = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var fieldName = field.Name;
            
            // Get accessibility from attribute
            var accessibility = "public";
            if (attribute != null)
            {
                var accessArg = attribute.NamedArguments.FirstOrDefault(a => a.Key == "accessibility");
                if (accessArg.Value.Value != null)
                {
                    var accessValue = (int)accessArg.Value.Value;
                    accessibility = accessValue switch
                    {
                        0 => "public",
                        1 => "protected",
                        2 => "private",
                        3 => "internal",
                        _ => "public"
                    };
                }
            }

            sb.AppendLine($"    {accessibility} void Set{propName}({typeName} value)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (global::System.Collections.Generic.EqualityComparer<{typeName}>.Default.Equals({fieldName}, value)) return;");
            sb.AppendLine($"        {fieldName} = value;");
            sb.AppendLine();
            sb.AppendLine("        // Only dispatch property name (receiver retrieves value via Get<T>)");
            sb.AppendLine("        if (_properties?._propertyChangedRelay != null)");
            sb.AppendLine("        {");
            sb.AppendLine($"            _properties._propertyChangedRelay.Dispatch(\"{propName}\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
    }

    private static void GenerateBindingKeyClass(
        StringBuilder sb,
        string ns,
        string className,
        string typeMods,
        List<(IFieldSymbol field, AttributeData attribute, string propName)> validFields)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"partial class {className}");
        sb.AppendLine("{");
        sb.AppendLine("    public static class BindingKey");
        sb.AppendLine("    {");

        foreach (var (field, _, propName) in validFields)
        {
            sb.AppendLine($"        public const string {propName} = nameof({propName});");
        }

        sb.AppendLine();
        sb.AppendLine("        internal static global::System.Collections.Generic.List<string> keys = new();");
        sb.AppendLine();
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        public static void Init()");
        sb.AppendLine("        {");
        
        foreach (var (_, _, propName) in validFields)
        {
            sb.AppendLine($"            keys.Add({propName});");
        }
        
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<string> GetBindingKeys() => BindingKey.keys;");
        sb.AppendLine("}");
    }

    private static string GetTypeKey(ITypeSymbol type)
    {
        var typeStr = type.ToDisplayString();
        return typeStr switch
        {
            "int" => "int",
            "string" => "string",
            "bool" => "bool",
            "float" => "float",
            "double" => "double",
            _ => "object"
        };
    }

    private static string GetTypeModifiers(INamedTypeSymbol t) =>
        t.TypeKind == TypeKind.Struct ? "public partial struct" : "public partial class";

    private static string ToPascal(string name)
    {
        var core = name.StartsWith("_") ? name.Substring(1) : name;
        return core.Length == 0 ? core : char.ToUpper(core[0]) + core.Substring(1);
    }
}
