using Flecs.NET.Bevy;
using Flecs.NET.Core;


using var ecs = World.Create();
var scheduler = new Scheduler(ecs);

scheduler.AddSystem((Query<Data<Position, Velocity>> query, Res<int> res) =>
{
    // foreach ((var pos, var vel) in query)
    // {

    // }
});



scheduler.Run();



struct Position
{
    public float X, Y, Z;
}

struct Velocity
{
    public float X, Y;
}