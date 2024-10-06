
using System.Diagnostics.CodeAnalysis;
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

	public FuncSystem<World> AddSystem<T0, T1>(Action<T0, T1> system, Stages stage = Stages.Update, ThreadingMode threadingType = ThreadingMode.Auto)
            where T0 : class, ISystemParam<World>, IIntoSystemParam<World>
            where T1 : class, ISystemParam<World>, IIntoSystemParam<World>
	{
		T0? obj0 = null;
		T1? obj1 = null;
		var checkInuse = () => obj0?.UseIndex != 0 || obj1?.UseIndex != 0;
		var fn = (World args, Func<World, bool> runIf) =>
		{
			if (runIf != null && !runIf.Invoke(args))
				return;

			obj0 ??= (T0)T0.Generate(args);
			obj1 ??= (T1)T1.Generate(args);
			obj0.Lock();
			obj1.Lock();
			system(obj0, obj1);
			obj0.Unlock();
			obj1.Unlock();
		};
		var sys = new FuncSystem<World>(_world, fn, checkInuse, threadingType);
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

public sealed class Query<TQueryData> : Query<TQueryData, Empty>
	where TQueryData : IData
{
	internal Query(Query query) : base(query) { }
}

public partial class Query<TQueryData, TQueryFilter> : SystemParam, IIntoSystemParam<World>
	where TQueryData : IData
	where TQueryFilter : IFilter
{
	private readonly Query _query;

	internal Query(Query query) => _query = query;

	public QueryIterator<T0, T1> Iter<T0, T1>()
		where T0 : struct
		where T1 : struct
	{
		return new QueryIterator<T0, T1>(_query);
	}

    public static ISystemParam<World> Generate(World arg)
    {
		if (arg.Has<Query<TQueryData>>())
			return arg.Get<Query<TQueryData>>();

		var builder = arg.QueryBuilder();
		TQueryData.AppendTerm(ref builder);
		TQueryFilter.AppendTerm(ref builder);

		var q = new Query<TQueryData, TQueryFilter>(builder.Build());
		arg.Set(q);

		return q;
    }
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




public unsafe ref struct QueryIterator<T0, T1>
	where T0 : struct
	where T1 : struct
{
	private NET.Bindings.flecs.ecs_iter_t _ecsIt;
	private readonly Query _query;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal QueryIterator(Query query)
	{
		_query = query;
		_ecsIt = query.GetIter();
	}

	[UnscopedRef]
	public ref QueryIterator<T0, T1> Current => ref this;


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Deconstruct(out Field<T0> field0, out Field<T1> field1)
	{
		fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
		{
			var iter = new Iter(ptr);
			field0 = iter.Field<T0>(0);
			field1 = iter.Field<T1>(1);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Deconstruct(out Field<ulong> entities, out Field<T0> field0, out Field<T1> field1)
	{
		fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
		{
			var iter = new Iter(ptr);
			entities = iter.Entities();
			field0 = iter.Field<T0>(0);
			field1 = iter.Field<T1>(1);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool MoveNext()
	{
		fixed (NET.Bindings.flecs.ecs_iter_t* ptr = &_ecsIt)
			return _query.GetNext(ptr);
	}

	public readonly QueryIterator<T0, T1> GetEnumerator() => this;
}




public interface ITermCreator
{
	public static abstract void AppendTerm(ref QueryBuilder builder);
}

public interface IData : ITermCreator { }
public interface IFilter : ITermCreator { }


public struct Data<T0, T1> : IData
	where T0 : struct
	where T1 : struct
{
	public static void AppendTerm(ref QueryBuilder builder)
	{
		builder.With<T0>().With<T1>();
	}
}

public struct Filter<T0, T1> : IFilter
	where T0 : IFilter
	where T1 : IFilter
{
	public static void AppendTerm(ref QueryBuilder builder)
	{
		T0.AppendTerm(ref builder);
		T1.AppendTerm(ref builder);
	}
}

public readonly struct Empty : IFilter
{
    public static void AppendTerm(ref QueryBuilder builder)
    {

    }
}
public readonly struct With<T> : IFilter
	where T : struct
{
    public static void AppendTerm(ref QueryBuilder builder)
    {
		builder.With<T>().InOutNone();
    }
}
public readonly struct Without<T> : IFilter
	where T : struct
{
	public static void AppendTerm(ref QueryBuilder builder)
    {
		builder.Without<T>();
    }
}
public readonly struct Optional<T> : IFilter
	where T : struct
{
	public static void AppendTerm(ref QueryBuilder builder)
    {
		builder.With<T>().Optional();
    }
}
