namespace Ansight.RemoteSimulator.Core.Simulator.Android.Input;

internal static class HidKeyMapper
{
    private static readonly IReadOnlyDictionary<uint, string> keyCodes = BuildKeyCodes();

    public static string? Map(uint usageCode)
        => keyCodes.TryGetValue(usageCode, out var keyCode) ? keyCode : null;

    public static bool IsModifier(uint usageCode) => usageCode is >= 224 and <= 231;

    private static IReadOnlyDictionary<uint, string> BuildKeyCodes()
    {
        var result = new Dictionary<uint, string>();
        for (uint usageCode = 4; usageCode <= 29; usageCode++)
        {
            result[usageCode] = $"KEYCODE_{(char)('A' + usageCode - 4)}";
        }

        for (uint usageCode = 30; usageCode <= 38; usageCode++)
        {
            result[usageCode] = $"KEYCODE_{usageCode - 29}";
        }

        result[39] = "KEYCODE_0";
        result[40] = "KEYCODE_ENTER";
        result[41] = "KEYCODE_ESCAPE";
        result[42] = "KEYCODE_DEL";
        result[43] = "KEYCODE_TAB";
        result[44] = "KEYCODE_SPACE";
        result[45] = "KEYCODE_MINUS";
        result[46] = "KEYCODE_EQUALS";
        result[47] = "KEYCODE_LEFT_BRACKET";
        result[48] = "KEYCODE_RIGHT_BRACKET";
        result[49] = "KEYCODE_BACKSLASH";
        result[51] = "KEYCODE_SEMICOLON";
        result[52] = "KEYCODE_APOSTROPHE";
        result[53] = "KEYCODE_GRAVE";
        result[54] = "KEYCODE_COMMA";
        result[55] = "KEYCODE_PERIOD";
        result[56] = "KEYCODE_SLASH";
        result[57] = "KEYCODE_CAPS_LOCK";
        for (uint usageCode = 58; usageCode <= 69; usageCode++)
        {
            result[usageCode] = $"KEYCODE_F{usageCode - 57}";
        }

        result[70] = "KEYCODE_SYSRQ";
        result[71] = "KEYCODE_SCROLL_LOCK";
        result[72] = "KEYCODE_BREAK";
        result[73] = "KEYCODE_INSERT";
        result[74] = "KEYCODE_MOVE_HOME";
        result[75] = "KEYCODE_PAGE_UP";
        result[76] = "KEYCODE_FORWARD_DEL";
        result[77] = "KEYCODE_MOVE_END";
        result[78] = "KEYCODE_PAGE_DOWN";
        result[79] = "KEYCODE_DPAD_RIGHT";
        result[80] = "KEYCODE_DPAD_LEFT";
        result[81] = "KEYCODE_DPAD_DOWN";
        result[82] = "KEYCODE_DPAD_UP";
        result[83] = "KEYCODE_NUM_LOCK";
        result[84] = "KEYCODE_NUMPAD_DIVIDE";
        result[85] = "KEYCODE_NUMPAD_MULTIPLY";
        result[86] = "KEYCODE_NUMPAD_SUBTRACT";
        result[87] = "KEYCODE_NUMPAD_ADD";
        result[88] = "KEYCODE_NUMPAD_ENTER";
        for (uint usageCode = 89; usageCode <= 97; usageCode++)
        {
            result[usageCode] = $"KEYCODE_NUMPAD_{usageCode - 88}";
        }

        result[98] = "KEYCODE_NUMPAD_0";
        result[99] = "KEYCODE_NUMPAD_DOT";
        result[103] = "KEYCODE_NUMPAD_EQUALS";
        result[224] = "KEYCODE_CTRL_LEFT";
        result[225] = "KEYCODE_SHIFT_LEFT";
        result[226] = "KEYCODE_ALT_LEFT";
        result[227] = "KEYCODE_META_LEFT";
        result[228] = "KEYCODE_CTRL_RIGHT";
        result[229] = "KEYCODE_SHIFT_RIGHT";
        result[230] = "KEYCODE_ALT_RIGHT";
        result[231] = "KEYCODE_META_RIGHT";
        return result;
    }
}
