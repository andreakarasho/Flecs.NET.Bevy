
using System.Runtime.CompilerServices;
using Flecs.NET.Core;

namespace Flecs.NET.Bevy;

// https://promethia-27.github.io/dependency_injection_like_bevy_from_scratch/introductions.html

public sealed partial class FuncSystem<TArg> where TArg : notnull
{
	private readonly TArg _arg;
    private readonly Action<TArg, Func<TArg, bool>> _fn;
	private readonly List<Func<TArg, bool>> _conditions;
	private readonly Func<TArg, bool> _validator;
	private readonly Func<bool> _checkInUse;
	private readonly ThreadingMode _threadingType;
	private readonly LinkedList<FuncSystem<TArg>> _after = new ();
	private readonly LinkedList<FuncSystem<TArg>> _before = new ();
	internal LinkedListNode<FuncSystem<TArg>>? Node { get; set; }


    internal FuncSystem(TArg arg, Action<TArg, Func<TArg, bool>> fn, Func<bool> checkInUse, ThreadingMode threadingType)
    {
		_arg = arg;
        _fn = fn;
		_conditions = new ();
		_validator = ValidateConditions;
		_checkInUse = checkInUse;
		_threadingType = threadingType;
    }

	internal void Run()
	{
		foreach (var s in _before)
			s.Run();

		_fn(_arg, _validator);

		foreach (var s in _after)
			s.Run();
	}

	public FuncSystem<TArg> RunIf(Func<bool> condition)
	{
		_conditions.Add(_ => condition());
		return this;
	}

	public FuncSystem<TArg> RunAfter(FuncSystem<TArg> parent)
	{
		if (this == parent || Contains(parent, s => s._after))
			throw new InvalidOperationException("Circular dependency detected");

		Node?.List?.Remove(Node);
		Node = parent._after.AddLast(this);

		return this;
	}

	public FuncSystem<TArg> RunBefore(FuncSystem<TArg> parent)
	{
		if (this == parent || Contains(parent, s => s._before))
			throw new InvalidOperationException("Circular dependency detected");

		Node?.List?.Remove(Node);
		Node = parent._before.AddLast(this);

		return this;
	}

	private bool Contains(FuncSystem<TArg> system, Func<FuncSystem<TArg>, LinkedList<FuncSystem<TArg>>> direction)
	{
		var current = this;
		while (current != null)
		{
			if (current == system)
				return true;

			var nextNode = direction(current)?.First;
			current = nextNode?.Value;
		}
		return false;
	}

	internal bool IsResourceInUse()
	{
		return _threadingType switch
		{
			ThreadingMode.Multi => false,
			ThreadingMode.Single => true,
			_ or ThreadingMode.Auto => _checkInUse()
		};
	}

	private bool ValidateConditions(TArg args)
	{
		foreach (var fn in _conditions)
			if (!fn(args))
				return false;
		return true;
	}
}

public enum Stages
{
	Startup,
	FrameStart,
	BeforeUpdate,
	Update,
	AfterUpdate,
	FrameEnd
}

public enum ThreadingMode
{
	Auto,
	Single,
	Multi
}

public sealed partial class Scheduler
{
	private readonly World _world;
	private readonly LinkedList<FuncSystem<World>>[] _systems = new LinkedList<FuncSystem<World>>[(int)Stages.FrameEnd + 1];
	private readonly List<FuncSystem<World>> _singleThreads = new ();
	private readonly List<FuncSystem<World>> _multiThreads = new ();

	public Scheduler(World world)
	{
		_world = world;

		for (var i = 0; i < _systems.Length; ++i)
			_systems[i] = new ();

		 AddSystemParam(new FlecsWorld(world));
		 AddSystemParam(new SchedulerState(this));
	}


    public void Run()
    {
		RunStage(Stages.Startup);
		_systems[(int) Stages.Startup].Clear();

		for (var stage = Stages.FrameStart; stage <= Stages.FrameEnd; stage += 1)
        	RunStage(stage);
    }

	private void RunStage(Stages stage)
	{
		_singleThreads.Clear();
		_multiThreads.Clear();

		var systems = _systems[(int) stage];

		foreach (var sys in systems)
		{
			if (sys.IsResourceInUse())
			{
				_singleThreads.Add(sys);
			}
			else
			{
				_multiThreads.Add(sys);
			}
		}

		var multithreading = _multiThreads;
		var singlethreading = _singleThreads;

		// var multithreading = systems.Where(static s => !s.IsResourceInUse());
		// var singlethreading = systems.Except(multithreading);

		if (multithreading.Any())
			Parallel.ForEach(multithreading, static s => s.Run());

		foreach (var system in singlethreading)
			system.Run();
	}

