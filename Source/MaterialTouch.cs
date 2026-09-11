using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

// CeleMod consumes Android touch in its overlay and publishes semantic gestures.
// Join that channel instead of enabling SDL mouse emulation globally (which
// would also turn gameplay fingers into menu clicks).
internal static class MaterialTouch {
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private sealed class Surface {
        internal MaterialInteractionTarget[] Targets = [];
        internal string Context = "";
        internal readonly Queue<(string Action, Vector2 Position, float Value, string Text, MaterialInteractionTarget Target)> Pending = new();
    }
    private static ConditionalWeakTable<Entity, Surface> surfaces = new();
    private static Hook? signatureHook;
    private static Type? host, boxType;
    private static MethodInfo? add;
    private static Delegate? dispatch;
    private static Entity? updating;
    private static Vector2 position;
    private static bool touched, pressed, released;
    private static int wheel;
    internal static bool Connected => signatureHook is not null;
    internal static Vector2 Position => touched && !MInput.Mouse.WasMoved ? position : MInput.Mouse.Position;
    internal static bool PressedLeftButton => pressed || MInput.Mouse.PressedLeftButton;
    internal static bool CheckLeftButton => pressed || MInput.Mouse.CheckLeftButton;
    internal static bool ReleasedLeftButton => released || MInput.Mouse.ReleasedLeftButton;
    internal static bool WasMoved => pressed || wheel != 0 || MInput.Mouse.WasMoved;
    internal static int WheelDelta => wheel != 0 ? wheel : MInput.Mouse.WheelDelta;

    internal static void Load() {
        if (!AndroidNativeLibrary.IsAndroid) return;
        try {
            host = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("GameTouch")).FirstOrDefault(t => t is not null);
            if (host is null) throw new NotSupportedException("CeleMod's direct-touch bridge is unavailable");
            boxType = host.GetNestedType("Box", Flags)!;
            add = host.GetMethod("Add", Flags)!;
            Type commandType = host.GetNestedType("Command", Flags)!;
            var command = Expression.Parameter(commandType, "command");
            dispatch = Expression.Lambda(typeof(Action<>).MakeGenericType(commandType),
                Expression.Call(typeof(MaterialTouch).GetMethod(nameof(Receive), Flags)!, Expression.Convert(command, typeof(object))), command).Compile();
            // Hook the final signature calculation, not Refresh: changing the
            // epoch twice in Refresh would invalidate every incoming gesture.
            signatureHook = new Hook(host.GetMethod("FinishSignature", Flags)!, (Action<Action<string>, string>)FinishSignature);
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Touch", "CeleMod direct touch connected");
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Touch", "Cannot connect direct touch: " + e.GetBaseException().Message);
        }
    }

    internal static void Unload() {
        signatureHook?.Dispose(); signatureHook = null;
        surfaces = new(); updating = null; dispatch = null; host = boxType = null; add = null;
        touched = pressed = released = false; wheel = 0;
    }

    private static Entity? ActiveOwner() {
        if (Engine.Scene is Overworld overworld) {
            if (overworld.Overlay is not null) return null;
            if (overworld.Current is MaterialChapterSelect { AcceptsTouch: true } chapters) return chapters;
            if (overworld.Current is MaterialModOptions { AcceptsTouch: true } options) return options;
        }
        return Engine.Scene?.Entities.FirstOrDefault(e => e.Active && e.Visible &&
            (e is QolSettingsOverlay { AcceptsTouch: true } || e is MaterialModOptions { AcceptsTouch: true }));
    }

    internal static void BeginUpdate(Entity owner) {
        if (!Connected) return;
        updating = owner;
        released = pressed; pressed = false; wheel = 0;
        if (!ReferenceEquals(owner, ActiveOwner())) return;
        Surface surface = surfaces.GetOrCreateValue(owner);
        if (!surface.Pending.TryDequeue(out var command)) return;
        position = command.Position; touched = true;
        if (command.Action == "text") command.Target.TouchTextChanged?.Invoke(command.Text);
        else if (command.Action == "scroll") wheel = -Math.Sign(command.Value);
        else {
            pressed = true;
            // Horizontal slider gestures keep the original row even if the
            // finger drifts vertically. The existing slider clamps its value.
            if (command.Action == "adjust") position.Y = command.Target.Bounds.Center.Y;
        }
    }

    internal static void Publish(Entity owner, IEnumerable<MaterialInteractionTarget> targets, string context) {
        if (!Connected) return;
        Surface surface = surfaces.GetOrCreateValue(owner);
        if (surface.Context != context) surface.Pending.Clear();
        surface.Context = context;
        surface.Targets = targets.ToArray();
    }

    private static void FinishSignature(Action<string> orig, string mode) {
        Entity? owner = ActiveOwner();
        if (owner is null || !surfaces.TryGetValue(owner, out Surface? surface)) { orig(mode); return; }
        try {
            ((System.Collections.IList)host!.GetField("targets", Flags)!.GetValue(null)!).Clear();
            host.GetField("root", Flags)!.SetValue(null, owner);
            host.GetField("kind", Flags)!.SetValue(null, "menu");
            Add(owner, new("mqol.background", new(0, 0, 1920, 1080)));
            // Small controls win over their containing rows, matching desktop hit testing.
            foreach (var target in surface.Targets.Where(t => t.Enabled).OrderByDescending(t => t.Bounds.Width * t.Bounds.Height)) Add(owner, target);
            orig("mqol:" + surface.Context);
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Touch", "Touch layout failed: " + e.GetBaseException().Message);
            orig(mode);
        }
    }

    private static void Add(Entity owner, MaterialInteractionTarget target) {
        MaterialRect r = target.Bounds;
        object box = Activator.CreateInstance(boxType!, r.X, r.Y, r.Width, r.Height)!;
        add!.Invoke(null, [target.Key, owner, box, target.TouchKind, target.Key, dispatch!, target.TouchText, target.TouchMaxLength]);
    }

    private static void Receive(object command) {
        Entity? owner = ActiveOwner();
        if (owner is null || !surfaces.TryGetValue(owner, out Surface? surface)) return;
        object? Read(string name) => command.GetType().GetProperty(name)!.GetValue(command);
        string id = (string)Read("id")!;
        var target = id == "mqol.background" ? new MaterialInteractionTarget(id, new(0, 0, 1920, 1080))
            : surface.Targets.FirstOrDefault(t => t.Key == id && t.Enabled);
        if (target.Key is null || surface.Pending.Count >= 32) return;
        string action = (string)Read("action")!;
        if (action == "text" && target.TouchTextChanged is null) return;
        surface.Pending.Enqueue((action, new((float)Read("x")! * 1920, (float)Read("y")! * 1080),
            (float)Read("value")!, (string)Read("text")!, target));
    }
}
