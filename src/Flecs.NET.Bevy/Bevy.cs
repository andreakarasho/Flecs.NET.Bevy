
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

		// AddSystemParam(new FlecsWorld());
		// AddSystemParam(new SchedulerState(this));
	}


	// public bool IsState<TState>(TState state) where TState : Enum =>
	// 	ISystemParam.Get<Res<TState>>(_resources, _resources, null!)?.Value?.Equals(state) ?? false;

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

    public void New(Scheduler arguments)
    {
        throw new NotImplementedException();
    }
}

public interface IPlugin
{
	void Build(Scheduler scheduler);
}

public abstract class SystemParam : ISystemParam<World>
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

public interface IIntoSystemParam<TArg>
{
	public static abstract ISystemParam<TArg> Generate(TArg arg);
}


public sealed class EventWriter<T> : SystemParam, IIntoSystemParam<World> where T : notnull
{
	private readonly Queue<T>? _queue;

	internal EventWriter(Queue<T> queue)
		=> _queue = queue;

	public EventWriter()
		=> throw new Exception("EventWriter must be initialized using the 'scheduler.AddEvent<T>' api");

	public bool IsEmpty
		=> _queue!.Count == 0;

	public void Clear()
		=> _queue?.Clear();

	public void Enqueue(T ev)
		=> _queue!.Enqueue(ev);

    public static ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<EventWriter<T>>())
			return arg.Get<EventWriter<T>>();

		var writer = new EventWriter<T>();
		arg.Set(writer);

		return writer;
    }
}

public sealed class EventReader<T> : SystemParam, IIntoSystemParam<World> where T : notnull
{
	private readonly Queue<T>? _queue;

	internal EventReader(Queue<T> queue)
		=> _queue = queue;

	public EventReader()
		=> throw new Exception("EventReader must be initialized using the 'scheduler.AddEvent<T>' api");

	public bool IsEmpty
		=> _queue!.Count == 0;

	public void Clear()
		=> _queue?.Clear();

	public EventReaderIterator GetEnumerator() => new (_queue!);

	public static ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<EventReader<T>>())
			return arg.Get<EventReader<T>>();

		var reader = new EventReader<T>();
		arg.Set(reader);

		return reader;
    }

	public ref struct EventReaderIterator
	{
		private readonly Queue<T> _queue;
		private T? _data;

		internal EventReaderIterator(Queue<T> queue)
		{
			_queue = queue;
			_data = default!;
		}

		public readonly T? Current => _data;

		public bool MoveNext() => _queue.TryDequeue(out _data);
	}
}

public sealed class FlecsWorld : SystemParam, IIntoSystemParam<World>
{
	internal FlecsWorld(World world) => Flecs = world;

	public World Flecs { get; }

    public static ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<FlecsWorld>())
			return arg.Get<FlecsWorld>();

		var flecsWorld = new FlecsWorld(arg);
		arg.Set(flecsWorld);

		return flecsWorld;
    }
}

public sealed class Query<TQueryData> : Query<TQueryData, Empty>, IIntoSystemParam<World>
	where TQueryData : struct, IData
{
	internal Query(Query query) : base(query) { }

	public static new ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<Query<TQueryData>>())
			return arg.Get<Query<TQueryData>>();

		var builder = arg.QueryBuilder();
		TQueryData.Build(ref builder);

		var q = new Query<TQueryData>(builder.Build());
		arg.Set(q);

		return q;
    }
}

public partial class Query<TQueryData, TQueryFilter> : SystemParam, IIntoSystemParam<World>
	where TQueryData : struct, IData
	where TQueryFilter : struct, IFilter
{
	private readonly Query _query;

	internal Query(Query query) => _query = query;

    public static ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<Query<TQueryData, TQueryFilter>>())
			return arg.Get<Query<TQueryData, TQueryFilter>>();

		var builder = arg.QueryBuilder();

		TQueryData.Build(ref builder);
		TQueryFilter.Build(builder: ref builder);

		var q = new Query<TQueryData, TQueryFilter>(builder.Build());
		arg.Set(q);

		return q;
    }

	public QueryIterator GetEnumerator() => new (_query);
}

public sealed class Res<T> : SystemParam, IIntoSystemParam<World> where T : notnull
{
	private T? _t;

    public ref T? Value => ref _t;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator T?(Res<T> reference)
		=> reference.Value;

    public static ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<Res<T>>())
			return arg.Get<Res<T>>();

		var res = new Res<T>();
		arg.Set(res);

		return res; ;
    }
}

public sealed class Local<T> : SystemParam, IIntoSystemParam<World> where T : notnull
{
	private T? _t;

    public ref T? Value => ref _t;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator T?(Local<T> reference)
		=> reference.Value;

    public static ISystemParam<World> Generate(World arg)
    {
        return new Local<T>();
    }
}

// public sealed class SchedulerState : SystemParam, IIntoSystemParam<Scheduler>
// {
// 	private readonly Scheduler _scheduler;

// 	internal SchedulerState(Scheduler scheduler)
// 	{
// 		_scheduler = scheduler;
// 	}

// 	public SchedulerState()
// 		=> throw new Exception("You are not allowed to initialize this object by yourself!");

// 	public void AddResource<T>(T resource) where T : notnull
// 		=> _scheduler.AddResource(resource);

// 	public bool ResourceExists<T>() where T : notnull
// 		=> _scheduler.ResourceExists<Res<T>>();

//     public static ISystemParam<Scheduler> Generate(Scheduler arg)
//     {
// 		return new SchedulerState(arg);
//     }


//     public void New(Scheduler arguments)
//     {
//         throw new NotImplementedException();
//     }
// }


public interface IComponent { }

public interface ITermCreator
{
	public static abstract void Build(ref QueryBuilder builder);
}

public interface IData : ITermCreator { }
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

public readonly struct Empty : IData, IComponent, IFilter
{
    public static void Build(ref QueryBuilder builder)
    {

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
public readonly struct Optional<T> : IFilter, INestedFilter
	where T : struct, IComponent
{
	public static void Build(ref QueryBuilder builder)
    {
		if (!FilterBuilder<T>.Build(ref builder))
			builder.With<T>().Optional();
		else
			builder.Optional();
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


public unsafe ref struct QueryIterator
{
	private readonly Query _query;
	private NET.Bindings.flecs.ecs_iter_t _ecsIt;

	internal QueryIterator(Query query)
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
