using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace JinShil.MixinSourceGenerator
{
    // Preserves active code and nullable settings while moving member syntax to a generated file.
    sealed class MemberContext : CSharpSyntaxRewriter
    {
        readonly SemanticModel model;
        readonly bool supportsNullable;

        public MemberContext(SemanticModel model)
        {
            this.model = model;
            // The consuming project's language version determines whether #nullable can be emitted.
            supportsNullable = ((CSharpParseOptions)model.SyntaxTree.Options).LanguageVersion >= LanguageVersion.CSharp8;
        }

        public MemberDeclarationSyntax Copy(MemberDeclarationSyntax member, ref NullableContext? previousContext)
        {
            // Ordinary members can be reused directly. Members with directives need a trivia pass.
            var copy = member.ContainsDirectives ? (MemberDeclarationSyntax)Visit(member)! : member;
            if (!supportsNullable)
                return copy;

            // Start before leading trivia so any existing directives apply in their original order.
            // Reapply settings when moving between files or jumping over an omitted declaration.
            var startContext = GetNullableContext(member.FullSpan.Start);
            if (startContext != previousContext)
                copy = copy.WithLeadingTrivia(NullableDirectives(startContext).Concat(copy.GetLeadingTrivia()));

            // Include trailing trivia when tracking the state carried into the next copied member.
            previousContext = GetNullableContext(member.FullSpan.End);
            return copy;
        }

        public override SyntaxTriviaList VisitList(SyntaxTriviaList list)
        {
            // Leave ordinary comments, documentation, and whitespace attached to the original tokens.
            if (!list.Any(trivia => trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
                return list;

            var result = new List<SyntaxTrivia>(list.Count);
            foreach (var trivia in list)
            {
                // Roslyn already parsed the active code. Remove conditional and region markers because
                // their pairs can span members omitted from the generated file.
                if (trivia.Kind() is SyntaxKind.DisabledTextTrivia or SyntaxKind.IfDirectiveTrivia or SyntaxKind.ElifDirectiveTrivia
                    or SyntaxKind.ElseDirectiveTrivia or SyntaxKind.EndIfDirectiveTrivia or SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia)
                    continue;

                if (trivia.IsDirective && trivia.GetStructure() is DirectiveTriviaSyntax directive)
                {
                    // Directives from inactive branches can still appear as trivia in the source tree.
                    if (!directive.IsActive)
                        continue;

                    // Resolve #nullable restore against the original file's settings and emit them
                    // explicitly because the generated file has its own defaults.
                    if (supportsNullable && directive is NullableDirectiveTriviaSyntax nullable && nullable.SettingToken.IsKind(SyntaxKind.RestoreKeyword))
                    {
                        result.AddRange(NullableDirectives(GetNullableContext(trivia.FullSpan.End), nullable.TargetToken.WithoutTrivia()));
                        continue;
                    }
                }

                result.Add(trivia);
            }

            return SyntaxFactory.TriviaList(result);
        }

        // Compare the effective settings. Roslyn also tracks whether each setting was inherited.
        NullableContext GetNullableContext(int position) => model.GetNullableContext(position) & NullableContext.Enabled;

        static IEnumerable<SyntaxTrivia> NullableDirectives(NullableContext context, SyntaxToken target = default)
        {
            // Annotations and warnings can be toggled independently. Preserve the requested scope
            // and use one directive for both settings when they agree.
            var annotationsEnabled = (context & NullableContext.AnnotationsEnabled) != 0;
            var warningsEnabled = (context & NullableContext.WarningsEnabled) != 0;
            if (target.IsKind(SyntaxKind.AnnotationsKeyword))
                yield return NullableDirective(annotationsEnabled, target);
            else if (target.IsKind(SyntaxKind.WarningsKeyword))
                yield return NullableDirective(warningsEnabled, target);
            else if (annotationsEnabled == warningsEnabled)
                yield return NullableDirective(annotationsEnabled, default);
            else
            {
                yield return NullableDirective(annotationsEnabled, SyntaxFactory.Token(SyntaxKind.AnnotationsKeyword));
                yield return NullableDirective(warningsEnabled, SyntaxFactory.Token(SyntaxKind.WarningsKeyword));
            }
        }

        static SyntaxTrivia NullableDirective(bool enabled, SyntaxToken target) => SyntaxFactory.Trivia(
            SyntaxFactory.NullableDirectiveTrivia(SyntaxFactory.Token(enabled ? SyntaxKind.EnableKeyword : SyntaxKind.DisableKeyword), target, true));
    }
}
