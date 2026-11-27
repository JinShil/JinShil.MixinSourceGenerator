/*
 * JinShil.MixinSourceGenerator.
 *
 * Copyright (C) 2025 Michael V. Franklin
 *
 * This software is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This software is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this software.  If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace JinShil.MixinSourceGenerator
{
    /// <summary>
    /// Copies mixin members into partial C# types so the compiler binds the copied code in the target type.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public class SourceGenerator : IIncrementalGenerator
    {
        // Use the same names in emitted declarations and in Roslyn's attribute lookup.
        const string MixinAttributeName = "MixinAttribute";
        const string MixinIgnoreAttributeName = "MixinIgnoreAttribute";
        const string MixinAttributeMetadataName = "JinShil.MixinSourceGenerator." + MixinAttributeName;
        const string MixinIgnoreAttributeMetadataName = "JinShil.MixinSourceGenerator." + MixinIgnoreAttributeName;

        // Give each failure a stable diagnostic ID so build output identifies the input that needs attention.
        static readonly DiagnosticDescriptor CyclicMixin = new(
            "MIXIN001", "Cyclic mixin reference", "Applying mixin '{0}' to '{1}' creates a cycle",
            "MixinSourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        static readonly DiagnosticDescriptor UnavailableImplementation = new(
            "MIXIN002", "Unavailable mixin implementation", "Mixin implementation '{0}' must be a class, struct, or interface declared in this compilation",
            "MixinSourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        static readonly DiagnosticDescriptor InvalidTarget = new(
            "MIXIN003", "Invalid mixin target", "Mixin target '{0}' and its containing types must be partial and available outside their source file",
            "MixinSourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        static readonly DiagnosticDescriptor ConstructedImplementation = new(
            "MIXIN004", "Constructed generic mixin implementation", "Use the open generic form of '{0}' and declare any type parameters its members need on the target",
            "MixinSourceGenerator", DiagnosticSeverity.Error, isEnabledByDefault: true);

        static void GenerateMixinAttribute(IncrementalGeneratorInitializationContext context)
        {
            // Each consuming compilation gets an attribute that takes the implementation type from typeof.
            var attributeSource = @$"
namespace JinShil.MixinSourceGenerator
{{
    /// <summary>
    /// Specifies the type whose members are to be mixed in to a partial class or struct.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct, AllowMultiple = true)]
    public class {MixinAttributeName} : global::System.Attribute
    {{
        /// <summary>
        /// The type whose members are to be mixed in.
        /// </summary>
        public global::System.Type Type {{ get; }}

        /// <summary>
        /// Initializes a new instance of the <see cref=""{MixinAttributeName}""/> class.
        /// </summary>
        /// <param name=""type"">The type whose members are to be mixed in.</param>
        public {MixinAttributeName}(global::System.Type type)
        {{
            Type = type;
        }}
    }}
}}";
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource($"{MixinAttributeName}.g.cs", SourceText.From(attributeSource, Encoding.UTF8)));
        }

        static void GenerateMixinIgnoreAttribute(IncrementalGeneratorInitializationContext context)
        {
            // This member-level marker lets the target supply declarations that the remaining mixin code uses.
            var attributeSource = @$"
namespace JinShil.MixinSourceGenerator
{{
    /// <summary>
    /// Used to identify a member that should be ignored when mixing in members from other types.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method | global::System.AttributeTargets.Property | global::System.AttributeTargets.Field | global::System.AttributeTargets.Event, AllowMultiple = false)]
    public class {MixinIgnoreAttributeName} : global::System.Attribute
    {{
        /// <summary>
        /// Initializes a new instance of the <see cref=""{MixinIgnoreAttributeName}""/> class.
        /// </summary>
        public {MixinIgnoreAttributeName}()
        {{ }}
    }}
}}";
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource($"{MixinIgnoreAttributeName}.g.cs", SourceText.From(attributeSource, Encoding.UTF8)));
        }

        static IEnumerable<TypeDeclarationSyntax> GetImplementationTypes(
            INamedTypeSymbol target, INamedTypeSymbol mixinAttribute, SourceProductionContext context, HashSet<Location> reported)
        {
            // Seed both sets with the target. The active set detects cycles in the current path.
            // The visited set makes shared and repeated dependencies contribute their members once.
            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { target };
            var active = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { target };

            // Keep iterators on an explicit stack so a deep chain of mixins uses an iterative walk.
            var pending = new Stack<(INamedTypeSymbol Type, ImmutableArray<AttributeData>.Enumerator Attributes)>();
            pending.Push((target, target.GetAttributes().GetEnumerator()));

            // Emit each implementation after its dependencies, retaining attribute order.
            while (pending.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var (type, attributes) = pending.Pop();
                if (!attributes.MoveNext())
                {
                    // All dependencies are complete. Copy every partial declaration of an implementation.
                    // The target itself already has its own members in the original compilation.
                    active.Remove(type);
                    if (!SymbolEqualityComparer.Default.Equals(type, target))
                    {
                        foreach (var reference in type.DeclaringSyntaxReferences)
                        {
                            if (reference.GetSyntax(context.CancellationToken) is TypeDeclarationSyntax declaration)
                                yield return declaration;
                        }
                    }

                    continue;
                }

                // Save the iterator position so this type resumes with its next attribute after a dependency.
                pending.Push((type, attributes));

                // Compare bound attribute types so qualified and aliased forms identify the same marker.
                var attribute = attributes.Current;
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, mixinAttribute) || attribute.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                // Let the compiler report invalid C# arguments. A valid mixin argument must provide source
                // declarations in this compilation because the generator copies their member syntax.
                var argument = attribute.ConstructorArguments[0];
                if (argument.Kind == TypedConstantKind.Error || argument.Value is IErrorTypeSymbol)
                    continue;

                if (argument.Value is not INamedTypeSymbol implementation ||
                    implementation.DeclaringSyntaxReferences.IsEmpty ||
                    implementation.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
                {
                    ReportMixinDiagnostic(context, reported, attribute, UnavailableImplementation, argument.Value?.ToString() ?? "null");
                    continue;
                }

                // Applying concrete generic arguments would require substituting types throughout the copied members.
                if (implementation.IsGenericType && !implementation.IsUnboundGenericType)
                {
                    ReportMixinDiagnostic(context, reported, attribute, ConstructedImplementation, implementation.ToDisplayString());
                    continue;
                }

                // Give an open generic reference and its original declaration the same graph identity.
                implementation = implementation.OriginalDefinition;
                if (active.Contains(implementation))
                {
                    ReportMixinDiagnostic(context, reported, attribute, CyclicMixin, implementation.ToDisplayString(), type.ToDisplayString());
                    continue;
                }

                // Expand an unvisited dependency before resuming the saved iterator for the current type.
                if (visited.Add(implementation))
                {
                    active.Add(implementation);
                    pending.Push((implementation, implementation.GetAttributes().GetEnumerator()));
                }
            }
        }

        static void ReportMixinDiagnostic(SourceProductionContext context, HashSet<Location> reported, AttributeData attribute,
            DiagnosticDescriptor descriptor, params string[] messageArguments)
        {
            // Point to the attribute and report it once even when several targets reach the same invalid reference.
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Location.None;
            if (reported.Add(location))
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArguments));
        }

        static IEnumerable<(List<MemberDeclarationSyntax> Members, Imports Imports, bool RequiresUnsafe)> GetCompositionParts(
            INamedTypeSymbol target, IEnumerable<TypeDeclarationSyntax> implementations, Compilation compilation,
            INamedTypeSymbol ignoreAttribute, CancellationToken cancellationToken)
        {
            // Accumulate consecutive mixins in one partial declaration while their imports can share a file.
            var members = new List<MemberDeclarationSyntax>();
            var imports = new Imports();
            NullableContext? nullableContext = null;
            bool requiresUnsafe = false;

            foreach (var implementation in implementations)
            {
                var semanticModel = compilation.GetSemanticModel(implementation.SyntaxTree);
                // Constructor and destructor declarations belong to their original type. Resolve the ignore
                // marker semantically so aliases for it work alongside other member attributes.
                var typeMembers = implementation.Members
                    .Where(m => m is not ConstructorDeclarationSyntax &&
                                m is not DestructorDeclarationSyntax &&
                                !m.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                                    semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is IMethodSymbol constructor &&
                                    SymbolEqualityComparer.Default.Equals(constructor.ContainingType, ignoreAttribute)))
                    .ToArray();
                if (typeMembers.Length == 0)
                    continue;

                // Start a fresh file scope when merging imports would introduce a binding conflict.
                var implementationImports = Imports.From(implementation, typeMembers, target.ContainingNamespace, semanticModel, cancellationToken);
                if (imports.ConflictsWith(implementationImports))
                {
                    yield return (members, imports, requiresUnsafe);
                    members = new();
                    imports = new();
                    nullableContext = null;
                    requiresUnsafe = false;
                }

                // Copy member syntax with its comments, attributes, and active compiler context.
                var memberContext = new MemberContext(semanticModel);
                foreach (var member in typeMembers)
                    members.Add(memberContext.Copy(member, ref nullableContext));

                imports.AddRange(implementationImports);

                // An implementation can inherit an unsafe context from any containing type.
                requiresUnsafe |= implementation.AncestorsAndSelf().OfType<TypeDeclarationSyntax>()
                    .Any(type => type.Modifiers.Any(SyntaxKind.UnsafeKeyword));
            }

            // Emit the final group after the dependency walk finishes.
            if (members.Count > 0)
                yield return (members, imports, requiresUnsafe);
        }

        static TypeDeclarationSyntax CreatePartialDeclaration(TypeDeclarationSyntax template, IEnumerable<MemberDeclarationSyntax> members)
        {
            // The generic parameter names and variance must match for the declarations to merge.
            TypeParameterListSyntax? typeParameters = null;
            if (template.TypeParameterList is { } originalParameters)
            {
                typeParameters = SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(originalParameters.Parameters.Select(parameter =>
                    SyntaxFactory.TypeParameter(parameter.Identifier.WithoutTrivia()).WithVarianceKeyword(parameter.VarianceKeyword.WithoutTrivia()))));
            }

            // The original part supplies attributes, base types, constraints, and any primary constructor.
            // Build an explicit body for the copied members, including for records declared with a semicolon.
            var declaration = template
                .WithAttributeLists(default)
                .WithModifiers(SyntaxFactory.TokenList(template.Modifiers.Select(modifier => modifier.WithoutTrivia())))
                .WithKeyword(template.Keyword.WithoutTrivia())
                .WithIdentifier(template.Identifier.WithoutTrivia())
                .WithTypeParameterList(typeParameters)
                .WithParameterList(null)
                .WithBaseList(null)
                .WithConstraintClauses(default)
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                .WithMembers(SyntaxFactory.List(members))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
                .WithSemicolonToken(default);

            // Preserve the optional class or struct keyword so a generated record has the same kind.
            return declaration is RecordDeclarationSyntax record
                ? record.WithClassOrStructKeyword(record.ClassOrStructKeyword.WithoutTrivia())
                : declaration;
        }

        static string GetHintName(INamedTypeSymbol target, int part, HashSet<string> usedNames)
        {
            // Metadata names include generic arity. Add the containing types and namespaces to distinguish
            // targets with the same simple name, and spell the arity separator in a filename-friendly form.
            var typeNames = new Stack<string>();
            for (var type = target; type is not null; type = type.ContainingType)
                typeNames.Push(type.MetadataName.Replace('`', '-'));

            var namespaceNames = new Stack<string>();
            for (var ns = target.ContainingNamespace; !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
                namespaceNames.Push(ns.Name);

            // Reserve separate names for the attribute files and each part of a target.
            var prefix = "Mixin." + string.Join(".", namespaceNames.Concat(new[] { string.Join("+", typeNames) }));
            if (part > 1)
                prefix += $"-part{part}";
            var hintName = $"{prefix}.g.cs";
            // C# permits names that differ only by case. A suffix also keeps them distinct on disk.
            for (var suffix = 2; !usedNames.Add(hintName); suffix++)
                hintName = $"{prefix}.{suffix}.g.cs";

            return hintName;
        }

        static SourceText CreateCompositionSource(TypeDeclarationSyntax target, List<MemberDeclarationSyntax> members, Imports imports, bool requiresUnsafe)
        {
            // Apply the source type's unsafe context where copied declarations may depend on it.
            var partialTarget = CreatePartialDeclaration(target, members);
            if (requiresUnsafe && !partialTarget.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                partialTarget = partialTarget.WithModifiers(partialTarget.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.UnsafeKeyword)));

            // Rebuild the path to the file root so the compiler finds the original nested type.
            MemberDeclarationSyntax declaration = partialTarget;
            foreach (var ancestor in target.Ancestors())
            {
                declaration = ancestor switch
                {
                    TypeDeclarationSyntax containingType => CreatePartialDeclaration(containingType, new[] { declaration }),
                    BaseNamespaceDeclarationSyntax containingNamespace => SyntaxFactory.NamespaceDeclaration(containingNamespace.Name.WithoutTrivia()).AddMembers(declaration),
                    _ => declaration
                };
            }

            // Extern aliases come first because using directives can refer to them.
            var compilationUnit = SyntaxFactory.CompilationUnit()
                .AddExterns(imports.ExternAliases.ToArray())
                .AddUsings(imports.Usings.ToArray())
                .AddMembers(declaration);

            return SourceText.From(compilationUnit.NormalizeWhitespace().ToFullString(), Encoding.UTF8);
        }

        static void GenerateSource(SourceProductionContext spc, (Compilation Compilation, ImmutableArray<INamedTypeSymbol> Targets) input)
        {
            // Resolve the generated attributes in the compilation that supplied the target symbols.
            var (compilation, targets) = input;
            var mixinAttribute = compilation.GetTypeByMetadataName(MixinAttributeMetadataName);
            var ignoreAttribute = compilation.GetTypeByMetadataName(MixinIgnoreAttributeMetadataName);
            if (mixinAttribute is null || ignoreAttribute is null)
                return;

            // A partial type can have several attributed declarations. Emit its composition once per symbol.
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var reported = new HashSet<Location>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                if (!seen.Add(target))
                    continue;

                // The generated file must be able to reopen the target and each of its containing types.
                var targetType = (TypeDeclarationSyntax)target.DeclaringSyntaxReferences[0].GetSyntax(spc.CancellationToken);
                if (targetType.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(type =>
                    !type.Modifiers.Any(SyntaxKind.PartialKeyword) || type.Modifiers.Any(SyntaxKind.FileKeyword)))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(InvalidTarget, targetType.Identifier.GetLocation(), target.ToDisplayString()));
                    continue;
                }

                // Walk the dependencies in order and emit each part with the imports its members need.
                var implementationTypes = GetImplementationTypes(target, mixinAttribute, spc, reported);
                int part = 0;
                foreach (var (members, imports, requiresUnsafe) in GetCompositionParts(target, implementationTypes, compilation, ignoreAttribute, spc.CancellationToken))
                {
                    spc.AddSource(GetHintName(target, ++part, usedNames), CreateCompositionSource(targetType, members, imports, requiresUnsafe));
                }
            }
        }

        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Post-initialization output makes the attributes available when Roslyn binds the input source.
            GenerateMixinAttribute(context);
            GenerateMixinIgnoreAttribute(context);

            // Use Roslyn's attribute matching to find the annotated class, struct, and record symbols.
            var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
                MixinAttributeMetadataName,
                predicate: (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);

            // Generation needs the full compilation to resolve dependencies and read member context from other files.
            context.RegisterSourceOutput(context.CompilationProvider.Combine(targets.Collect()), GenerateSource);
        }
    }
}
