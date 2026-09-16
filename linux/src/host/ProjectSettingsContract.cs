using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

/// <summary>
/// Fixed-build, read-only project contract. Invoke after the D035 controls and
/// the SDK core-API callback, before loading the game assembly or any autoload.
/// RawFiles is the already audited D035 Linux x86-64 descriptor-read helper.
/// This class neither sets ProjectSettings nor changes InputMap.
/// </summary>
public static class ProjectSettingsContract
{
    public const string OriginalSha256 = "88bc2e4f6a2627204b20a714cb8746a269c009e48255dcfc7a0eaef9503d2227";
    public const int OriginalBytes = 24693;
    public const int OriginalFrameCount = 171;
    private const string AssemblyKey = "dotnet/project/assembly_name";
    private const string FeatureKey = "_custom_features";
    private const string SecondaryOverrideKey = "application/config/project_settings_override";
    private const string FeatureFrameHex = "0400000006000000646f746e65740000";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<string, string[]> ObjectProperties = new(StringComparer.Ordinal)
    {
        ["InputEventKey"] = new[] { "resource_local_to_scene", "resource_name", "device", "window_id",
            "alt_pressed", "shift_pressed", "ctrl_pressed", "meta_pressed", "pressed", "keycode",
            "physical_keycode", "key_label", "unicode", "location", "echo", "script" },
        ["InputEventJoypadMotion"] = new[] { "resource_local_to_scene", "resource_name", "device",
            "axis", "axis_value", "script" },
        ["InputEventJoypadButton"] = new[] { "resource_local_to_scene", "resource_name", "device",
            "button_index", "pressure", "pressed", "script" }
    };

    private sealed record N(int Type, uint Flags, object? Value);
    private sealed record Pair(N Key, N Value);
    private sealed record Property(string Name, N Value);
    private sealed record ObjectShape(string ClassName, List<Property> Properties);
    private sealed record Frame(int Index, string Key, int Offset, byte[] Bytes, N Shape);
    private sealed class Counts
    {
        public Dictionary<int, int> Types { get; } = new();
        public Dictionary<string, int> Objects { get; } = new(StringComparer.Ordinal);
        public int Nodes;
        public int ObjectProperties;
        public int NullScripts;
    }
    private sealed class Comparisons
    {
        public int Nodes;
        public int StringDictionaryKeys;
        public int ObjectPairs;
        public int SerializedProperties;
        public int ContainerHeaderChecks;
        public int ScriptNullNormalizations;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("ProjectSettingsContract: " + message);
    }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static void Bump<T>(Dictionary<T, int> counts, T key) where T : notnull
        => counts[key] = counts.TryGetValue(key, out int value) ? checked(value + 1) : 1;

    /// <summary>Returns only JSON-serializable CLR values; any failed check throws.</summary>
    public static object Verify(string originalProjectBinaryPath, string expectedBridgeAssembly,
        string observerAutoloadName, string observerScriptPath)
    {
        Require(Path.IsPathFullyQualified(originalProjectBinaryPath)
            && Path.GetFullPath(originalProjectBinaryPath) == originalProjectBinaryPath,
            "Original project path must be canonical and absolute");
        Require(SafeIdentifier(expectedBridgeAssembly) && SafeIdentifier(observerAutoloadName),
            "Bridge and observer names must be simple identifiers");
        Require(observerScriptPath.StartsWith("res://", StringComparison.Ordinal)
            && observerScriptPath.EndsWith(".cs", StringComparison.Ordinal)
            && observerScriptPath.Length <= 200 && !observerScriptPath.Contains("..", StringComparison.Ordinal)
            && observerScriptPath[6..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '/' or '.'),
            "Observer script must be a bounded canonical res:// C# path");
        string directory = Path.GetDirectoryName(originalProjectBinaryPath)
            ?? throw new InvalidDataException("Missing project directory");
        RawFiles.Result input = RawFiles.Inspect(directory, Path.GetFileName(originalProjectBinaryPath),
            OriginalBytes, null, true, true, true);
        Require(input.Stat.Size == OriginalBytes && input.Sha256 == OriginalSha256 && input.Bytes is not null,
            "Original project bytes/hash differ from the fixed, statically reviewed input");

        // All 171 frames, nested classes, serialized property names, and nil scripts
        // are parsed and rejected on any mismatch BEFORE object-enabled native decoding.
        Counts parsed = new();
        List<Frame> frames = ReadFrames(input.Bytes!, parsed);
        ValidateInventory(frames, parsed);
        string observerKey = "autoload/" + observerAutoloadName;
        Require(!frames.Any(f => f.Key == observerKey), "Observer collides with an original setting");

        // config_version is the native text-loader header, not a third setting.
        string overrideText = "config_version=5\n\n[dotnet]\n\nproject/assembly_name=\""
            + expectedBridgeAssembly + "\"\n\n[autoload]\n\n" + observerAutoloadName
            + "=\"*" + observerScriptPath + "\"\n";
        byte[] overrideBytes = StrictUtf8.GetBytes(overrideText);
        RawFiles.Result overrideFile = RawFiles.Inspect(directory, "override.cfg", 4096,
            null, true, true, true);
        Require(overrideFile.Bytes is not null && overrideFile.Bytes.AsSpan().SequenceEqual(overrideBytes),
            "override.cfg is not the exact version-5, two-setting contract");
        Require(string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("GODOT_EDITOR_CUSTOM_FEATURES")),
            "Additional custom features are supplied by GODOT_EDITOR_CUSTOM_FEATURES");

