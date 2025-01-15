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
            var generics = GenerateSequence(i + 1, ", ", j => $"T{j}");
				var whereGenerics = GenerateSequence(i + 1, " ", j => $"where T{j} : struct");
				var ptrList = GenerateSequence(i + 1, "\n", j => $"private Field<T{j}> _current{j};");
				var sizeDeclarations = GenerateSequence(i + 1, "\n", j => $"private int _size{j};");
				var ptrSet = GenerateSequence(i + 1, "\n", j => $"_current{j} = _iterator.DataRefWithSize<T{j}>({j}, out _size{j});");
				// var ptrAdvance = GenerateSequence(i + 1, "\n", j => $"_current{j}.Ref = ref Unsafe.Add(ref _current{j}.Ref, _size{j});");
				var fieldSign = GenerateSequence(i + 1, ", ", j => $"out Ptr<T{j}> ptr{j}");
				var fieldAssignments = GenerateSequence(i + 1, "\n", j => $"Unsafe.SkipInit<Ptr<T{j}>>(out ptr{j}); ptr{j}.Ref = ref _current{j}[_index];");
				var queryBuilderCalls = GenerateSequence(i + 1, "\n", j => $"if (!FilterBuilder<T{j}>.Build(ref builder)) builder.With<T{j}>();");

				sb.AppendLine($@"
					[SkipLocalsInit]
					public unsafe ref struct Data<{generics}> : IData<Data<{generics}>>, IQueryIterator<Data<{generics}>>
						{whereGenerics}
					{{
						private QueryIterator _iterator;
                        private int _index, _count;
						private Field<ulong> _entities;

						{ptrList}
						{sizeDeclarations}

						[MethodImpl(MethodImplOptions.AggressiveInlining)]
						internal Data(QueryIterator queryIterator)
						{{
							_iterator = queryIterator;
                            _count = -1;
						}}

						public static void Build(ref QueryBuilder builder)
						{{
							{queryBuilderCalls}
						}}

						[MethodImpl(MethodImplOptions.AggressiveInlining)]
						public static Data<{generics}> CreateIterator(QueryIterator iterator)
							=> new Data<{generics}>(iterator);

						[System.Diagnostics.CodeAnalysis.UnscopedRef]
						public ref Data<{generics}> Current
						{{
							[MethodImpl(MethodImplOptions.AggressiveInlining)]
							get => ref this;
						}}

						[MethodImpl(MethodImplOptions.AggressiveInlining)]
						public readonly void Deconstruct({fieldSign})
						{{
							{fieldAssignments}
						}}

						[MethodImpl(MethodImplOptions.AggressiveInlining)]
						public readonly void Deconstruct(out PtrRO<ulong> entity, {fieldSign})
						{{
							entity = new (ref _entities[_index]);
							{fieldAssignments}
						}}

						[MethodImpl(MethodImplOptions.AggressiveInlining)]
						public bool MoveNext()
						{{
							if (_index >= _count)
							{{
								if (!_iterator.MoveNext())
									return false;

								{ptrSet}

                                _entities = _iterator.EntitiesDangerous();
                                _count = _entities.Length;
                                _index = 0;
							}}
							else
							{{
								_index += 1;
							}}

							return true;
						}}

						[MethodImpl(MethodImplOptions.AggressiveInlining)]
						public readonly Data<{generics}> GetEnumerator() => this;
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