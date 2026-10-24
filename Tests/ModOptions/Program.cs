using System.Reflection;
using Celeste.Mod.MicroblocksQolUtils;

int assertions = 0;
void Check(bool condition, string message) {
    assertions++;
    if (!condition) throw new Exception(message);
}

List<object> Entries(params (string Label, int Value)[] pairs) =>
    pairs.Select(pair => (object)Tuple.Create(pair.Label, pair.Value)).ToList();

foreach (string value in new[] {
             "0", "42", "-5", "+7", "1.5", "1,000", "1e3", "1x", "2×", "80%", "10 fps", "1.5 ms"
         }) {
    Check(OptionRules.LooksNumeric(value), $"expected a numeric label: \"{value}\"");
}
foreach (string value in new[] {
             "", " ", "-", "+", "1.2.3", "1 (recommended)", "1280×720", "low", "NaN", "Infinity"
         }) {
    Check(!OptionRules.LooksNumeric(value), $"expected a non-numeric label: \"{value}\"");
}

Check(OptionRules.EntryValue(Tuple.Create("label", 7)) is 7, "EntryValue should read Item2");

Check(OptionRules.Classify(true, false, false, null, [], []) == OptionKind.Switch,
    "bool option should be a switch");
Check(OptionRules.Classify(false, false, false, null, ["Off", "On"], []) == OptionKind.Dropdown,
    "non-int option should be a dropdown");
Check(OptionRules.Classify(false, false, true, null, ["0", "1", "2"],
        Entries(("0", 0), ("1", 1), ("2", 2))) == OptionKind.Range,
    "consecutive numeric int option should be a range");
Check(OptionRules.Classify(false, false, true, null, ["0", "5", "10"],
        Entries(("0", 0), ("5", 5), ("10", 10))) == OptionKind.Dropdown,
    "sparse int option should be a dropdown");
Check(OptionRules.Classify(false, false, true, null, ["Low", "Mid", "High"],
        Entries(("Low", 0), ("Mid", 1), ("High", 2))) == OptionKind.Dropdown,
    "named int option should be a dropdown");
Check(OptionRules.Classify(false, true, true, null, ["0", "1", "2"],
        Entries(("0", 0), ("1", 1), ("2", 2))) == OptionKind.Range,
    "numeric slider with an unknown backing should be a range");
Check(OptionRules.Classify(false, true, true, false, ["Low", "Mid"],
        Entries(("Low", 0), ("Mid", 1))) == OptionKind.Range,
    "int-backed slider should be a range");
Check(OptionRules.Classify(false, true, true, true, ["0", "1", "2"],
        Entries(("0", 0), ("1", 1), ("2", 2))) == OptionKind.Dropdown,
    "enum-backed slider should be a dropdown");
Check(OptionRules.Classify(false, false, true, true, ["Low", "Mid"],
        Entries(("Low", 0), ("Mid", 1))) == OptionKind.Dropdown,
    "enum-backed int option should be a dropdown");

var enumProperty = typeof(TestModeHolder).GetProperty(nameof(TestModeHolder.Value))!;
var intProperty = typeof(TestIntHolder).GetProperty(nameof(TestIntHolder.Value))!;
var enumChange = new Action<int>(new PropertyHolder { EnumProperty = enumProperty }.Apply);
var intChange = new Action<int>(new PropertyHolder { IntProperty = intProperty }.Apply);
var mixedChange = new Action<int>(new PropertyHolder {
    EnumProperty = enumProperty,
    IntProperty = intProperty
}.Apply);

Check(OptionRules.ChangeTargetsEnum(null) is null, "a missing handler should be unknown");
Check(OptionRules.ChangeTargetsEnum(enumChange) is true, "enum property should be detected");
Check(OptionRules.ChangeTargetsEnum(intChange) is false, "int property should be detected");
Check(OptionRules.ChangeTargetsEnum(mixedChange) is null, "mixed captures should be ambiguous");

Console.WriteLine($"OptionRules tests passed ({assertions} assertions)");

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
    public PropertyInfo? EnumProperty;
    public PropertyInfo? IntProperty;
    public void Apply(int value) { }
}
