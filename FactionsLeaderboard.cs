using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.UI;
using UnityEngine;
using UnityEngine.UI;

namespace GroundControlFactions;

/// <summary>
/// Gives the faction list one column per HQ. The stock list has two columns side by side and waits
/// for exactly two HQs, so with more it never fills at all; this clones the last column for each
/// extra HQ, sets every column up, puts them in rows (one row up to four, else two), and scales the
/// menu down when it would not fit the screen.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Unity creates it through AddComponent.")]
internal sealed class FactionsLeaderboard : MonoBehaviour
{
    private const int SingleRowMax = 4;

    /// <summary>Space kept clear round the menu when it is scaled to fit, in canvas units.</summary>
    private const float ScreenMargin = 40f;

    private const string FlagName = "GC Faction Flag";

    private static readonly Dictionary<string, Sprite?> Pictures = new();

    private readonly List<Transform> rows = new();
    private LeaderboardFactionList list = null!;
    private RectTransform? organizer;
    private RectTransform? menuRoot;
    private int shown;

    /// <summary>The stock columns' row, made a column of rows with the same spacing.</summary>
    private static Transform AddRow(RectTransform organizer, HorizontalLayoutGroup template, int index)
    {
        GameObject row = new("GC Faction Row " + index, typeof(RectTransform));
        row.transform.SetParent(organizer, false);
        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = template.spacing;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = template.childControlWidth;
        layout.childControlHeight = template.childControlHeight;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        ContentSizeFitter fit = row.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return row.transform;
    }

