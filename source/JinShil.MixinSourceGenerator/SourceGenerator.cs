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
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace JinShil.MixinSourceGenerator
{
    [Generator(LanguageNames.CSharp)]
    public class SourceGenerator : IIncrementalGenerator
    {
        const string MixinAttributeName = "MixinAttribute";
        const string MixinIgnoreAttributeName = "MixinIgnoreAttribute";

        static bool IsMixinAttribute(AttributeSyntax attribute)
        {
            string name = attribute.Name.ToString();
            string attributeName = MixinAttributeName;
            var attributeNameWithoutSuffix = attributeName.Substring(0, attributeName.Length - "Attribute".Length);
            return name == attributeName || name == attributeNameWithoutSuffix;
        }

        static bool IsMixinIgnoreAttribute(AttributeSyntax attribute)
        {
            string name = attribute.Name.ToString();
            string attributeName = MixinIgnoreAttributeName;
            var attributeNameWithoutSuffix = attributeName.Substring(0, attributeName.Length - "Attribute".Length);
            return name == attributeName || name == attributeNameWithoutSuffix;
        }

        static void GenerateMixinAttribute(IncrementalGeneratorInitializationContext context)
        {
            var attributeSource = @$"
namespace JinShil.MixinSourceGenerator
{{
    /// <summary>
    /// Specifies the type whose members are to be mixed in to a partial class or struct.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, AllowMultiple = true)]
    public class {MixinAttributeName} : System.Attribute
    {{
        public System.Type Type {{ get; }}
        public {MixinAttributeName}(System.Type type)
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
            var attributeSource = @$"
namespace JinShil.MixinSourceGenerator
{{
    /// <summary>
    /// Used to identify a member that should be ignored when mixing in members from other types.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Property | System.AttributeTargets.Field | System.AttributeTargets.Event, AllowMultiple = false)]
    public class {MixinIgnoreAttributeName} : System.Attribute
    {{
        public {MixinIgnoreAttributeName}()
        {{ }}
    }}
}}";
            context.RegisterPostInitializationOutput(ctx =>
                ctx.AddSource($"{MixinIgnoreAttributeName}.g.cs", SourceText.From(attributeSource, Encoding.UTF8)));
        }

        static bool IsTargetType(TypeDeclarationSyntax type)
        {
            // Check if the type is a partial class or struct with Mixin attributes
            return type.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                   type.AttributeLists.Any(al => al.Attributes.Any(a => IsMixinAttribute(a)));
        }

        static IEnumerable<TypeDeclarationSyntax> GetTargetTypes(IEnumerable<TypeDeclarationSyntax> types)
        {
            // Find all partial classes and structs with Mixin attributes
            return types.Where(t => IsTargetType(t));
        }

        static IEnumerable<TypeDeclarationSyntax> GetImplementationTypes(TypeDeclarationSyntax targetType, IEnumerable<TypeDeclarationSyntax> types)
        {
            // Collect all the types that are referenced in Mixin attributes
            foreach (var attributeList in targetType.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    if (IsMixinAttribute(attribute))
                    {
                        if (attribute.ArgumentList?.Arguments.Count > 0)
                        {
                            var typeArg = attribute.ArgumentList.Arguments[0].Expression;
                            if (typeArg is TypeOfExpressionSyntax typeOf)
                            {
                                var typeName = typeOf.Type.ToString();
                                var implType = types.FirstOrDefault(t => t.Identifier.Text == typeName);
                                if (implType != null)
                                {
                                    // If the implementation type is also a partial class or struct, recursively get its implementations
                                    if (IsTargetType(implType))
                                    {
                                        foreach (var i in GetImplementationTypes(implType, types))
                                        {
                                            yield return i;
                                        }
                                    }

                                    yield return implType;
                                }
                            }
                        }
                    }
                }
            }
        }

        static (IEnumerable<MemberDeclarationSyntax> Members, IEnumerable<UsingDirectiveSyntax> Usings) GetMembersAndUsings(IEnumerable<TypeDeclarationSyntax> implementationTypes)
        {
            var members = new List<MemberDeclarationSyntax>();
            var usings = new HashSet<UsingDirectiveSyntax>(SyntaxNodeComparer.Instance);

            foreach (var implType in implementationTypes)
            {
                // Don't add constructors or destructors
                var typeMembers = implType.Members
                    .Where(m => m is not ConstructorDeclarationSyntax &&
                                m is not DestructorDeclarationSyntax &&
                                !m.AttributeLists.Any(al => al.Attributes.Any(a => IsMixinIgnoreAttribute(a))));

                members.AddRange(typeMembers);

                // Collect using directives from the compilation unit containing the implementation type
                var compilationUnit = implType.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault();
                if (compilationUnit != null)
                {
                    foreach (var usingDirective in compilationUnit.Usings)
                    {
                        usings.Add(usingDirective);
                    }
                }
            }

            return (members, usings);
        }

        static string GetNamespace(TypeDeclarationSyntax type)
        {
            // Get the namespace of the type
            var parent = type.Parent;
            while (parent != null)
            {
                if (parent is NamespaceDeclarationSyntax namespaceDecl)
                {
                    return namespaceDecl.Name.ToString();
                }
                else if (parent is FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDecl)
                {
                    return fileScopedNamespaceDecl.Name.ToString();
                }
                parent = parent.Parent;
            }
            return string.Empty;
        }

        private class SyntaxNodeComparer : IEqualityComparer<UsingDirectiveSyntax>
        {
            public static readonly SyntaxNodeComparer Instance = new SyntaxNodeComparer();

            public bool Equals(UsingDirectiveSyntax x, UsingDirectiveSyntax y)
            {
                if (x == null || y == null) return false;
                return x.ToString() == y.ToString();
            }

            public int GetHashCode(UsingDirectiveSyntax obj)
            {
                return obj.ToString().GetHashCode();
            }
        }

        static void GenerateSource(SourceProductionContext spc, ImmutableArray<TypeDeclarationSyntax> types)
        {
            // Find all partial classes and structs with Implementation attributes
            var targetTypes = GetTargetTypes(types).ToArray();

            // Generate source for each target type
            foreach (var targetType in targetTypes)
            {
                // Get the implementation types referenced in the target type's Mixin attributes
                var implementationTypes = GetImplementationTypes(targetType, types).ToArray();

                // Get the members and using directives from the implementation types
                var (membersToAdd, usingsToAdd) = GetMembersAndUsings(implementationTypes);

                // Generate the new partial type (class or struct)
                TypeDeclarationSyntax newType = targetType switch
                {
                    ClassDeclarationSyntax => SyntaxFactory
                        .ClassDeclaration(targetType.Identifier.Text)
                        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword))
                        .AddMembers(membersToAdd.ToArray()),
                    StructDeclarationSyntax => SyntaxFactory
                        .StructDeclaration(targetType.Identifier.Text)
                        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword))
                        .AddMembers(membersToAdd.ToArray()),
                    _ => throw new InvalidOperationException("Unsupported type declaration")
                };

                // Get the namespace of the target type
                var namespaceName = GetNamespace(targetType);

                // Create the compilation unit
                var compilationUnit = SyntaxFactory.CompilationUnit();

                // Add using directives
                compilationUnit = compilationUnit.AddUsings(usingsToAdd.ToArray());

                // Add the type to the appropriate namespace
                if (namespaceName != null && namespaceName.Trim() != string.Empty)
                {
                    var namespaceDeclaration = SyntaxFactory
                        .NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
                        .AddMembers(newType);
                    compilationUnit = compilationUnit.AddMembers(namespaceDeclaration);
                }
                else
                {
                    compilationUnit = compilationUnit.AddMembers(newType);
                }

                // Add nullable enable directive
                compilationUnit = compilationUnit.WithLeadingTrivia(SyntaxFactory.TriviaList(
                        SyntaxFactory.Trivia(
                            SyntaxFactory.NullableDirectiveTrivia(
                                SyntaxFactory.Token(SyntaxKind.EnableKeyword),
                                true))));

                // Format the output
                var sourceText = compilationUnit
                    .NormalizeWhitespace()
                    .ToFullString();

                // Add the generated source with type-specific file name
                spc.AddSource($"{targetType.Identifier.Text}.g.cs", SourceText.From(sourceText, Encoding.UTF8));
            }
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
#if DEBUG
            //if (!Debugger.IsAttached)
            //{
            //    Debugger.Launch();
            //}
#endif

            // Emit attributes
            GenerateMixinAttribute(context);
            GenerateMixinIgnoreAttribute(context);

            // Collect all class and struct declarations
            var typeDeclarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (node, _) => node is TypeDeclarationSyntax,
                    transform: (ctx, _) => (TypeDeclarationSyntax)ctx.Node)
                .Collect();

            // Process types with Implementation attributes and generate output
            context.RegisterSourceOutput(typeDeclarations, GenerateSource);
        }
    }
}
