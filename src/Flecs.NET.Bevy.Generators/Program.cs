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
			postContext.AddSource($"{NAMESPACE_NAME}.QueryIterators.g.cs", CodeFormatter.Format(GenerateQueryIterators()));
			postContext.AddSource($"{NAMESPACE_NAME}.Systems.g.cs", CodeFormatter.Format(GenerateSystems()));
		});
    }

    private string GenerateQueryIterators()
    {
        return $@"
                #pragma warning disable 1591
                #nullable enable

                {DEFAULT_USINGS}

                namespace {NAMESPACE_NAME}
                {{

                }}

                #pragma warning restore 1591
            ";
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
            var genericsArgsWhere = GenerateSequence(i + 1, "\n", j => $"where T{j} : struct");
            var queryBuilderCalls = GenerateSequence(i + 1, "\n", j => $"builder.With<T{j}>();");

            var queryAssignFields = GenerateSequence(i + 1, "\n", j => $"field{j} = iter.Field<T{j}>({j});");
            var queryDctorFields = GenerateSequence(i + 1, ", ", j => $"out Field<T{j}> field{j}");


            sb.AppendLine($@"
                public readonly struct Data<{genericsArgs}> : IData
                    {genericsArgsWhere}
                {{
                    public static void AppendTerm(ref QueryBuilder builder)
                    {{
                        {queryBuilderCalls}
                    }}


                    public unsafe struct QueryIterator
                    {{
                        private NET.Bindings.flecs.ecs_iter_t _ecsIt;
                        private readonly Query _query;

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        internal QueryIterator(Query query)
                        {{
                            _query = query;
                            _ecsIt = query.GetIter();
                        }}

                        [UnscopedRef]
                        public ref QueryIterator Current => ref this;


                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public void Deconstruct({queryDctorFields})
                        {{
                            fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
                            {{
                                var iter = new Iter(ptr);
                                {queryAssignFields}
                            }}
                        }}

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public void Deconstruct(out Field<ulong> entities, {queryDctorFields})
                        {{
                            fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
                            {{
                                var iter = new Iter(ptr);
                                entities = iter.Entities();
                                {queryAssignFields}
                            }}
                        }}

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        public bool MoveNext()
                        {{
                            fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
                                return _query.GetNext(ptr);
                        }}

                        public readonly QueryIterator GetEnumerator() => this;
                    }}
                }}
            ");
        }

        for (var i = 0; i < MAX_PARAMS; ++i)
        {
            var genericsArgs = GenerateSequence(i + 1, ", ", j => $"T{j}");
            var genericsArgsWhere = GenerateSequence(i + 1, "\n", j => $"where T{j} : IFilter");
            var appendTermsCalls = GenerateSequence(i + 1, "\n", j => $"T{j}.AppendTerm(ref builder);");

            sb.AppendLine($@"
                public readonly struct Filter<{genericsArgs}> : IFilter
                    {genericsArgsWhere}
                {{
                    public static void AppendTerm(ref QueryBuilder builder)
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
            var genericsArgsWhere = GenerateSequence(i + 1, "\n", j => $"where T{j} : class, ISystemParam<World>, IIntoSystemParam<World>");
            var objs = GenerateSequence(i + 1, "\n", j => $"T{j}? obj{j} = null;");
            var objsGen = GenerateSequence(i + 1, "\n", j => $"obj{j} ??= (T{j})T{j}.Generate(args);");
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