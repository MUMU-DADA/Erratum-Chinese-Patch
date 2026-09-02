using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace ErratumChinesePatch;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "community.erratum.zhcn";
    public const string PluginName = "Erratum Simplified Chinese Patch";
    // BepInEx 5 validates this as a System.Version; prerelease suffixes make
    // the plugin descriptor invalid and cause the chainloader to skip it.
    public const string PluginVersion = "0.1.0";

    internal static LocalizationStore Store { get; private set; } = new LocalizationStore();
    internal static ChineseFontManager Fonts { get; private set; } = new ChineseFontManager();
    internal static ManualLogSource LogSource { get; private set; } = null!;
    internal static string? FontPath { get; private set; }

    private Harmony? _harmony;
    private bool _dialogueParserPatched;
    private bool _interactableLoaderPatched;
    private bool _dialogueTypePatched;
    private bool _interactableTypePatched;

    private void Awake()
    {
        LogSource = Logger;
        // Erratum clears ordinary root objects while switching out of its
        // bootstrap scene. Match BepInEx's HideManagerGameObject protection
        // here as well, so the patch survives even when an existing user
        // configuration keeps that global option disabled.
        gameObject.hideFlags |= HideFlags.HideAndDontSave;
        var pluginRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
        Store = LocalizationStore.Load(Path.Combine(pluginRoot, "Localization", "strings.tsv"), Logger);
        FontPath = Path.Combine(pluginRoot, "fonts", "SourceHanSansSC-Regular.otf");
        _harmony = new Harmony(PluginGuid);
        PatchTextAssignments(_harmony);
        PatchTitleStartup(_harmony);
        _dialogueParserPatched = PatchDialogueParser(_harmony);
        _interactableLoaderPatched = PatchInteractableLoader(_harmony);
        _dialogueTypePatched = PatchDialogueType(_harmony);
        _interactableTypePatched = PatchInteractableType(_harmony);
        if (!_dialogueParserPatched || !_interactableLoaderPatched ||
            !_dialogueTypePatched || !_interactableTypePatched)
            StartCoroutine(InitializeGamePatches());
        SceneManager.sceneLoaded += OnSceneLoaded;
        StartCoroutine(RefreshTextOverSeveralFrames());
        StartCoroutine(RefreshTextContinuously());
        Logger.LogInfo($"Plugin component state: enabled={enabled}, activeSelf={gameObject.activeSelf}, " +
                       $"activeInHierarchy={gameObject.activeInHierarchy}, scene={SceneManager.GetActiveScene().name}.");
        Logger.LogInfo($"Loaded {Store.Count} localization rules; waiting for Unity font initialization.");
    }

    private void Start()
    {
        Logger.LogInfo("Unity Start callback entered; initializing Chinese font.");
        // Start runs after all plugin Awake methods and Unity has initialized
        // the native font/TMP subsystems. Retry on later frames if the game
        // has not loaded those subsystems yet.
        if (FontPath != null && !Fonts.IsReady)
        {
            LoadFonts(FontPath);
            if (!Fonts.IsReady)
                StartCoroutine(InitializeFontsAfterUnityStartup(FontPath));
        }
    }

    private void OnDestroy()
    {
        LogSource.LogWarning("Plugin component is being destroyed; removing runtime hooks.");
        SceneManager.sceneLoaded -= OnSceneLoaded;
        _harmony?.UnpatchSelf();
        Fonts.Dispose();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        LogSource.LogInfo($"Scene loaded: {scene.name}; initializing localization display.");
        // BepInEx adds plugins while Unity is still between scenes in this
        // game. Unity invokes Awake, but does not invoke Start on the dynamic
        // plugin component before the title scene becomes active. Initialize
        // the font here as the reliable post-scene boundary, and keep Start as
        // a compatibility fallback for other runtime versions.
        if (FontPath != null && !Fonts.IsReady)
            LoadFonts(FontPath);
        StartCoroutine(RefreshTextOverSeveralFrames());
    }

    private static bool PatchTitleStartup(Harmony harmony)
    {
        var targetType = FindGameType("TitleManager");
        var target = targetType == null ? null : AccessTools.Method(targetType, "Start");
        return TryPatch(harmony, target,
            AccessTools.Method(typeof(Plugin), nameof(InitializeDisplayBeforeTitleStart)),
            AccessTools.Method(typeof(Plugin), nameof(RefreshDisplayAfterTitleStart)),
            "TitleManager.Start");
    }

    private static void InitializeDisplayBeforeTitleStart()
    {
        LogSource.LogInfo("TitleManager.Start entered; initializing localization display.");
        if (FontPath != null && !Fonts.IsReady)
            LoadFonts(FontPath);
    }

    private static void RefreshDisplayAfterTitleStart()
    {
        var translatedCount = RefreshAllText();
        LogSource.LogInfo($"Title screen refresh applied {translatedCount} translation(s).");
    }

    private IEnumerator InitializeFontsAfterUnityStartup(string fontPath)
    {
        for (var attempt = 0; attempt < 10 && !Fonts.IsReady; attempt++)
        {
            yield return null;
            LoadFonts(fontPath);
        }
    }

    private IEnumerator InitializeGamePatches()
    {
        for (var attempt = 0; attempt < 30 && (!_dialogueParserPatched || !_interactableLoaderPatched ||
                                               !_dialogueTypePatched || !_interactableTypePatched); attempt++)
        {
            yield return null;
            if (_harmony == null)
                yield break;
            if (!_dialogueParserPatched)
                _dialogueParserPatched = PatchDialogueParser(_harmony);
            if (!_interactableLoaderPatched)
                _interactableLoaderPatched = PatchInteractableLoader(_harmony);
            if (!_dialogueTypePatched)
                _dialogueTypePatched = PatchDialogueType(_harmony);
            if (!_interactableTypePatched)
                _interactableTypePatched = PatchInteractableType(_harmony);
        }
    }

    private static void LoadFonts(string fontPath)
    {
        var previous = Fonts;
        Fonts = ChineseFontManager.Load(fontPath, LogSource, Store.TranslationCodePoints());
        previous.Dispose();
        RefreshAllText();
        LogSource.LogInfo($"Chinese font ready: {Fonts.IsReady}.");
    }

    private static string Hierarchy(Component component)
    {
        var names = new List<string>();
        var current = component.transform;
        while (current != null)
        {
            names.Add(current.gameObject.name);
            current = current.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    internal static string Translate(Component component, string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;
        return Store.Translate(SceneManager.GetActiveScene().name, Hierarchy(component), source);
    }

    internal static Type? FindGameType(string name)
    {
        // Resolve the game's own assembly first. AccessTools.TypeByName scans
        // every loaded assembly and can otherwise select a same-named type from
        // another mod, or run before MainAssembly has been indexed.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "MainAssembly", StringComparison.Ordinal))
                continue;
            var gameType = assembly.GetType(name, false);
            if (gameType != null)
                return gameType;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var gameType = assembly.GetType(name, false);
            if (gameType != null)
                return gameType;
        }

        return AccessTools.TypeByName(name);
    }

    private static bool PatchDialogueParser(Harmony harmony)
    {
        var targetType = FindGameType("DialogueManager");
        var target = targetType == null ? null : AccessTools.Method(targetType, "ScriptParser");
        var prefix = AccessTools.Method(typeof(Plugin), nameof(TranslateDialogueLines));
        var postfix = AccessTools.Method(typeof(Plugin), nameof(TranslateParsedDialogue));
        if (target == null || prefix == null || postfix == null)
        {
            LogSource.LogWarning("DialogueManager.ScriptParser was not found.");
            return false;
        }
        return TryPatch(harmony, target, prefix, postfix, "DialogueManager.ScriptParser");
    }

    private static bool PatchInteractableLoader(Harmony harmony)
    {
        var targetType = FindGameType("InteractableDialogueManager");
        var target = targetType == null ? null : AccessTools.Method(targetType, "LoadInteractableScript");
        var prefix = AccessTools.Method(typeof(Plugin), nameof(TranslateInteractableList));
        if (target == null || prefix == null)
        {
            LogSource.LogWarning("InteractableDialogueManager.LoadInteractableScript was not found.");
            return false;
        }
        return TryPatch(harmony, target, prefix, null, "InteractableDialogueManager.LoadInteractableScript");
    }

    private static bool PatchDialogueType(Harmony harmony)
    {
        var targetType = FindGameType("DialogueManager");
        var target = targetType == null ? null : AccessTools.Method(targetType, "Type");
        var prefix = AccessTools.Method(typeof(Plugin), nameof(TranslateDialogueInstance));
        if (target == null || prefix == null)
        {
            LogSource.LogWarning("DialogueManager.Type was not found.");
            return false;
        }
        return TryPatch(harmony, target, prefix, null, "DialogueManager.Type");
    }

    private static bool PatchInteractableType(Harmony harmony)
    {
        var targetType = FindGameType("InteractableDialogueManager");
        var target = targetType == null ? null : AccessTools.Method(targetType, "Type");
        var prefix = AccessTools.Method(typeof(Plugin), nameof(TranslateInteractableInstance));
        if (target == null || prefix == null)
        {
            LogSource.LogWarning("InteractableDialogueManager.Type was not found.");
            return false;
        }
        return TryPatch(harmony, target, prefix, null, "InteractableDialogueManager.Type");
    }

    private static void PatchTextAssignments(Harmony harmony)
    {
        // Do not detour Unity/TMP base setters. In this Unity 2022.3 build the
        // methods are native/managed bridge methods: Harmony can report a
        // successful patch, but the first real text assignment then stalls the
        // main thread before any MonoBehaviour Start callback. Static UI is
        // covered by the periodic scan, while dialogue, interactions and
        // tooltips have game-owned managed entry points below.
        var tooltipType = FindGameType("Tooltip");
        TryPatch(harmony,
            tooltipType == null ? null : AccessTools.Method(tooltipType, "SetText", new[] { typeof(string) }),
            AccessTools.Method(typeof(TooltipSetTextPatch), "Prefix"),
            null,
            "Tooltip.SetText(string)");
    }

    private static bool TryPatch(Harmony harmony, MethodInfo? target, MethodInfo? prefix,
        MethodInfo? postfix, string label)
    {
        if (target == null)
        {
            LogSource.LogWarning($"Patch target not found: {label}.");
            return false;
        }
        if (prefix == null && postfix == null)
            return false;
        try
        {
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(prefix),
                postfix: postfix == null ? null : new HarmonyMethod(postfix));
            var patchInfo = Harmony.GetPatchInfo(target);
            if (patchInfo == null || !patchInfo.Owners.Contains(PluginGuid))
            {
                LogSource.LogError($"Harmony reported no installed patch for {label}.");
                return false;
            }
            LogSource.LogInfo($"Patched {label}.");
            return true;
        }
        catch (Exception ex)
        {
            // A missing optional overload must not prevent the other text
            // paths from being installed.
            LogSource.LogError($"Unable to patch {label}: {ex.Message}");
            return false;
        }
    }

    // DialogueManager.Type() reveals mapScript[index][3] character by character.
    // Translate the parsed speaker/text arrays before that coroutine can start.
    private static void TranslateDialogueLines(List<string> __0)
    {
        var scene = SceneManager.GetActiveScene().name;
        for (var index = 0; index < __0.Count; index++)
        {
            var line = __0[index];
            if (string.IsNullOrEmpty(line))
                continue;

            var first = line.IndexOf(',');
            var second = first < 0 ? -1 : line.IndexOf(',', first + 1);
            var third = second < 0 ? -1 : line.IndexOf(',', second + 1);
            if (third < 0)
                continue;

            var speaker = line.Substring(second + 1, third - second - 1);
            var text = line.Substring(third + 1);
            var translatedSpeaker = Store.Translate(scene, "*", speaker);
            var translatedText = Store.Translate(scene, "*", text);
            if (translatedSpeaker != speaker || translatedText != text)
                __0[index] = line.Substring(0, second + 1) + translatedSpeaker + "," + translatedText;
        }
    }

    private static void TranslateParsedDialogue(object __instance)
    {
        var field = AccessTools.Field(__instance.GetType(), "mapScript");
        if (field?.GetValue(__instance) is not IDictionary mapScript)
            return;
        var scene = SceneManager.GetActiveScene().name;
        foreach (DictionaryEntry entry in mapScript)
        {
            if (entry.Value is not string[] columns || columns.Length < 4)
                continue;
            columns[2] = Store.Translate(scene, "*", columns[2]);
            columns[3] = Store.Translate(scene, "*", columns[3]);
        }
    }

    // Type() is the final boundary before the dialogue state machine starts
    // writing one character at a time. Translate the backing dictionary here
    // as a fallback for runtimes where a Unity/TMP virtual setter cannot be
    // detoured reliably.
    private static void TranslateDialogueInstance(object __instance)
    {
        var field = AccessTools.Field(__instance.GetType(), "mapScript");
        if (field?.GetValue(__instance) is not IDictionary mapScript)
            return;
        var scene = SceneManager.GetActiveScene().name;
        foreach (DictionaryEntry entry in mapScript)
        {
            if (entry.Value is not string[] columns || columns.Length < 4)
                continue;
            columns[2] = Store.Translate(scene, "*", columns[2]);
            columns[3] = Store.Translate(scene, "*", columns[3]);
        }
    }

    private static void TranslateInteractableInstance(object __instance)
    {
        var field = AccessTools.Field(__instance.GetType(), "interactableScript");
        if (field?.GetValue(__instance) is not IList script)
            return;
        var scene = SceneManager.GetActiveScene().name;
        for (var index = 0; index < script.Count; index++)
        {
            if (script[index] is string line)
                script[index] = Store.Translate(scene, "*", line);
        }
    }

    // The interactable typewriter reads directly from this list; mutating it on
    // entry gives the coroutine the complete Chinese sentence from its first glyph.
    private static void TranslateInteractableList(List<string> __0)
    {
        var scene = SceneManager.GetActiveScene().name;
        for (var index = 0; index < __0.Count; index++)
            __0[index] = Store.Translate(scene, "*", __0[index]);
    }

    private IEnumerator RefreshTextOverSeveralFrames()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            RefreshAllText();
            yield return null;
        }
    }

    private IEnumerator RefreshTextContinuously()
    {
        // Some panels are instantiated well after sceneLoaded (for example
        // completion and collectible popups). Keep a low-frequency pass so
        // those components are translated even if their setter was native or
        // another mod replaced the component after our Harmony patch.
        var interval = new WaitForSecondsRealtime(0.25f);
        while (true)
        {
            var translatedCount = RefreshAllText();
            if (translatedCount > 0)
                LogSource.LogInfo($"Runtime text refresh applied {translatedCount} translation(s).");
            yield return interval;
        }
    }

    private static int RefreshAllText()
    {
        var translatedCount = 0;
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            Fonts.Apply(text);
            var translated = Translate(text, text.text);
            if (!string.Equals(translated, text.text, StringComparison.Ordinal))
            {
                text.text = translated;
                translatedCount++;
            }
        }
        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            Fonts.Apply(text);
            var translated = Translate(text, text.text);
            if (!string.Equals(translated, text.text, StringComparison.Ordinal))
            {
                text.text = translated;
                translatedCount++;
            }
        }
        return translatedCount;
    }
}

