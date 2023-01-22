using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Text;

namespace HDF5.NET.SourceGenerator;

// TODO: Make group itself accessible (for attributes)
// TODO: generate Docstring
// TODO: Docs (note also that attribute alias is not supported)
// TODO: Replace sample file by own or publicly available one
// TODO: Generated bindings need constructor and properties need to be instantiated (or use structs?) 
// and final property needs to actually return the requested H5Dataset.
// TODO: Add support for H5CommitedDatatype
// TODO: Add support for link?
// TODO: create Nuget package
// TODO: Nuget package: when referencing to other project, add version property, otherwise it references >=1.0.0

[Generator]
public class SourceGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        // while (!System.Diagnostics.Debugger.IsAttached)
        //     Thread.Sleep(1000);

        var attributeFullName = typeof(H5SourceGeneratorAttribute).FullName;

        foreach (var syntaxTree in context.Compilation.SyntaxTrees)
        {
            var model = context.Compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            // find attribute syntax
            var collector = new H5SourceGeneratorAttributeCollector();
            collector.Visit(root);

            foreach (var attributeSyntax in collector.Attributes)
            {
                try
                {
                    var classDeclarationSyntax = (ClassDeclarationSyntax)attributeSyntax.Parent!.Parent!;
                    var sourceFilePath = classDeclarationSyntax.SyntaxTree.FilePath;
                    var sourceFolderPath = Path.GetDirectoryName(sourceFilePath);

                    var isPartial = classDeclarationSyntax.Modifiers
                        .Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));

                    // TODO Create diagnostic here to notify user that the partial keyword is missing.
                    if (!isPartial)
                        continue;

                    var classSymbol = (INamedTypeSymbol)model.GetDeclaredSymbol(classDeclarationSyntax)!;
                    var accessibility = classSymbol.DeclaredAccessibility;

                    var isPublic = accessibility == Accessibility.Public;
                    var isInternal = accessibility == Accessibility.Internal;

                    // TODO Create diagnostic here to notify user about the accessibility problem.
                    var accessibilityString = isPublic 
                        ? "public" 
                        : isInternal 
                            ? "internal" 
                            : throw new Exception("Class accessibility must be public or internal.");

                    var className = classSymbol.Name;
                    var classNamespace = classSymbol.ContainingNamespace.ToDisplayString();
                    var attributes = classSymbol.GetAttributes();

                    var attribute = attributes
                        .Where(attribute => 
                            attribute.AttributeClass is not null && 
                            attribute.AttributeClass.ToDisplayString() == attributeFullName)
                        .FirstOrDefault();

                    if (attribute is null)
                        continue;

                    var h5FilePath = attribute.ConstructorArguments[0].Value!.ToString();

                    if (!Path.IsPathRooted(h5FilePath))
                        h5FilePath = Path.Combine(sourceFolderPath, h5FilePath);

                    using var h5File = H5File.OpenRead(h5FilePath);
                    var source = GenerateSource(className, classNamespace, accessibilityString, h5File);

                    context.AddSource($"{classSymbol.ToDisplayString()}.g.cs", source);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                }
            }
        }
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        // No initialization required for this one
    }

    public static string NormalizeName(string input)
    {
        return input.Replace(" ", "_");
    }

    private static string GenerateSource(string className, string classNamespace, string accessibilityString, H5File root)
    {
        var classDefinitions = new List<string>();

        ProcessGroup(className, root, accessibilityString, classDefinitions);

        var source =
        $$"""
        // <auto-generated/>
        using HDF5.NET;
        using {{classNamespace}}.Generated{{className}}Bindings;

        namespace {{classNamespace}}
        {
        {{classDefinitions.Last()}}
        }

        namespace {{classNamespace}}.Generated{{className}}Bindings
        {
        {{string.Join("\n\n", classDefinitions.Take(classDefinitions.Count - 1))}}
        }
        """;

        return source;
    }

    private static string ProcessGroup(
        string className, 
        H5Group group, 
        string accessibilityString,
        List<string> classDefinitions)
    {
        var constructorBuilder = new StringBuilder();
        var propertyBuilder = new StringBuilder();

        foreach (var link in group.Children)
        {
            var propertyName = NormalizeName(link.Name);

            var constructor = link switch
            {
                H5Group subGroup    => $"""            {propertyName} = new {GetHelperClassName(className, group, subGroup)}(parent.Group("{link.Name}"));""",
                _                   => $"""            {propertyName} = parent.Get<{link.GetType().Name}>("{link.Name}");"""
            };

            constructorBuilder.AppendLine(constructor);

            var property = link switch
            {
                H5Group subGroup    => ProcessGroup(
                                        className: GetHelperClassName(className, group, subGroup),
                                        subGroup,
                                        accessibilityString,
                                        classDefinitions),
                H5Dataset           => $$"""        public H5Dataset {{propertyName}} { get; }""",
                H5CommitedDatatype  => $$"""        public H5CommitedDatatype {{propertyName}} { get; }""",
                H5UnresolvedLink    => $$"""        public H5UnresolvedLink {{propertyName}} { get; }""",
                _                   => throw new Exception("Unknown link type")
            };

            propertyBuilder.AppendLine(property);
        }

        var partialString = group.Name == "/" ? "partial " : "";
        var constructorAccessibilityString = group.Name == "/" ? "public " : "internal";
        var parentGroupString = group.Name == "/" ? "H5File file" : "H5Group parent";

        var classSource =
        $$"""
            {{accessibilityString}} {{partialString}}class {{className}}
            {
                {{constructorAccessibilityString}} {{className}}({{parentGroupString}})
                {
        {{(group.Name == "/" ? "            var parent = file;\n" : "")}}{{constructorBuilder}}        }

        {{propertyBuilder}}    }
        """;

        classDefinitions.Add(classSource);

        return $$"""        public {{className}} {{NormalizeName(group.Name)}} { get; }""";
    }

    private static string GetHelperClassName(string className, H5Group group, H5Group subGroup)
    {
        return group.Name == "/" 
            ? subGroup.Name 
            : $"{className}_{subGroup.Name}";
    }
}