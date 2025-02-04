﻿using System.Diagnostics;
using System.Runtime.CompilerServices;
using Flecs.NET.Bevy;

const int ENTITIES_COUNT = (524_288 * 2 * 1);


using var ecs = Flecs.NET.Core.World.Create();
for (var i = 0; i < ENTITIES_COUNT; ++i)
    ecs.Entity().Set(new Position()).Set(new Velocity());



ecs.Import<CustomPlugin>();
// ecs.App().EnableRest().Run();


var scheduler = new Scheduler(ecs);

var sys = (Query<Data<Position, Velocity>> query) =>
{
    foreach ((var pos, var vel) in query)
    {
        pos.Ref.X *= vel.Ref.X;
        pos.Ref.Y *= vel.Ref.Y;
    }
};

scheduler.AddSystem(sys);






var sw = Stopwatch.StartNew();
var start = 0f;
var last = 0f;


while (true)
{
	//var cur = (start - last) / 1000f;
	for (int i = 0; i < 3600; ++i)
	{
        scheduler.Run();
        // ecs.Each(static (ref Position pos, ref Velocity vel) => {
        //     pos.X *= vel.X;
        //     pos.Y *= vel.Y;
        //  });

        // query.Iter(static (Iter it, Field<Position> posA, Field<Velocity> velA) => {

        //     ref var pos = ref posA[0];
        //     ref var vel = ref velA[0];
        //     ref var last = ref Unsafe.Add(ref pos, it.Count());

        //     while (Unsafe.IsAddressLessThan(ref pos, ref last))
        //     {
        //         pos.X *= vel.X;
        //         pos.Y *= vel.Y;

        //         pos = ref Unsafe.Add(ref pos, 1);
        //         vel = ref Unsafe.Add(ref vel, 1);
        //     }
        // });

	}

	last = start;
	start = sw.ElapsedMilliseconds;

	Console.WriteLine("query done in {0} ms", start - last);
}



struct Position
{
    public float X, Y, Z;
}

struct Velocity
{
    public float X, Y;
}

struct PlayerTag { }


class CustomPlugin : Plugin
{
    protected override void OnBuild(FlecsWorld world)
    {

    }
}

abstract class Plugin : Flecs.NET.Core.IFlecsModule
{
    public void InitModule(Flecs.NET.Core.World world)
    {
         //OnBuild(world.Get<FlecsWorld>());
    }

    protected abstract void OnBuild(FlecsWorld world);
}