	internal void Add(FuncSystem<World> sys, Stages stage)
	{
		sys.Node = _systems[(int)stage].AddLast(sys);
	}

	public FuncSystem<World> AddSystem(Action system, Stages stage = Stages.Update, ThreadingMode threadingType = ThreadingMode.Auto)
	{
		var sys = new FuncSystem<World>(_world, (args,runIf) => { if (runIf?.Invoke(args) ?? true) system(); }, () => false, threadingType);
		Add(sys, stage);

		return sys;
	}


	public Scheduler AddPlugin<T>() where T : notnull, IPlugin, new()
		=> AddPlugin(new T());

	public Scheduler AddPlugin<T>(T plugin) where T : IPlugin
	{
		plugin.Build(this);

		return this;
	}

	public Scheduler AddEvent<T>() where T : notnull
	{
		var queue = new Queue<T>();
		return AddSystemParam(new EventWriter<T>(queue))
			.AddSystemParam(new EventReader<T>(queue));
	}

	public Scheduler AddState<T>(T initialState = default!) where T : notnull, Enum
	{
		return AddResource(initialState);
	}

    public Scheduler AddResource<T>(T resource) where T : notnull
    {
		return AddSystemParam(new Res<T>() { Value = resource });
    }

	public Scheduler AddSystemParam<T>(T param) where T : notnull, ISystemParam<World>
	{
		_world.Set(param);

		return this;
	}

	internal bool ResourceExists<T>() where T : notnull, ISystemParam<World>
	{
		return _world.Has<T>();
	}
}

public interface IPlugin
{
	void Build(Scheduler scheduler);
}

public abstract class SystemParam<T> : ISystemParam<T>
{
    private int _useIndex;
    ref int ISystemParam.UseIndex => ref _useIndex;
}

public interface ISystemParam
{
	internal ref int UseIndex { get; }

	void Lock() => Interlocked.Increment(ref UseIndex);
	void Unlock() => Interlocked.Decrement(ref UseIndex);
}

public interface ISystemParam<TParam> : ISystemParam
{

}

public interface IIntoSystemParam<TArg, TResult> where TResult : ISystemParam<TArg>
{
	public static abstract TResult Generate(TArg arg);
}


internal sealed class EventParam<T> : SystemParam<World>, IIntoSystemParam<World, EventParam<T>> where T : notnull
{
    private readonly Queue<T> _queue = new();

    internal EventParam()
    {
        Writer = new EventWriter<T>(_queue);
        Reader = new EventReader<T>(_queue);
    }

    public EventWriter<T> Writer { get; }
    public EventReader<T> Reader { get; }


    public static EventParam<T> Generate(World arg)
    {
        if (arg.Has<EventParam<T>>())
            return arg.Get<EventParam<T>>();

        var ev = new EventParam<T>();
        arg.Set(ev);
        return ev;
    }
}

public sealed class EventWriter<T> : SystemParam<World>, IIntoSystemParam<World, EventWriter<T>> where T : notnull
{
    private readonly Queue<T> _queue;

    internal EventWriter(Queue<T> queue)
        => _queue = queue;

    public bool IsEmpty
        => _queue.Count == 0;

    public void Clear()
        => _queue.Clear();

    public void Enqueue(T ev)
        => _queue.Enqueue(ev);

    public static EventWriter<T> Generate(World arg)
    {
        if (arg.Has<EventParam<T>>())
            return arg.Get<EventParam<T>>().Writer;

        throw new NotImplementedException("EventWriter<T> must be created using the scheduler.AddEvent<T>() method");
    }
}

public sealed class EventReader<T> : SystemParam<World>, IIntoSystemParam<World, EventReader<T>> where T : notnull
{
    private readonly Queue<T> _queue;

    internal EventReader(Queue<T> queue)
        => _queue = queue;

    public bool IsEmpty
        => _queue.Count == 0;

    public void Clear()
        => _queue.Clear();

    public EventReaderIterator GetEnumerator() => new(_queue!);

    public static EventReader<T> Generate(World arg)
    {
        if (arg.Has<EventParam<T>>())
            return arg.Get<EventParam<T>>().Reader;

        throw new NotImplementedException("EventReader<T> must be created using the scheduler.AddEvent<T>() method");
    }

    public ref struct EventReaderIterator
    {
        private readonly Queue<T> _queue;
        private T _data;

        internal EventReaderIterator(Queue<T> queue)
        {
            _queue = queue;
            _data = default!;
        }

        public readonly T Current => _data;

        public bool MoveNext() => _queue.TryDequeue(out _data!);
    }
}