[HarmonyPatch(typeof(TMP_Text), "set_text")]
internal static class TmpTextSetterPatch
{
    private static void Prefix(TMP_Text __instance, ref string value)
    {
        Plugin.Fonts.Apply(__instance);
        value = Plugin.Translate(__instance, value);
    }
}

[HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.SetText), new[] { typeof(string), typeof(bool) })]
internal static class TmpSetTextPatch
{
    // TMP names this first argument sourceText (unlike the text property
    // setter, whose argument is value). Keeping the original name lets
    // Harmony bind the by-ref argument reliably on Unity's Mono runtime.
    private static void Prefix(TMP_Text __instance, ref string sourceText)
    {
        Plugin.Fonts.Apply(__instance);
        sourceText = Plugin.Translate(__instance, sourceText);
    }
}

[HarmonyPatch(typeof(Text), "set_text")]
internal static class LegacyTextSetterPatch
{
    private static void Prefix(Text __instance, ref string value)
    {
        Plugin.Fonts.Apply(__instance);
        value = Plugin.Translate(__instance, value);
    }
}

[HarmonyPatch]
internal static class TooltipSetTextPatch
{
    // Tooltip.SetText is a game-owned method and is more reliable to hook than
    // relying only on the Unity/TMP base setter. Its argument is the complete
    // tooltip sentence, so exact and prefix rules can be applied before TMP
    // receives it.
    private static MethodBase? TargetMethod()
    {
        var type = Plugin.FindGameType("Tooltip");
        return type == null ? null : AccessTools.Method(type, "SetText", new[] { typeof(string) });
    }

