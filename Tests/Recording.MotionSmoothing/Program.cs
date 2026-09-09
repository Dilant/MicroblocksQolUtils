using System.Reflection;
using System.Runtime.Loader;
using Celeste;
using Celeste.Mod.MicroblocksQolUtils;
using Microsoft.Xna.Framework;
using Monocle;

// Uses the user's real relinked MotionSmoothing assembly, but never starts the
// game, touches a save, draws a frame, or changes any installed mod settings.
if (args.Length != 1) throw new ArgumentException("Pass the MotionSmoothing DLL path");
Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
Type TypeOf(string name) => assembly.GetType("Celeste.Mod.MotionSmoothing." + name, true)!;
int checks = 0;
void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }

var handlerType = TypeOf("Smoothing.MotionSmoothingHandler");
object handler = Activator.CreateInstance(handlerType)!;
object value = handlerType.GetProperty("ValueSmoother")!.GetValue(handler)!;
object push = handlerType.GetProperty("PushSpriteSmoother")!.GetValue(handler)!;
MethodInfo valueAdd = value.GetType().GetMethod("SmoothObject", fields)!;
MethodInfo pushAdd = push.GetType().GetMethod("SmoothObject", fields,
    [typeof(object), TypeOf("Smoothing.States.IPositionSmoothingState")])!;
FieldInfo updated = handlerType.GetField("_positionsWereUpdated", fields)!;

RecordingMotionSmoothing.Initialize(assembly);
RecordingMotionSmoothing.Initialize(assembly); // Repeated saves reuse one hook.
for (int load = 0; load < 3; load++) {
    // Exactly what MS's SRT callback does: create camera/object states, without
    // an Engine/Scene update. Hires reads SmoothedRealPosition immediately.
    var camera = new Camera(320, 180) { Position = new Vector2(410 + load, -95 - load) };
    object cameraState = Activator.CreateInstance(TypeOf("Smoothing.States.CameraSmoothingState"))!;
    valueAdd.Invoke(value, [camera, cameraState]);
    var actor = new Entity { Position = new Vector2(450 + load, 30) };
    object actorState = Activator.CreateInstance(TypeOf("Smoothing.States.EntitySmoothingState"))!;
    pushAdd.Invoke(push, [actor, actorState]);
    PropertyInfo smooth = cameraState.GetType().GetProperty("SmoothedRealPosition")!;
    Check((Vector2)smooth.GetValue(cameraState)! == Vector2.Zero, "fixture no longer reproduces uninitialized camera");
    updated.SetValue(handler, true);
    RecordingMotionSmoothing.PrimeRestoredState();
    Check((Vector2)smooth.GetValue(cameraState)! == camera.Position, "restored hires camera still points at zero before physics");
    Check((Vector2)actorState.GetType().GetProperty("SmoothedRealPosition")!.GetValue(actorState)! == actor.Position,
        "restored push-sprite history was not primed");
    Check(updated.GetValue(handler) is false, "frozen draw extrapolates from the abandoned attempt's clock");
    Check(camera.Position == new Vector2(410 + load, -95 - load) && actor.Position == new Vector2(450 + load, 30),
        "priming history advanced real camera/entities");
}

Type drawType = TypeOf("Utilities.UpdateAtDraw");
object atDraw = Activator.CreateInstance(drawType)!;
var backdrop = new CountingBackdrop();
var renderer = new BackdropRenderer(); renderer.Backdrops.Add(backdrop);
var particle = new CountingParticle();
((List<Renderer>)drawType.GetField("_renderersToUpdate", fields)!.GetValue(atDraw)!).Add(renderer);
((List<Entity>)drawType.GetField("_entitiesToUpdate", fields)!.GetValue(atDraw)!).Add(particle);
MethodInfo drawUpdate = drawType.GetMethod("Update", fields, [typeof(Scene)])!;
var scene = (Level)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Level));
drawUpdate.Invoke(atDraw, [scene]);
Check(backdrop.Updates == 1 && particle.Updates == 1, "normal render-driven updates changed");
RecordingSavePause.Active = true;
for (int i = 0; i < 180; i++) drawUpdate.Invoke(atDraw, [scene]);
Check(backdrop.Updates == 1 && particle.Updates == 1, "background/particle time advanced during the save/load presentation gate");
RecordingSavePause.Active = false;
drawUpdate.Invoke(atDraw, [scene]);
Check(backdrop.Updates == 2 && particle.Updates == 2, "backgrounds did not resume normally");
RecordingMotionSmoothing.Unload();
RecordingSavePause.Active = true;
drawUpdate.Invoke(atDraw, [scene]);
Check(backdrop.Updates == 3 && particle.Updates == 3, "unload left a draw-update hook installed");
RecordingSavePause.Active = false;
RecordingMotionSmoothing.Initialize(typeof(string).Assembly);
RecordingMotionSmoothing.PrimeRestoredState();
drawUpdate.Invoke(atDraw, [scene]);
Check(backdrop.Updates == 4 && particle.Updates == 4, "unsupported optional assembly changed normal updates");
RecordingMotionSmoothing.Unload();
Console.WriteLine($"PASS: {checks} actual MotionSmoothing camera history / background pause checks");

sealed class CountingBackdrop : Backdrop {
    public int Updates;
    public override void Update(Scene scene) => Updates++;
}
sealed class CountingParticle : Entity {
    public int Updates;
    public override void Update() => Updates++;
}
namespace Celeste.Mod.MicroblocksQolUtils {
    internal static class RecordingSavePause { internal static bool Active; }
}
