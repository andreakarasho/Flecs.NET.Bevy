using Flecs.NET.Bevy;
using Flecs.NET.Core;


using var ecs = World.Create();
var scheduler = new Scheduler(ecs);

scheduler.AddSystem((Query<Data<Position, Velocity>> query, Res<int> res) =>
{
    foreach ((var entities, var field0, var field1) in query.Iter<Position, Velocity>())
    {
        var count = entities.Length;

        for (var i = 0; i < count; ++i)
        {
            ref var pos = ref field0[i];
            ref var vel = ref field1[i];

            pos.X *= vel.X;
            pos.Y *= vel.Y;
        }
    }

    // foreach (var it in query)
    // {
    //     var count = it.Count();
    //     var field0 = it.Field<Position>(0);
    //     var field1 = it.Field<Velocity>(1);

    //     for (var i = 0; i < count; ++i)
    //     {
    //         ref var pos = ref field0[i];
    //         ref var vel = ref field1[i];

    //         pos.X *= vel.X;
    //         pos.Y *= vel.Y;
    //     }
    // }
});



// for (var i = 0; i < 1_000_000; ++i)
//     ecs.Entity().Set(new Position()).Set(new Velocity());

// for (var i = 0; i < 1_000_000; ++i)
//     ecs.Entity().Set(new Position()).Set(new Velocity()).Add<PlayerTag>();


scheduler.Run();



struct Position
{
    public float X, Y, Z;
}

struct Velocity
{
    public float X, Y;
}

struct PlayerTag { }