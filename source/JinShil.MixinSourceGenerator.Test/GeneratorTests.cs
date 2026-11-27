using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JinShil.MixinSourceGenerator.Test;

// Run the generator against in-memory compilations to inspect generated symbols and compiler diagnostics.
public class GeneratorTests
{
    [Fact]
    public void RecognizesQualifiedAndAliasedAttributes()
    {
        // Exercise both spellings of each attribute. The targets supply the property used by Value.
        var compilation = Generate("""
            using Compose = JinShil.MixinSourceGenerator.MixinAttribute;
            using Skip = JinShil.MixinSourceGenerator.MixinIgnoreAttribute;

            class Implementation
            {
                [Skip]
                public void FirstStub() { }

                [global::JinShil.MixinSourceGenerator.MixinIgnore]
                public int SecondStub { get; }

                public int Value => SecondStub;
            }

            [Compose(typeof(Implementation))]
            partial class AliasedTarget
            {
                public int SecondStub => 42;
            }

            [global::JinShil.MixinSourceGenerator.Mixin(typeof(Implementation))]
            partial class QualifiedTarget
            {
                public int SecondStub => 42;
            }
            """);

        // A successful compile also proves the copied code can use the target's property without a duplicate declaration.
        AssertCompiles(compilation);
        foreach (var name in new[] { "AliasedTarget", "QualifiedTarget" })
        {
            var target = compilation.GetTypeByMetadataName(name)!;
            Assert.Single(target.GetMembers("Value"));
            Assert.Empty(target.GetMembers("FirstStub"));
        }
    }

    [Fact]
    public void LeavesUnrelatedAttributesAlone()
    {
        // Give unrelated attributes the same simple names to verify that symbol identity controls generation.
        var compilation = Generate("""
            namespace Other
            {
                class MixinAttribute : System.Attribute
                {
                    public MixinAttribute(System.Type type) { }
                }

                class MixinIgnoreAttribute : System.Attribute { }

                class Implementation
                {
                    [MixinIgnore]
                    public int Value => 42;
                }

                [Mixin(typeof(Implementation))]
                partial class UnrelatedTarget { }

                [global::JinShil.MixinSourceGenerator.Mixin(typeof(Implementation))]
                partial class Target { }
            }
            """);

        AssertCompiles(compilation);
        Assert.Empty(compilation.GetTypeByMetadataName("Other.UnrelatedTarget")!.GetMembers("Value"));
        var property = Assert.Single(compilation.GetTypeByMetadataName("Other.Target")!.GetMembers("Value"));
        Assert.Equal("Other.MixinIgnoreAttribute", Assert.Single(property.GetAttributes()).AttributeClass!.ToDisplayString());
    }

