using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.MissionEditorScripts.Buttons;
using NuclearOption.MissionEditorScripts.MultiSelect;
using NuclearOption.SavedMission;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GroundControlFactions;

/// <summary>
/// The Factions setting: a slider under Starting Funds on Customize Mission, and a row at the
/// bottom of the editor's Mission Settings. Both copy a slider the game already has, so they look
/// like the stock rows, and both write the mission tag.
/// </summary>
[HarmonyPatch]
internal static class FactionsSettingsPatches
{
    private const string Title = "Factions";

    private const string Hover = "How many factions fight, each with its own funds, warheads and base. No more than the map has bases (carriers count); a faction the mission gives no base is handed a free one.";

    private static Slider? editorSlider;

    private static TextMeshProUGUI? editorValue;

    [HarmonyPatch(typeof(CustomizeMissionMenu), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void CustomizeAwakePostfix(CustomizeMissionMenu __instance)
    {
        Transform? funds = Find(__instance.transform, "FundsPanel");
        Slider? template = funds == null ? null : funds.GetComponentInChildren<Slider>(true);
        if (template == null)
        {
            FactionsPlugin.Log.LogWarning("Customize Mission has changed shape; the Factions slider was not added.");
            return;
        }

        GameObject row = Object.Instantiate(template.gameObject, template.transform.parent);
        row.name = "GroundControlFactions Factions";
        row.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
        if (row.TryGetComponent(out ShowHoverText hover))
        {
            hover.SetText(Hover);
        }

        Transform? label = row.transform.Find("Label");
        TextMeshProUGUI? value = null;
        foreach (TextMeshProUGUI text in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text.transform == label)
            {
                text.text = Title;
            }
            else
            {
                value = text;
            }
        }

        Slider slider = row.GetComponent<Slider>();
        slider.onValueChanged = new Slider.SliderEvent();
        slider.minValue = FactionsService.MinFactions;
        slider.maxValue = FactionsService.MaxFactions;
        slider.wholeNumbers = true;
        slider.onValueChanged.AddListener(raw =>
        {
            Mission? mission = Traverse.Create(__instance).Field("mission").GetValue<Mission>();
            if (mission != null)
            {
                FactionsService.SetRequestedCount(mission, Mathf.RoundToInt(raw));
                RefreshFactionTabs(mission);
            }

            Show(value, Mathf.RoundToInt(raw));
        });
        row.AddComponent<RefreshWhenShown>().Refresh = () =>
        {
            int count = FactionsService.RequestedCount(Traverse.Create(__instance).Field("mission").GetValue<Mission>());
            slider.SetValueWithoutNotify(count);
            Show(value, count);
        };
    }

    /// <summary>
    /// A GC FACTIONS SETTINGS button beside Close that swaps the mission tabs for a page with the
    /// faction count and the unit cap, built like GC RTS's own page. With GC installed the button
    /// sits to the right of GC's, and opening one page closes the other.
    /// </summary>
    [HarmonyPatch(typeof(CustomizeMissionMenu), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SettingsPagePostfix(CustomizeMissionMenu __instance, Button ___closeButton)
    {
        Transform? columnTemplate = Find(__instance.transform, "MissionSettingsMenu");
        Transform? boxTemplate = Find(__instance.transform, "FundsPanel");
        Slider? sliderTemplate = boxTemplate == null ? null : boxTemplate.GetComponentInChildren<Slider>(true);
        if (___closeButton == null || columnTemplate == null || columnTemplate.parent == null || boxTemplate == null || sliderTemplate == null)
        {
            FactionsPlugin.Log.LogWarning("Customize Mission has changed shape; the GC Factions Settings page was not added.");
            return;
        }

        Transform row = columnTemplate.parent;
        List<GameObject> tabs = new();
        for (int i = 0; i < row.childCount; i++)
        {
            if (row.GetChild(i).gameObject.activeSelf)
            {
                tabs.Add(row.GetChild(i).gameObject);
            }
        }

        // Built inactive, so the copied column's scripts never wake before they are removed.
        GameObject page = new("GroundControlFactionsPage", typeof(RectTransform));
        page.SetActive(false);
        page.transform.SetParent(row, false);
        RectTransform pageRect = (RectTransform)page.transform;
        pageRect.anchorMin = pageRect.anchorMax = pageRect.pivot = new Vector2(0f, 1f);
        HorizontalLayoutGroup pageLayout = page.AddComponent<HorizontalLayoutGroup>();
        pageLayout.childAlignment = TextAnchor.UpperLeft;
        pageLayout.childControlWidth = false;
        pageLayout.childControlHeight = true;
        pageLayout.childForceExpandWidth = false;
        pageLayout.childForceExpandHeight = false;
        LayoutElement pageSize = page.AddComponent<LayoutElement>();
        ContentSizeFitter fit = page.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Transform column = Object.Instantiate(columnTemplate.gameObject, page.transform).transform;
        column.name = "GroundControlFactions Column";
        Object.DestroyImmediate(column.GetComponent<MissionSettingsTab>());
        Object.DestroyImmediate(column.GetComponent<ContentSizeFitter>());
        for (int i = column.childCount - 1; i >= 0; i--)
        {
            Transform child = column.GetChild(i);
            if (child.name == "Title")
            {
                child.GetComponent<TextMeshProUGUI>().text = "Factions";
            }
            else
            {
                Object.DestroyImmediate(child.gameObject);
            }
        }

        Transform box = Object.Instantiate(boxTemplate.gameObject, column).transform;
        for (int i = box.childCount - 1; i >= 0; i--)
        {
            Object.DestroyImmediate(box.GetChild(i).gameObject);
        }

        Mission? Edited()
        {
            return Traverse.Create(__instance).Field("mission").GetValue<Mission>();
        }

        List<System.Action> refreshers = new();
        AddPageSlider(
            refreshers,
            box,
            sliderTemplate,
            Title,
            Hover,
            FactionsService.MinFactions,
            FactionsService.MaxFactions,
            () => FactionsService.RequestedCount(Edited()),
            value =>
            {
                Mission? mission = Edited();
                if (mission != null)
                {
                    FactionsService.SetRequestedCount(mission, value);
                    RefreshFactionTabs(mission);
                }
            },
            value => value == FactionsService.MinFactions ? "2 (stock)" : value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddPageSlider(
            refreshers,
            box,
            sliderTemplate,
            "Unit Cap",
            "Most vehicles, ships and aircraft each side may have on the map at once; with more factions each gets its share. Without GC RTS it holds the stock AI's deployments.",
            0,
            FactionsUnitCapPatches.ModeCount,
            () => FactionsUnitCapPatches.Read(Edited()),
            value =>
            {
                Mission? mission = Edited();
                if (mission != null)
                {
                    FactionsUnitCapPatches.Write(mission, value);
                }
            },
            FactionsUnitCapPatches.Label);
        AddPageSlider(
            refreshers,
            box,
            sliderTemplate,
            "AI Supplies",
            "What the base game's AI may deploy. Mission uses the mission editor's AI aircraft limit and supply list; a size gives every faction that many of each aircraft and fighting vehicle instead.",
            0,
            FactionsAiSupplies.ModeCount,
            () => FactionsAiSupplies.Read(Edited()),
            value =>
            {
                Mission? mission = Edited();
                if (mission != null)
                {
                    FactionsAiSupplies.Write(mission, value);
                }
            },
            FactionsAiSupplies.Label);
        AddPageSlider(
            refreshers,
            box,
            sliderTemplate,
            "Random Bases",
            "On: at the start every land base goes neutral, whoever the mission gave it to, and each faction is handed a random one, so no two matches start alike.",
            0,
            1,
            () => FactionsService.RandomBases(Edited()) ? 1 : 0,
            value =>
            {
                Mission? mission = Edited();
                if (mission != null)
                {
                    FactionsUnitCapPatches.WriteTag(mission, FactionsService.RandomBasesTagPrefix, value);
                }
            },
            value => value == 0 ? "Off (mission)" : "On: all neutral, random starts");

        // A copy of Close, right of it; right of GC RTS's own button (200 wide, 120 out) when GC is in.
        GameObject copy = Object.Instantiate(___closeButton.gameObject, ___closeButton.transform.parent);
        copy.name = "GroundControlFactionsButton";
        RectTransform rect = (RectTransform)copy.transform;
        float closeWidth = rect.rect.width;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x + (220f - closeWidth), rect.sizeDelta.y);
        rect.anchoredPosition += new Vector2((closeWidth / 2f) + 130f + (FactionsService.GcLoaded ? 220f : 0f), 0f);
        TextMeshProUGUI? label = copy.GetComponentInChildren<TextMeshProUGUI>(true);
        Button button = copy.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        HorizontalLayoutGroup? rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        void Show(bool on)
        {
            if (on)
            {
                // GC's page shares the row: close it through its own button so its label flips back.
                Transform? gcPage = row.Find("GroundControlRts");
                Transform? gcButton = ___closeButton.transform.parent.Find("GroundControlRtsButton");
                if (gcPage != null && gcPage.gameObject.activeSelf && gcButton != null)
                {
                    gcButton.GetComponent<Button>().onClick.Invoke();
                }

                if (!page.activeSelf && rowLayout != null)
                {
                    Rect tabsRect = ((RectTransform)row).rect;
                    pageSize.minWidth = tabsRect.width - rowLayout.padding.horizontal;
                    pageSize.minHeight = tabsRect.height - rowLayout.padding.vertical;
                }

                refreshers.ForEach(r => r());
            }

            page.SetActive(on);
            tabs.ForEach(t => t.SetActive(!on));
            if (label != null)
            {
                label.text = on ? "Mission Settings" : "GC Factions Settings";
            }
        }

        button.onClick.AddListener(() => Show(!page.activeSelf));
        ___closeButton.onClick.AddListener(() => Show(false));

        // GC's page has hidden the tabs already: step aside without bringing them back beside it.
        page.AddComponent<YieldToGcPage>().Hide = () =>
        {
            page.SetActive(false);
            if (label != null)
            {
                label.text = "GC Factions Settings";
            }
        };
        Show(false);
    }

    private static void AddPageSlider(List<System.Action> refreshers, Transform box, Slider template, string title, string hover, int min, int max, System.Func<int> read, System.Action<int> write, System.Func<int, string> format)
    {
        GameObject row = Object.Instantiate(template.gameObject, box);
        row.name = "GroundControlFactions " + title;
        if (row.TryGetComponent(out ShowHoverText hoverText))
        {
            hoverText.SetText(hover);
        }

        Transform labelNode = row.transform.Find("Label");
        TextMeshProUGUI? value = null;
        foreach (TextMeshProUGUI text in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text.transform == labelNode)
            {
                text.text = title;
            }
            else
            {
                value = text;
            }
        }

        Slider slider = row.GetComponent<Slider>();
        slider.onValueChanged = new Slider.SliderEvent();
        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = true;
        slider.onValueChanged.AddListener(raw =>
        {
            write(Mathf.RoundToInt(raw));
            if (value != null)
            {
                value.text = format(Mathf.RoundToInt(raw));
            }
        });
        refreshers.Add(() =>
        {
            int current = Mathf.Clamp(read(), min, max);
            slider.SetValueWithoutNotify(current);
            if (value != null)
            {
                value.text = format(current);
            }
        });
    }

    [HarmonyPatch(typeof(MissionSettingsTab), "Awake")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void EditorAwakePostfix(Slider ___startingRankSlider, TextMeshProUGUI ___startingRankLabel)
    {
        if (GameManager.gameState != GameState.Editor || ___startingRankSlider == null)
        {
            return;
        }

        Transform source = RowOf(___startingRankSlider.transform);
        string? sliderPath = PathFrom(source, ___startingRankSlider.transform);
        string? labelPath = ___startingRankLabel == null ? null : PathFrom(source, ___startingRankLabel.transform);
        GameObject row = Object.Instantiate(source.gameObject, source.parent);
        row.name = "GroundControlFactions";
        row.transform.SetAsLastSibling();

        // Starting Rank shares its row with Score Rank Multiplier: keep the slider and its label,
        // and shrink the row by the share of children dropped.
        Transform? sliderTop = sliderPath == null ? null : row.transform.Find(sliderPath.Split('/')[0]);
        if (sliderTop != null && row.transform.childCount > 2)
        {
            int keep = sliderTop.GetSiblingIndex();
            int children = row.transform.childCount;
            for (int i = children - 1; i >= 0; i--)
            {
                if (i != keep && i != keep - 1)
                {
                    Object.DestroyImmediate(row.transform.GetChild(i).gameObject);
                }
            }

            RectTransform rect = (RectTransform)row.transform;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, rect.sizeDelta.y * row.transform.childCount / children);
        }

        editorSlider = (sliderPath == null ? null : row.transform.Find(sliderPath)?.GetComponent<Slider>()) ?? row.GetComponentInChildren<Slider>(true);
        editorValue = labelPath == null ? null : row.transform.Find(labelPath)?.GetComponent<TextMeshProUGUI>();
        foreach (TextMeshProUGUI text in row.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text != editorValue)
            {
                text.text = Title + " (each base list then offers them)";
            }
        }

        editorSlider.onValueChanged = new Slider.SliderEvent();
        editorSlider.minValue = FactionsService.MinFactions;
        editorSlider.maxValue = FactionsService.MaxFactions;
        editorSlider.wholeNumbers = true;
        editorSlider.onValueChanged.AddListener(raw =>
        {
            if (MissionManager.CurrentMission != null)
            {
                FactionsService.SetRequestedCount(MissionManager.CurrentMission, Mathf.RoundToInt(raw));
                RefreshFactionTabs(MissionManager.CurrentMission);
            }

            Show(editorValue, Mathf.RoundToInt(raw));
        });

        // The game fills the tab (SetMission) before it first wakes, so the row is filled here too.
        if (MissionManager.CurrentMission != null)
        {
            ShowEditor(MissionManager.CurrentMission);
        }
    }

    [HarmonyPatch(typeof(MissionSettingsTab), "SetMission")]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void SetMissionPostfix(Mission mission)
    {
        if (GameManager.gameState == GameState.Editor)
        {
            ShowEditor(mission);
        }
    }

    /// <summary>The faction tab lists the mission's factions: give it one per extra first.</summary>
    [HarmonyPatch(typeof(FactionSettingsTab), nameof(FactionSettingsTab.SetMission))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void FactionTabSetMissionPrefix(Mission mission)
    {
        if (mission != null)
        {
            FactionsService.SyncMissionFactions(mission);
        }
    }

    [HarmonyPatch(typeof(FactionSettingsTab), nameof(FactionSettingsTab.SetMission))]
    [HarmonyPostfix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static void FactionTabSetMissionPostfix(TMP_Dropdown ___factionSelect)
    {
        if (___factionSelect != null && ___factionSelect.options.Count > 0)
        {
            ___factionSelect.options[0].text = "All";
            ___factionSelect.RefreshShownValue();
        }
    }

    /// <summary>The game picks "every faction" by the option's text, "Both", renamed above.</summary>
    [HarmonyPatch(typeof(FactionSettingsTab), nameof(FactionSettingsTab.SelectFaction))]
    [HarmonyPrefix]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Harmony calls it.")]
    private static bool SelectFactionPrefix(TMP_Dropdown ___factionSelect, MultiSelect<MissionFaction> ___selectedFactions, Mission ___mission)
    {
        if (___factionSelect.value != 0 || ___mission == null)
        {
            return true;
        }

        ___selectedFactions.ReplaceTargets(___mission.factions);
        return false;
    }

    private static void RefreshFactionTabs(Mission mission)
    {
        foreach (FactionSettingsTab tab in Resources.FindObjectsOfTypeAll<FactionSettingsTab>())
        {
            if (tab.gameObject.scene.IsValid())
            {
                tab.SetMission(mission);
            }
        }
    }

    private static void ShowEditor(Mission mission)
    {
        if (editorSlider != null)
        {
            int count = FactionsService.RequestedCount(mission);
            editorSlider.SetValueWithoutNotify(count);
            Show(editorValue, count);
        }
    }

    private static void Show(TextMeshProUGUI? value, int count)
    {
        if (value != null)
        {
            value.text = count == FactionsService.MinFactions ? "2 (stock)" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static Transform? Find(Transform root, string name)
    {
        Stack<Transform> open = new();
        open.Push(root);
        while (open.Count > 0)
        {
            Transform node = open.Pop();
            if (node.name == name)
            {
                return node;
            }

            for (int i = 0; i < node.childCount; i++)
            {
                open.Push(node.GetChild(i));
            }
        }

        return null;
    }

    /// <summary>The child path from <paramref name="root"/> down to <paramref name="node"/>, or null
    /// when it is not under it.</summary>
    private static string? PathFrom(Transform root, Transform node)
    {
        string path = string.Empty;
        for (Transform? at = node; at != null; at = at.parent)
        {
            if (at == root)
            {
                return path;
            }

            path = path.Length == 0 ? at.name : at.name + "/" + path;
        }

        return null;
    }

    /// <summary>The slider's row in the column: the first ancestor whose parent lays rows out.</summary>
    private static Transform RowOf(Transform node)
    {
        while (node.parent != null && node.parent.GetComponent<LayoutGroup>() == null)
        {
            node = node.parent;
        }

        return node;
    }

    /// <summary>Closes the Factions page when GC RTS's page is opened beside it.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Unity creates it through AddComponent.")]
    private sealed class YieldToGcPage : MonoBehaviour
    {
        internal System.Action? Hide { get; set; }

        private void Update()
        {
            Transform? gc = transform.parent.Find("GroundControlRts");
            if (gc != null && gc.gameObject.activeSelf)
            {
                Hide?.Invoke();
            }
        }
    }

    /// <summary>Reads the mission's count when the menu opens. A frame late: the menu is shown
    /// before it is handed the mission.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Unity creates it through AddComponent.")]
    private sealed class RefreshWhenShown : MonoBehaviour
    {
        private bool pending;

        internal System.Action? Refresh { get; set; }

        private void OnEnable()
        {
            pending = true;
        }

        private void Update()
        {
            if (pending)
            {
                pending = false;
                Refresh?.Invoke();
            }
        }
    }
}