public sealed class FlecsWorld : SystemParam<World>, IIntoSystemParam<World, FlecsWorld>
{
	internal FlecsWorld(World world) => Flecs = world;

	public World Flecs { get; }

    public static FlecsWorld Generate(World arg)
    {
		if (arg.Has<FlecsWorld>())
			return arg.GetMut<FlecsWorld>();

		var flecsWorld = new FlecsWorld(arg);
		arg.Set(flecsWorld);

		return flecsWorld;
    }
}

public sealed class Query<TQueryData> : Query<TQueryData, Empty>, IIntoSystemParam<World, Query<TQueryData>>
	where TQueryData : struct, IData<TQueryData>
{
	internal Query(Query query) : base(query) { }

	public static new Query<TQueryData> Generate(World arg)
    {
		if (arg.Has<Query<TQueryData>>())
			return arg.GetMut<Query<TQueryData>>();

		var builder = arg.QueryBuilder();
		TQueryData.Build(ref builder);

		var q = new Query<TQueryData>(builder.Build());
		arg.Set(q);

		return q;
    }
}

public class Query<TQueryData, TQueryFilter> : SystemParam<World>, IIntoSystemParam<World, Query<TQueryData, TQueryFilter>>
	where TQueryData : struct, IData<TQueryData>
	where TQueryFilter : struct, IFilter
{
	private readonly Query _query;

	internal Query(Query query) => _query = query;

    public static Query<TQueryData, TQueryFilter> Generate(World arg)
    {
		if (arg.Has<Query<TQueryData, TQueryFilter>>())
			return arg.GetMut<Query<TQueryData, TQueryFilter>>();

		var builder = arg.QueryBuilder();

		TQueryData.Build(ref builder);
		TQueryFilter.Build(builder: ref builder);

		var q = new Query<TQueryData, TQueryFilter>(builder.Build());
		arg.Set(q);

		return q;
    }

	public QueryIterator GetEnumerator() => new (in _query);

	public int Count() => _query.Count();

	public Entity Single()
	{
		var count = Count();
		if (count != 1)
			throw new Exception("query must match only one entity");

		foreach (var it in this)
			return it.Entity(0);

		return Entity.Null();
	}

	public ref T Single<T>(int fieldIndex) where T : IComponent
	{
        var count = Count();
        if (count != 1)
            throw new Exception("query must match only one entity");

		foreach (var it in this)
			return ref it.Field<T>(fieldIndex)[0];

		return ref Unsafe.NullRef<T>();
    }

	public IQueryIterator<TQueryData> Iter()
	{
		return TQueryData.CreateIterator(GetEnumerator());
	}
}

public sealed class Res<T> : SystemParam<World>, IIntoSystemParam<World, Res<T>> where T : notnull
{
	private T? _t;

    public ref T? Value => ref _t;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator T?(Res<T> reference)
		=> reference.Value;

    public static Res<T> Generate(World arg)
    {
		if (arg.Has<Res<T>>())
			return arg.GetMut<Res<T>>();

		var res = new Res<T>();
		arg.Set(res);

		return res;
    }
}

public sealed class Local<T> : SystemParam<World>, IIntoSystemParam<World, Local<T>> where T : notnull
{
	private T? _t;

    public ref T? Value => ref _t;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator T?(Local<T> reference)
		=> reference.Value;

    public static Local<T> Generate(World arg)
    {
        return new Local<T>();
    }
}

public sealed class SchedulerState : SystemParam<World>, IIntoSystemParam<World, SchedulerState>
{
    private readonly Scheduler _scheduler;

