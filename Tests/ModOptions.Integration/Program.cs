using System.Reflection;
using Celeste;
using Celeste.Mod.MicroblocksQolUtils;

int assertions = 0;
void Check(bool condition, string message) {
    assertions++;
    if (!condition) throw new Exception(message);
}

const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
Type optionsType = typeof(MaterialModOptions);
var tryGetOption = optionsType.GetMethod("TryGetOption", PrivateStatic)!;

(string Kind, string[] Labels) Snapshot(TextMenu.Item item) {
    object?[] arguments = [item, null];
    Check((bool)tryGetOption.Invoke(null, arguments)!, $"no option snapshot: {item.GetType().Name}");
    object snapshot = arguments[1]!;
    Type snapshotType = snapshot.GetType();
    string kind = snapshotType.GetProperty("Kind")!.GetValue(snapshot)!.ToString()!;
    var options = (List<string>)snapshotType.GetProperty("Options")!.GetValue(snapshot)!;
    return (kind, options.ToArray());
}

Check(Snapshot(new TextMenu.Option<bool>("toggle").Add("Off", false, true).Add("On", true)).Kind
    == "Switch", "bool option should be a switch");
Check(Snapshot(new TextMenu.Option<string>("mode").Add("Off", "off", true).Add("On", "on")).Kind
    == "Dropdown", "string option should be a dropdown");
Check(Snapshot(new TextMenu.Option<int>("int-range")
        .Add("0", 0, true).Add("1", 1).Add("2", 2)).Kind
    == "Range", "consecutive numeric int option should be a range");
Check(Snapshot(new TextMenu.Option<int>("int-sparse")
        .Add("0", 0, true).Add("5", 5).Add("10", 10)).Kind
    == "Dropdown", "sparse int option should be a dropdown");
Check(Snapshot(new TextMenu.Option<int>("int-named")
        .Add("Low", 0, true).Add("Mid", 1).Add("High", 2)).Kind
    == "Dropdown", "named int option should be a dropdown");
Check(Snapshot(new TextMenu.Slider("slider", value => value.ToString(), 0, 2)).Kind
    == "Range", "numeric slider should be a range");
Check(Snapshot(new TextMenu.Slider("named-slider",
        value => new[] { "Low", "Mid", "High" }[value], 0, 2)).Kind
    == "Dropdown", "named slider should be a dropdown");

// The cached classification must follow a change handler that is attached
// after the item has already been shown (Everest exposes Change() on the item).
var enumProperty = typeof(TestModeHolder).GetProperty(nameof(TestModeHolder.Value))!;
var intProperty = typeof(TestIntHolder).GetProperty(nameof(TestIntHolder.Value))!;

var lateEnum = new TextMenu.Slider("late-enum", value => value.ToString(), 0, 2);
Check(Snapshot(lateEnum).Kind == "Range", "numeric slider should start as a range");
lateEnum.OnValueChange = new Action<int>(new PropertyHolder { Property = enumProperty }.Apply);
Check(Snapshot(lateEnum).Kind == "Dropdown", "cached kind did not follow a late enum change handler");

var lateInt = new TextMenu.Slider("late-int", value => value.ToString(), 0, 2);
lateInt.OnValueChange = new Action<int>(new PropertyHolder { Property = intProperty }.Apply);
Check(Snapshot(lateInt).Kind == "Range", "int-backed slider should stay a range");

// Values can also be mutated in place; labels and kind must follow on the next read.
var mutable = new TextMenu.Slider("mutable", value => value.ToString(), 0, 2);
Check(Snapshot(mutable).Kind == "Range", "mutable slider should start as a range");
mutable.Values[0] = Tuple.Create("Low", 0);
Check(Snapshot(mutable).Labels[0] == "Low", "cached labels did not follow a replaced value");
Check(Snapshot(mutable).Kind == "Dropdown", "cached kind did not follow a replaced value");

mutable.Values.Clear();
mutable.Values.Add(Tuple.Create("Off", 0));
mutable.Values.Add(Tuple.Create("On", 1));
(string Kind, string[] Labels) refreshed = Snapshot(mutable);
Check(refreshed.Kind == "Dropdown", "cached kind did not follow a resized value list");
Check(string.Join(',', refreshed.Labels) == "Off,On", "cached labels did not follow a resized value list");

Console.WriteLine($"ModOptions integration tests passed ({assertions} assertions)");

enum TestMode {
    Off,
    On,
    Auto
}

static class TestModeHolder {
    public static TestMode Value { get; set; }
}

static class TestIntHolder {
    public static int Value { get; set; }
}

sealed class PropertyHolder {
    public PropertyInfo? Property;
    public void Apply(int value) { }
}