    private static void Prefix(ref string content)
    {
        content = Plugin.Store.Translate(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, "*", content);
    }
}

internal sealed class LocalizationStore
{
    private readonly Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<Rule> _patterns = new List<Rule>();

    internal int Count => _exact.Count + _patterns.Count;

    internal static LocalizationStore Load(string path, ManualLogSource log)
    {
        var store = new LocalizationStore();
        if (!File.Exists(path))
        {
            log.LogWarning($"Localization file not found: {path}");
            return store;
        }
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (lineNumber == 1 || string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            var fields = line.Split('\t');
            if (fields.Length != 7)
            {
                log.LogWarning($"Ignoring malformed localization row {lineNumber}.");
                continue;
            }
            try
            {
                var rule = new Rule(fields[0], fields[1], fields[2], fields[3],
                    fields[4], int.Parse(fields[5]), Decode(fields[6]));
                store.Add(rule, log);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Ignoring localization row {lineNumber}: {ex.Message}");
            }
        }
        store._patterns.Sort((a, b) => b.SourceLength.CompareTo(a.SourceLength));
        return store;
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static string Key(string scene, string hierarchy, string sourceHash) => scene + "\u001f" + hierarchy + "\u001f" + sourceHash;

    private static string Digest(string value)
    {
        using (var sha256 = SHA256.Create())
        {
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            var result = new StringBuilder(bytes.Length * 2);
            foreach (var item in bytes)
                result.Append(item.ToString("x2"));
            return result.ToString();
        }
    }

    private void Add(Rule rule, ManualLogSource log)
    {
        if (rule.Mode == "exact")
        {
            var key = Key(rule.Scene, rule.Hierarchy, rule.SourceHash);
            if (_exact.TryGetValue(key, out var existing) && existing != rule.Translation)
                log.LogWarning($"Conflicting exact rule ignored: {rule.Id}");
            else
                _exact[key] = rule.Translation;
        }
        else
        {
            _patterns.Add(rule);
        }
    }

    internal string Translate(string scene, string hierarchy, string source)
    {
        // DialogueManager parses CRLF TextAsset lines with a regex whose final
        // capture includes the carriage return. Keep that delimiter in the
        // returned value, but do not require it in localization keys.
        var contentLength = source.Length;
        while (contentLength > 0 && source[contentLength - 1] == '\r')
            contentLength--;
        var content = contentLength == source.Length ? source : source.Substring(0, contentLength);
        var translated = TranslateContent(scene, hierarchy, content);
        if (translated == content)
            return source;
        return translated + source.Substring(contentLength);
    }

    private string TranslateContent(string scene, string hierarchy, string source)
    {
        var sourceHash = Digest(source);
        foreach (var key in new[] {
            Key(scene, hierarchy, sourceHash), Key("*", hierarchy, sourceHash),
            Key(scene, "*", sourceHash), Key("*", "*", sourceHash),
        })
            if (_exact.TryGetValue(key, out var translated))
                return translated;

        foreach (var rule in _patterns)
        {
            if (rule.Scene != "*" && rule.Scene != scene)
                continue;
            if (rule.Hierarchy != "*" && rule.Hierarchy != hierarchy)
                continue;
            if (rule.SourceLength > source.Length)
                continue;
            if (rule.Mode == "prefix" && Digest(source.Substring(0, rule.SourceLength)) == rule.SourceHash)
                return rule.Translation + source.Substring(rule.SourceLength);
            if (rule.Mode == "suffix" && Digest(source.Substring(source.Length - rule.SourceLength)) == rule.SourceHash)
                return source.Substring(0, source.Length - rule.SourceLength) + rule.Translation;
        }
        return source;
    }

    internal HashSet<uint> TranslationCodePoints()
    {
        var codePoints = new HashSet<uint>();
        for (uint value = 32; value <= 126; value++)
            codePoints.Add(value);
        foreach (var translation in _exact.Values)
            AddCodePoints(translation, codePoints);
        foreach (var rule in _patterns)
            AddCodePoints(rule.Translation, codePoints);
        return codePoints;
    }

    private static void AddCodePoints(string value, HashSet<uint> destination)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var codePoint = (uint)char.ConvertToUtf32(value, index);
            if (char.IsHighSurrogate(value[index]))
                index++;
            if (codePoint >= 32)
                destination.Add(codePoint);
        }
    }

    private sealed class Rule
    {
        internal Rule(string id, string mode, string scene, string hierarchy, string sourceHash, int sourceLength, string translation)
        {
            Id = id; Mode = mode; Scene = scene; Hierarchy = hierarchy;
            SourceHash = sourceHash; SourceLength = sourceLength; Translation = translation;
        }
        internal string Id { get; }
        internal string Mode { get; }
        internal string Scene { get; }
        internal string Hierarchy { get; }
        internal string SourceHash { get; }
        internal int SourceLength { get; }
        internal string Translation { get; }
    }
}

