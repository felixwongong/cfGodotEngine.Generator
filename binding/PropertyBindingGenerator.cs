using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            static (node, _) => node is VariableDeclaratorSyntax || node is PropertyDeclarationSyntax,
            static (ctx, _) =>
            {
                var symbol = ctx.TargetSymbol;
                var attribute = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttrMeta);
                return (symbol, attribute);
            });

        var grouped = matched.Collect();

        context.RegisterSourceOutput(grouped, (spc, members) =>
        {
            if (members.IsDefaultOrEmpty) return;

            foreach (var group in members.GroupBy(m => m.symbol.ContainingType, SymbolEqualityComparer.Default))
            {
                var type = group.Key as INamedTypeSymbol;
                if (type == null) continue;
                
                var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
                var className = type.Name;
                var typeMods = GetTypeModifiers(type);

                // Group members by type for efficient switch generation
                var membersByType = new Dictionary<string, List<(ISymbol symbol, AttributeData attribute, string propName)>>();
                var validMembers = new List<(ISymbol symbol, AttributeData attribute, string propName)>();
                
                // Track property dependencies: field -> list of dependent properties
                var propertyDependencies = new Dictionary<string, List<string>>();

                foreach (var (symbol, attribute) in group)
                {
                    var memberName = symbol.Name;
                    var memberType = symbol switch
                    {
                        IFieldSymbol field => field.Type,
                        IPropertySymbol prop => prop.Type,
                        _ => null
                    };
                    
                    if (memberType == null) continue;
                    
                    // For fields, enforce naming convention - field must start with underscore
                    if (symbol is IFieldSymbol && !memberName.StartsWith("_"))
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
                            symbol.Locations.FirstOrDefault(),
                            memberName,
                            memberName.Length > 0 ? char.ToLower(memberName[0]) + memberName.Substring(1) : memberName
                        );
                        
                        spc.ReportDiagnostic(diagnostic);
                        continue;
                    }

                    // For properties starting with underscore, convert to PascalCase; otherwise use as-is
                    // For fields, always convert to PascalCase
                    var propName = symbol switch
                    {
                        IPropertySymbol when memberName.StartsWith("_") => ToPascal(memberName),
                        IPropertySymbol => memberName,
                        _ => ToPascal(memberName)
                    };
                    var typeKey = GetTypeKey(memberType);
                    
                    if (!membersByType.ContainsKey(typeKey))
                        membersByType[typeKey] = new List<(ISymbol, AttributeData, string)>();
                    
                    // Analyze property dependencies
                    if (symbol is IPropertySymbol property)
                    {
                        var declaringSyntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                        if (declaringSyntax is PropertyDeclarationSyntax propertySyntax)
                        {
                            var identifiers = propertySyntax.DescendantNodes()
                                .OfType<IdentifierNameSyntax>()
                                .Select(id => id.Identifier.ValueText)
                                .Where(name => name.StartsWith("_"))
                                .Distinct()
                                .ToList();
                            
                            foreach (var fieldName in identifiers)
                            {
                                if (!propertyDependencies.ContainsKey(fieldName))
                                    propertyDependencies[fieldName] = new List<string>();
                                
                                propertyDependencies[fieldName].Add(propName);
                            }
                        }
                    }
                    
                    membersByType[typeKey].Add((symbol, attribute, propName));
                    validMembers.Add((symbol, attribute, propName));
                }

                if (validMembers.Count == 0) continue;

                var sb = new StringBuilder();
                
                // Generate Properties class
                GeneratePropertiesClass(sb, ns, className, type, validMembers, membersByType, propertyDependencies);
                spc.AddSource($"{className}.Properties.generated.cs", sb.ToString());

                // Generate main class partial
                sb.Clear();
                GenerateMainClassPartial(sb, ns, className, typeMods, validMembers);
                spc.AddSource($"{className}.Binding.generated.cs", sb.ToString());

                // Generate BindingKey class
                sb.Clear();
                GenerateBindingKeyClass(sb, ns, className, typeMods, validMembers);
                spc.AddSource($"{className}.BindingKey.generated.cs", sb.ToString());
            }
        });
    }

    private static void GeneratePropertiesClass(
        StringBuilder sb,
        string ns,
        string className,
        INamedTypeSymbol type,
        List<(ISymbol symbol, AttributeData attribute, string propName)> validMembers,
        Dictionary<string, List<(ISymbol symbol, AttributeData attribute, string propName)>> membersByType,
        Dictionary<string, List<string>> propertyDependencies)
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
        
        // Add subscription field if there are dependencies
        if (propertyDependencies.Count > 0)
        {
            sb.AppendLine("        private global::cfEngine.Rx.Subscription _dependencySubscription;");
        }
        
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
        sb.AppendLine($"        public {className}_Properties({className} owner)");
        sb.AppendLine("        {");
        sb.AppendLine("            _owner = owner;");
        
        // Register dependency dispatcher if there are any dependencies
        if (propertyDependencies.Count > 0)
        {
            sb.AppendLine("            _dependencySubscription = propertyChangedRelay.AddListener(_DispatchDependentProperties);");
        }
        
        sb.AppendLine("        }");
        sb.AppendLine();
        
        // Generate dependency dispatcher method
        if (propertyDependencies.Count > 0)
        {
            GenerateDependencyDispatcher(sb, propertyDependencies, validMembers);
        }

        // Generate type-specific getter methods
        GenerateTypeSpecificGetters(sb, membersByType, type);

        // Generate generic Get<T> method
        GenerateGenericGet(sb, membersByType);

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

    private static void GenerateDependencyDispatcher(
        StringBuilder sb,
        Dictionary<string, List<string>> propertyDependencies,
        List<(ISymbol symbol, AttributeData attribute, string propName)> validMembers)
    {
        sb.AppendLine("        private void _DispatchDependentProperties(string propertyName)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (propertyName)");
        sb.AppendLine("            {");
        
        // Build a map of field propName -> field symbol name for the switch
        var fieldPropNameToSymbolName = validMembers
            .Where(m => m.symbol is IFieldSymbol)
            .ToDictionary(m => m.propName, m => m.symbol.Name);
        
        foreach (var kvp in propertyDependencies)
        {
            var fieldSymbolName = kvp.Key;
            var dependentProperties = kvp.Value;
            
            // Find the propName for this field
            var fieldPropName = fieldPropNameToSymbolName
                .FirstOrDefault(x => x.Value == fieldSymbolName).Key;
            
            if (fieldPropName == null) continue;
            
            sb.AppendLine($"                case \"{fieldPropName}\":");
            foreach (var dependentProp in dependentProperties)
            {
                sb.AppendLine($"                    _propertyChangedRelay.Dispatch(\"{dependentProp}\");");
            }
            sb.AppendLine("                    break;");
        }
        
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void GenerateTypeSpecificGetters(
        StringBuilder sb,
        Dictionary<string, List<(ISymbol symbol, AttributeData attribute, string propName)>> membersByType,
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

            if (membersByType.TryGetValue(typeKey, out var members))
            {
                foreach (var (symbol, _, propName) in members)
                {
                    sb.AppendLine($"                case \"{propName}\": ");
                    sb.AppendLine($"                    value = _owner.{symbol.Name};");
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

        if (membersByType.TryGetValue("object", out var objectMembers))
        {
            foreach (var (symbol, _, propName) in objectMembers)
            {
                var memberType = symbol switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null
                };
                
                if (memberType == null) continue;
                
                var typeStr = memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sb.AppendLine($"                case \"{propName}\":");
                sb.AppendLine($"                    if (typeof(T) == typeof({typeStr}))");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        value = (T)(object)_owner.{symbol.Name};");
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

    private static void GenerateGenericGet(StringBuilder sb, Dictionary<string, List<(ISymbol symbol, AttributeData attribute, string propName)>> membersByType)
    {
        sb.AppendLine("        public bool Get<T>(string key, out T value)");
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
        List<(ISymbol symbol, AttributeData attribute, string propName)> validMembers)
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

        // Generate setter methods (only for fields, not properties)
        foreach (var (symbol, attribute, propName) in validMembers)
        {
            // Skip properties - they manage their own setters
            if (symbol is not IFieldSymbol field)
                continue;
                
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
        List<(ISymbol symbol, AttributeData attribute, string propName)> validMembers)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"partial class {className}");
        sb.AppendLine("{");
        sb.AppendLine("    public static class BindingKey");
        sb.AppendLine("    {");

        foreach (var (symbol, _, propName) in validMembers)
        {
            sb.AppendLine($"        public const string {propName} = nameof({propName});");
        }

        sb.AppendLine();
        sb.AppendLine("        internal static global::System.Collections.Generic.List<string> keys = new();");
        sb.AppendLine();
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        public static void Init()");
        sb.AppendLine("        {");
        
        foreach (var (_, _, propName) in validMembers)
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
