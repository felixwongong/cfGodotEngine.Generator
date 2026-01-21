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

                var sb = new StringBuilder();
                GenerateClassHeader(sb);
                
                // Solution 1: Lazy PropertyMap allocation
                sb.AppendLine("     private global::cfEngine.DataStructure.PropertyMap _bindingMap;");
                sb.AppendLine("     private bool _hasBindings;");
                sb.AppendLine();
                sb.AppendLine("     public global::cfEngine.DataStructure.IPropertyMap GetBindings");
                sb.AppendLine("     {");
                sb.AppendLine("         get");
                sb.AppendLine("         {");
                sb.AppendLine("             if (_bindingMap == null && _hasBindings)");
                sb.AppendLine("             {");
                sb.AppendLine("                 _bindingMap = new global::cfEngine.DataStructure.PropertyMap();");
                sb.AppendLine("             }");
                sb.AppendLine("             return _bindingMap;");
                sb.AppendLine("         }");
                sb.AppendLine("     }");
                sb.AppendLine();
                sb.AppendLine("     public void __EnableBindings() => _hasBindings = true;");

                foreach (var (field, attribute) in group)
                {
                    var fieldName = field.Name;
                    
                    // Solution 2: Enforce naming convention - field must start with underscore
                    if (!fieldName.StartsWith("_"))
                    {
                        var descriptor = new DiagnosticDescriptor(
                            id: "BIND001",
                            title: "PropertyBinding field must start with underscore",
                            messageFormat: "Field '{0}' with [PropertyBinding] attribute must use '_camelCase' naming (e.g., '_{1}')",
                            category: "Binding",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true,
                            description: "Fields marked with [PropertyBinding] must follow C# naming conventions with underscore prefix to avoid conflicts with generated properties."
                        );
                        
                        var diagnostic = Diagnostic.Create(
                            descriptor,
                            field.Locations.FirstOrDefault(),
                            fieldName,
                            fieldName.Length > 0 ? char.ToLower(fieldName[0]) + fieldName.Substring(1) : fieldName
                        );
                        
                        spc.ReportDiagnostic(diagnostic);
                        continue; // Skip generation for this field
                    }
                    
                    var typeName = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var propName = ToPascal(fieldName);

                    sb.AppendLine($"     public {typeName} {propName}");
                    sb.AppendLine("     {");
                    sb.AppendLine($"         get => {fieldName};");
                    sb.AppendLine("         set");
                    sb.AppendLine("         {");
                    sb.AppendLine($"             if (global::System.Collections.Generic.EqualityComparer<{typeName}>.Default.Equals({fieldName}, value)) return;");
                    sb.AppendLine($"             {fieldName} = value;");
                    sb.AppendLine();
                    sb.AppendLine("             // Only use PropertyMap if bindings are enabled");
                    sb.AppendLine("             if (_hasBindings && _bindingMap != null)");
                    sb.AppendLine("             {");
                    sb.AppendLine($"                 _bindingMap.Set(BindingKey.{propName}, value);");
                    sb.AppendLine("             }");
                    sb.AppendLine("         }");
                    sb.AppendLine("     }");
                    sb.AppendLine();
                }

                sb.AppendLine("}");
                
                spc.AddSource($"{className}.Binding.generated.cs", sb.ToString());

                sb.Clear();
                GenerateClassHeader(sb);
                sb.Append(
                    @"    public static class BindingKey
    {
"
                    );
                foreach (var (field, attribute) in group)
                {
                    var propName = ToPascal(field.Name);
                    sb.AppendLine($"         public const string {propName} = nameof({propName});");
                }

                sb.AppendLine();
                sb.AppendLine("         internal static System.Collections.Generic.List<string> keys = new();");
                sb.AppendLine();
                sb.AppendLine("         [System.Runtime.CompilerServices.ModuleInitializer]");
                sb.AppendLine("         public static void Init()");
                sb.AppendLine("         {");
                foreach (var (field, _) in group)
                {
                    var propName = ToPascal(field.Name);
                    sb.AppendLine($"            keys.Add({propName});");
                }
                sb.AppendLine("         }");
                
                
                sb.AppendLine("    }");
                
                sb.AppendLine("     public static System.Collections.Generic.IReadOnlyList<string> GetBindingKeys() => BindingKey.keys;");
                
                sb.AppendLine("}");
                
                spc.AddSource($"{className}.BindingKey.generated.cs", sb.ToString());

                void GenerateClassHeader(StringBuilder sb)
                {
                    sb.AppendLine("// <auto-generated />");
                    sb.Append("namespace ").Append(ns).Append(";\n\n");
                    sb.Append(typeMods).Append(' ').Append(className).Append(" : global::cfGodotEngine.Binding.IBindingSource").AppendLine().AppendLine("{");
                }
            }
        });
    }

    private static string GetTypeModifiers(INamedTypeSymbol t) =>
        t.TypeKind == TypeKind.Struct ? "public partial struct" : "public partial class";

    private static string ToPascal(string name)
    {
        var core = name.StartsWith("_") ? name.Substring(1) : name;
        return core.Length == 0 ? core : char.ToUpper(core[0]) + core.Substring(1);
    }
}