        List<string> allPropertyNames = PropertyNames(ProjectSettings.Singleton, 32768);
        string[] forbiddenOverrideKeys = allPropertyNames.Where(name =>
            name.StartsWith(SecondaryOverrideKey + ".", StringComparison.Ordinal)).ToArray();
        Require(forbiddenOverrideKeys.Length == 0, "A feature-specific second-layer override setting exists");
        Require(ProjectSettings.HasSetting(SecondaryOverrideKey), "Missing native second-layer override setting");
        using (Variant rawOverride = ProjectSettings.GetSetting(SecondaryOverrideKey))
            RequireString(rawOverride, "", SecondaryOverrideKey + " (raw)");
        using (Variant effectiveOverride = ProjectSettings.GetSettingWithOverride(SecondaryOverrideKey))
            RequireString(effectiveOverride, "", SecondaryOverrideKey + " (effective)");

        Comparisons comparison = new();
        var frameEvidence = new List<object>(OriginalFrameCount);
        int unchanged = 0, changed = 0, nativeConsumed = 0, decoded = 0;
        bool dotnetObserved = false;
        foreach (Frame frame in frames)
        {
            // The only object classes reachable here were whitelisted above; every
            // serialized script value was proved nil in the fixed original bytes.
            using Variant reference = GD.BytesToVarWithObjects(frame.Bytes.AsSpan());
            decoded++;
            Require((int)reference.VariantType == frame.Shape.Type, "Native frame type mismatch: " + frame.Key);
            string disposition;
            if (frame.Key == FeatureKey)
            {
                RequireString(reference, "dotnet", FeatureKey + " native reference");
                Require(!ProjectSettings.HasSetting(FeatureKey) && !allPropertyNames.Contains(FeatureKey, StringComparer.Ordinal),
                    "Native-consumed feature metadata unexpectedly appears as a public setting");
                dotnetObserved = OS.HasFeature("dotnet");
                Require(dotnetObserved, "OS.HasFeature(dotnet) is false");
                disposition = "native_consumed_feature_input_and_public_postcondition";
                nativeConsumed++;
            }
            else
            {
                Require(ProjectSettings.HasSetting(frame.Key), "Missing original setting: " + frame.Key);
                using Variant actual = ProjectSettings.GetSetting(frame.Key);
                if (frame.Key == AssemblyKey)
                {
                    RequireString(reference, "sts2", AssemblyKey + " original");
                    RequireString(actual, expectedBridgeAssembly, AssemblyKey + " permitted replacement");
                    disposition = "allowed_assembly_name_replacement";
                    changed++;
                }
                else
                {
                    Compare(frame.Shape, reference, actual, frame.Key, comparison, 0);
                    disposition = "typed_deep_original_value_preserved";
                    unchanged++;
                }
            }
            frameEvidence.Add(new { index = frame.Index, key = frame.Key, variantOffset = frame.Offset,
                variantBytes = frame.Bytes.Length, variantSha256 = Sha(frame.Bytes),
                serializedVariantType = frame.Shape.Type, disposition, checkedSuccessfully = true });
        }
        Require(decoded == 171 && unchanged == 169 && changed == 1 && nativeConsumed == 1,
            "Not all original frames were accounted for exactly once");
        Require(comparison.ObjectPairs == 49 && comparison.SerializedProperties == 406,
            "A serialized input object or property was not compared");
        Require(comparison.ContainerHeaderChecks == 236, "A container annotation check was omitted");
        Require(comparison.StringDictionaryKeys == 236, "A typed dictionary-key comparison was omitted");

