
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using TMPro;
using Debug = UnityEngine.Debug;

[Serializable]
public class AIReadingScores
{
    public float joy;
    public float sadness;
    public float fear;
    public float anger;
    public float surprise;
    public float disgust;
    public float neutral;
}

[Serializable]
public class AIReadingPassage
{
    public int id;
    public int page;
    public string part;
    public string text;
    public string emotion;
    public float confidence;
    public string musicMood;
    public AIReadingScores scores;
}

[Serializable]
public class AIReadingDatabase
{
    public string bookTitle;
    public AIReadingPassage[] passages;
}

[Serializable]
public class ParagraphMusicCueAssignment
{
    [Tooltip("Resources key for the book JSON, for example Books/alice_emotion_results_unity.")]
    public string jsonResourcePath;

    [Tooltip("The passage id from that book's JSON data.")]
    public int passageId;

    public AudioClip musicClip;

    [Range(0f, 1f)]
    public float volume = 1f;
}

[Serializable]
public class WordMusicCueAssignment
{
    [Tooltip("Resources key for the book JSON, for example Books/alice_emotion_results_unity.")]
    public string jsonResourcePath;

    [Tooltip("The passage id that contains the target word.")]
    public int passageId;

    [Tooltip("The word to match. Matching ignores case and surrounding punctuation.")]
    public string word;

    public AudioClip musicClip;

    [Range(0f, 1f)]
    public float volume = 1f;
}

public class AIReadingController : MonoBehaviour
{
    [Header("Reading UI")]
    public TMP_Text titleText;
    public TMP_Text storyText;
    public TMP_Text pageNumberText;

    [Header("Page Navigation")]
    public Button previousButton;
    public Button nextButton;

    [Header("Book Resources")]
    public string jsonResourcePath = "Books/book_emotion_results_unity";
    public string bookTitle = "The Little Star That Fell Off the Sky";

    [Header("Little Star Manual Pages")]
    [Tooltip("Used only for Books/book_emotion_results_unity. Enter up to 24 page texts here; blank entries keep the imported text.")]
    [TextArea(3, 8)]
    public List<string> littleStarManualPages = CreateEmptyLittleStarPages();

    [Tooltip("Page number to show when using the context menu preview command in Edit Mode.")]
    [Min(1)]
    public int littleStarEditorPreviewPage = 1;

    [Header("Screen Panels")]
    public GameObject homePanel;
    public GameObject browseBooksPanel;
    public GameObject bookPagePanel;
    public GameObject interactiveBookPagePanel;
    public GameObject eyeTrackingRoot;

    public GameObject ActiveReaderPanel
    {
        get
        {
            return IsLittleStarBook() && interactiveBookPagePanel != null
                ? interactiveBookPagePanel
                : bookPagePanel;
        }
    }

    public GameObject GetReaderPanelForResource(string resourcePath)
    {
        return string.Equals(
                resourcePath,
                LittleStarResourcePath,
                StringComparison.OrdinalIgnoreCase) &&
               interactiveBookPagePanel != null
            ? interactiveBookPagePanel
            : bookPagePanel;
    }

    public void SetReaderPanelsActive(bool active)
    {
        if (eyeTrackingRoot != null)
            eyeTrackingRoot.SetActive(active);

        GameObject selectedPanel = ActiveReaderPanel;

        if (bookPagePanel != null)
            bookPagePanel.SetActive(active && bookPagePanel == selectedPanel);

        if (interactiveBookPagePanel != null)
            interactiveBookPagePanel.SetActive(
                active && interactiveBookPagePanel == selectedPanel
            );

        BindReaderUiReferences();
    }