    /// <summary>The join card is one picture with the stock faction's banner in it: an extra
    /// faction's card shows its own picture (<c>Banners/&lt;name&gt;.jpg</c>, embedded) with its
    /// flag on top.</summary>
    private static void SetJoinCard(LeaderboardFactionItem display, FactionHQ hq)
    {
        Transform? card = display.transform.Find("Join Header/factionImage");
        if (card == null || !card.TryGetComponent(out Image image))
        {
            return;
        }

        // A column cloned from an extra one carries its flag along.
        Transform? flag = card.Find(FlagName);
        if (!FactionsService.IsExtra(hq.faction))
        {
            if (flag != null)
            {
                Destroy(flag.gameObject);
            }

            return;
        }

        if (flag == null)
        {
            GameObject made = new(FlagName, typeof(RectTransform), typeof(Image));
            made.transform.SetParent(card, false);
            RectTransform rect = (RectTransform)made.transform;
            rect.anchorMin = new Vector2(0f, 0.05f);
            rect.anchorMax = new Vector2(1f, 0.95f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            flag = made.transform;
        }

        Image flagImage = flag.GetComponent<Image>();
        flagImage.sprite = hq.faction.factionColorLogo;
        flagImage.preserveAspect = true;
        flagImage.raycastTarget = false;
        Sprite? picture = GetPicture(hq.faction.factionName);
        image.sprite = picture != null ? picture : hq.faction.factionColorLogo;
        image.preserveAspect = picture == null;
        flagImage.enabled = picture != null;
    }

    /// <summary>The faction's embedded card picture, loaded once; null if there is none.</summary>
    private static Sprite? GetPicture(string faction)
    {
        if (Pictures.TryGetValue(faction, out Sprite? cached))
        {
            return cached;
        }

        Sprite? sprite = null;
        using (System.IO.Stream? stream = typeof(FactionsLeaderboard).Assembly.GetManifestResourceStream("Banners." + faction + ".jpg"))
        {
            if (stream != null)
            {
                using System.IO.MemoryStream bytes = new();
                stream.CopyTo(bytes);
                Texture2D texture = new(2, 2, TextureFormat.RGB24, false);
                if (texture.LoadImage(bytes.ToArray()))
                {
                    // Kept through the asset cleanup every scene load runs.
                    texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
            }
        }

        Pictures[faction] = sprite;
        return sprite;
    }

    private void Awake()
    {
        list = GetComponent<LeaderboardFactionList>();
    }

    private void Update()
    {
        int count = FactionRegistry.HQLookup.Count;
        if (count <= 2)
        {
            return;
        }

        if (count != shown)
        {
            shown = count;
            Rebuild();
        }

        FitToScreen();
    }

    private void Rebuild()
    {
        Traverse traverse = Traverse.Create(list);
        LeaderboardFactionItem[] displays = traverse.Field("factionDisplays").GetValue<LeaderboardFactionItem[]>();
        if (displays == null || displays.Length == 0)
        {
            return;
        }

        FactionHQ[] hqs = FactionRegistry.GetSortedHQs();
        List<LeaderboardFactionItem> grown = new(displays);
        LeaderboardFactionItem template = displays[displays.Length - 1];
        while (grown.Count < hqs.Length)
        {
            LeaderboardFactionItem copy = Instantiate(template, template.transform.parent);
            copy.name = template.name + " " + grown.Count;
            grown.Add(copy);
        }

        // Unhooked before the array changes, so the new columns are hooked by the stock code below.
        traverse.Method("RemovePlayerFactionEvent").GetValue();
        displays = grown.ToArray();
        traverse.Field("factionDisplays").SetValue(displays);
        MenuMode mode = LeaderboardMenu.i != null ? Traverse.Create(LeaderboardMenu.i).Field("currentMode").GetValue<MenuMode>() : MenuMode.Join;
        for (int i = 0; i < displays.Length; i++)
        {
            displays[i].gameObject.SetActive(i < hqs.Length);
            if (i < hqs.Length && displays[i].HQ != hqs[i])
            {
                displays[i].SetupHq(hqs[i]);
                SetJoinCard(displays[i], hqs[i]);
            }

            displays[i].SetMenuMode(mode);
        }

        traverse.Field("hasHQs").SetValue(true);
        traverse.Method("AddPlayerFactionEvent").GetValue();
        Arrange(displays, hqs.Length);
    }

    private void Arrange(LeaderboardFactionItem[] displays, int count)
    {
        if (organizer == null)
        {
            organizer = displays[0].transform.parent as RectTransform;
            menuRoot = organizer != null ? organizer.parent as RectTransform : null;
            HorizontalLayoutGroup? horizontal = organizer != null ? organizer.GetComponent<HorizontalLayoutGroup>() : null;
            if (organizer == null || horizontal == null)
            {
                return;
            }

            // A layout group cannot be swapped for another on the same object in one frame.
            float spacing = horizontal.spacing;
            rows.Add(AddRow(organizer, horizontal, 0));
            rows.Add(AddRow(organizer, horizontal, 1));
            DestroyImmediate(horizontal);
            VerticalLayoutGroup vertical = organizer.gameObject.AddComponent<VerticalLayoutGroup>();
            vertical.spacing = spacing;
            vertical.childAlignment = TextAnchor.UpperCenter;
            vertical.childControlWidth = false;
            vertical.childControlHeight = false;
            vertical.childForceExpandWidth = false;
            vertical.childForceExpandHeight = false;
        }

        if (rows.Count < 2)
        {
            return;
        }

        int perRow = count <= SingleRowMax ? count : (count + 1) / 2;
        for (int i = 0; i < displays.Length; i++)
        {
            displays[i].transform.SetParent(rows[i < perRow ? 0 : 1], false);
            displays[i].transform.SetAsLastSibling();
        }

        rows[1].gameObject.SetActive(count > perRow);
        LayoutRebuilder.ForceRebuildLayoutImmediate(organizer!);
    }

    private void FitToScreen()
    {
        // The menu's own rect does not grow with the rows (only the rows fit their content), so the
        // rows are measured, in screen canvas units, against the root canvas.
        Canvas? canvas = GetComponentInParent<Canvas>();
        if (menuRoot == null || canvas == null || rows.Count < 2)
        {
            return;
        }

        Transform root = canvas.rootCanvas.transform;
        Vector2 room = ((RectTransform)root).rect.size - new Vector2(ScreenMargin, ScreenMargin);
        float width = 0f;
        float height = 0f;
        foreach (Transform row in rows)
        {
            if (row.gameObject.activeSelf)
            {
                Rect rect = ((RectTransform)row).rect;
                width = Mathf.Max(width, rect.width * row.lossyScale.x / root.lossyScale.x);
                height += rect.height * row.lossyScale.y / root.lossyScale.y;
            }
        }

        // Measured at the current scale, so divided back out to the unscaled size.
        float current = Mathf.Max(0.01f, menuRoot.localScale.x);
        float scale = Mathf.Min(1f, room.x * current / Mathf.Max(1f, width), room.y * current / Mathf.Max(1f, height));
        if (Mathf.Abs(menuRoot.localScale.x - scale) > 0.01f)
        {
            menuRoot.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
