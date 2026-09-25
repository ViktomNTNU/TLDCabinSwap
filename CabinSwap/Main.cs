// CabinSwap - replaces the Cannery Worker Residences house in Bleak Inlet with the Mindful Cabin from Forsaken Airfield.
//
// Mods/CabinSwap/:
//   mindfulcabin.bundle - the cabin's looks and collision; everything inside keeps the game's script names
//   originals.json      - the original Mindful Cabin interior's settings, recorded once in Forsaken Airfield
//
// When Bleak Inlet loads, the mod:
//   1. places the cabin and hides the old house
//   2. gives the cabin the game's own materials and shaders
//   3. writes the original settings into everything inside (doors, cabinets, stove, bed, trunk, decorations)
//   4. adds Bleak Inlet's indoor-space and snow-blocking volumes
//   5. turns the water tank into a 20 L water storage
//   6. levels the ground and removes the grass under the cabin
//   7. moves, copies and replaces the Bleak Inlet objects listed in MovedObjects, CopiedObjects and ReplacedObjects

using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Reflection;
using MelonLoader.Utils;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(CabinSwap.CabinSwapMod), "CabinSwap", "2.0", "KanarieWilfried")]
[assembly: MelonGame("Hinterland", "TheLongDark")]
// Runs before other mods (default 0), so the cabin exists when e.g. Safehouse Customization Plus scans the scene
[assembly: MelonPriority(-100)]

namespace CabinSwap
{
    public class CabinSwapMod : MelonMod
    {

        //  SETTINGS

        // The cabin
        static readonly Vector3 CabinPosition = new Vector3(172.4801f, 30.14f, -432.73f);
        static readonly Vector3 CabinRotation = new Vector3(0f, 353.0534f, 0f);
        const string OldHousePath = "Art/Structures/Houses/STR_WoodCabinA_Prefab";

        // Bleak Inlet objects to move
        // path = the object's full path in UnityExplorer;
        // position, rotation and scale = the values from unity explorer (Position, Rotation, Scale) after you've placed it there.
        // Objects in Bleak Inlet's sub-scenes (e.g. CanneryRegion_SANDBOX) work too.
        // Several objects with the same path? Two ways to pick one:
        //  (moosemeat method, thank you bro <3) "[n]" after a name: the child at position n under its parent, counting ALL children from 0 in UnityExplorer's order
        //   "@x,y,z" at the end: the one whose ORIGINAL position (before any moving) is there
        static readonly (string path, Vector3 position, Vector3 rotation, Vector3 scale)[] MovedObjects =
        {
            ("Art/Trees/TRN_PineTreeLog_SingleC1_Prefab (3)",
                new Vector3(200.6389f, 20.62f, -395.8405f), new Vector3(299.9551f, 335.9424f, 312.673f), new Vector3(1f, 1f, 1f)),
        };

        // path = the object to hide; copyFrom = the object to copy; then the copy's position, rotation, scale.
        static readonly (string path, string copyFrom, Vector3 position, Vector3 rotation, Vector3 scale)[] ReplacedObjects =
        {
            ("Art/Docks/OBJ_DockShortEndCapB_Prefab@166.9531,24.8526,-451.0543",
                "Art/Structures/LighthouseIsland/OBJ_DockShortEndCapB_Prefab",
                new Vector3(166.9531f, 24.7526f, -450.7544f), new Vector3(337.9621f, 0f, 19.9666f), new Vector3(1f, 1f, 1f)),
        };

        // Copies of Bleak Inlet objects: the path of the object to copy, and where the copy goes.
        // Same values as above. The same object can be copied several times (one line per copy).
        static readonly (string path, Vector3 position, Vector3 rotation, Vector3 scale)[] CopiedObjects =
        {
            ("Art/Structures/LighthouseIsland/OBJ_DockSteps_Prefab (1)",
                new Vector3(129.8f, 28.38f, -434.24f), new Vector3(359.8419f, 172.5264f, 345.3203f), new Vector3(1f, 1f, 1.05f)),
            ("Art/Structures/LighthouseIsland/OBJ_DockSteps_Prefab (1)",
                new Vector3(127.55f, 26.9255f, -434.54f), new Vector3(359.8419f, 172.5264f, 345.3203f), new Vector3(1f, 1f, 1.05f)),
            ("Art/Structures/LighthouseIsland/OBJ_DockSteps_Prefab (1)",
                new Vector3(125.3f, 25.471f, -434.84f), new Vector3(359.8419f, 172.5264f, 345.3203f), new Vector3(1f, 1f, 1.05f)),
        };

        // Files in Mods/CabinSwap/
        const string ModFolder = "CabinSwap";
        const string BundleFile = "mindfulcabin.bundle";
        const string PrefabName = "MindfulCabin";
        const string OriginalsFile = "originals.json";
        const string DummyPrefix = "CabinSwapDummy/";

        // Shelter volumes: marker in the cabin -> Bleak Inlet object that has the game logic
        static readonly (string marker, string bleakInletSource)[] ShelterVolumes =
        {
            ("IndoorSpace", "IndoorSpace"),
            ("FallingSnowParticleKiller", "ParticleKiller"),
        };

        // Ground and grass under the cabin (metres from the cabin's centre; X = length, Z = width)
        static readonly Vector2 FlatMin = new Vector2(-6.3f, -3.5f);
        static readonly Vector2 FlatMax = new Vector2(6.3f, 3.2f);
        const float FlatMargin = 0.5f;          // extra level ground around the cabin
        const float FalloffDistance = 3f;       // how far the ground slopes back to its original shape
        const float GroundBelowFloor = 0.05f;   // the ground ends up this far below the cabin floor
        const float GrassMargin = 0.3f;         // grass is cleared this far beyond the roof edges
        static readonly string[] GrassObjectWords = { "grass", "weed", "reed", "fern", "plant", "shrub" };

        // Water storage
        const float WaterCapacityLiters = 20f;
        const float WaterStartLiters = 0f;      // how full the tank is the first time you find it
        const bool SwapWaterButtons = true;     // Take on the game's "AltFire" hook, Store on "Interact"
        const string TakeButtonLabel = "Take Water";
        const string StoreButtonLabel = "Store Water";
        const string TakeWord = "Take";         // replaces "Transfer" in the game's water menu
        const string StoreWord = "Store";
        const string WaterNameKey = "GAMEPLAY_WaterStorage";
        const string NoWaterInStorageMessage = "No water in storage";
        const string NoWaterInInventoryMessage = "No water in inventory";
        const string StorageFullMessage = "Water storage full";
        const string TakeSound = "Play_WaterCollectionToilet";
        const string StoreSound = "Play_WaterCollectionToilet";
        const float SoundBaseSeconds = 0.8f;    // the sound loops: it plays for base + per-liter seconds,
        const float SoundSecondsPerLiter = 0.3f; // within the min/max, then fades out
        const float SoundMinSeconds = 1f;
        const float SoundMaxSeconds = 4f;
        const int SoundFadeOutMs = 300;