    internal SchedulerState(Scheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public void AddResource<T>(T resource) where T : notnull
        => _scheduler.AddResource(resource);

    public bool ResourceExists<T>() where T : notnull
        => _scheduler.ResourceExists<Res<T>>();

    public static SchedulerState Generate(World arg)
    {
        if (arg.Has<SchedulerState>())
            return arg.GetMut<SchedulerState>();
        throw new NotImplementedException();
    }
}


public interface IComponent { }

public interface ITermCreator
{
	public static abstract void Build(ref QueryBuilder builder);
}

public interface IQueryIterator<TData> where TData : IData<TData>
{
	public IQueryIterator<TData> GetEnumerator();
	public TData Current { get; }
	public bool MoveNext();
}

public interface IData<TData> : ITermCreator where TData : IData<TData>
{
	public static abstract IQueryIterator<TData> CreateIterator(QueryIterator iterator);
}

public interface IFilter : ITermCreator { }
public interface INestedFilter
{
	void BuildAsParam(ref QueryBuilder builder);
}

internal static class FilterBuilder<T> where T : struct
{
	public static bool Build(ref QueryBuilder builder)
	{
		if (default(T) is INestedFilter nestedFilter)
		{
			nestedFilter.BuildAsParam(ref builder);
			return true;
		}

		return false;
	}
}

public readonly struct Wildcard : IComponent { }

public readonly struct Empty : IData<Empty>, IComponent, IFilter
{
    public static void Build(ref QueryBuilder builder)
    {

    }

    public static IQueryIterator<Empty> CreateIterator(QueryIterator iterator)
    {
        throw new NotImplementedException();
    }
}

public readonly struct With<T> : IFilter, INestedFilter
	where T : struct, IComponent
{
    public static void Build(ref QueryBuilder builder)
    {
		if (!FilterBuilder<T>.Build(ref builder))
			builder.With<T>().InOutNone();
		else
			builder.Oper(Bindings.flecs.ecs_oper_kind_t.EcsAnd).InOutNone();
    }

    public void BuildAsParam(ref QueryBuilder builder)
    {
		Build(ref builder);
    }
}

public readonly struct Without<T> : IFilter, INestedFilter
	where T : struct, IComponent
{
	public static void Build(ref QueryBuilder builder)
    {
		if (!FilterBuilder<T>.Build(ref builder))
			builder.Without<T>().InOutNone();
		else
			builder.Oper(Bindings.flecs.ecs_oper_kind_t.EcsNot).InOutNone();
    }

    public void BuildAsParam(ref QueryBuilder builder)
    {
		Build(ref builder);
    }
}

public readonly struct Optional<T> : IData<Optional<T>>, INestedFilter
	where T : struct, IComponent
{
	public static void Build(ref QueryBuilder builder)
    {
		if (!FilterBuilder<T>.Build(ref builder))
			builder.With<T>().Optional();
		else
			builder.Optional();
    }

    public static IQueryIterator<Optional<T>> CreateIterator(QueryIterator iterator)
    {
        throw new NotImplementedException();
    }

    public void BuildAsParam(ref QueryBuilder builder)
    {
		Build(ref builder);
    }
}

public readonly struct Pair<TFirst, TSecond> : IComponent, IFilter, INestedFilter
	where TFirst : struct, IComponent
	where TSecond : struct, IComponent
{
	public static void Build(ref QueryBuilder builder)
    {
		if (typeof(TFirst) == typeof(Wildcard))
			builder.With(Ecs.Wildcard);
		else
			builder.With<TFirst>();

		if (typeof(TSecond) == typeof(Wildcard))
			builder.Second(Ecs.Wildcard);
		else
			builder.Second<TSecond>();
    }

	public void BuildAsParam(ref QueryBuilder builder)
    {
		Build(ref builder);
    }
}


public unsafe struct QueryIterator
{
	private Query _query;
	private NET.Bindings.flecs.ecs_iter_t _ecsIt;

	internal QueryIterator(ref readonly Query query)
	{
		_query = query;
		_ecsIt = query.GetIter();
	}

	public Iter Current
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
				return new Iter(ptr);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool MoveNext()
	{
		fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
			return _query.GetNext(ptr);
	}
}



public struct Data2<T0, T1> : IData<Data2<T0, T1>>,  IQueryIterator<Data2<T0, T1>>
	where T0 : struct, IComponent
	where T1 : struct, IComponent
{
	private QueryIterator _iterator;


	internal Data2(QueryIterator iterator)
	{
		_iterator = iterator;
	}

	public static void Build(ref QueryBuilder builder)
	{
		if (!FilterBuilder<T0>.Build(ref builder))
			builder.With<T0>();
		if (!FilterBuilder<T1>.Build(ref builder))
			builder.With<T1>();
	}

    public static IQueryIterator<Data2<T0, T1>> CreateIterator(QueryIterator iterator)
    {
        return new Data2<T0, T1>(iterator);
    }


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Deconstruct(out Field<T0> field0, out Field<T1> field1)
	{
		var iter = _iterator.Current;
		field0 = iter.Field<T0>(0);
		field1 = iter.Field<T1>(1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Deconstruct(out Field<ulong> entities, out Field<T0> field0, out Field<T1> field1)
	{
		var iter = _iterator.Current;
		entities = iter.Entities();
		field0 = iter.Field<T0>(0);
		field1 = iter.Field<T1>(1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool MoveNext() => _iterator.MoveNext();

    readonly Data2<T0, T1> IQueryIterator<Data2<T0, T1>>.Current => this;

    readonly IQueryIterator<Data2<T0, T1>> IQueryIterator<Data2<T0, T1>>.GetEnumerator()
    {
		return this;
    }
}
