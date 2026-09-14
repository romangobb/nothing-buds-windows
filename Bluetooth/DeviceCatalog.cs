// Full Nothing/CMF audio-device matrix, mined from the official app:
//   - identity: bluetoothName -> modelId (devices_info_list.json deviceSpu)
//   - capabilities: ear_white_list.json per-model configs
//     (ultraBass, earTipFitTest, earDetection, personalizedAnc, ancLevel...)
//   - images: res/drawable renders bundled under Assets/Buds
// Unknown/stale names fall back to a generic TWS profile; the RFCOMM
// handshake (NothingProbe) remains the real gatekeeper.

namespace NothingBuds.Bluetooth;

public enum EqHint { Legacy, Listening }

public sealed record DeviceProfile(
    string ModelId,
    string MarketingName,
    string[] BtNames,
    bool HasAnc,
    bool HasInEar,
    bool HasBass,
    bool HasEarFit,
    bool HasPersonalAnc,
    EqHint Eq,
    string? ImagePrefix,      // e.g. "espeon" -> {prefix}_{color}_{left|right}.png
    string[] Colors,          // display names; token = lowercase, spaces -> _
    bool SingleImage)         // neckband/headphones: one product shot per color
{
    public string ColorToken(string color) =>
        color.ToLowerInvariant().Replace(' ', '_');
}

public static class DeviceCatalog
{
    public static readonly DeviceProfile Generic = new(
        "?", "Nothing earbuds", [], true, true, true, true, false,
        EqHint.Listening, null, [], false);

    private static readonly DeviceProfile[] All =
    {
        new("B181", "Nothing Ear (1)", new[]{"Nothing ear (1)"},
            true, true, false, false, false, EqHint.Legacy,
            "ear_one", new[]{"Black","White"}, false),
        new("B157", "Nothing Ear (stick)", new[]{"Ear (Stick)"},
            false, true, false, false, false, EqHint.Legacy,
            "ear_stick", [], false),
        new("B155", "Nothing Ear (2)", new[]{"Ear (2)"},
            true, true, false, true, true, EqHint.Legacy,
            "ear_two", new[]{"Black","White"}, false),
        // B183 shares the "Nothing Ear (a)" name (union profile keeps bass).
        new("B162", "Nothing Ear (a)", new[]{"Nothing Ear (a)"},
            true, true, true, true, false, EqHint.Legacy,
            "ear_color", new[]{"Black","White","Yellow"}, false),
        new("B163", "CMF Buds Pro", new[]{"Buds Pro"},
            true, true, false, false, false, EqHint.Legacy,
            "ear_corsola", new[]{"Black","Orange","White"}, false),
        new("B164", "CMF Neckband Pro", new[]{"Neckband Pro"},
            true, false, false, false, false, EqHint.Legacy,
            "crobat", new[]{"Black","White"}, true),
        new("B168", "CMF Buds", new[]{"CMF Buds"},
            true, true, true, false, false, EqHint.Listening,
            "donphan", new[]{"Black","White","Orange"}, false),
        new("B171", "Nothing Ear", new[]{"Nothing Ear"},
            true, true, true, true, false, EqHint.Legacy,
            "ear_twos", new[]{"Black","White"}, false),
        // B187 shares name + profile with B172.
        new("B172", "CMF Buds Pro 2", new[]{"CMF Buds Pro 2"},
            true, true, true, true, false, EqHint.Listening,
            "espeon", new[]{"Black","White","Orange","Blue"}, false),
        new("B174", "Nothing Ear (open)", new[]{"Nothing Ear (open)"},
            false, false, false, false, false, EqHint.Listening,
            "flaffy", new[]{"White"}, false),
        new("B179", "CMF Buds 2", new[]{"CMF Buds 2"},
            true, true, true, true, false, EqHint.Listening,
            "girafarig", new[]{"Black","Green","Orange"}, false),
        new("B184", "CMF Buds 2 Plus", new[]{"CMF Buds 2 Plus"},
            true, true, true, true, false, EqHint.Listening,
            "gligar", new[]{"Blue","White"}, false),
        new("B185", "CMF Buds 2a", new[]{"CMF Buds 2a"},
            true, false, true, false, false, EqHint.Listening,
            "hoothoot", new[]{"Black","White","Orange"}, false),
        new("B173", "Nothing Ear (3)", new[]{"Nothing Ear (3)"},
            true, true, true, true, false, EqHint.Listening,
            null, [], false),
        new("B190", "Nothing Ear (3a)", new[]{"Nothing Ear (3a)"},
            true, true, false, true, false, EqHint.Listening,
            null, [], false),
        new("B193", "CMF Buds Neo", new[]{"CMF Buds Neo","Buds Neo"},
            true, false, true, false, false, EqHint.Listening,
            null, [], false),
        new("B170", "Nothing Headphone (1)", new[]{"Nothing Headphone (1)"},
            true, false, false, false, false, EqHint.Listening,
            null, [], true),
        new("B175", "CMF Headphone Pro", new[]{"CMF Headphone Pro"},
            true, false, false, false, false, EqHint.Listening,
            "forretress", new[]{"Dark grey","Light green","Light grey"}, true),
        // B198 shares name + profile with B186.
        new("B186", "Nothing Headphone (a)", new[]{"Nothing Headphone (a)"},
            true, false, true, false, false, EqHint.Listening,
            "elekid", new[]{"Black","Grey"}, true),
        new("B189", "CMF Clip Pro", new[]{"CMF Clip Pro"},
            false, false, true, false, false, EqHint.Listening,
            null, [], false),
    };

    private static string Norm(string s) =>
        string.Join(" ", s.Trim().ToLowerInvariant().Split(
            (char[])null!, StringSplitOptions.RemoveEmptyEntries));

    public static DeviceProfile Resolve(string? friendlyName)
    {
        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            string n = Norm(friendlyName);
            foreach (var p in All)
                foreach (var b in p.BtNames)
                    if (Norm(b) == n) return p;
        }
        return Generic;
    }

    public static DeviceProfile ByModel(string modelId) =>
        All.FirstOrDefault(p => p.ModelId == modelId) ?? Generic;
}