internal sealed class ChineseFontManager : IDisposable
{
    private const uint FrPrivate = 0x10;
    private string? _registeredPath;
    private Font? _legacy;
    private TMP_FontAsset? _tmp;

    internal bool IsReady => _legacy != null && _tmp != null;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveFontResourceEx(string fileName, uint flags, IntPtr reserved);

    internal static ChineseFontManager Load(string fontPath, ManualLogSource log, HashSet<uint> codePoints)
    {
        var manager = new ChineseFontManager();
        if (!File.Exists(fontPath))
        {
            log.LogError($"Bundled Chinese font not found: {fontPath}");
            return manager;
        }
        try
        {
            log.LogInfo($"Registering bundled Chinese font: {fontPath}");
            var registered = AddFontResourceEx(fontPath, FrPrivate, IntPtr.Zero);
            log.LogInfo($"Bundled font registration result: {registered}.");
            if (registered > 0)
                manager._registeredPath = fontPath;
            var candidates = new[] { "Source Han Sans SC", "Microsoft YaHei UI", "Microsoft YaHei", "SimHei" };
            manager._legacy = Font.CreateDynamicFontFromOSFont(candidates, 32);
            log.LogInfo($"Dynamic legacy font created: {manager._legacy != null}.");
            if (manager._legacy == null)
            {
                log.LogError("No usable Simplified Chinese font family was found.");
                return manager;
            }
            manager._legacy.name = "Erratum Chinese Dynamic Font";
            manager._tmp = CreateStaticTmpFontAsset(fontPath, codePoints, log);
            if (manager._tmp == null)
            {
                log.LogError("TMP could not create a static font asset from the bundled font; continuing with legacy UI font only.");
                return manager;
            }
            manager._tmp.name = "Erratum Chinese Static TMP Font";
        }
        catch (Exception ex)
        {
            log.LogError($"Unable to initialize Chinese font: {ex}");
        }
        return manager;
    }