    private void BindReaderUiReferences()
    {
        if (boundPreviousButton != null)
            boundPreviousButton.onClick.RemoveListener(PreviousPage);
        if (boundNextButton != null)
            boundNextButton.onClick.RemoveListener(NextPage);

        GameObject panel = ActiveReaderPanel;
        if (panel == null)
            return;

        TMP_Text[] texts = panel.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] == null)
                continue;

            if (texts[i].name == "BookTitleText")
                titleText = texts[i];
            else if (texts[i].name == "PageNumber")
                pageNumberText = texts[i];
            else if (texts[i].name == "StoryText (1)")
                storyText = texts[i];
        }

        Button[] buttons = panel.GetComponentsInChildren<Button>(true);
        previousButton = null;
        nextButton = null;
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null)
                continue;

            if (buttons[i].name == "PreviousButton")
                previousButton = buttons[i];
            else if (buttons[i].name == "NextButton")
                nextButton = buttons[i];
        }

        boundPreviousButton = previousButton;
        boundNextButton = nextButton;
        if (boundPreviousButton != null)
            boundPreviousButton.onClick.AddListener(PreviousPage);
        if (boundNextButton != null)
            boundNextButton.onClick.AddListener(NextPage);
    }

    [Header("Eye Tracking")]
    public bool startEyeTrackingWhenBookOpens = true;

    [Header("Paragraph Music")]
    [Tooltip("How long gaze must stay on a passage before its assigned music starts.")]
    public float paragraphMusicDwellTime = 0.8f;

    [Tooltip("Add an entry only where you want music to start or change. Unlisted passages keep the current track playing.")]
    public List<ParagraphMusicCueAssignment> paragraphMusicCues =
        new List<ParagraphMusicCueAssignment>();

    [Tooltip("A matching word starts or changes music after Word Dwell Time on StoryText's GazeRegion.")]
    public List<WordMusicCueAssignment> wordMusicCues =
        new List<WordMusicCueAssignment>();

    [Header("Optional Import")]
    public Button chooseBookButton;
    public string aiServiceUrl = "http://127.0.0.1:5005/analyze";

    private AIReadingDatabase database;

    private int currentPageIndex;

    private string[] pages;

    private readonly List<AIReadingPassage> pagePassages =
        new List<AIReadingPassage>();

    private readonly List<List<AIReadingPassage>> passageGroupsByPage =
        new List<List<AIReadingPassage>>();

    private struct PassageTextRange
    {
        public AIReadingPassage passage;
        public int startIndex;
        public int endIndex;
    }

    private readonly List<PassageTextRange> currentPagePassageRanges =
        new List<PassageTextRange>();

    private TMP_Text[] littleStarPageTextObjects = new TMP_Text[0];

    private const string LittleStarResourcePath =
        "Books/book_emotion_results_unity";

    private static List<string> CreateEmptyLittleStarPages()
    {
        List<string> entries = new List<string>(24);
        for (int i = 0; i < 24; i++)
            entries.Add(string.Empty);
        return entries;
    }

    private int focusedPassageId = -1;
    private float passageGazeTimer;
    private bool passageCueTriggered;
    private bool wordCueTriggeredForPassage;
    private GazeRegion storyTextGazeRegion;

    private Coroutine storyStartCoroutine;
    private Coroutine readerTrackingCoroutine;
    private Button boundPreviousButton;
    private Button boundNextButton;

    public int CurrentPageIndex
    {
        get { return currentPageIndex; }
    }

    public int TotalPages
    {
        get { return pages == null ? 0 : pages.Length; }
    }

    public AIReadingPassage CurrentPassage
    {
        get
        {
            if (pagePassages == null ||
                currentPageIndex < 0 ||
                currentPageIndex >= pagePassages.Count)
            {
                return null;
            }

            return pagePassages[currentPageIndex];
        }
    }

    public AIReadingDatabase CurrentDatabase
    {
        get { return database; }
    }

    private void Start()
    {
        BindReaderUiReferences();

        if (chooseBookButton != null)
            chooseBookButton.onClick.AddListener(ChooseBookFile);

        BindStoryTextGazeRegion();
    }

    private void Update()
    {
        UpdateParagraphMusicFromGaze();
    }

    private void OnDestroy()
    {
        if (boundPreviousButton != null)
            boundPreviousButton.onClick.RemoveListener(PreviousPage);

        if (boundNextButton != null)
            boundNextButton.onClick.RemoveListener(NextPage);

        if (chooseBookButton != null)
            chooseBookButton.onClick.RemoveListener(ChooseBookFile);

        if (storyTextGazeRegion != null)
        {
            storyTextGazeRegion.onWordDwellComplete.RemoveListener(
                HandleWordDwellComplete
            );
        }

        if (storyStartCoroutine != null)
            StopCoroutine(storyStartCoroutine);

        if (readerTrackingCoroutine != null)
            StopCoroutine(readerTrackingCoroutine);
    }

    // --------------------------------------------------
    // BROWSE BOOKS: BUTTON METHODS
    // --------------------------------------------------

    public void OpenLittleStar()
    {
        OpenBuiltInBook(
            "Books/book_emotion_results_unity",
            "The Little Star That Fell Off the Sky"
        );
    }

    public void OpenAlice()
    {
        OpenBuiltInBook(
            "Books/alice_emotion_results_unity",
            "Alice in Wonderland"
        );
    }

    private void OpenBuiltInBook(string resourcePath, string fallbackTitle)
    {
        OpenBook(resourcePath, fallbackTitle);
    }

    public void OpenBook(string resourcePath, string fallbackTitle)
    {
        jsonResourcePath = resourcePath;
        bookTitle = fallbackTitle;

        if (homePanel != null)
            homePanel.SetActive(false);

        if (browseBooksPanel != null)
            browseBooksPanel.SetActive(false);

        SetReaderPanelsActive(true);
        LoadBook();

        if (pages == null || pages.Length == 0)
            return;

        StartReaderTracking();
    }

    // --------------------------------------------------
    // LOAD BUILT-IN BOOK AND RESTORE PROGRESS
    // --------------------------------------------------

    public void LoadBook()
    {
        SetReaderPanelsActive(true);

        TextAsset jsonFile = Resources.Load<TextAsset>(jsonResourcePath);

        if (jsonFile == null)
        {
            Debug.LogError(
                "Could not find book JSON in Resources: " + jsonResourcePath
            );

            ShowMessage("Could not load this book.");

            pages = null;
            pagePassages.Clear();
            passageGroupsByPage.Clear();
            currentPagePassageRanges.Clear();

            return;
        }

        try
        {
            database = ParseDatabase(jsonFile.text);

            if (database == null ||
                database.passages == null ||
                database.passages.Length == 0)
            {
                Debug.LogError("Book JSON contains no passages.");

                ShowMessage("This book has no passages.");

                pages = null;
                pagePassages.Clear();
                passageGroupsByPage.Clear();
                currentPagePassageRanges.Clear();

                return;
            }

            if (!string.IsNullOrWhiteSpace(database.bookTitle))
                bookTitle = database.bookTitle;

            PrepareBuiltInPages();

            // Little Star should always open at the beginning. Other built-in
            // books continue restoring their saved reading position.
            currentPageIndex = IsLittleStarBook()
                ? 0
                : ReadingProgressStore.LoadPageIndex(jsonResourcePath);

            currentPageIndex = Mathf.Clamp(
                currentPageIndex,
                0,
                pages.Length - 1
            );

            DisplayCurrentPage();

            Debug.Log(
                "Loaded book: " + bookTitle +
                " | Pages: " + pages.Length
            );
        }
        catch (Exception ex)
        {
            Debug.LogError("Error loading book: " + ex.Message);

            ShowMessage("Could not read this book's data.");

            pages = null;
            pagePassages.Clear();
            passageGroupsByPage.Clear();
            currentPagePassageRanges.Clear();
        }
    }

    private void PrepareBuiltInPages()
    {
        SortedDictionary<int, List<AIReadingPassage>> groupedPages =
            new SortedDictionary<int, List<AIReadingPassage>>();

        foreach (AIReadingPassage passage in database.passages)
        {
            if (passage == null ||
                string.IsNullOrWhiteSpace(passage.text))
            {
                continue;
            }

            int pageNumber = passage.page;

            if (pageNumber <= 0)
                pageNumber = groupedPages.Count + 1;

            if (!groupedPages.ContainsKey(pageNumber))
            {
                groupedPages.Add(
                    pageNumber,
                    new List<AIReadingPassage>()
                );
            }

            groupedPages[pageNumber].Add(passage);
        }

        List<string> pageTexts = new List<string>();
        pagePassages.Clear();
        passageGroupsByPage.Clear();

        foreach (KeyValuePair<int, List<AIReadingPassage>> entry
                 in groupedPages)
        {
            List<AIReadingPassage> passages = entry.Value;

            if (passages.Count == 0)
                continue;

            StringBuilder combinedText = new StringBuilder();

            foreach (AIReadingPassage passage in passages)
            {
                if (combinedText.Length > 0)
                    combinedText.Append("\n\n");

                combinedText.Append(passage.text);
            }

            pageTexts.Add(combinedText.ToString());

            // The first passage represents this page's metadata.
            pagePassages.Add(passages[0]);
            passageGroupsByPage.Add(new List<AIReadingPassage>(passages));
        }

        pages = pageTexts.ToArray();

        ApplyLittleStarManualPages();

        if (pages.Length == 0)
        {
            pages = new string[] { "No readable text was found." };
            pagePassages.Clear();
            passageGroupsByPage.Clear();
        }
    }

    private void ApplyLittleStarManualPages()
    {
        if (!string.Equals(
                jsonResourcePath,
                LittleStarResourcePath,
                StringComparison.OrdinalIgnoreCase) ||
            littleStarManualPages == null ||
            littleStarManualPages.Count == 0)
        {
            return;
        }

        int count = Mathf.Min(
            pages.Length,
            Mathf.Min(littleStarManualPages.Count, pagePassages.Count)
        );

        for (int i = 0; i < count; i++)
        {
            string manualText = littleStarManualPages[i];
            if (string.IsNullOrWhiteSpace(manualText))
                continue;

            pages[i] = manualText;

            // Keep the JSON passage metadata, but make gaze-to-text ranges
            // refer to the manually entered page content.
            AIReadingPassage pagePassage = pagePassages[i];
            if (pagePassage != null)
                pagePassage.text = manualText;

            List<AIReadingPassage> grouped = passageGroupsByPage[i];
            if (grouped.Count > 0 && grouped[0] != pagePassage)
                grouped[0].text = manualText;
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Preview Little Star Page In Scene")]
    public void PreviewLittleStarPageInScene()
    {
        if (storyText == null ||
            !string.Equals(jsonResourcePath, LittleStarResourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning("Little Star preview needs its reader and resource path.");
            return;
        }

        int index = Mathf.Clamp(littleStarEditorPreviewPage - 1, 0, 23);
        string text = littleStarManualPages != null &&
                      index < littleStarManualPages.Count &&
                      !string.IsNullOrWhiteSpace(littleStarManualPages[index])
            ? littleStarManualPages[index]
            : GetLittleStarImportedPageText(index);

        UnityEditor.Undo.RecordObject(storyText, "Preview Little Star Page");
        storyText.text = text;
        storyText.ForceMeshUpdate();
        UnityEditor.EditorUtility.SetDirty(storyText);
        Debug.Log("[Reading] Previewing Little Star page " + (index + 1) + ".");
    }

    private string GetLittleStarImportedPageText(int pageIndex)
    {
        TextAsset jsonFile = Resources.Load<TextAsset>(LittleStarResourcePath);
        if (jsonFile == null)
            return string.Empty;

        AIReadingDatabase previewDatabase = ParseDatabase(jsonFile.text);
        if (previewDatabase == null || previewDatabase.passages == null)
            return string.Empty;

        for (int i = 0; i < previewDatabase.passages.Length; i++)
        {
            AIReadingPassage passage = previewDatabase.passages[i];
            if (passage != null && passage.page == pageIndex + 1)
                return passage.text;
        }

        return string.Empty;
    }
#endif

    // --------------------------------------------------
    // EYE TRACKING LIFECYCLE
    // --------------------------------------------------

    public void StartReaderTracking()
    {
        if (!startEyeTrackingWhenBookOpens)
            return;

        if (readerTrackingCoroutine != null)
            StopCoroutine(readerTrackingCoroutine);

        readerTrackingCoroutine =
            StartCoroutine(StartReaderTrackingAfterActivation());
    }

    private IEnumerator StartReaderTrackingAfterActivation()
    {
        // BeamGazeProvider can be nested under BookPagePanel. Wait one frame
        // after activating the panel so its Start() has completed before we
        // request tracking (Start() performs an initial StopTracking call).
        yield return null;

        readerTrackingCoroutine = null;

        GameObject activePanel = ActiveReaderPanel;
        if (activePanel != null && !activePanel.activeInHierarchy)
        {
            Debug.LogWarning(
                "[Reading] Cannot start gaze because the reader is inactive."
            );

            yield break;
        }

        if (BeamGazeProvider.Instance == null)
        {
            Debug.LogError(
                "[Reading] BeamGazeProvider was not found in the scene."
            );

            yield break;
        }

        BeamGazeProvider.Instance.StartTracking();

        GazeDwellManager manager = GazeDwellManager.Instance;

        if (manager == null)
            manager = FindObjectOfType<GazeDwellManager>();

        if (manager != null)
        {
            manager.EnableGaze();
        }
        else
        {
            Debug.LogError(
                "[Reading] GazeDwellManager was not found in the scene."
            );
        }

        if (GazeCalibration.Instance != null)
            GazeCalibration.Instance.StartCalibration();

        Debug.Log("[Reading] Reader opened. Gaze tracking enabled.");
    }

    public void StopReaderTracking()
    {
        if (readerTrackingCoroutine != null)
        {
            StopCoroutine(readerTrackingCoroutine);
            readerTrackingCoroutine = null;
        }

        GazeDwellManager manager = GazeDwellManager.Instance;

        if (manager == null)
            manager = FindObjectOfType<GazeDwellManager>();

        if (manager != null)
            manager.DisableGaze();

        if (BeamGazeProvider.Instance != null)
            BeamGazeProvider.Instance.StopTracking();

        Debug.Log("[Reading] Reader closed. Gaze tracking stopped.");
    }

    // --------------------------------------------------
    // PAGE NAVIGATION AND PROGRESS SAVING
    // --------------------------------------------------

    public void NextPage()
    {
        if (pages == null ||
            currentPageIndex >= pages.Length - 1)
        {
            return;
        }

        currentPageIndex++;
        DisplayCurrentPage();
    }

    public void PreviousPage()
    {
        if (pages == null || currentPageIndex <= 0)
            return;

        currentPageIndex--;
        DisplayCurrentPage();
    }

    public void DisplayCurrentPage()
    {
        if (pages == null || pages.Length == 0)
            return;

        SetReaderPanelsActive(true);

        currentPageIndex = Mathf.Clamp(
            currentPageIndex,
            0,
            pages.Length - 1
        );

        if (StoryAudioManager.Instance != null)
            StoryAudioManager.Instance.FadeOutPageEffects();

        if (titleText != null)
            titleText.text = bookTitle;

        bool showFullInteractiveLittleStar =
            IsLittleStarBook() &&
            interactiveBookPagePanel != null &&
            ActiveReaderPanel == interactiveBookPagePanel &&
            storyText != null;

        bool usingManualLittleStarObjects =
            !showFullInteractiveLittleStar &&
            IsLittleStarBook() &&
            EnsureLittleStarPageTextObjects();

        // Display only the actual story text.
        // Emotion labels and confidence values stay in JSON metadata.
        if (showFullInteractiveLittleStar)
        {
            // The interactive Little Star panel contains one authored text
            // object with the entire book. Preserve it so the ScrollRect
            // reveals the full story instead of replacing it with one page.
            storyText.enabled = true;
            storyText.ForceMeshUpdate();
            currentPagePassageRanges.Clear();
        }
        else if (usingManualLittleStarObjects)
        {
            for (int i = 0; i < littleStarPageTextObjects.Length; i++)
            {
                TMP_Text pageText = littleStarPageTextObjects[i];
                if (pageText == null)
                    continue;

                bool isCurrentPage = i == currentPageIndex;
                // Keep every authored page object visible in the Hierarchy.
                // Only its text renderer and gaze trigger are enabled for
                // the current page; never deactivate/delete the GameObject.
                pageText.enabled = isCurrentPage;

                GazeRegion pageGazeRegion =
                    pageText.GetComponent<GazeRegion>();
                if (pageGazeRegion != null)
                    pageGazeRegion.enabled = isCurrentPage;

                // Each page can hold multiple manually placed gaze boxes in
                // its child Trigger Group. Keep only the displayed page's
                // group active so its child GazeRegions participate in gaze.
                Transform triggerGroup =
                    pageText.transform.Find("Trigger Group");
                if (triggerGroup != null)
                {
                    if (triggerGroup.gameObject.activeSelf != isCurrentPage)
                        triggerGroup.gameObject.SetActive(isCurrentPage);

                    GazeRegion[] groupRegions =
                        triggerGroup.GetComponentsInChildren<GazeRegion>(true);
                    for (int regionIndex = 0;
                         regionIndex < groupRegions.Length;
                         regionIndex++)
                    {
                        if (groupRegions[regionIndex] != null)
                        {
                            if (groupRegions[regionIndex].textTarget == null)
                                groupRegions[regionIndex].textTarget = pageText;

                            groupRegions[regionIndex].priority = Mathf.Max(
                                groupRegions[regionIndex].priority,
                                100
                            );
                        }
                    }
                }

                GazeVideoImageSwitcher[] pageVideoSwitchers =
                    pageText.GetComponentsInChildren<GazeVideoImageSwitcher>(true);
                for (int switcherIndex = 0;
                     switcherIndex < pageVideoSwitchers.Length;
                     switcherIndex++)
                {
                    if (pageVideoSwitchers[switcherIndex] != null)
                    {
                        pageVideoSwitchers[switcherIndex].SetPageVisible(
                            isCurrentPage
                        );
                    }
                }
            }

            SetStoryTextReference(littleStarPageTextObjects[currentPageIndex]);

            // The controller is the source of truth for each page. Always
            // overwrite stale text left on the individual scene objects so
            // manual page entries (or imported fallback text) match the page.
            if (storyText != null && currentPageIndex < pages.Length)
            {
                storyText.text = pages[currentPageIndex];
            }

            storyText.ForceMeshUpdate();
            currentPagePassageRanges.Clear();
        }
        else if (storyText != null)
        {
            storyText.text = pages[currentPageIndex];
            storyText.ForceMeshUpdate();
            BuildCurrentPagePassageRanges();
        }
        else
        {
            currentPagePassageRanges.Clear();
        }

        ResetPageScrollPosition();
        ResetParagraphMusicDwell();

        if (interactiveBookPagePanel != null)
        {
            LittleStarGazeCharacter character =
                interactiveBookPagePanel.GetComponentInChildren<LittleStarGazeCharacter>(true);
            if (character != null)
                character.SetPage(storyText, currentPageIndex + 1);
        }

        // Enable only the manually placed gaze music zones for this page.
        PageGazeMusicTrigger.RefreshForPage(
            currentPageIndex + 1,
            jsonResourcePath
        );

        if (pageNumberText != null)
        {
            pageNumberText.text =
                "Page " + (currentPageIndex + 1) + " / " + pages.Length;
        }

        if (previousButton != null)
            previousButton.interactable = currentPageIndex > 0;

        if (nextButton != null)
        {
            nextButton.interactable =
                currentPageIndex < pages.Length - 1;
        }

        ReadingProgressStore.Save(
            jsonResourcePath,
            currentPageIndex
        );

        Debug.Log(
            "[Reading] Displayed page " +
            (currentPageIndex + 1) +
            " | Passage ID: " +
            (CurrentPassage != null ? CurrentPassage.id.ToString() : "None") +
            " | Emotion: " +
            (CurrentPassage != null ? CurrentPassage.emotion : "None")
        );
    }

    private void ResetPageScrollPosition()
    {
        GameObject activePanel = ActiveReaderPanel;
        if (activePanel == null)
            return;

        ScrollRect scrollRect =
            activePanel.GetComponentInChildren<ScrollRect>(true);
        if (scrollRect == null)
            return;

        Canvas.ForceUpdateCanvases();
        scrollRect.StopMovement();
        scrollRect.velocity = Vector2.zero;
        scrollRect.verticalNormalizedPosition = 1f;
        scrollRect.horizontalNormalizedPosition = 0f;
    }

    private bool IsLittleStarBook()
    {
        return string.Equals(
            jsonResourcePath,
            LittleStarResourcePath,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private bool EnsureLittleStarPageTextObjects()
    {
        if (littleStarPageTextObjects != null &&
            littleStarPageTextObjects.Length == 24)
        {
            return true;
        }

        GameObject activePanel = ActiveReaderPanel;
        if (activePanel == null)
        {
            littleStarPageTextObjects = new TMP_Text[0];
            return false;
        }

        TMP_Text[] found = activePanel.GetComponentsInChildren<TMP_Text>(true);
        TMP_Text[] ordered = new TMP_Text[24];

        for (int i = 0; i < found.Length; i++)
        {
            TMP_Text candidate = found[i];
            const string prefix = "StoryText (";
            if (candidate == null || !candidate.name.StartsWith(prefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string suffix = candidate.name.Substring(prefix.Length);
            if (!suffix.EndsWith(")", StringComparison.Ordinal))
                continue;

            suffix = suffix.Substring(0, suffix.Length - 1);
            if (!int.TryParse(suffix, out int pageNumber) ||
                pageNumber < 1 || pageNumber > ordered.Length)
            {
                continue;
            }

            ordered[pageNumber - 1] = candidate;
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i] == null)
            {
                Debug.LogWarning(
                    "[Reading] Little Star needs StoryText (1) through " +
                    "StoryText (24) under BookPagePanel. Found only " +
                    CountPageTextObjects(ordered) + " numbered page texts. " +
                    "Using the single-text page mode."
                );

                littleStarPageTextObjects = new TMP_Text[0];
                return false;
            }
        }

        littleStarPageTextObjects = ordered;
        return true;
    }

    private int CountPageTextObjects(TMP_Text[] texts)
    {
        int count = 0;
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null)
                count++;
        }

        return count;
    }

    private void SetStoryTextReference(TMP_Text newStoryText)
    {
        if (storyTextGazeRegion != null)
        {
            storyTextGazeRegion.onWordDwellComplete.RemoveListener(
                HandleWordDwellComplete
            );
        }

        storyText = newStoryText;
        storyTextGazeRegion = storyText != null
            ? storyText.GetComponent<GazeRegion>()
            : null;

        if (storyTextGazeRegion != null)
        {
            storyTextGazeRegion.onWordDwellComplete.AddListener(
                HandleWordDwellComplete
            );
        }
    }

    private void BindStoryTextGazeRegion()
    {
        SetStoryTextReference(storyText);
    }

    private void BuildCurrentPagePassageRanges()
    {
        currentPagePassageRanges.Clear();

        if (currentPageIndex < 0 ||
            currentPageIndex >= passageGroupsByPage.Count)
        {
            return;
        }

        List<AIReadingPassage> passages =
            passageGroupsByPage[currentPageIndex];

        int textIndex = 0;

        for (int i = 0; i < passages.Count; i++)
        {
            AIReadingPassage passage = passages[i];

            if (passage == null || string.IsNullOrEmpty(passage.text))
                continue;

            if (currentPagePassageRanges.Count > 0)
                textIndex += 2; // Matches the "\n\n" page separator.

            PassageTextRange range = new PassageTextRange
            {
                passage = passage,
                startIndex = textIndex,
                endIndex = textIndex + passage.text.Length
            };

            currentPagePassageRanges.Add(range);
            textIndex = range.endIndex;
        }
    }

    private void UpdateParagraphMusicFromGaze()
    {
        // When manual page zones are present, they own music triggering.
        // This prevents automatic text cues from competing with box cues.
        if (PageGazeMusicTrigger.HasConfiguredTriggers)
        {
            ResetParagraphMusicDwell();
            return;
        }

        GazeDwellManager dwellManager = GazeDwellManager.Instance;
        GazeInputMapper mapper = GazeInputMapper.Instance;

        GameObject activePanel = ActiveReaderPanel;
        if (activePanel == null ||
            !activePanel.activeInHierarchy ||
            !startEyeTrackingWhenBookOpens ||
            dwellManager == null ||
            !dwellManager.gazeEnabled ||
            mapper == null ||
            !mapper.IsValid ||
            storyText == null ||
            currentPagePassageRanges.Count == 0 ||
            (GazeCalibration.Instance != null &&
             GazeCalibration.Instance.IsRunning))
        {
            ResetParagraphMusicDwell();
            return;
        }

        Canvas canvas = storyText.canvas;
        Camera eventCamera = canvas != null &&
                             canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;

        int characterInfoIndex = TMP_TextUtilities.FindIntersectingCharacter(
            storyText,
            mapper.ScreenPosition,
            eventCamera,
            true
        );

        if (characterInfoIndex < 0 ||
            characterInfoIndex >= storyText.textInfo.characterCount)
        {
            ResetParagraphMusicDwell();
            return;
        }

        int stringIndex =
            storyText.textInfo.characterInfo[characterInfoIndex].index;

        AIReadingPassage passageUnderGaze = null;

        for (int i = 0; i < currentPagePassageRanges.Count; i++)
        {
            PassageTextRange range = currentPagePassageRanges[i];
            if (stringIndex >= range.startIndex &&
                stringIndex < range.endIndex)
            {
                passageUnderGaze = range.passage;
                break;
            }
        }

        if (passageUnderGaze == null)
        {
            ResetParagraphMusicDwell();
            return;
        }

        if (focusedPassageId != passageUnderGaze.id)
        {
            focusedPassageId = passageUnderGaze.id;
            passageGazeTimer = 0f;
            passageCueTriggered = false;
            wordCueTriggeredForPassage = false;
        }

        passageGazeTimer += Time.unscaledDeltaTime;

        if (passageCueTriggered ||
            passageGazeTimer < Mathf.Max(0f, paragraphMusicDwellTime))
        {
            return;
        }

        passageCueTriggered = true;

        // If this passage has word-specific rules, do not start its general
        // passage track while the reader is looking at some other word.
        if (HasWordMusicCueForPassage(passageUnderGaze.id))
        {
            Debug.Log(
                "[Reading Music] Passage " + passageUnderGaze.id +
                " has word-specific cues; waiting for a matching word."
            );
            return;
        }

        ParagraphMusicCueAssignment assignment = FindParagraphMusicCue(
            passageUnderGaze.id
        );

        if (assignment == null || assignment.musicClip == null)
        {
            Debug.LogWarning(
                "[Reading Music] No music clip for book '" +
                jsonResourcePath + "', passage " + passageUnderGaze.id + "."
            );

            return;
        }

        if (wordCueTriggeredForPassage)
            return;

        if (StoryAudioManager.Instance == null)
        {
            Debug.LogError("[Reading Music] StoryAudioManager was not found.");
            return;
        }

        Debug.Log(
            "[Reading Music] Requesting '" + assignment.musicClip.name +
            "' for book '" + jsonResourcePath + "', passage " +
            passageUnderGaze.id + "."
        );

        StoryAudioManager.Instance.PlayMusic(
            assignment.musicClip,
            assignment.volume
        );
    }

    private ParagraphMusicCueAssignment FindParagraphMusicCue(int passageId)
    {
        if (paragraphMusicCues == null)
            return null;

        for (int i = 0; i < paragraphMusicCues.Count; i++)
        {
            ParagraphMusicCueAssignment cue = paragraphMusicCues[i];

            if (cue != null &&
                cue.passageId == passageId &&
                string.Equals(
                    cue.jsonResourcePath,
                    jsonResourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return cue;
            }
        }

        return null;
    }

    private void HandleWordDwellComplete(string word)
    {
        if (focusedPassageId < 0 ||
            ActiveReaderPanel == null ||
            !ActiveReaderPanel.activeInHierarchy)
        {
            return;
        }

        WordMusicCueAssignment assignment = FindWordMusicCue(
            focusedPassageId,
            word
        );

        if (assignment == null || assignment.musicClip == null ||
            StoryAudioManager.Instance == null)
        {
            return;
        }

        wordCueTriggeredForPassage = true;
        StoryAudioManager.Instance.PlayMusic(
            assignment.musicClip,
            assignment.volume
        );

        Debug.Log(
            "[Reading] Word music cue triggered: '" + word +
            "' in passage " + focusedPassageId
        );
    }

    private WordMusicCueAssignment FindWordMusicCue(
        int passageId,
        string word)
    {
        if (wordMusicCues == null)
            return null;

        string normalizedWord = NormalizeWord(word);

        for (int i = 0; i < wordMusicCues.Count; i++)
        {
            WordMusicCueAssignment cue = wordMusicCues[i];

            if (cue != null &&
                cue.passageId == passageId &&
                string.Equals(
                    cue.jsonResourcePath,
                    jsonResourcePath,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    NormalizeWord(cue.word),
                    normalizedWord,
                    StringComparison.OrdinalIgnoreCase))
            {
                return cue;
            }
        }

        return null;
    }

    private bool HasWordMusicCueForPassage(int passageId)
    {
        if (wordMusicCues == null)
            return false;

        for (int i = 0; i < wordMusicCues.Count; i++)
        {
            WordMusicCueAssignment cue = wordMusicCues[i];

            if (cue != null &&
                cue.passageId == passageId &&
                !string.IsNullOrWhiteSpace(cue.word) &&
                string.Equals(
                    cue.jsonResourcePath,
                    jsonResourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeWord(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        StringBuilder normalized = new StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsLetterOrDigit(value[i]))
                normalized.Append(char.ToLowerInvariant(value[i]));
        }

        return normalized.ToString();
    }

    private void ResetParagraphMusicDwell()
    {
        focusedPassageId = -1;
        passageGazeTimer = 0f;
        passageCueTriggered = false;
        wordCueTriggeredForPassage = false;
    }

    // --------------------------------------------------
    // OPTIONAL IMPORT SUPPORT
    // --------------------------------------------------

    public void ChooseBookFile()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            string script =
                "Add-Type -AssemblyName System.Windows.Forms; " +
                "$d=New-Object System.Windows.Forms.OpenFileDialog; " +
                "$d.Filter='Text files (*.txt)|*.txt|All files (*.*)|*.*'; " +
                "if($d.ShowDialog() -eq 'OK'){[Console]::WriteLine($d.FileName)}";

            string encoded = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(script)
            );

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -STA -EncodedCommand " + encoded,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            string path;

            using (Process process = Process.Start(info))
            {
                path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
            }

            if (!string.IsNullOrWhiteSpace(path))
                StartCoroutine(AnalyzeBook(path));
        }
        catch (Exception ex)
        {
            Debug.LogError("File picker failed: " + ex.Message);
            ShowMessage("Could not open the file picker.");
        }
#else
        ShowMessage("Importing books currently requires Windows.");
#endif
    }

    private IEnumerator AnalyzeBook(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ShowMessage("File not found.");
            yield break;
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        WWWForm form = new WWWForm();

        form.AddBinaryData(
            "file",
            bytes,
            Path.GetFileName(filePath),
            "text/plain"
        );

        ShowMessage("Analyzing book...");

        using (UnityWebRequest request =
               UnityWebRequest.Post(aiServiceUrl, form))
        {
            request.timeout = 300;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Book analysis failed: " + request.error);
                ShowMessage("Analysis failed. Check the AI service.");
                yield break;
            }

            AIReadingDatabase result =
                ParseDatabase(request.downloadHandler.text);

            if (result == null ||
                result.passages == null ||
                result.passages.Length == 0)
            {
                ShowMessage("No readable passages returned.");
                yield break;
            }

            database = result;

            bookTitle = string.IsNullOrWhiteSpace(result.bookTitle)
                ? Path.GetFileNameWithoutExtension(filePath)
                : result.bookTitle;

            BuildImportedPages(database.passages);

            currentPageIndex = 0;

            jsonResourcePath =
                "Imported_" + Path.GetFileNameWithoutExtension(filePath);

            DisplayCurrentPage();

            if (homePanel != null)
                homePanel.SetActive(false);

            if (browseBooksPanel != null)
                browseBooksPanel.SetActive(false);

            SetReaderPanelsActive(true);

            StartReaderTracking();

            Debug.Log("[Reading] Imported book opened: " + bookTitle);
        }
    }

    private void BuildImportedPages(AIReadingPassage[] passages)
    {
        List<string> pageTexts = new List<string>();

        pagePassages.Clear();
        passageGroupsByPage.Clear();

        if (passages != null)
        {
            foreach (AIReadingPassage passage in passages)
            {
                if (passage == null ||
                    string.IsNullOrWhiteSpace(passage.text))
                {
                    continue;
                }

                pageTexts.Add(passage.text);
                pagePassages.Add(passage);
                passageGroupsByPage.Add(
                    new List<AIReadingPassage> { passage }
                );
            }
        }

        pages = pageTexts.ToArray();

        if (pages.Length == 0)
        {
            pages = new string[] { "No readable text was found." };
            pagePassages.Clear();
            passageGroupsByPage.Clear();
        }
    }

    // --------------------------------------------------
    // HELPERS
    // --------------------------------------------------

    private AIReadingDatabase ParseDatabase(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        json = json.Trim();

        if (json.StartsWith("["))
            json = "{\"passages\":" + json + "}";

        return JsonUtility.FromJson<AIReadingDatabase>(json);
    }

    private void ShowMessage(string message)
    {
        if (storyText != null && !string.IsNullOrEmpty(message))
            storyText.text = message;
    }
}