        Frame[] originalAutoloads = frames.Where(f => f.Key.StartsWith("autoload/", StringComparison.Ordinal)).ToArray();
        string[] expectedAutoloadKeys = originalAutoloads.Select(f => f.Key).Append(observerKey).ToArray();
        string[] observedAutoloadOrder = allPropertyNames.Where(name => name.StartsWith("autoload/", StringComparison.Ordinal)).ToArray();
        Require(observedAutoloadOrder.Length == expectedAutoloadKeys.Length
            && observedAutoloadOrder.Distinct(StringComparer.Ordinal).Count() == expectedAutoloadKeys.Length
            && new HashSet<string>(expectedAutoloadKeys, StringComparer.Ordinal).SetEquals(observedAutoloadOrder),
            "Missing, duplicate, or extra autoload setting");
        Require(ProjectSettings.HasSetting(observerKey), "Observer autoload is absent");
        using (Variant observer = ProjectSettings.GetSetting(observerKey))
            RequireString(observer, "*" + observerScriptPath, observerKey);

        return new
        {
            schema = "e004b-original-project-settings-contract-v1", passed = true,
            originalProject = new { path = originalProjectBinaryPath, bytes = input.Stat.Size,
                sha256 = input.Sha256, ordinaryRead = FileEvidence(input) },
            counts = new { originalFrames = 171, nativeDecodedFrames = decoded, publicOriginalSettings = 170,
                unchangedPublicSettingsCompared = unchanged, allowedChangedOriginalSettings = changed,
                nativeConsumedMetadataFrames = nativeConsumed, addedObserverSettings = 1 },
            originalFrames = frameEvidence,
            allowedChanges = new { setting = AssemblyKey, originalValue = "sts2", replacementValue = expectedBridgeAssembly,
                addedObserverSetting = observerKey, addedObserverValue = "*" + observerScriptPath },
            customFeatures = new { frameIndex = 0, key = FeatureKey, originalValue = "dotnet",
                originalVariantHex = FeatureFrameHex, originalVariantSha256 = Sha(frames[0].Bytes),
                nativeConsumedOutsidePublicSettings = true, observedOsHasFeatureDotnet = dotnetObserved,
                editorCustomFeaturesEnvironmentEmpty = true, privateFeatureSetEnumerated = false,
                exactPrivateFeatureSetEqualityVerified = false,
                proofScope = "Fixed original feature bytes, exact two-key override, and empty editor feature environment; original PCK and exact argv binding are required from the outer runner" },
            autoloads = new { expectedSettingKeys = expectedAutoloadKeys,
                observedPropertyListOrder = observedAutoloadOrder, exactNameSet = true,
                originalValuesPreserved = true, observerValueVerified = true,
                orderScope = "Observed public GetPropertyList order only; does not prove native construction, EnterTree, or Ready order" },
            overrideChecks = new { path = Path.Combine(directory, "override.cfg"), bytes = overrideFile.Stat.Size,
                sha256 = overrideFile.Sha256, ordinaryRead = FileEvidence(overrideFile), exactUtf8LfBytes = true,
                configVersionHeader = 5, settingKeyCount = 2, secondaryRawPath = "", secondaryEffectivePath = "",
                secondaryFeatureOverrideKeys = forbiddenOverrideKeys },
            comparison = new { mechanism = "Native fixed-frame reference plus explicit recursive public Variant access",
                parsedVariantTypeCounts = parsed.Types, parsedObjectClassCounts = parsed.Objects,
                parsedNodes = parsed.Nodes, parsedSerializedObjectProperties = parsed.ObjectProperties,
                parsedNilScriptProperties = parsed.NullScripts, comparedNodes = comparison.Nodes,
                comparedStringDictionaryKeys = comparison.StringDictionaryKeys,
                comparedObjectPairs = comparison.ObjectPairs, comparedSerializedObjectProperties = comparison.SerializedProperties,
                checkedUntypedContainers = comparison.ContainerHeaderChecks,
                nativeScriptNullRepresentationNormalizations = comparison.ScriptNullNormalizations,
                dictionaryOrder = "Ignored; unique String key type, ordinal key set, value type, and recursive value all match",
                arrayOrder = "Exact index and count",
                floatingPoint = "IEEE-754 bit equality: double for Variant.Float; float per Vector2/Color component; no tolerance",
                objects = "Whitelisted native reference class and all 406 serialized properties; reference identity is never used",
                objectSetterSemantics = "Native decode applies the original serialized setters; live getters are compared to those reference getters",
                containerAnnotations = "After bounded deep comparison, public GD.VarToBytes header must encode an untyped container; object IDs are not compared or recorded" },
            limitations = new[] {
                "Must be called after D035 controls and SDK core-API initialization, before game assembly, autoloads, and main scene; caller proves placement.",
                "This module verifies a fixed original project.binary copy. Outer runner must prove it is the project.binary in the fixed original main PCK and enforce the exact authorized argv.",
                "_custom_features is native-consumed metadata. OS.HasFeature(dotnet) is a public postcondition, not enumeration or exact equality proof of the private custom feature set.",
                "GetPropertyList includes native defaults; 171 is the original frame count, not the public property-list length.",
                "Autoload property-list order is observation only. Construction and tree-entry order remain for the normal-boot observer.",
                "Read-only means no live settings, InputMap, game objects, or files are modified. Native reference decoding creates 49 temporary, script-free built-in InputEvent objects.",
                "Source/static QA alone does not establish compilation, runtime success, game behavior, or training fidelity."
            }
        };
    }

    private static bool SafeIdentifier(string text) => text.Length is > 0 and <= 100
        && (char.IsAsciiLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static object FileEvidence(RawFiles.Result file) => new { device = file.Stat.Device,
        inode = file.Stat.Inode, links = file.Stat.Links, mode = file.Stat.Mode, owner = file.Stat.UserId,
        file.Stat.Size, noFollow = true, boundedDescriptorRead = true, beforeAfterStatEqual = true,
        noWriteBits = true, ownerIsCurrentEuid = true };
    private static void RequireString(Variant value, string expected, string path) => Require(
        value.VariantType == Variant.Type.String && string.Equals(value.AsString(), expected, StringComparison.Ordinal),
        "String type/value mismatch: " + path);

    private static List<string> PropertyNames(GodotObject value, int maximum)
    {
        Godot.Collections.Array<GDictionary> properties = value.GetPropertyList();
        // Array<T> itself does not implement IDisposable in this fixed SDK. Its
        // public explicit conversion returns the owned untyped backing wrapper.
        using GArray propertyStorage = (GArray)properties;
        Require(properties.Count <= maximum, "Property list exceeds bound");
        var names = new List<string>(properties.Count);
        for (int i = 0; i < properties.Count; i++)
        {
            using GDictionary row = properties[i];
            Require(row.ContainsKey("name"), "Property-list row lacks name");
            using Variant name = row["name"];
            Require(name.VariantType is Variant.Type.String or Variant.Type.StringName,
                "Property-list metadata name has an unexpected type");
            names.Add(name.AsString());
        }
        return names;
    }

    private static void Compare(N shape, Variant reference, Variant actual, string path, Comparisons counts, int depth)
    {
        Require(depth <= 16 && ++counts.Nodes <= 4096, "Comparison exceeds fixed graph bounds: " + path);
        Require(reference.VariantType == actual.VariantType, "Live/reference Variant type differs: " + path);
        Require((int)reference.VariantType == shape.Type, "Native reference shape differs: " + path);
        switch (shape.Type)
        {
            case 0: return;
            case 1: Require(reference.AsBool() == actual.AsBool(), "Bool differs: " + path); return;
            case 2: Require(reference.AsInt64() == actual.AsInt64(), "Int differs: " + path); return;
            case 3:
                Require(BitConverter.DoubleToInt64Bits(reference.AsDouble()) == BitConverter.DoubleToInt64Bits(actual.AsDouble()),
                    "Float bits differ: " + path); return;
            case 4:
                Require(string.Equals(reference.AsString(), actual.AsString(), StringComparison.Ordinal), "String differs: " + path); return;
            case 5:
                Vector2 rv = reference.AsVector2(), av = actual.AsVector2();
                Require(FloatBitsEqual(rv.X, av.X) && FloatBitsEqual(rv.Y, av.Y), "Vector2 bits differ: " + path); return;
            case 20:
                Color rc = reference.AsColor(), ac = actual.AsColor();
                Require(FloatBitsEqual(rc.R, ac.R) && FloatBitsEqual(rc.G, ac.G)
                    && FloatBitsEqual(rc.B, ac.B) && FloatBitsEqual(rc.A, ac.A), "Color bits differ: " + path); return;
            case 34:
                Require(reference.AsStringArray().SequenceEqual(actual.AsStringArray(), StringComparer.Ordinal),
                    "PackedStringArray differs: " + path); return;
            case 28:
                using (GArray r = reference.AsGodotArray())
                using (GArray a = actual.AsGodotArray())
                {
                    var elements = (List<N>)shape.Value!;
                    Require(r.Count == elements.Count && a.Count == elements.Count, "Array count differs: " + path);
                    for (int i = 0; i < elements.Count; i++)
                    {
                        using Variant re = r[i]; using Variant ae = a[i];
                        Compare(elements[i], re, ae, path + "[" + i + "]", counts, depth + 1);
                    }
                }
                CheckUntypedHeader(actual, shape.Type, path); counts.ContainerHeaderChecks++; return;
            case 27:
                using (GDictionary r = reference.AsGodotDictionary())
                using (GDictionary a = actual.AsGodotDictionary())
                {
                    var pairs = (List<Pair>)shape.Value!;
                    string[] keys = pairs.Select(p => (string)p.Key.Value!).ToArray();
                    CheckStringKeys(r, keys, path + " reference"); CheckStringKeys(a, keys, path + " live");
                    counts.StringDictionaryKeys += keys.Length;
                    foreach (Pair pair in pairs)
                    {
                        string name = (string)pair.Key.Value!;
                        using Variant key = name;
                        using Variant re = r[key]; using Variant ae = a[key];
                        Compare(pair.Value, re, ae, path + "[" + name + "]", counts, depth + 1);
                    }
                }
                CheckUntypedHeader(actual, shape.Type, path); counts.ContainerHeaderChecks++; return;
            case 24:
                CompareObject((ObjectShape)shape.Value!, reference, actual, path, counts, depth); return;
            default: throw new InvalidDataException("Unsupported live Variant type at " + path);
        }
    }
    private static bool FloatBitsEqual(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static void CheckStringKeys(GDictionary dictionary, string[] expected, string path)
    {
        Require(dictionary.Count == expected.Length, "Dictionary count differs: " + path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<Variant, Variant> pair in dictionary)
        {
            using Variant key = pair.Key; using Variant value = pair.Value;
            Require(key.VariantType == Variant.Type.String && names.Add(key.AsString()),
                "Dictionary key type or uniqueness differs: " + path);
        }
        Require(names.SetEquals(expected), "Dictionary key set differs: " + path);
    }
    private static void CheckUntypedHeader(Variant value, int expectedType, string path)
    {
        // Run only after the entire graph has matched the bounded, script-free shape.
        // WithObjects is deliberately absent: encode only native object IDs and discard
        // the buffer after inspecting its container header. No identity value is evidence.
        byte[] encoded = GD.VarToBytes(value);
        Require(encoded.Length is >= 8 and <= 65536 && BinaryPrimitives.ReadUInt32LittleEndian(encoded) == (uint)expectedType,
            "Container has an unexpected native type annotation/header: " + path);
    }
    private static void CompareObject(ObjectShape shape, Variant reference, Variant actual, string path, Comparisons counts, int depth)
    {
        GodotObject? r = reference.AsGodotObject(), a = actual.AsGodotObject();
        Require(r is not null && a is not null && r.GetClass() == shape.ClassName && a.GetClass() == shape.ClassName,
            "Input object class differs: " + path);
        Require(ObjectProperties.ContainsKey(shape.ClassName), "Input object class is not whitelisted: " + path);
        // Check the bound native script getter before requesting a property list or
        // any serialized value, so an unexpected attached script is rejected first.
        using Variant referenceScript = r!.GetScript(); using Variant actualScript = a!.GetScript();
        Require(IsNull(referenceScript) && IsNull(actualScript)
            && referenceScript.VariantType == actualScript.VariantType,
            "Input event acquired a script or changed null representation: " + path);
        List<string> rn = PropertyNames(r!, 512), an = PropertyNames(a!, 512);
        counts.ObjectPairs++;
        foreach (Property property in shape.Properties)
        {
            Require(rn.Count(n => n == property.Name) == 1 && an.Count(n => n == property.Name) == 1,
                "Serialized object property is missing or ambiguous: " + path + "." + property.Name);
            if (property.Name == "script")
            {
                // GetScript may represent a null script as Object(null), while
                // serialization deliberately wrote Nil. Require the native reference's
                // exact public getter type and nullness on the live object as well.
                Require(property.Value.Type == 0, "Original serialized script was not Nil: " + path);
                if (referenceScript.VariantType != Variant.Type.Nil) counts.ScriptNullNormalizations++;
                counts.Nodes++;
            }
            else
            {
                using Variant rp = r!.Get(property.Name); using Variant ap = a!.Get(property.Name);
                Compare(property.Value, rp, ap, path + "." + property.Name, counts, depth + 1);
            }
            counts.SerializedProperties++;
        }
    }
    private static bool IsNull(Variant value) => value.VariantType == Variant.Type.Nil
        || (value.VariantType == Variant.Type.Object && value.AsGodotObject() is null);

    private static List<Frame> ReadFrames(byte[] bytes, Counts counts)
    {
        Reader reader = new(bytes);
        Require(Encoding.ASCII.GetString(reader.Take(4)) == "ECFG", "Missing ECFG signature");
        Require(reader.Count(171) == 171, "Original frame count differs");
        var frames = new List<Frame>(171); var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 171; i++)
        {
            string name = reader.Text(false, false);
            Require(name.Length is > 0 and <= 512 && names.Add(name), "Empty, duplicate, or oversized original key");
            int length = reader.Count(65536), offset = reader.Position;
            Require(length > 0 && length % 4 == 0, "Invalid original Variant frame length");
            byte[] raw = reader.Take(length); Reader variant = new(raw);
            N shape = ReadNode(variant, counts, 0);
            Require(variant.Remaining == 0, "Unconsumed Variant frame bytes: " + name);
            frames.Add(new Frame(i, name, offset, raw, shape));
        }
        Require(reader.Remaining == 0, "Trailing project.binary bytes");
        return frames;
    }
    private static N ReadNode(Reader reader, Counts counts, int depth)
    {
        Require(depth <= 16 && ++counts.Nodes <= 4096, "Original Variant graph exceeds bounds");
        uint header = reader.U32(); int type = (int)(header & 255); uint flags = header & ~255u;
        Require(flags == 0 || (type == 3 && flags == 0x10000),
            "Unsupported type annotation, object ID, or data flags in original graph");
        Bump(counts.Types, type);
        object? value;
        switch (type)
        {
            case 0: value = null; break;
            case 1:
                uint boolean = reader.U32(); Require(boolean <= 1, "Noncanonical original Boolean"); value = boolean != 0; break;
            case 2: value = (long)unchecked((int)reader.U32()); break;
            case 3:
                double number = flags == 0 ? reader.F32() : reader.F64();
                Require(double.IsFinite(number), "Nonfinite original float"); value = number; break;
            case 4: value = reader.Text(true, false); break;
            case 5: value = new[] { reader.F32(), reader.F32() }; break;
            case 20: value = new[] { reader.F32(), reader.F32(), reader.F32(), reader.F32() }; break;
            case 34:
                int stringCount = reader.Count(1024); string[] strings = new string[stringCount];
                for (int i = 0; i < stringCount; i++) strings[i] = reader.Text(true, true);
                value = strings; break;
            case 28:
                int elementCount = reader.Count(1024); var elements = new List<N>(elementCount);
                for (int i = 0; i < elementCount; i++) elements.Add(ReadNode(reader, counts, depth + 1));
                value = elements; break;
            case 27:
                int pairCount = reader.Count(1024); var pairs = new List<Pair>(pairCount);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < pairCount; i++)
                {
                    N key = ReadNode(reader, counts, depth + 1);
                    Require(key.Type == 4 && keys.Add((string)key.Value!), "Original dictionary key is non-String or duplicate");
                    pairs.Add(new Pair(key, ReadNode(reader, counts, depth + 1)));
                }
                value = pairs; break;
            case 24:
                string className = reader.Text(true, false);
                Require(ObjectProperties.TryGetValue(className, out string[]? allowed), "Original object class is outside the fixed InputEvent whitelist");
                int propertyCount = reader.Count(64);
                Require(propertyCount == allowed!.Length, "Original object serialized property count differs");
                var properties = new List<Property>(propertyCount);
                for (int i = 0; i < propertyCount; i++)
                {
                    string name = reader.Text(true, false);
                    Require(name == allowed[i], "Original object serialized property schema/order differs");
                    N property = ReadNode(reader, counts, depth + 1);
                    if (name == "script")
                    {
                        Require(property.Type == 0, "Original serialized object contains a script");
                        counts.NullScripts++;
                    }
                    properties.Add(new Property(name, property)); counts.ObjectProperties++;
                }
                Bump(counts.Objects, className); value = new ObjectShape(className, properties); break;
            default: throw new InvalidDataException("Unsupported original Variant type " + type);
        }
        return new N(type, flags, value);
    }
    private static void ValidateInventory(List<Frame> frames, Counts counts)
    {
        Require(frames.Count == 171 && frames[0].Key == FeatureKey && frames[0].Shape.Type == 4
            && (string)frames[0].Shape.Value! == "dotnet" && Hex(frames[0].Bytes) == FeatureFrameHex,
            "Original native-consumed feature frame differs");
        Require(frames.Single(f => f.Key == AssemblyKey).Shape.Value is string originalAssembly && originalAssembly == "sts2",
            "Original assembly-name frame differs");
        Require(frames.Count(f => f.Key.StartsWith("autoload/", StringComparison.Ordinal)) == 2
            && frames[13].Key == "autoload/SentryBootstrap" && frames[14].Key == "autoload/FmodManager",
            "Original autoload frames/order differ");
        Require(frames.Count(f => f.Key.StartsWith("input/", StringComparison.Ordinal) && f.Shape.Type == 27) == 118,
            "Original input action dictionary count differs");
        var expectedTypes = new Dictionary<int, int> { [0] = 49, [1] = 144, [2] = 154, [3] = 158,
            [4] = 303, [5] = 1, [20] = 2, [24] = 49, [27] = 118, [28] = 118, [34] = 2 };
        Require(counts.Types.Count == expectedTypes.Count && expectedTypes.All(p => counts.Types.GetValueOrDefault(p.Key) == p.Value),
            "Original nested Variant inventory differs");
        Require(counts.Objects.Count == 3 && counts.Objects.GetValueOrDefault("InputEventKey") == 9
            && counts.Objects.GetValueOrDefault("InputEventJoypadMotion") == 18
            && counts.Objects.GetValueOrDefault("InputEventJoypadButton") == 22
            && counts.ObjectProperties == 406 && counts.NullScripts == 49 && counts.Nodes == 1098,
            "Original object/property/script inventory differs");
    }

    private sealed class Reader
    {
        private readonly byte[] bytes;
        public int Position { get; private set; }
        public int Remaining => bytes.Length - Position;
        public Reader(byte[] data) => bytes = data;
        public byte[] Take(int count)
        {
            Require(count >= 0 && count <= Remaining, "Truncated bounded byte input");
            byte[] part = bytes.AsSpan(Position, count).ToArray(); Position += count; return part;
        }
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int Count(int maximum)
        {
            uint count = U32(); Require(count <= maximum, "Encoded count exceeds the fixed bound"); return (int)count;
        }
        public float F32()
        {
            float value = BitConverter.Int32BitsToSingle(unchecked((int)U32()));
            Require(float.IsFinite(value), "Nonfinite original component"); return value;
        }
        public double F64() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));
        public string Text(bool padded, bool allowTerminalNul)
        {
            int count = Count(65536); byte[] data = Take(count); int textBytes = count;
            if (allowTerminalNul && count > 0 && data[^1] == 0) textBytes--;
            string text = StrictUtf8.GetString(data, 0, textBytes);
            Require(!text.Contains('\0'), "Embedded NUL in original text");
            if (padded)
            {
                int pad = (4 - count % 4) % 4;
                Require(Take(pad).All(b => b == 0), "Nonzero original string padding");
            }
            return text;
        }
    }
}
