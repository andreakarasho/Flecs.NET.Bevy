using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


[Generator]
public sealed class MyGenerator : IIncrementalGenerator
{
    const string NAMESPACE_NAME = "Flecs.NET.Bevy";
    const string DEFAULT_USINGS = "using Flecs.NET.Core;\nusing System.Runtime.CompilerServices;\nusing System.Diagnostics.CodeAnalysis;";
    const int MAX_PARAMS = 16;


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput((IncrementalGeneratorPostInitializationContext postContext) =>
		{
			postContext.AddSource($"{NAMESPACE_NAME}.Systems.g.cs", CodeFormatter.Format(GenerateSystems()));
		});
    }

    private string GenerateSystems()
    {
        return $@"
                #pragma warning disable 1591
                #nullable enable

                {DEFAULT_USINGS}

                namespace {NAMESPACE_NAME}
                {{
                    {CreateSystems()}
                    {CreateDataAndFilterStructs()}
                }}

                #pragma warning restore 1591
            ";
    }

    private string CreateDataAndFilterStructs()
    {
        var sb = new StringBuilder();

        for (var i = 0; i < MAX_PARAMS; ++i)
        {
            var genericsArgs = GenerateSequence(i + 1, ", ", j => $"T{j}");
            var genericsArgsWhere = GenerateSequence(i + 1, "\n", j => $"where T{j} : struct, IComponent");
            var queryBuilderCalls = GenerateSequence(i + 1, "\n", j => $"if (!FilterBuilder<T{j}>.Build(ref builder)) builder.With<T{j}>();");
            var fieldSign = GenerateSequence(i + 1, ", ", j => $"out Field<T{j}> field{j}");
            var fieldAssignments = GenerateSequence(i + 1, "\n", j => $"field{j} = T{j}.GetField<T{j}>(iter, {j});");

            sb.AppendLine($@"
                public struct Data<{genericsArgs}> : IData<Data<{genericsArgs}>>, IQueryIterator<Data<{genericsArgs}>>
                    {genericsArgsWhere}
                {{
                    private QueryIterator _iterator;

                    internal Data(QueryIterator iterator) => _iterator = iterator;

                    public static void Build(ref QueryBuilder builder)
                    {{
                        {queryBuilderCalls}
                    }}

                    public static IQueryIterator<Data<{genericsArgs}>> CreateIterator(QueryIterator iterator)
                        => new Data<{genericsArgs}>(iterator);

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public void Deconstruct({fieldSign})
                    {{
                        var iter = _iterator.Current;
                        {fieldAssignments}
                    }}

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public unsafe void Deconstruct(out Field<ulong> entities, {fieldSign})
                    {{
                        var iter = _iterator.Current;
                        entities = iter.Entities();
                        {fieldAssignments}
                    }}

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	                public bool MoveNext() => _iterator.MoveNext();

                    readonly Data<{genericsArgs}> IQueryIterator<Data<{genericsArgs}>>.Current => this;

                    readonly IQueryIterator<Data<{genericsArgs}>> IQueryIterator<Data<{genericsArgs}>>.GetEnumerator() => this;
                }}
            ");
        }

        for (var i = 0; i < MAX_PARAMS; ++i)
        {
            var genericsArgs = GenerateSequence(i + 1, ", ", j => $"T{j}");
            var genericsArgsWhere = GenerateSequence(i + 1, "\n", j => $"where T{j} : struct, IFilter");
            var appendTermsCalls = GenerateSequence(i + 1, "\n", j => $"if (!FilterBuilder<T{j}>.Build(ref builder)) T{j}.Build(ref builder);");

            sb.AppendLine($@"
                public readonly struct Filter<{genericsArgs}> : IFilter
                    {genericsArgsWhere}
                {{
                    public static void Build(ref QueryBuilder builder)
                    {{
                        {appendTermsCalls}
                    }}
                }}
            ");
        }

        return sb.ToString();
    }

    private string CreateSystems()
    {
        var sb = new StringBuilder();
        sb.AppendLine("public partial class Scheduler");
        sb.AppendLine("{");

        for (var i = 0; i < MAX_PARAMS; ++i)
        {
            var genericsArgs = GenerateSequence(i + 1, ", ", j => $"T{j}");
            var genericsArgsWhere = GenerateSequence(i + 1, "\n", j => $"where T{j} : class, ISystemParam<World>, IIntoSystemParam<World, T{j}>");
            var objs = GenerateSequence(i + 1, "\n", j => $"T{j}? obj{j} = null;");
            var objsGen = GenerateSequence(i + 1, "\n", j => $"obj{j} ??= T{j}.Generate(args);");
            var objsLock = GenerateSequence(i + 1, "\n", j => $"obj{j}.Lock();");
            var objsUnlock = GenerateSequence(i + 1, "\n", j => $"obj{j}.Unlock();");
            var systemCall = GenerateSequence(i + 1, ", ", j => $"obj{j}");
            var objsCheckInuse = GenerateSequence(i + 1, " ", j => $"obj{j}?.UseIndex != 0" + (j < i ? "||" : ""));

            sb.AppendLine($@"
            public FuncSystem<World> AddSystem<{genericsArgs}>(Action<{genericsArgs}> system, Stages stage = Stages.Update, ThreadingMode threadingType = ThreadingMode.Auto)
                {genericsArgsWhere}
            {{
                {objs}
                var checkInuse = () => {objsCheckInuse};
                var fn = (World args, Func<World, bool> runIf) =>
                {{
                    if (runIf != null && !runIf.Invoke(args))
                        return;

                    {objsGen}
                    {objsLock}
                    system({systemCall});
                    {objsUnlock}
                }};
                var sys = new FuncSystem<World>(_world, fn, checkInuse, threadingType);
                Add(sys, stage);
                return sys;
            }}
            ");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }


    static string GenerateSequence(int count, string separator, Func<int, string> generator)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; ++i)
        {
            sb.Append(generator(i));

            if (i < count - 1)
            {
                sb.Append(separator);
            }
        }

        return sb.ToString();
    }


    internal sealed class CodeFormatter : CSharpSyntaxRewriter
    {
        public static string Format(string source)
        {
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
            SyntaxNode normalized = syntaxTree.GetRoot().NormalizeWhitespace();

            normalized = new CodeFormatter().Visit(normalized);

            return normalized.ToFullString();
        }

        private static T FormatMembers<T>(T node, IEnumerable<SyntaxNode> members) where T : SyntaxNode
        {
            SyntaxNode[] membersArray = members as SyntaxNode[] ?? members.ToArray();

            int memberCount = membersArray.Length;
            int current = 0;

            return node.ReplaceNodes(membersArray, RewriteTrivia);

            SyntaxNode RewriteTrivia<TNode>(TNode oldMember, TNode _) where TNode : SyntaxNode
            {
                string trailingTrivia = oldMember.GetTrailingTrivia().ToFullString().TrimEnd() + "\n\n";
                return current++ != memberCount - 1
                    ? oldMember.WithTrailingTrivia(SyntaxFactory.Whitespace(trailingTrivia))
                    : oldMember;
            }
        }

        public override SyntaxNode VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            return base.VisitNamespaceDeclaration(FormatMembers(node, node.Members))!;
        }

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            return base.VisitClassDeclaration(FormatMembers(node, node.Members))!;
        }

        public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
        {
            return base.VisitStructDeclaration(FormatMembers(node, node.Members))!;
        }
    }
}