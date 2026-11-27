using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace JinShil.MixinSourceGenerator
{
    // Reconstructs the imports needed to compile copied members in a new file or namespace.
    sealed class Imports
    {
        // Qualify relocated names and preserve nullable annotations. Full framework type names also
        // work in using aliases written for earlier C# versions.
        static readonly SymbolDisplayFormat QualifiedNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)
            .RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        // Keep directives in emission order and index them by normalized text for deduplication.
        readonly HashSet<string> keys = new();
        readonly List<(string Key, SyntaxNode Directive, HashSet<string> Names)> directives = new();

        // Member references determine whether overlapping import names can affect binding.
        readonly HashSet<string> referencedNames = new();
        bool usesImplicitMemberLookup;

        public IEnumerable<ExternAliasDirectiveSyntax> ExternAliases => directives.Select(import => import.Directive).OfType<ExternAliasDirectiveSyntax>();
        public IEnumerable<UsingDirectiveSyntax> Usings => directives.Select(import => import.Directive).OfType<UsingDirectiveSyntax>();

        public static Imports From(TypeDeclarationSyntax implementation, MemberDeclarationSyntax[] members, INamespaceSymbol targetNamespace,
            SemanticModel model, CancellationToken cancellationToken)
        {
            var imports = new Imports();
            // Walk from the nearest scope outward so the first declaration of an alias wins.
            var aliases = new HashSet<string>();
            foreach (var ancestor in implementation.Ancestors())
            {
                // Both block and file-scoped namespaces can supply imports, as can the file itself.
                var (externs, usings) = ancestor switch
                {
                    BaseNamespaceDeclarationSyntax ns => (ns.Externs, ns.Usings),
                    CompilationUnitSyntax root => (root.Externs, root.Usings),
                    _ => default
                };

                foreach (var directive in externs)
                {
                    if (aliases.Add(directive.Identifier.ValueText))
                        imports.Add(directive.WithoutTrivia(), new() { directive.Identifier.ValueText });
                }

                foreach (var directive in usings)
                {
                    // The compilation already applies global usings to generated source.
                    if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                        continue;

                    var alias = directive.Alias?.Name.Identifier.ValueText;
                    if (alias is not null && !aliases.Add(alias))
                        continue;

                    // Resolve relative names where they were written. Preserve extern alias qualifiers
                    // because they select an assembly reference that global qualification cannot express.
                    var name = directive.NamespaceOrType;
                    var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                    var usesExternAlias = name.DescendantNodesAndSelf().OfType<AliasQualifiedNameSyntax>()
                        .Any(qualified => qualified.Alias.Identifier.Text != "global");
                    if (symbol is not null && !usesExternAlias)
                        name = SyntaxFactory.ParseTypeName(symbol.ToDisplayString(QualifiedNameFormat));

                    // An alias contributes one name. Other imports can expose many names.
                    imports.Add(directive.WithNamespaceOrType(name).WithoutTrivia(), alias is null ? GetImportedNames(symbol) : new() { alias });
                }
            }

            // The target already has its own namespace chain in scope. Import the implementation's
            // enclosing namespaces up to the point where the chains meet.
            var targetNamespaces = new HashSet<INamespaceSymbol>(SymbolEqualityComparer.Default);
            for (var ns = targetNamespace; !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
                targetNamespaces.Add(ns);

            var relocatedNamespaces = new HashSet<INamespaceSymbol>(SymbolEqualityComparer.Default);
            var sourceNamespace = model.GetDeclaredSymbol(implementation, cancellationToken)?.ContainingNamespace;
            for (var ns = sourceNamespace; ns is not null && !ns.IsGlobalNamespace && !targetNamespaces.Contains(ns); ns = ns.ContainingNamespace)
            {
                var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns.ToDisplayString(QualifiedNameFormat)));
                imports.Add(directive, GetImportedNames(ns));
                relocatedNamespaces.Add(ns);
            }

            // Include names in XML documentation because cref references also depend on imports.
            imports.referencedNames.UnionWith(members.SelectMany(member => member.DescendantTokens(descendIntoTrivia: true))
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText));
            // These expressions can bind imported members whose names are absent from the source text.
            imports.usesImplicitMemberLookup = members.SelectMany(member => member.DescendantNodes()).Any(node =>
                node is AwaitExpressionSyntax or CommonForEachStatementSyntax or QueryExpressionSyntax or PositionalPatternClauseSyntax
                    or TupleExpressionSyntax or CollectionExpressionSyntax or InitializerExpressionSyntax);
            imports.AddAliasesForMovedNames(members, relocatedNamespaces, aliases, model, cancellationToken);
            return imports;
        }

        void AddAliasesForMovedNames(MemberDeclarationSyntax[] members, HashSet<INamespaceSymbol> relocatedNamespaces,
            HashSet<string> aliases, SemanticModel model, CancellationToken cancellationToken)
        {
            // Moving directives into one file scope can make names from different source scopes compete.
            var importedNames = new HashSet<string>();
            var ambiguousNames = new HashSet<string>();
            foreach (var (_, _, names) in directives)
            {
                foreach (var name in names)
                {
                    if (!importedNames.Add(name))
                        ambiguousNames.Add(name);
                }
            }

            if (relocatedNamespaces.Count == 0 && ambiguousNames.Count == 0)
                return;

            // Look at unqualified identifiers and the first component of qualified names, where
            // surrounding imports take part in binding.
            var identifiers = members.SelectMany(member => member.DescendantNodes(descendIntoTrivia: true)).OfType<IdentifierNameSyntax>();
            foreach (var identifier in identifiers)
            {
                var name = identifier.Identifier.ValueText;
                var startsQualifiedName = identifier.Parent switch
                {
                    QualifiedNameSyntax qualified => qualified.Left == identifier,
                    MemberAccessExpressionSyntax access => access.Expression == identifier,
                    QualifiedCrefSyntax cref => cref.Container == identifier,
                    _ => false
                };
                var isUnqualified = startsQualifiedName || identifier.Parent is not (QualifiedNameSyntax or MemberAccessExpressionSyntax or AliasQualifiedNameSyntax);
                if (!isUnqualified || aliases.Contains(name) || (!startsQualifiedName && !ambiguousNames.Contains(name)))
                    continue;

                // Add aliases for child namespaces and supported type names whose original bindings
                // may be lost after relocation. The alias preserves the spelling in the member itself.
                var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
                var needsAlias = symbol switch
                {
                    INamespaceSymbol ns => relocatedNamespaces.Contains(ns.ContainingNamespace),
                    INamedTypeSymbol type => ambiguousNames.Contains(name) && type.Name == name && type.Arity == 0 && type.ContainingType is null,
                    _ => false
                };
                // Explicit aliases already carry the source binding.
                if (!needsAlias || model.GetAliasInfo(identifier, cancellationToken) is not null)
                    continue;

                var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(symbol!.ToDisplayString(QualifiedNameFormat)))
                    .WithAlias(SyntaxFactory.NameEquals(identifier.WithoutTrivia()));
                Add(directive, new() { name });
                aliases.Add(name);
            }
        }

        static HashSet<string> GetImportedNames(ISymbol? symbol)
        {
            var names = new HashSet<string>();
            if (symbol is INamespaceSymbol ns)
            {
                // Namespace imports participate in lookup for both type names and extension methods.
                foreach (var type in ns.GetTypeMembers())
                {
                    names.Add(type.Name);
                    if (type.MightContainExtensionMethods)
                        names.UnionWith(type.GetMembers().OfType<IMethodSymbol>().Where(method => method.IsExtensionMethod).Select(method => method.Name));
                }
            }
            else if (symbol is INamedTypeSymbol type)
            {
                // A using static directive exposes static members and nested types by simple name.
                names.UnionWith(type.GetMembers().Where(member => member.IsStatic || member is INamedTypeSymbol).Select(member => member.Name));
            }

            return names;
        }

        void Add(SyntaxNode directive, HashSet<string> names)
        {
            // Strip source-file trivia, including preprocessor directives. Normalize spacing so
            // equivalent directives share a key.
            directive = directive.ReplaceTokens(directive.DescendantTokens(), (original, _) => original.WithoutTrivia()).NormalizeWhitespace();
            var key = directive.ToString();
            if (keys.Add(key))
                directives.Add((key, directive, names));
        }

        public bool ConflictsWith(Imports other)
        {
            // Check whether combining two scopes would change how their members bind.
            foreach (var (leftKey, left, leftNames) in directives)
            {
                foreach (var (rightKey, right, rightNames) in other.directives)
                {
                    // A pair that already coexists in both scopes introduces no new lookup candidates.
                    if (other.keys.Contains(leftKey) && keys.Contains(rightKey))
                        continue;

                    // Duplicate aliases are errors even when unused. Other overlaps matter when code
                    // refers to a shared name or relies on implicit member lookup.
                    var bothDeclareAliases = DeclaresAlias(left) && DeclaresAlias(right);
                    if (leftNames.Any(name => rightNames.Contains(name) &&
                        (bothDeclareAliases || usesImplicitMemberLookup || other.usesImplicitMemberLookup || References(name) || other.References(name))))
                        return true;
                }
            }

            return false;
        }

        static bool DeclaresAlias(SyntaxNode directive) => directive is ExternAliasDirectiveSyntax or UsingDirectiveSyntax { Alias: not null };

        bool References(string name)
        {
            // In attribute syntax, the compiler also tries names with the Attribute suffix.
            const string attributeSuffix = "Attribute";
            return referencedNames.Contains(name) || (name.EndsWith(attributeSuffix, StringComparison.Ordinal) &&
                referencedNames.Contains(name.Substring(0, name.Length - attributeSuffix.Length)));
        }

        public void AddRange(Imports other)
        {
            // Retain the first occurrence of each directive to keep the generated order deterministic.
            foreach (var import in other.directives)
            {
                if (keys.Add(import.Key))
                    directives.Add(import);
            }

            // Include both groups of members when checking conflicts with the next mixin.
            referencedNames.UnionWith(other.referencedNames);
            usesImplicitMemberLookup |= other.usesImplicitMemberLookup;
        }
    }
}