    private static TMP_FontAsset? CreateStaticTmpFontAsset(string fontPath, HashSet<uint> codePoints,
        ManualLogSource log)
    {
        const int pointSize = 32;
        const int padding = 5;
        const int atlasWidth = 4096;
        const int atlasHeight = 4096;
        const GlyphRenderMode renderMode = GlyphRenderMode.SDFAA;

        var initializeResult = FontEngine.InitializeFontEngine();
        if (initializeResult != FontEngineError.Success)
        {
            log.LogError($"FontEngine initialization failed: {initializeResult}.");
            return null;
        }
        var loadResult = FontEngine.LoadFontFace(fontPath, pointSize, 0);
        if (loadResult != FontEngineError.Success)
        {
            log.LogError($"Unable to load bundled font face from file: {loadResult}.");
            return null;
        }

        var unicodeToGlyph = new Dictionary<uint, uint>();
        var uniqueGlyphIndexes = new HashSet<uint>();
        foreach (var unicode in codePoints)
        {
            if (!FontEngine.TryGetGlyphIndex(unicode, out var glyphIndex) || glyphIndex == 0)
                continue;
            unicodeToGlyph[unicode] = glyphIndex;
            uniqueGlyphIndexes.Add(glyphIndex);
        }

        var glyphIndexes = new List<uint>(uniqueGlyphIndexes);
        var freeGlyphRects = new List<GlyphRect>
        {
            new GlyphRect(0, 0, atlasWidth - 1, atlasHeight - 1),
        };
        var usedGlyphRects = new List<GlyphRect>();
        var atlasTexture = new Texture2D(atlasWidth, atlasHeight, TextureFormat.Alpha8, false)
        {
            name = "Erratum Chinese Glyph Atlas",
        };

        MethodInfo? renderMethod = null;
        foreach (var method in typeof(FontEngine).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
        {
            var parameters = method.GetParameters();
            if (method.Name == "TryAddGlyphsToTexture" && parameters.Length == 8 &&
                parameters[0].ParameterType == typeof(List<uint>))
            {
                renderMethod = method;
                break;
            }
        }
        if (renderMethod == null)
        {
            log.LogError("The bundled Unity FontEngine does not expose the expected glyph renderer.");
            UnityEngine.Object.Destroy(atlasTexture);
            return null;
        }

        var renderArguments = new object?[]
        {
            glyphIndexes, padding, GlyphPackingMode.BestShortSideFit,
            freeGlyphRects, usedGlyphRects, renderMode, atlasTexture, null,
        };
        var allGlyphsFit = (bool)(renderMethod.Invoke(null, renderArguments) ?? false);
        var renderedGlyphs = renderArguments[7] as Glyph[] ?? Array.Empty<Glyph>();
        var glyphByIndex = new Dictionary<uint, Glyph>();
        var validGlyphs = new List<Glyph>();
        foreach (var glyph in renderedGlyphs)
        {
            if (glyph == null)
                continue;
            glyphByIndex[glyph.index] = glyph;
            validGlyphs.Add(glyph);
        }
        if (validGlyphs.Count == 0)
        {
            log.LogError("FontEngine did not render any glyphs into the Chinese atlas.");
            UnityEngine.Object.Destroy(atlasTexture);
            return null;
        }
        var characters = new List<TMP_Character>();
        foreach (var pair in unicodeToGlyph)
            if (glyphByIndex.TryGetValue(pair.Value, out var glyph))
                characters.Add(new TMP_Character(pair.Key, glyph));

        var fontAsset = ScriptableObject.CreateInstance<TMP_FontAsset>();
        fontAsset.name = "Erratum Chinese Static TMP Font";
        fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
        fontAsset.faceInfo = FontEngine.GetFaceInfo();
        fontAsset.atlasTextures = new[] { atlasTexture };
        fontAsset.isMultiAtlasTexturesEnabled = false;
        SetFontAssetField(fontAsset, "m_Version", "1.1.0");
        SetFontAssetField(fontAsset, "m_GlyphTable", validGlyphs);
        SetFontAssetField(fontAsset, "m_CharacterTable", characters);
        SetFontAssetField(fontAsset, "m_AtlasTexture", atlasTexture);
        SetFontAssetField(fontAsset, "m_AtlasTextureIndex", 0);
        SetFontAssetField(fontAsset, "m_AtlasWidth", atlasWidth);
        SetFontAssetField(fontAsset, "m_AtlasHeight", atlasHeight);
        SetFontAssetField(fontAsset, "m_AtlasPadding", padding);
        SetFontAssetField(fontAsset, "m_AtlasRenderMode", renderMode);
        SetFontAssetField(fontAsset, "m_FreeGlyphRects", freeGlyphRects);
        SetFontAssetField(fontAsset, "m_UsedGlyphRects", usedGlyphRects);

        var shader = Shader.Find("TextMeshPro/Mobile/Distance Field") ?? Shader.Find("TextMeshPro/Distance Field");
        if (shader == null)
        {
            log.LogError("A TextMeshPro distance-field shader was not found.");
            UnityEngine.Object.Destroy(fontAsset);
            UnityEngine.Object.Destroy(atlasTexture);
            return null;
        }
        var material = new Material(shader) { name = "Erratum Chinese TMP Material" };
        material.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture);
        material.SetFloat(Shader.PropertyToID("_TextureWidth"), atlasWidth);
        material.SetFloat(Shader.PropertyToID("_TextureHeight"), atlasHeight);
        material.SetFloat(Shader.PropertyToID("_GradientScale"), padding + 1);
        fontAsset.material = material;
        fontAsset.ReadFontAssetDefinition();
        log.LogInfo($"Static TMP font created with {characters.Count} characters and {validGlyphs.Count} glyphs" +
                    (allGlyphsFit ? "." : "; atlas capacity was reached."));
        return fontAsset;
    }