    [Fact]
    public void PreservesAttributeArgumentsAndTargetsOnCopiedMembers()
    {
        var compilation = Generate("""
            using JinShil.MixinSourceGenerator;

            [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
            class MarkerAttribute(string label) : System.Attribute
            {
                public string Label { get; } = label;
                public int Order { get; set; }
            }

            class Source
            {
                [Marker("method", Order = 1)]
                [Marker("second marker", Order = 2)]
                [return: Marker("result", Order = 3)]
                public int Echo([Marker("input", Order = 4)] int value) => value;

                [Marker("property", Order = 5)]
                [field: Marker("backing field", Order = 6)]
                public int Count
                {
                    [Marker("getter", Order = 7)]
                    get;
                    set;
                }
            }

            [Mixin(typeof(Source))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
        var marker = compilation.GetTypeByMetadataName("MarkerAttribute")!;
        var target = compilation.GetTypeByMetadataName("Target")!;
        var method = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(target.GetMembers("Echo")));
        var property = Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(target.GetMembers("Count")));

        // Inspect bound attributes to verify their destinations and arguments after generation.
        Assert.Collection(method.GetAttributes(),
            attribute => CheckMarker(attribute, "method", 1),
            attribute => CheckMarker(attribute, "second marker", 2));
        CheckMarker(Assert.Single(method.GetReturnTypeAttributes()), "result", 3);
        CheckMarker(Assert.Single(method.Parameters[0].GetAttributes()), "input", 4);
        CheckMarker(Assert.Single(property.GetAttributes()), "property", 5);
        CheckMarker(Assert.Single(property.GetMethod!.GetAttributes()), "getter", 7);

        var backingField = Assert.Single(target.GetMembers().OfType<IFieldSymbol>(), field =>
            SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property));
        var backingFieldMarker = Assert.Single(backingField.GetAttributes(), attribute =>
            SymbolEqualityComparer.Default.Equals(marker, attribute.AttributeClass));
        CheckMarker(backingFieldMarker, "backing field", 6);

        void CheckMarker(AttributeData attribute, string label, int order)
        {
            Assert.True(SymbolEqualityComparer.Default.Equals(marker, attribute.AttributeClass));
            Assert.Equal(label, Assert.IsType<string>(Assert.Single(attribute.ConstructorArguments).Value));
            var namedArgument = Assert.Single(attribute.NamedArguments);
            Assert.Equal("Order", namedArgument.Key);
            Assert.Equal(order, Assert.IsType<int>(namedArgument.Value.Value));
        }
    }

    [Fact]
    public void ResolvesTheReferencedTypeAndCopiesAllItsParts()
    {
        // Put a same-named decoy first and split the real implementation across two files.
        var compilation = Generate("""
            namespace First
            {
                class Implementation
                {
                    public int Wrong => 0;
                }
            }
            namespace Second
            {
                partial class Implementation
                {
                    public int One => 1;
                }
            }
            """, """
            using JinShil.MixinSourceGenerator;
            using Source = Second.Implementation;

            namespace Second
            {
                partial class Implementation
                {
                    public int Two => 2;
                }

                [Mixin(typeof(Implementation))]
                partial class UnqualifiedTarget { }
                [Mixin(typeof(Second.Implementation))]
                partial class QualifiedTarget { }
                [Mixin(typeof(Source))]
                partial class AliasedTarget { }
            }
            """);

        AssertCompiles(compilation);
        // All three reference forms should resolve to Second.Implementation and copy both parts.
        foreach (var name in new[] { "UnqualifiedTarget", "QualifiedTarget", "AliasedTarget" })
        {
            var target = compilation.GetTypeByMetadataName($"Second.{name}")!;
            Assert.Single(target.GetMembers("One"));
            Assert.Single(target.GetMembers("Two"));
            Assert.Empty(target.GetMembers("Wrong"));
        }
    }

    [Fact]
    public void CopiesSharedDependenciesOnceAcrossPartialTargetDeclarations()
    {
        // Reach Shared directly and through two branches, and repeat Left across partial declarations.
        var compilation = Generate("""
            using JinShil.MixinSourceGenerator;

            class Shared
            {
                public int Value => 42;
            }

            [Mixin(typeof(Shared))]
            partial class Left
            {
                public int LeftValue => Value;
            }

            [Mixin(typeof(Shared))]
            partial class Right
            {
                public int RightValue => Value;
            }

            [Mixin(typeof(Left))]
            partial class Target { }
            """, """
            using JinShil.MixinSourceGenerator;

            [Mixin(typeof(Right))]
            [Mixin(typeof(Shared))]
            [Mixin(typeof(Left))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Target")!;
        Assert.Single(target.GetMembers("Value"));
        Assert.Single(target.GetMembers("LeftValue"));
        Assert.Single(target.GetMembers("RightValue"));
    }

    [Fact]
    public void ReportsCyclesAndContinuesGeneratingOtherTargets()
    {
        // Include a self-reference, a two-type cycle, and a valid target to exercise error recovery.
        var (compilation, diagnostics) = RunGenerator("""
            using JinShil.MixinSourceGenerator;

            [Mixin(typeof(Self))]
            partial class Self { }

            [Mixin(typeof(Second))]
            partial class First { }

            [Mixin(typeof(First))]
            partial class Second { }

            class Source { public int Value => 42; }

            [Mixin(typeof(Source))]
            partial class Target { }
            """);

        // Each offending attribute should get one source diagnostic. The valid target should still get its member.
        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("MIXIN001", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.True(diagnostic.Location.IsInSource);
        });
        Assert.Single(compilation.GetTypeByMetadataName("Target")!.GetMembers("Value"));
    }

    // Metadata types, array types, and null each lack a source declaration to copy.
    [Theory]
    [InlineData("typeof(string)")]
    [InlineData("typeof(int[])")]
    [InlineData("null")]
    public void ReportsImplementationsWithoutCopyableSource(string implementation)
    {
        var (_, diagnostics) = RunGenerator($$"""
            using JinShil.MixinSourceGenerator;

            [Mixin({{implementation}})]
            partial class Target { }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MIXIN002", diagnostic.Id);
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Fact]
    public void DistinguishesTargetsByNamespaceNameAndArity()
    {
        // Exercise filename collisions with the attribute file and with targets that share a name or differ only by case.
        var compilation = Generate("""
            using Compose = JinShil.MixinSourceGenerator.MixinAttribute;

            class Source { public int Value => 42; }

            [Compose(typeof(Source))]
            partial class MixinAttribute { }

            namespace First
            {
                [Compose(typeof(Source))]
                partial class Target { }

                [Compose(typeof(Source))]
                partial class Target<T> where T : class { }

                [Compose(typeof(Source))]
                partial class target { }
            }
            namespace Second
            {
                [Compose(typeof(Source))]
                partial struct Target { }
            }
            """);

        AssertCompiles(compilation);
        foreach (var name in new[] { "MixinAttribute", "First.Target", "First.Target`1", "First.target", "Second.Target" })
            Assert.Single(compilation.GetTypeByMetadataName(name)!.GetMembers("Value"));
    }

    [Fact]
    public void RecreatesNestedTargetsAndContainingDeclarations()
    {
        // Combine block and file-scoped namespaces with generic containers and records to exercise how the type is reopened.
        var compilation = Generate("""
            class Source { public int Value => 42; }
            """, """
            using JinShil.MixinSourceGenerator;

            namespace First
            {
                namespace Second
                {
                    partial record Container<T>(T Item) where T : notnull
                    {
                        public partial interface INested<TItem>
                        {
                            [Mixin(typeof(Source))]
                            public readonly partial record struct Target<U>(U Item);
                        }

                        [Mixin(typeof(Source))]
                        public partial record Target;
                    }
                }
            }
            """, """
            using JinShil.MixinSourceGenerator;

            namespace First.Second;

            [Mixin(typeof(Source))]
            partial class Target { }
            """);

        // Look up the full metadata paths to confirm each member landed in the intended target.
        AssertCompiles(compilation);
        Assert.Single(compilation.GetTypeByMetadataName("First.Second.Target")!.GetMembers("Value"));
        Assert.Single(compilation.GetTypeByMetadataName("First.Second.Container`1+INested`1+Target`1")!.GetMembers("Value"));
        Assert.Single(compilation.GetTypeByMetadataName("First.Second.Container`1+Target")!.GetMembers("Value"));
    }

    [Fact]
    public void ReportsTargetsThatCannotBeExtendedFromAnotherFile()
    {
        // Exercise three ways a declaration can prevent the generated file from reopening the target.
        var (_, diagnostics) = RunGenerator("""
            using JinShil.MixinSourceGenerator;

            class Source { public int Value => 42; }

            [Mixin(typeof(Source))]
            class NonPartial { }

            [Mixin(typeof(Source))]
            file partial class FileLocal { }

            class Outer
            {
                [Mixin(typeof(Source))]
                partial class Nested { }
            }
            """);

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("MIXIN003", diagnostic.Id));
    }

    [Fact]
    public void ResolvesNamespaceScopedAndRelativeImports()
    {
        // Moving the members to Application requires the original aliases and the names inherited from Library.
        var compilation = Generate("""
            global using System.Collections.Generic;

            namespace Library
            {
                public class ParentHelper { }

                namespace Models
                {
                    public class Model { }
                    public static class Constants { public const int Value = 42; }
                }

                namespace Mixins
                {
                    using Models;
                    using Short = Models.Model;
                    using static Models.Constants;

                    public class LocalHelper { }

                    class Source
                    {
                        public Model Create() => new();
                        public List<Short> Items => new();
                        public ParentHelper Parent => new();
                        public LocalHelper Local => new();
                        public Models.Model QualifiedModel => new();
                        public int Constant => Value;
                    }
                }
            }
            """, """
            using JinShil.MixinSourceGenerator;

            namespace Application;

            [Mixin(typeof(Library.Mixins.Source))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Application.Target")!;
        Assert.Single(target.GetMembers("Constant"));
        // The global using is already visible in generated files. CS8933 would indicate a redundant import.
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS8933");
    }

    [Fact]
    public void KeepsConflictingImportsInTheirOwnScopes()
    {
        // Both source files use Value and Other, with each name bound to a different type in each file.
        var compilation = Generate("""
            using Value = First.Item;
            using First;

            namespace First
            {
                public class Item { }
                public class Other { }

                class One
                {
                    public Value FirstItem => new();
                    public Other FirstOther => new();
                }
            }
            """, """
            using Value = Second.Item;
            using Second;

            namespace Second
            {
                public class Item { }
                public class Other { }

                class Two
                {
                    public Value SecondItem => new();
                    public Other SecondOther => new();
                }
            }
            """, """
            using JinShil.MixinSourceGenerator;

            [Mixin(typeof(First.One))]
            [Mixin(typeof(Second.Two))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Target")!;
        // Check the property types as well as compilation success to catch accidental rebinding.
        foreach (var (name, expected) in new[]
        {
            ("FirstItem", "First.Item"), ("FirstOther", "First.Other"),
            ("SecondItem", "Second.Item"), ("SecondOther", "Second.Other")
        })
        {
            var property = Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(target.GetMembers(name)));
            Assert.Equal(expected, property.Type.ToDisplayString());
        }
    }

    [Fact]
    public void PreservesThePrecedenceOfNamespaceScopedImports()
    {
        // Item binds to the nearer import. Extra keeps the outer import necessary for the same implementation.
        var compilation = Generate("""
            using OuterImports;

            namespace OuterImports
            {
                class Item { }
                class Extra { }
            }

            namespace InnerImports
            {
                class Item { }
            }

            namespace Library
            {
                using InnerImports;

                class Source
                {
                    public Item Primary => new();
                    public Extra Secondary => new();
                }
            }
            """, """
            using JinShil.MixinSourceGenerator;

            [Mixin(typeof(Library.Source))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Target")!;
        var primary = Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(target.GetMembers("Primary")));
        Assert.Equal("InnerImports.Item", primary.Type.ToDisplayString());
    }

    [Fact]
    public void KeepsFieldsTogetherWhenImportsOverlapOnUnusedNames()
    {
        // Both imports expose Item. The copied fields use int, so the overlap is harmless.
        var compilation = Generate("""
            using Library;

            namespace Library { class Item { } }
            struct First { public int One; }
            """, """
            using OtherLibrary;

            namespace OtherLibrary { class Item { } }
            struct Second { public int Two; }
            """, """
            using JinShil.MixinSourceGenerator;

            [Mixin(typeof(First))]
            [Mixin(typeof(Second))]
            partial struct Target { }
            """);

        // Splitting the fields across partial struct declarations would trigger a field-order warning.
        AssertCompiles(compilation);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0282");
    }

    [Fact]
    public void CopiesActiveCodeWhenDirectivesSpanSkippedMembers()
    {
        // Omitting the stub removes the member that carries the opening directives. The inactive #error exposes any leaked branch.
        var compilation = Generate("""
            #define FEATURE
            using JinShil.MixinSourceGenerator;

            class Source
            {
                #region Members
                #if FEATURE
                [MixinIgnore]
                void Stub() { }

                /// <summary>A copied member.</summary>
                public int Active => 42;
                #else
                #error This branch must stay inactive.
                public int Inactive => 0;
                #endif

                public int After()
                {
                    #if FEATURE
                    return Active;
                    #else
                    return Inactive;
                    #endif
                }
                #endregion
            }

            [Mixin(typeof(Source))]
            partial class Target { }
            """);

        // Check both the active branch choice and the documentation attached to the copied member.
        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Target")!;
        Assert.Empty(target.GetMembers("Inactive"));
        Assert.Contains("A copied member.", Assert.Single(target.GetMembers("Active")).GetDocumentationCommentXml());
    }

    [Fact]
    public void PreservesOrdinaryCommentsAndXmlDocumentationOnCopiedMembers()
    {
        var compilation = Generate("""
            using JinShil.MixinSourceGenerator;

            class Source
            {
                // The caller stores the new value.
                /// <summary>Advances the counter.</summary>
                /// <param name="count">The current count.</param>
                /// <returns>The updated count.</returns>
                [System.Obsolete]
                public int Advance(int count)
                {
                    /* Count the current item. */
                    return count + 1; // The next update uses this count.
                }
            }

            [Mixin(typeof(Source))]
            partial struct Target { }
            """);

        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Target")!;
        var method = Assert.Single(target.GetMembers("Advance"));
        var declaration = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());

        // Ordinary comments live in syntax trivia. Check their text on the copied declaration and statement.
        var leadingComment = Assert.Single(declaration.GetLeadingTrivia(), trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia));
        Assert.Equal("// The caller stores the new value.", leadingComment.ToFullString());
        var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(declaration.Body!.Statements));
        var blockComment = Assert.Single(statement.GetLeadingTrivia(), trivia => trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));
        Assert.Equal("/* Count the current item. */", blockComment.ToFullString());
        var trailingComment = Assert.Single(statement.GetTrailingTrivia(), trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia));
        Assert.Equal("// The next update uses this count.", trailingComment.ToFullString());