        //  CABIN

        const string RootName = "CabinSwap_MindfulCabin";
        static AssetBundle _bundle;
        static Il2CppSystem.IO.MemoryStream _bundleStream;       // must stay alive while the bundle is loaded
        static GameObject _cabinPrefab;

        static string ModFile(string name) => Path.Combine(MelonEnvironment.ModsDirectory, ModFolder, name);

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName == "MainMenu")
            {
                // Another save may be loaded next: read the tank's amount from that save
                _waterLoaded = false;
                _storedUnits = 0;
            }

        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!sceneName.StartsWith("CanneryRegion")) return;

            // The cabin is built here, once Bleak Inlet's main scene has been set up: the same moment other mods look through the scene (for Safehouse Customization Plus compatibility)
            if (sceneName == "CanneryRegion")
                SwapCabin();

            Scene scene = SceneManager.GetSceneByName(sceneName);
            MoveObjects(scene);
            CopyObjects(scene);
            ReplaceObjects(scene);
        }


        static void MoveObjects(Scene scene)
        {
            foreach (var (path, position, rotation, scale) in MovedObjects)
            {
                Transform t = FindInScene(scene, path);
                if (t == null) continue;                                 // not in this one of the region's scenes
                t.SetPositionAndRotation(position, Quaternion.Euler(rotation));
                t.localScale = scale;
            }
        }

        static void CopyObjects(Scene scene)
        {
            for (int i = 0; i < CopiedObjects.Length; i++)
            {
                var (path, position, rotation, scale) = CopiedObjects[i];
                Transform source = FindInScene(scene, path);
                if (source == null) continue;                            // not in this one of the region's scenes
                PlaceCopy(source, scene, position, rotation, scale, $"{source.name} (CabinSwap copy {i})");
            }
        }

        static void ReplaceObjects(Scene scene)
        {
            foreach (var (path, copyFrom, position, rotation, scale) in ReplacedObjects)
            {
                Transform original = FindInScene(scene, path);
                Transform source = FindInScene(scene, copyFrom);
                if (original == null || source == null) continue;        // not in this one of the region's scenes

                PlaceCopy(source, scene, position, rotation, scale, original.name + " (CabinSwap)");
                original.gameObject.SetActive(false);                    // also takes it out of the combined mesh
            }
        }

        static void PlaceCopy(Transform source, Scene scene, Vector3 position, Vector3 rotation, Vector3 scale, string name)
        {
            // Made under a switched-off holder, so the copy only switches on once it's in place
            GameObject holder = new GameObject("CabinSwap_CopyHolder");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, scene);
            GameObject copy = GameObject.Instantiate<GameObject>(source.gameObject, holder.transform, false);
            copy.name = name;

            // Placed first, then moved next to the original (keeping its place) - that switches it on
            copy.transform.SetPositionAndRotation(position, Quaternion.Euler(rotation));
            copy.transform.SetParent(source.parent, true);
            copy.transform.localScale = scale;
            GameObject.Destroy(holder);
        }

        const float NearTolerance = 0.5f;                               // metres, for "@x,y,z"

        // Every object in the scene that fits the path - following ALL branches, since parents can share a name too - then the one at "@x,y,z" if given, otherwise the first.
        static Transform FindInScene(Scene scene, string path)
        {
            Vector3? near = null;
            int at = path.LastIndexOf('@');
            if (at > 0)
            {
                string[] xyz = path.Substring(at + 1).Split(',');
                near = new Vector3(ParseFloat(xyz[0]), ParseFloat(xyz[1]), ParseFloat(xyz[2]));
                path = path.Substring(0, at);
            }

            var current = new List<Transform>();
            bool first = true;
            foreach (string segment in path.Split('/'))
            {
                (string name, int index) = SplitIndex(segment);
                var next = new List<Transform>();

                if (first)                                               // the first name: one of the scene's top objects
                {
                    GameObject[] roots = scene.GetRootGameObjects();
                    for (int i = 0; i < roots.Length; i++)
                        if (roots[i].name == name && (index < 0 || index == i)) next.Add(roots[i].transform);
                    first = false;
                }
                else
                {
                    foreach (Transform parent in current)
                        for (int i = 0; i < parent.childCount; i++)
                        {
                            Transform child = parent.GetChild(i);
                            if (child.name == name && (index < 0 || index == i)) next.Add(child);
                        }
                }

                if (next.Count == 0) return null;
                current = next;
            }

            if (near == null) return current[0];

            Transform best = null;
            float bestDistance = NearTolerance;
            foreach (Transform t in current)
            {
                float d = Vector3.Distance(t.position, near.Value);
                if (d <= bestDistance) { best = t; bestDistance = d; }
            }
            return best;
        }

        static float ParseFloat(string s) => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture);

        // "OBJ_DockShortEndCapB_Prefab[3]" -> ("OBJ_DockShortEndCapB_Prefab", 3); no "[n]" -> (name, -1)
        static (string name, int index) SplitIndex(string segment)
        {
            int open = segment.LastIndexOf('[');
            if (open > 0 && segment.EndsWith("]") &&
                int.TryParse(segment.Substring(open + 1, segment.Length - open - 2), out int index) && index >= 0)
                return (segment.Substring(0, open), index);
            return (segment, -1);
        }

        void SwapCabin()
        {
            // Everything is built while switched off, so the game's scripts start with their final settings
            GameObject root = new GameObject(RootName);
            root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, SceneManager.GetSceneByName("CanneryRegion"));
            root.transform.SetPositionAndRotation(CabinPosition, Quaternion.Euler(CabinRotation));

            GameObject cabin = GameObject.Instantiate<GameObject>(LoadCabinPrefab(), root.transform, false);
            cabin.name = PrefabName;

            FixMaterials(cabin);
            ApplyOriginalSettings(cabin.transform);
            SetupWaterStorage(cabin.transform);
            AddShelterVolumes(cabin.transform);
            root.SetActive(true);

            GameObject oldHouse = GameObject.Find(OldHousePath);
            if (oldHouse != null) oldHouse.SetActive(false);

            FlattenGroundUnder(root.transform);
            ClearGrassUnder(root.transform);
        }

        static GameObject LoadCabinPrefab()
        {
            // The bundle is loaded once per game session: Unity refuses to load the same bundle twice.
            if (_bundle == null)
            {
                _bundleStream = new Il2CppSystem.IO.MemoryStream(File.ReadAllBytes(ModFile(BundleFile)));
                _bundle = AssetBundle.LoadFromStream(_bundleStream);
            }

            // The prefab is fetched again whenever the game has cleared it from memory (e.g. after a loading screen)
            if (_cabinPrefab == null)
                _cabinPrefab = _bundle.LoadAsset<GameObject>(PrefabName);
            return _cabinPrefab;
        }

        // The game's own material where one with the same name exists; otherwise the copy with the game's real shader
        static readonly Dictionary<string, Material> _gameMaterials = new Dictionary<string, Material>();

        static void FixMaterials(GameObject cabin)
        {
            Shader diffuse = Shader.Find("Shader Forge/TLD_StandardDiffuse");
            Shader transparent = Shader.Find("Shader Forge/TLD_StandardTransparent");

            foreach (Renderer r in cabin.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null) continue;
                    string name = m.name.Replace(" (Instance)", "");

                    Material game = GetGameMaterial(name);
                    if (game != null)
                    {
                        mats[i] = game;
                        continue;
                    }

                    string shaderName = m.shader.name;
                    if (!shaderName.StartsWith(DummyPrefix)) continue;
                    string realName = shaderName.Substring(DummyPrefix.Length);
                    Shader real = Shader.Find(realName);
                    if (real == null)                                                // shader not loaded: the game's standard one
                        real = (name + realName).Contains("Transparent") || (name + realName).Contains("Alpha") || name.Contains("Glass")
                            ? transparent : diffuse;
                    m.shader = real;
                }
                r.sharedMaterials = mats;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;                    // no baked lighting in a bundle
            }
        }

        static Material GetGameMaterial(string name)
        {
            if (!_gameMaterials.TryGetValue(name, out Material material))
            {
                string key = ResolveKey(name + ".mat");
                material = key == null ? null : Addressables.LoadAssetAsync<Material>(key).WaitForCompletion();
                _gameMaterials[name] = material;
            }
            return material;
        }

        // Copies Bleak Inlet's own indoor-space and snow-blocking objects onto the cabin's markers
        static void AddShelterVolumes(Transform cabin)
        {
            foreach (var (markerName, sourceName) in ShelterVolumes)
            {
                Transform marker = FindDeep(cabin, markerName);
                GameObject source = GameObject.Find(sourceName);         // the cabin is still switched off, so this is Bleak Inlet's
                if (marker == null || source == null) continue;

                GameObject copy = GameObject.Instantiate<GameObject>(source, marker.parent, false);
                copy.name = sourceName + "_CabinSwap";
                copy.transform.localPosition = marker.localPosition;
                copy.transform.localRotation = marker.localRotation;
                copy.transform.localScale = marker.localScale;

                BoxCollider from = marker.GetComponent<BoxCollider>();
                BoxCollider to = copy.GetComponent<BoxCollider>();
                if (from != null && to != null)
                {
                    to.center = from.center;
                    to.size = from.size;
                    from.enabled = false;
                }
            }
        }

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }


        //  ORIGINAL SETTINGS
        //  AssetRipper exported the working parts' scripts with every setting empty. originals.json holds the real settings, captured at the Mindful Cabin in Forsaken Airfield; they are written into the copies before the cabin switches on. Save IDs (ObjectGuid) are never written.

        static void ApplyOriginalSettings(Transform cabin)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ModFile(OriginalsFile)));
            foreach (JsonElement partJson in doc.RootElement.GetProperty("parts").EnumerateArray())
            {
                Transform part = FindDeep(cabin, partJson.GetProperty("part").GetString());
                foreach (JsonElement objJson in partJson.GetProperty("objects").EnumerateArray())
                {
                    Transform t = FindRel(part, objJson.GetProperty("path").GetString());
                    if (t == null) continue;                              // laid out slightly differently
                    t.gameObject.SetActive(objJson.GetProperty("active").GetBoolean());

                    var seen = new Dictionary<string, int>();
                    foreach (JsonElement compJson in objJson.GetProperty("components").EnumerateArray())
                    {
                        if (compJson.ValueKind != JsonValueKind.Object) continue;
                        string cls = compJson.GetProperty("class").GetString();
                        int n = seen[cls] = seen.GetValueOrDefault(cls) + 1;

                        // Unity's own components have no "fields": they come with the bundle as they are
                        if (cls.EndsWith("ObjectGuid") || !compJson.TryGetProperty("fields", out JsonElement fieldsJson)) continue;
                        Component comp = NthComponent(t.gameObject, cls, n);
                        if (comp == null) continue;
                        Il2CppSystem.Type type = comp.GetIl2CppType();

                        foreach (JsonProperty fp in fieldsJson.EnumerateObject())
                        {
                            if (fp.Name.StartsWith("(runtime)") || fp.Name.Contains(" [")) continue;
                            FieldInfo f = FindField(type, fp.Name);
                            if (f == null) continue;
                            try
                            {
                                ValueKind k = KindOfType(f.FieldType);
                                Il2CppSystem.Object existing = (k == ValueKind.Struct || k == ValueKind.DataClass) ? f.GetValue(comp) : null;
                                if (TryFromJson(fp.Value, f.FieldType, existing, part, out Il2CppSystem.Object value))
                                    f.SetValue(comp, value);
                            }
                            catch { }                                     // a setting that can't be written stays as it is
                        }

                        if (compJson.TryGetProperty("enabled", out JsonElement enabled))
                        {
                            Behaviour b = comp.TryCast<Behaviour>();
                            if (b != null) b.enabled = enabled.GetBoolean();
                        }
                    }
                }
            }
        }

        static Transform FindRel(Transform part, string rel) => rel == "." ? part : part.Find(rel);

        static Component NthComponent(GameObject go, string fullClassName, int n)
        {
            int count = 0;
            foreach (Component c in go.GetComponents<Component>())
                if (c != null && c.GetIl2CppType().FullName == fullClassName && ++count == n) return c;
            return null;
        }

        static List<Component> ComponentsNamed(GameObject go, string cls)
        {
            var result = new List<Component>();
            foreach (Component c in go.GetComponents<Component>())
                if (c != null && c.GetIl2CppType().Name == cls) result.Add(c);
            return result;
        }

        static FieldInfo FindField(Il2CppSystem.Type type, string name)
        {
            for (Il2CppSystem.Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        enum ValueKind { Simple, UnityRef, List, Struct, DataClass, Skip }

        // What kind of value a field holds, decided from its type alone
        static ValueKind KindOfType(Il2CppSystem.Type t)
        {
            string full = t.FullName ?? t.Name;
            if (t.IsPointer || t.IsByRef || full == "System.IntPtr" || full == "System.UIntPtr") return ValueKind.Skip;
            if (Il2CppType.Of<Il2CppSystem.Delegate>().IsAssignableFrom(t)) return ValueKind.Skip;
            if (t.IsPrimitive || t.IsEnum || full == "System.String") return ValueKind.Simple;
            if (Il2CppType.Of<UnityEngine.Object>().IsAssignableFrom(t)) return ValueKind.UnityRef;
            if (t.IsArray) return ValueKind.List;
            if (t.IsGenericType && t.Name.StartsWith("List`1")) return ValueKind.List;
            if (t.Name.Contains("<")) return ValueKind.Skip;
            if (t.IsValueType) return ValueKind.Struct;
            if (t.IsSerializable && !t.IsInterface && !t.IsAbstract) return ValueKind.DataClass;
            return ValueKind.Skip;
        }

        // Texts the recording holds instead of a value it didn't read, e.g. "(not read: callback)"
        static bool IsMarker(JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.String) return false;
            string s = json.GetString();
            return s.StartsWith("(not read") || s.StartsWith("(error") || s.StartsWith("(missing") || s.StartsWith("(too deep")
                || s.StartsWith("(list not") || s.StartsWith("(+") || s.StartsWith("(not a Unity");
        }

        // Turns a recorded value back into the game's own value of the field's type
        static bool TryFromJson(JsonElement json, Il2CppSystem.Type type, Il2CppSystem.Object existing,
                                Transform part, out Il2CppSystem.Object value)
        {
            value = null;
            if (IsMarker(json)) return false;
            ValueKind kind = KindOfType(type);
            if (kind == ValueKind.Skip) return false;
            if (json.ValueKind == JsonValueKind.Null) return kind == ValueKind.UnityRef;   // a reference to nothing

            switch (kind)
            {
                case ValueKind.Simple:
                    value = SimpleFromJson(json, type);
                    return value != null;
                case ValueKind.UnityRef:
                    value = RefFromJson(json, type, part);
                    return value != null;
                case ValueKind.List:
                    return TryListFromJson(json, type, part, out value);
                default:
                    return TryObjectFromJson(json, type, existing, part, out value);
            }
        }

        static Il2CppSystem.Object SimpleFromJson(JsonElement json, Il2CppSystem.Type type)
        {
            if (type.IsEnum)
            {
                string s = json.GetString();                              // e.g. "RotateBy (?)"
                int cut = s.LastIndexOf(" (");
                return Il2CppSystem.Enum.Parse(type, cut > 0 ? s.Substring(0, cut) : s);
            }
            switch (type.FullName)
            {
                case "System.String": return new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(json.GetString() ?? ""));
                case "System.Boolean": return Box(type, json.GetBoolean());
                case "System.Single": return Box(type, json.GetSingle());
                case "System.Double": return Box(type, json.GetDouble());
                case "System.Int32": return Box(type, json.GetInt32());
                case "System.UInt32": return Box(type, json.GetUInt32());
                case "System.Int64": return Box(type, json.GetInt64());
                case "System.UInt64": return Box(type, json.GetUInt64());
                case "System.Int16": return Box(type, json.GetInt16());
                case "System.Byte": return Box(type, json.GetByte());
                default: return null;
            }
        }

        // A plain C# number/bool as the game's own boxed value of the given type
        static unsafe Il2CppSystem.Object Box<T>(Il2CppSystem.Type type, T value) where T : unmanaged
        {
            IntPtr klass = IL2CPP.il2cpp_class_from_system_type(type.Pointer);
            return new Il2CppSystem.Object(IL2CPP.il2cpp_value_box(klass, (IntPtr)(&value)));
        }

        // References: to an object inside the same part, or to a game asset (loot table, HUD panel, sound...)
        static Il2CppSystem.Object RefFromJson(JsonElement json, Il2CppSystem.Type type, Transform part)
        {
            string name = json.GetProperty("ref").GetString();
            string refType = json.GetProperty("type").GetString();
            string where = json.GetProperty("where").GetString();

            if (where.StartsWith("inside: "))
            {
                Transform t = FindRel(part, where.Substring("inside: ".Length));
                if (t == null) return null;
                if (type.FullName == "UnityEngine.GameObject") return t.gameObject;
                if (type.FullName == "UnityEngine.Transform") return t;
                return ComponentsNamed(t.gameObject, refType).FirstOrDefault() ?? t.GetComponent(type);
            }
            return where == "asset" ? FindLoadedAsset(type, name) : null;
        }

        static readonly Dictionary<string, UnityEngine.Object> _assetCache = new Dictionary<string, UnityEngine.Object>();

        // Found by type and name among what's loaded, or loaded through Addressables if it has a key of its own
        static UnityEngine.Object FindLoadedAsset(Il2CppSystem.Type type, string name)
        {
            // Re-found if the game has cleared the cached one from memory since the last visit
            string cacheKey = type.FullName + "|" + name;
            if (_assetCache.TryGetValue(cacheKey, out UnityEngine.Object found) && found != null) return found;
            found = null;

            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(type))
                if (o != null && o.name == name) { found = o; break; }

            if (found == null)
            {
                string key = ResolveKey(name + ".asset");
                if (key != null)
                {
                    UnityEngine.Object loaded = Addressables.LoadAssetAsync<UnityEngine.Object>(key).WaitForCompletion();
                    if (loaded != null && type.IsAssignableFrom(loaded.GetIl2CppType())) found = loaded;
                }
            }
            _assetCache[cacheKey] = found;
            return found;
        }

        static bool TryListFromJson(JsonElement json, Il2CppSystem.Type type, Transform part, out Il2CppSystem.Object value)
        {
            value = null;
            Il2CppSystem.Type element = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];

            var items = new List<Il2CppSystem.Object>();
            foreach (JsonElement e in json.EnumerateArray())
            {
                if (!TryFromJson(e, element, null, part, out Il2CppSystem.Object item)) return false;
                items.Add(item);
            }

            if (type.IsArray)
            {
                Il2CppSystem.Array arr = Il2CppSystem.Array.CreateInstance(element, items.Count);
                for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                value = arr;
                return true;
            }

            // List<T>: a new list with the items added one by one
            Il2CppSystem.Object list = Il2CppSystem.Activator.CreateInstance(type);
            MethodInfo add = type.GetMethod("Add");
            foreach (Il2CppSystem.Object item in items)
            {
                var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                args[0] = item;
                add.Invoke(list, args);
            }
            value = list;
            return true;
        }

        // Structs arrive as a copy: the copy is filled in and the caller writes it back
        static bool TryObjectFromJson(JsonElement json, Il2CppSystem.Type type, Il2CppSystem.Object existing,
                                      Transform part, out Il2CppSystem.Object value)
        {
            value = existing ?? Il2CppSystem.Activator.CreateInstance(type);
            foreach (JsonProperty p in json.EnumerateObject())
            {
                if (p.Name == "$type") continue;
                FieldInfo f = FindField(type, p.Name);
                if (f == null) continue;

                ValueKind k = KindOfType(f.FieldType);
                Il2CppSystem.Object innerExisting = (k == ValueKind.Struct || k == ValueKind.DataClass) ? f.GetValue(value) : null;
                if (TryFromJson(p.Value, f.FieldType, innerExisting, part, out Il2CppSystem.Object inner))
                    f.SetValue(value, inner);
            }
            return true;
        }

        //  WATER STORAGE
        //  The tank's own water logic is replaced: Take and Store open the game's water menu in its "generic transfer" mode. The amount is saved with the game through ModData.

        const long UnitsPerLiter = 1_000_000_000L;              // the game stores 2 L as 2000000000
        const string WaterSaveTag = "waterStorage";
        static readonly ModData.ModDataManager _modData = new ModData.ModDataManager("CabinSwap");

        static GameObject _tank;
        static Il2CppTLD.Interactions.SimpleInteraction _tankInteraction;
        static long _storedUnits;
        static bool _waterLoaded;

        static long CapacityUnits => (long)(WaterCapacityLiters * UnitsPerLiter);
        static float ToLiters(long units) => units / (float)UnitsPerLiter;

        static void SetupWaterStorage(Transform cabin)
        {
            Transform tank = FindDeep(cabin, "INTERACTIVE_Water");
            tank.gameObject.SetActive(true);

            // The cabin is still switched off here, so the game's own water logic never starts
            foreach (Component c in tank.GetComponents<Component>())
                if (c != null && c.GetIl2CppType().Name == "WaterSource")
                    GameObject.DestroyImmediate(c);

            // The same hooks Safehouse Customization Plus uses for its fuel tank
            var si = tank.GetComponent<Il2CppTLD.Interactions.SimpleInteraction>();
            si.AddEventCallback(Il2CppTLD.Interactions.InteractionEventType.PerformInteraction,
                (UnityAction<Il2CppTLD.Interactions.BaseInteraction>)(_ => OpenWaterMenu(taking: !SwapWaterButtons)));
            si.AddEventCallback(Il2CppTLD.Interactions.InteractionEventType.InitializeInteraction,
                (UnityAction<Il2CppTLD.Interactions.BaseInteraction>)(_ => ShowWaterPrompt(true)));
            si.AddEventCallback(Il2CppTLD.Interactions.InteractionEventType.HideInteraction,
                (UnityAction<Il2CppTLD.Interactions.BaseInteraction>)(_ => ShowWaterPrompt(false)));

            _tank = tank.gameObject;
            _tankInteraction = si;
            UpdateWaterName();
        }

        // The two buttons at the bottom of the screen while you look at the tank
        static void ShowWaterPrompt(bool show)
        {
            var prompt = Il2Cpp.InterfaceManager.GetPanel<Il2Cpp.Panel_HUD>().m_GenericInteractionPrompt;
            if (!show)
            {
                prompt.HideInteraction();
                return;
            }

            EnsureWaterLoaded();
            UpdateWaterName();
            if (prompt.IsShowing()) return;
            prompt.ShowInteraction(TakeButtonLabel, SwapWaterButtons ? "AltFire" : "Interact", true);
            prompt.ShowInteraction(StoreButtonLabel, SwapWaterButtons ? "Interact" : "AltFire", true);
        }

        // "Water Storage (7.5 / 20 L)" as the tank's name. The game looks names up by key and shows an unknown key as it is, so the finished text goes in as the key.
        static void UpdateWaterName()
        {
            string amount = _waterLoaded ? $"{ToLiters(_storedUnits):0.#} / {WaterCapacityLiters:0} L" : $"{WaterCapacityLiters:0} L";
            _tankInteraction.m_DefaultHoverText.m_LocalizationID = $"{Il2Cpp.Localization.Get(WaterNameKey)} ({amount})";
        }

        static bool IsLookingAtTank()
        {
            if (_tank == null) return false;
            var pm = Il2Cpp.GameManager.GetPlayerManagerComponent();
            float range = pm.ComputeModifiedPickupRange(Il2Cpp.GameManager.GetGlobalParameters().m_MaxPickupRange);
            GameObject looked = pm.GetInteractiveObjectUnderCrosshairs(range);
            return looked != null && (looked == _tank || looked.transform.IsChildOf(_tank.transform));
        }

        // taking = from the tank into your inventory; otherwise from your inventory into the tank
        static void OpenWaterMenu(bool taking)
        {
            EnsureWaterLoaded();
            long carried = Il2Cpp.GameManager.GetPlayerManagerComponent()
                .GetTotalLiters(Il2CppTLD.Gear.LiquidType.GetPotableWater()).m_Units;
            long max = taking ? _storedUnits : Math.Min(carried, CapacityUnits - _storedUnits);

            if (max <= 0)
            {
                Il2Cpp.HUDMessage.AddMessage(taking ? NoWaterInStorageMessage
                                           : carried <= 0 ? NoWaterInInventoryMessage
                                           : StorageFullMessage);
                Il2Cpp.GameAudioManager.PlayGUIError();
                return;
            }

            // You pick an amount up to max; the game calls us back with it
            var panel = Il2Cpp.InterfaceManager.GetPanel<Il2Cpp.Panel_PickWater>();
            var supply = Il2Cpp.GameManager.GetInventoryComponent().GetPotableWaterSupply().m_WaterSupply;
            var onChosen = (Il2CppSystem.Action<Il2CppTLD.IntBackedUnit.ItemLiquidVolume>)
                (Action<Il2CppTLD.IntBackedUnit.ItemLiquidVolume>)(amount => Transfer(taking, amount.m_Units));
            panel.SetWaterSupplyForGenericTransfer(supply, new Il2CppTLD.IntBackedUnit.ItemLiquidVolume(max), onChosen);

            // Remember which menu is the mods, so only this menu gets relabelled (see OnLateUpdate)
            _menuPanel = panel;
            _menuAction = (int)panel.m_ExecuteAction;
            _menuTaking = taking;
            _menuOpen = true;

            panel.Enable(true);
        }

        // The game's water menu says "Transfer" both ways; while this mods menu is open it says Take / Store

        static Il2Cpp.Panel_PickWater _menuPanel;
        static int _menuAction;
        static bool _menuTaking, _menuOpen;

        // Runs after the game's own UI code every frame, so the game can't write "Transfer" back over
        public override void OnLateUpdate()
        {
            if (!_menuOpen) return;

            // Stop as soon as the menu closes, or is reused by something else (a toilet, another container...)
            if (_menuPanel == null || !_menuPanel.gameObject.activeInHierarchy || (int)_menuPanel.m_ExecuteAction != _menuAction)
            {
                _menuOpen = false;
                return;
            }

            string word = _menuTaking ? TakeWord : StoreWord;
            foreach (Il2Cpp.UILabel label in _menuPanel.gameObject.GetComponentsInChildren<Il2Cpp.UILabel>(true))
            {
                string text = label.text;
                if (string.IsNullOrEmpty(text) || text.IndexOf("transfer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // keep the label's capitals: TRANSFER -> TAKE, Transfer -> Take
                label.text = Regex.Replace(text, "transfer", m => m.Value == m.Value.ToUpperInvariant() ? word.ToUpperInvariant() : word,
                                           RegexOptions.IgnoreCase);
            }
        }

        static void Transfer(bool taking, long units)
        {
            var pm = Il2Cpp.GameManager.GetPlayerManagerComponent();
            var drinkable = Il2CppTLD.Gear.LiquidType.GetPotableWater();

            // Measure the player's water before and after, so the tank only changes by what really moved
            long before = pm.GetTotalLiters(drinkable).m_Units;
            if (taking)
                Il2Cpp.GameManager.GetInventoryComponent().AddToPotableWaterSupply(
                    new Il2CppTLD.IntBackedUnit.ItemLiquidVolume(Math.Min(units, _storedUnits)));
            else
                pm.DeductLiquidFromInventory(
                    new Il2CppTLD.IntBackedUnit.ItemLiquidVolume(Math.Min(units, Math.Min(before, CapacityUnits - _storedUnits))), drinkable);
            long moved = Math.Abs(pm.GetTotalLiters(drinkable).m_Units - before);

            _storedUnits = Math.Clamp(_storedUnits + (taking ? -moved : moved), 0, CapacityUnits);
            SaveWater();
            UpdateWaterName();
            if (moved > 0) PlayWaterSound(taking ? TakeSound : StoreSound, ToLiters(moved));
        }

        // Played from the tank itself. The sound loops, so it is stopped after a length that fits the amount.
        static void PlayWaterSound(string eventName, float liters)
        {
            uint playingId = Il2Cpp.GameAudioManager.PlaySound(eventName, _tank);
            float seconds = Math.Clamp(SoundBaseSeconds + SoundSecondsPerLiter * liters, SoundMinSeconds, SoundMaxSeconds);
            MelonCoroutines.Start(StopSoundLater(playingId, seconds));
        }

        static IEnumerator StopSoundLater(uint playingId, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            Il2Cpp.AkSoundEngine.StopPlayingID(playingId, SoundFadeOutMs);
        }

        static void EnsureWaterLoaded()
        {
            if (_waterLoaded) return;
            _waterLoaded = true;
            string saved = _modData.Load(WaterSaveTag);
            _storedUnits = long.TryParse(saved, out long units)
                ? Math.Clamp(units, 0, CapacityUnits)
                : (long)(WaterStartLiters * UnitsPerLiter);
        }

        static void SaveWater()
        {
            if (!_waterLoaded) return;             // never overwrite the save with an amount that wasn't loaded
            _modData.Save(_storedUnits.ToString(), WaterSaveTag);
        }

        // The other button while looking at the tank ("AltFire")
        [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.InputManager), nameof(Il2Cpp.InputManager.ExecuteAltFire))]
        private static class WaterStorageAltFire
        {
            private static void Postfix()
            {
                if (IsLookingAtTank())
                    OpenWaterMenu(taking: SwapWaterButtons);
            }
        }

        // Written into the save file along with the game's own data. Guarded, so a problem here can never stop the game itself from saving.
        [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.SaveGameSystem), nameof(Il2Cpp.SaveGameSystem.SaveSceneData))]
        private static class WaterStorageSave
        {
            private static void Prefix()
            {
                try { SaveWater(); }
                catch { }
            }
        }


        //  GROUND: levelled under the cabin. The terrain edit uses the same native calls as Seamless Interiors (github.com/karsontr13/TLD-Seamless-Interiors): Unity's normal GetHeights/SetHeights crash because they use a two-dimensional array.

        class TerrainBackup
        {
            public int X, Z, Width, Height;
            public float[] Heights;
        }
        static readonly Dictionary<int, TerrainBackup> _terrainBackups = new Dictionary<int, TerrainBackup>();

        void FlattenGroundUnder(Transform root)
        {
            float targetY = root.position.y - GroundBelowFloor;     // the cabin floor sits at its origin point
            float reach = FlatMargin + FalloffDistance;

            // World-space box that covers the level area plus the slope around it
            Vector3[] corners =
            {
                root.TransformPoint(new Vector3(FlatMin.x - reach, 0f, FlatMin.y - reach)),
                root.TransformPoint(new Vector3(FlatMax.x + reach, 0f, FlatMin.y - reach)),
                root.TransformPoint(new Vector3(FlatMin.x - reach, 0f, FlatMax.y + reach)),
                root.TransformPoint(new Vector3(FlatMax.x + reach, 0f, FlatMax.y + reach)),
            };
            float minX = corners.Min(c => c.x), maxX = corners.Max(c => c.x);
            float minZ = corners.Min(c => c.z), maxZ = corners.Max(c => c.z);

            Vector3 rootPos = root.position;
            Quaternion toLocal = Quaternion.Inverse(root.rotation);
            Terrain[] terrains = Terrain.activeTerrains;
            foreach (Terrain terrain in terrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData td = terrain.terrainData;
                Vector3 tPos = terrain.transform.position;
                Vector3 size = td.size;
                int res = td.heightmapResolution;

                // Skip terrain tiles that don't touch the cabin area
                if (maxX < tPos.x || minX > tPos.x + size.x || maxZ < tPos.z || minZ > tPos.z + size.z) continue;

                int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - tPos.x) / size.x * (res - 1)), 0, res - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - tPos.x) / size.x * (res - 1)), 0, res - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - tPos.z) / size.z * (res - 1)), 0, res - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((maxZ - tPos.z) / size.z * (res - 1)), 0, res - 1);
                int w = x1 - x0 + 1, h = z1 - z0 + 1;

                // Undo an earlier edit first, so we always start from the original ground
                int id = td.GetInstanceID();
                if (_terrainBackups.TryGetValue(id, out TerrainBackup old))
                    WriteHeights(terrain, old.X, old.Z, old.Width, old.Height, old.Heights);

                IntPtr heights = GetHeightsNative(td, x0, z0, w, h);
                if (heights == IntPtr.Zero || Il2CppInterop.Runtime.IL2CPP.il2cpp_array_length(heights) < (uint)(w * h))
                    throw new InvalidOperationException("TerrainData.GetHeights returned an array of the wrong size");

                float target01 = Math.Clamp((targetY - tPos.y) / size.y, 0f, 1f);
                var original = new float[w * h];

                unsafe
                {
                    float* data = (float*)((byte*)heights + 32);      // skip the IL2CPP array header
                    for (int i = 0; i < original.Length; i++) original[i] = data[i];

                    for (int zi = 0; zi < h; zi++)
                    {
                        for (int xi = 0; xi < w; xi++)
                        {
                            // Where this height sample is, measured from the cabin's centre
                            float wx = tPos.x + (x0 + xi) / (float)(res - 1) * size.x;
                            float wz = tPos.z + (z0 + zi) / (float)(res - 1) * size.z;
                            Vector3 local = toLocal * new Vector3(wx - rootPos.x, 0f, wz - rootPos.z);

                            // Distance outside the level area (0 = inside it)
                            float dx = Math.Max(0f, Math.Max(FlatMin.x - FlatMargin - local.x, local.x - (FlatMax.x + FlatMargin)));
                            float dz = Math.Max(0f, Math.Max(FlatMin.y - FlatMargin - local.z, local.z - (FlatMax.y + FlatMargin)));
                            float d = (float)Math.Sqrt(dx * dx + dz * dz);

                            float weight = d <= 0f ? 1f : d >= FalloffDistance ? 0f : SmoothStep(1f - d / FalloffDistance);
                            int i = zi * w + xi;
                            data[i] = original[i] + (target01 - original[i]) * weight;
                        }
                    }
                }

                _terrainBackups[id] = new TerrainBackup { X = x0, Z = z0, Width = w, Height = h, Heights = original };
                SetHeightsDelayLODNative(td, x0, z0, heights);
                terrain.ApplyDelayedHeightmapModification();
            }
        }

        static float SmoothStep(float t) => t * t * (3f - 2f * t);


        //  GRASS: removed under the cabin (terrain grass and separate grass objects)

        void ClearGrassUnder(Transform root)
        {
            Vector3 rootPos = root.position;
            Quaternion toLocal = Quaternion.Inverse(root.rotation);

            // Is this world position inside the roof area (+ margin + pad)?
            bool Inside(float wx, float wz, float pad)
            {
                Vector3 l = toLocal * new Vector3(wx - rootPos.x, 0f, wz - rootPos.z);
                float m = GrassMargin + pad;
                return l.x >= FlatMin.x - m && l.x <= FlatMax.x + m && l.z >= FlatMin.y - m && l.z <= FlatMax.y + m;
            }

            // World-space box around the area
            float reach = GrassMargin + 2f;
            Vector3[] corners =
            {
                root.TransformPoint(new Vector3(FlatMin.x - reach, 0f, FlatMin.y - reach)),
                root.TransformPoint(new Vector3(FlatMax.x + reach, 0f, FlatMin.y - reach)),
                root.TransformPoint(new Vector3(FlatMin.x - reach, 0f, FlatMax.y + reach)),
                root.TransformPoint(new Vector3(FlatMax.x + reach, 0f, FlatMax.y + reach)),
            };
            float minX = corners.Min(c => c.x), maxX = corners.Max(c => c.x);
            float minZ = corners.Min(c => c.z), maxZ = corners.Max(c => c.z);

            // 1. Terrain grass (Unity "detail" layers)
            Terrain[] terrains = Terrain.activeTerrains;
            foreach (Terrain terrain in terrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData td = terrain.terrainData;
                Vector3 tPos = terrain.transform.position;
                Vector3 size = td.size;
                int dw = td.detailWidth, dh = td.detailHeight;
                int layers = td.detailPrototypes.Length;
                if (layers == 0 || dw == 0 || dh == 0) continue;
                if (maxX < tPos.x || minX > tPos.x + size.x || maxZ < tPos.z || minZ > tPos.z + size.z) continue;

                int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - tPos.x) / size.x * dw), 0, dw - 1);
                int x1 = Mathf.Clamp(Mathf.FloorToInt((maxX - tPos.x) / size.x * dw), 0, dw - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - tPos.z) / size.z * dh), 0, dh - 1);
                int z1 = Mathf.Clamp(Mathf.FloorToInt((maxZ - tPos.z) / size.z * dh), 0, dh - 1);
                int w = x1 - x0 + 1, h = z1 - z0 + 1;

                // A grass cell is cleared if any part of it reaches into the area
                float pad = 0.5f * Math.Max(size.x / dw, size.z / dh);

                for (int layer = 0; layer < layers; layer++)
                {
                    IntPtr map = GetDetailLayerNative(td, x0, z0, w, h, layer);
                    if (map == IntPtr.Zero || Il2CppInterop.Runtime.IL2CPP.il2cpp_array_length(map) < (uint)(w * h)) continue;

                    int changed = 0;
                    unsafe
                    {
                        int* data = (int*)((byte*)map + 32);            // skip the IL2CPP array header
                        for (int zi = 0; zi < h; zi++)
                        {
                            for (int xi = 0; xi < w; xi++)
                            {
                                int i = zi * w + xi;
                                if (data[i] == 0) continue;
                                float wx = tPos.x + (x0 + xi + 0.5f) / dw * size.x;   // centre of the cell
                                float wz = tPos.z + (z0 + zi + 0.5f) / dh * size.z;
                                if (!Inside(wx, wz, pad)) continue;
                                data[i] = 0;
                                changed++;
                            }
                        }
                    }

                    if (changed > 0)
                        SetDetailLayerNative(td, x0, z0, layer, map);
                }
            }

            // 2. Grass placed as separate objects
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded || !scene.name.StartsWith("CanneryRegion")) continue;

                foreach (GameObject go in scene.GetRootGameObjects())
                {
                    foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(false))
                    {
                        if (r == null || r.transform.IsChildOf(root)) continue;
                        string n = r.gameObject.name.ToLowerInvariant();
                        if (!GrassObjectWords.Any(word => n.Contains(word))) continue;

                        Vector3 c = r.bounds.center;
                        if (Math.Abs(c.y - rootPos.y) > 3f || !Inside(c.x, c.z, 0f)) continue;

                        r.gameObject.SetActive(false);
                    }
                }
            }

        }

        static IntPtr s_GetDetailLayer = IntPtr.Zero;
        static IntPtr s_SetDetailLayer = IntPtr.Zero;

        static unsafe IntPtr GetDetailLayerNative(TerrainData td, int xBase, int yBase, int width, int height, int layer)
        {
            IntPtr method = ResolveTerrainDataMethod(ref s_GetDetailLayer, "GetDetailLayer", 5);
            IntPtr* args = stackalloc IntPtr[5];
            args[0] = (IntPtr)(&xBase);
            args[1] = (IntPtr)(&yBase);
            args[2] = (IntPtr)(&width);
            args[3] = (IntPtr)(&height);
            args[4] = (IntPtr)(&layer);

            IntPtr exc = IntPtr.Zero;
            IntPtr result = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, td.Pointer, (void**)args, ref exc);
            Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
            return result;
        }

        static unsafe void SetDetailLayerNative(TerrainData td, int xBase, int yBase, int layer, IntPtr details)
        {
            IntPtr method = ResolveTerrainDataMethod(ref s_SetDetailLayer, "SetDetailLayer", 4);
            IntPtr* args = stackalloc IntPtr[4];
            args[0] = (IntPtr)(&xBase);
            args[1] = (IntPtr)(&yBase);
            args[2] = (IntPtr)(&layer);
            args[3] = details;

            IntPtr exc = IntPtr.Zero;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, td.Pointer, (void**)args, ref exc);
            Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
        }

        static void WriteHeights(Terrain terrain, int x, int z, int w, int h, float[] values)
        {
            TerrainData td = terrain.terrainData;
            IntPtr heights = GetHeightsNative(td, x, z, w, h);
            if (heights == IntPtr.Zero || Il2CppInterop.Runtime.IL2CPP.il2cpp_array_length(heights) < (uint)values.Length) return;
            unsafe
            {
                float* data = (float*)((byte*)heights + 32);
                for (int i = 0; i < values.Length; i++) data[i] = values[i];
            }
            SetHeightsDelayLODNative(td, x, z, heights);
            terrain.ApplyDelayedHeightmapModification();
        }

        static IntPtr s_GetHeights = IntPtr.Zero;
        static IntPtr s_SetHeightsDelayLOD = IntPtr.Zero;

        static IntPtr ResolveTerrainDataMethod(ref IntPtr cached, string name, int argCount)
        {
            if (cached == IntPtr.Zero)
            {
                cached = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(
                    Il2CppInterop.Runtime.Il2CppClassPointerStore<TerrainData>.NativeClassPtr, name, argCount);
                if (cached == IntPtr.Zero)
                    throw new InvalidOperationException("TerrainData." + name + " not found");
            }
            return cached;
        }

        static unsafe IntPtr GetHeightsNative(TerrainData td, int xBase, int yBase, int width, int height)
        {
            IntPtr method = ResolveTerrainDataMethod(ref s_GetHeights, "GetHeights", 4);
            IntPtr* args = stackalloc IntPtr[4];
            args[0] = (IntPtr)(&xBase);
            args[1] = (IntPtr)(&yBase);
            args[2] = (IntPtr)(&width);
            args[3] = (IntPtr)(&height);

            IntPtr exc = IntPtr.Zero;
            IntPtr result = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, td.Pointer, (void**)args, ref exc);
            Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
            return result;
        }

        static unsafe void SetHeightsDelayLODNative(TerrainData td, int xBase, int yBase, IntPtr heights)
        {
            IntPtr method = ResolveTerrainDataMethod(ref s_SetHeightsDelayLOD, "SetHeightsDelayLOD", 3);
            IntPtr* args = stackalloc IntPtr[3];
            args[0] = (IntPtr)(&xBase);
            args[1] = (IntPtr)(&yBase);
            args[2] = heights;

            IntPtr exc = IntPtr.Zero;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, td.Pointer, (void**)args, ref exc);
            Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
        }


        //  ADDRESSABLES KEYS: find the game's own assets by file name

        static List<string> _allKeys;

        static string ResolveKey(string file)
        {
            _allKeys ??= ReadAllKeys();

            foreach (string key in _allKeys)
                if (key.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) || key.Equals(file, StringComparison.OrdinalIgnoreCase))
                    return key;

            string bareName = Path.GetFileNameWithoutExtension(file);
            foreach (string key in _allKeys)
                if (key.Equals(bareName, StringComparison.OrdinalIgnoreCase))
                    return key;

            return null;
        }

        // Same technique Safehouse Customization Plus uses to read catalog keys
        static List<string> ReadAllKeys()
        {
            var all = new List<string>();
            var locators = Il2CppSystem.Linq.Enumerable.ToList(Addressables.ResourceLocators);
            for (int i = 0; i < locators.Count; i++)
            {
                IResourceLocator locator = locators[i];
                if (locator == null || locator.Keys == null) continue;
                var keys = Il2CppSystem.Linq.Enumerable.ToList(locator.Keys);
                for (int k = 0; k < keys.Count; k++)
                    if (keys[k] != null) all.Add(keys[k].ToString());
            }
            return all;
        }
    }
}

// Needed by the Box<T> helper ("where T : unmanaged") in this project setup
namespace System.Runtime.CompilerServices
{
    internal sealed class IsUnmanagedAttribute : Attribute { }
}