    private static void SetFontAssetField(TMP_FontAsset fontAsset, string name, object value)
    {
        var field = typeof(TMP_FontAsset).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            throw new MissingFieldException(typeof(TMP_FontAsset).FullName, name);
        field.SetValue(fontAsset, value);
    }

    internal void Apply(TMP_Text text)
    {
        if (_tmp == null)
            return;
        if (text.font == null)
        {
            text.font = _tmp;
            return;
        }
        if (text.font == _tmp)
            return;
        // The game's shipped TMP atlases are not CPU-readable even when their
        // population mode is Dynamic. Prevent TMP from trying (and failing) to
        // write every Chinese glyph into those atlases before consulting our
        // fallback asset.
        if (text.font.atlasPopulationMode == AtlasPopulationMode.Dynamic)
            text.font.atlasPopulationMode = AtlasPopulationMode.Static;
        var fallbacks = text.font.fallbackFontAssetTable;
        if (fallbacks == null)
        {
            fallbacks = new List<TMP_FontAsset>();
            text.font.fallbackFontAssetTable = fallbacks;
        }
        if (!fallbacks.Contains(_tmp))
            fallbacks.Add(_tmp);
    }

    internal void Apply(Text text)
    {
        if (_legacy != null && text.font != _legacy)
            text.font = _legacy;
    }

    public void Dispose()
    {
        if (_tmp != null)
        {
            if (_tmp.material != null) UnityEngine.Object.Destroy(_tmp.material);
            foreach (var texture in _tmp.atlasTextures)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            UnityEngine.Object.Destroy(_tmp);
        }
        if (_legacy != null) UnityEngine.Object.Destroy(_legacy);
        if (_registeredPath != null) RemoveFontResourceEx(_registeredPath, FrPrivate, IntPtr.Zero);
    }
}