        // Read the compiler's documentation for the target member to verify where the XML is attached.
        var documentation = XElement.Parse(method.GetDocumentationCommentXml()!);
        Assert.Equal("Advances the counter.", documentation.Element("summary")?.Value.Trim());
        var parameter = Assert.Single(documentation.Elements("param"));
        Assert.Equal("count", (string?)parameter.Attribute("name"));
        Assert.Equal("The current count.", parameter.Value.Trim());
        Assert.Equal("The updated count.", documentation.Element("returns")?.Value.Trim());
    }

    [Fact]
    public void PreservesNullableContextsAcrossCopiedMembers()
    {
        // Change nullable settings before an ignored member and restore them inside a copied method body.
        var compilation = Generate("""
            using JinShil.MixinSourceGenerator;

            #nullable disable
            class Source
            {
                public string Oblivious => null;

                #nullable enable annotations
                [MixinIgnore]
                void Stub() { }

                public string Required => "";
                public string? Optional => null;

                #nullable disable
                public string ObliviousAgain => null;

                public string Body()
                {
                    #nullable restore
                    string? value = null;
                    return value ?? "";
                }

                public string Restored => "";
            }

            [Mixin(typeof(Source))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id is "CS8669" or "CS8632");

        // Compare bound annotations because the same property syntax can mean different things under different settings.
        var source = compilation.GetTypeByMetadataName("Source")!;
        var target = compilation.GetTypeByMetadataName("Target")!;
        foreach (var original in source.GetMembers().OfType<IPropertySymbol>())
        {
            var copied = Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(target.GetMembers(original.Name)));
            Assert.Equal(original.NullableAnnotation, copied.NullableAnnotation);
        }
    }

    [Fact]
    public void PreservesUnsafeContextFromAnEnclosingType()
    {
        // Pointer obtains its unsafe context from Outer. Copying the method also requires that context on the target.
        var compilation = Generate("""
            using JinShil.MixinSourceGenerator;

            unsafe class Outer
            {
                internal class Source
                {
                    public int* Pointer() => null;
                }
            }

            [Mixin(typeof(Outer.Source))]
            partial class Target { }
            """);

        AssertCompiles(compilation);
    }

    [Fact]
    public void GeneratesCodeForPreNullableLanguageVersions()
    {
        // Compile the generated source as C# 7.3 to catch directives that require C# 8 or later.
        var (compilation, diagnostics) = RunGenerator(CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp7_3), """
            using JinShil.MixinSourceGenerator;

            class Source { public int Value => 42; }

            [Mixin(typeof(Source))]
            partial class Target { }
            """);

        Assert.Empty(diagnostics);
        AssertCompiles(compilation);
    }

    [Fact]
    public void CopiesAnOpenGenericImplementationIntoACompatibleTarget()
    {
        // The open mixin refers to T by name. The target declares the parameter the copied member will bind to.
        var compilation = Generate("""
            using JinShil.MixinSourceGenerator;

            class Source<T> { public T Echo(T value) => value; }

            [Mixin(typeof(Source<>))]
            partial class Target<T> { }
            """);

        AssertCompiles(compilation);
        var target = compilation.GetTypeByMetadataName("Target`1")!;
        var method = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(target.GetMembers("Echo")));
        Assert.True(SymbolEqualityComparer.Default.Equals(target.TypeParameters[0], method.ReturnType));
    }

    [Fact]
    public void ReportsClosedGenericImplementationsThatWouldRebindTypeParameters()
    {
        // Target<T> would accept the copied T even though the mixin explicitly supplies int. Require a diagnostic for that input.
        var (_, diagnostics) = RunGenerator("""
            using JinShil.MixinSourceGenerator;

            class Source<T> { public T Echo(T value) => value; }

            [Mixin(typeof(Source<int>))]
            partial class Target<T> { }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MIXIN004", diagnostic.Id);
        Assert.True(diagnostic.Location.IsInSource);
    }

    static Compilation Generate(params string[] sources)
    {
        // Success cases require a clean generator run before inspecting the generated compilation.
        var (compilation, diagnostics) = RunGenerator(sources);
        Assert.Empty(diagnostics);
        return compilation;
    }

    static (Compilation, ImmutableArray<Diagnostic>) RunGenerator(params string[] sources)
        => RunGenerator(CSharpParseOptions.Default, sources);

    static (Compilation, ImmutableArray<Diagnostic>) RunGenerator(CSharpParseOptions parseOptions, params string[] sources)
    {
        // Give each snippet its own syntax tree so file-scoped imports and partial declarations behave as they do in a project.
        var compilation = CSharpCompilation.Create("Mixins",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, parseOptions, $"Source{index}.cs")),
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: parseOptions.LanguageVersion >= LanguageVersion.CSharp8 ? NullableContextOptions.Enable : NullableContextOptions.Disable));

        // Parse generated and original code with the same options, then return the updated compilation alongside generator diagnostics.
        CSharpGeneratorDriver.Create(new[] { new SourceGenerator().AsSourceGenerator() }, parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (output, diagnostics);
    }

    static void AssertCompiles(Compilation compilation)
    {
        // Compilation errors can expose invalid generated code even when the generator reported no diagnostic.
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(errors);
    }
}
