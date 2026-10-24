using System.Globalization;
using System.Reflection;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum OptionKind {
    Switch,
    Dropdown,
    Range
}

internal static class OptionRules {
    internal static OptionKind Classify(bool isBool, bool isSlider, bool isIntOption,
        bool? enumBacked, IReadOnlyList<string> labels, IReadOnlyList<object> entries) {
        if (isBool) return OptionKind.Switch;
        if (!isSlider && !isIntOption) return OptionKind.Dropdown;
        if (enumBacked is true) return OptionKind.Dropdown;
        if (isSlider && enumBacked is false) return OptionKind.Range;
        return HasConsecutiveInts(entries) && labels.Count > 0 && labels.All(LooksNumeric)
            ? OptionKind.Range
            : OptionKind.Dropdown;
    }

    internal static bool? ChangeTargetsEnum(Delegate? change) {
        object? target = change?.Target;
        if (target is null) return null;
        bool sawEnum = false;
        bool sawNumeric = false;
        foreach (FieldInfo field in target.GetType().GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            object? value;
            try {
                value = field.GetValue(target);
            } catch {
                continue;
            }
            if (value is PropertyInfo property) {
                if (property.PropertyType.IsEnum) sawEnum = true;
                else if (IsNumericType(property.PropertyType)) sawNumeric = true;
            } else if (value is Array array && array.GetType().GetElementType()?.IsEnum == true) {
                sawEnum = true;
            } else if (field.FieldType.IsEnum) {
                sawEnum = true;
            }
        }
        if (sawEnum && !sawNumeric) return true;
        if (sawNumeric && !sawEnum) return false;
        return null;
    }

    internal static bool IsNumericType(Type type) =>
        type == typeof(int) || type == typeof(float) || type == typeof(double)
        || type == typeof(long) || type == typeof(short) || type == typeof(byte);

    internal static bool HasConsecutiveInts(IReadOnlyList<object> entries) {
        int? previous = null;
        foreach (object entry in entries) {
            if (EntryValue(entry) is not int value) return false;
            if (previous.HasValue && value != previous.Value + 1) return false;
            previous = value;
        }
        return previous.HasValue;
    }

    internal static bool LooksNumeric(string value) {
        string text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
            return true;
        int index = text.StartsWith('-') || text.StartsWith('+') ? 1 : 0;
        bool digits = false;
        while (index < text.Length && char.IsDigit(text[index])) {
            index++;
            digits = true;
        }
        if (!digits) return false;
        if (index < text.Length && text[index] == '.') {
            index++;
            int fractionStart = index;
            while (index < text.Length && char.IsDigit(text[index])) index++;
            if (index == fractionStart) return false;
        }
        // Try to parse a short unit suffix ["1x", "80%", "10 fps"],
        // but reject notes ["1 (recommended)"] and compound labels ["1280×720"].
        // This is based on heuristics and not guaranteed to be accurate.
        string suffix = text[index..].Trim();
        return suffix.Length == 0 || suffix is "%" or "×"
            || suffix.Length <= 6 && suffix.All(char.IsLetter);
    }

    internal static object? EntryValue(object entry) =>
        entry.GetType().GetProperty("Item2")?.GetValue(entry)
        ?? entry.GetType().GetField("Item2")?.GetValue(entry);
}
