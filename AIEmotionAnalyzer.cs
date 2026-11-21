
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class AIEmotionAnalyzer : MonoBehaviour
{
    [Header("Reading System")]
    public AIReadingController readingController;

    [Header("Groq API")]
    public string model = "openai/gpt-oss-120b";
    public bool analyzeOnStart = true;

    [Header("Emotion Settings")]
    [Min(1)]
    public int paragraphsPerEmotion = 2;

    [Header("API Protection")]
    [Range(300, 2000)]
    public int maxCharactersPerGroup = 1200;

    [Min(5f)]
    public float secondsBetweenRequests = 10f;

    [Header("Cache")]
    public bool useCache = true;
    public bool showCacheLogs = true;

    [Header("Console and Screen")]
    public bool logAllEmotionsWhenOpened = true;
    public bool showOnScreenStatus = true;

    [Header("Progress")]
    public int completedGroups;
    public int totalGroups;
    public string currentBookName = "";
    public string currentStatus = "Waiting for book";

    [Header("Results")]
    public List<PassageEmotion> results =
        new List<PassageEmotion>();

    public bool analysisComplete;
    public bool isAnalyzing;
    public bool loadedFromCache;
    public bool savedSuccessfully;

    private const string ApiUrl =
        "https://api.groq.com/openai/v1/chat/completions";

    // Preserve previous cache compatibility.
    private const string CacheVersion = "emotion-v2";

    private AIReadingDatabase observedDatabase;
    private string observedSignature = "";
    private string lastAttemptSignature = "";
    private float nextCheckTime;

    private string notification = "";
    private bool notificationIsError;

    [Serializable]
    public class PassageEmotion
    {
        public int id;
        public int page;
        public string emotion;
    }

    [Serializable]
    private class CacheData
    {
        public string signature;
        public string bookTitle;
        public List<PassageEmotion> passages;
    }

    [Serializable]
    private class GroupCache
    {
        public string signature;
        public int group;
        public string previousEmotion;
        public string emotion;
    }

    [Serializable]
    private class AIEmotionResult
    {
        public string emotion;
    }

    [Serializable]
    private class Message
    {
        public string role;
        public string content;
    }

    [Serializable]
    private class ResponseFormat
    {
        public string type = "json_object";
    }

    [Serializable]
    private class StrictRequest
    {
        public string model;
        public Message[] messages;
        public ResponseFormat response_format;
        public int max_completion_tokens = 400;
    }

    [Serializable]
    private class LooseRequest
    {
        public string model;
        public Message[] messages;
        public int max_completion_tokens = 400;
    }

    [Serializable]
    private class Choice
    {
        public Message message;
    }

    [Serializable]
    private class ResponseBody
    {
        public Choice[] choices;
    }

    private void Start()
    {
        FindReadingController();

        Debug.Log(
            "[AI] Universal emotion analyzer ready.");

        SetNotification("Waiting for a book...");
    }

    private void Update()
    {
        if (!analyzeOnStart || isAnalyzing)
            return;

        if (Time.unscaledTime < nextCheckTime)
            return;

        nextCheckTime = Time.unscaledTime + 0.5f;

        AIReadingDatabase db;
        List<AIReadingPassage> passages;

        if (!TryGetBook(out db, out passages))
        {
            observedDatabase = null;
            observedSignature = "";
            return;
        }

        string signature =
            BuildSignature(db, passages);

        if (observedDatabase == db &&
            observedSignature == signature)
        {
            return;
        }

        observedDatabase = db;
        observedSignature = signature;

        if (lastAttemptSignature == signature)
            return;

        LoadOrAnalyze(db, passages, signature);
    }

    private void FindReadingController()
    {
        if (readingController == null)
        {
            readingController =
                FindObjectOfType<AIReadingController>();
        }
    }

    private bool TryGetBook(
        out AIReadingDatabase db,
        out List<AIReadingPassage> passages)
    {
        db = null;
        passages = new List<AIReadingPassage>();

        FindReadingController();

        if (readingController == null)
            return false;

        db = readingController.CurrentDatabase;

        if (db == null ||
            db.passages == null ||
            db.passages.Length == 0)
        {
            return false;
        }

        HashSet<int> ids = new HashSet<int>();

        foreach (AIReadingPassage p in db.passages)
        {
            if (p == null ||
                string.IsNullOrWhiteSpace(p.text))
                continue;

            if (!ids.Add(p.id))
            {
                Debug.LogError(
                    "[AI] Duplicate passage ID: " + p.id);
                return false;
            }

            passages.Add(p);
        }

        passages.Sort((a, b) =>
        {
            int page = a.page.CompareTo(b.page);

            return page != 0
                ? page
                : a.id.CompareTo(b.id);
        });

        return passages.Count > 0;
    }

    private string BookTitle(AIReadingDatabase db)
    {
        if (db != null &&
            !string.IsNullOrWhiteSpace(db.bookTitle))
            return db.bookTitle;

        if (readingController != null)
            return readingController.bookTitle;

        return "Untitled Book";
    }

    private string Hash(string value)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(
                Encoding.UTF8.GetBytes(value));

            StringBuilder text = new StringBuilder();

            foreach (byte b in hash)
                text.Append(b.ToString("x2"));

            return text.ToString();
        }
    }

    private string BuildSignature(
        AIReadingDatabase db,
        List<AIReadingPassage> passages)
    {
        StringBuilder text = new StringBuilder();

        text.AppendLine(CacheVersion);
        text.AppendLine(model);

        text.AppendLine(
            Mathf.Max(1, paragraphsPerEmotion).ToString());

        text.AppendLine(BookTitle(db));

        foreach (AIReadingPassage passage in passages)
        {
            text.Append(passage.id).Append("|");
            text.Append(passage.page).Append("|");

            string content = passage.text ?? "";

            text.Append(content.Length).Append(":");
            text.Append(content).AppendLine();
        }

        return Hash(text.ToString());
    }

    private string CacheFolder()
    {
        return Path.Combine(
            Application.persistentDataPath,
            "EmotionCache");
    }

    private string FullCachePath(string signature)
    {
        return Path.Combine(
            CacheFolder(),
            signature + ".json");
    }

    private string GroupCachePath(
        string signature,
        int group)
    {
        return Path.Combine(
            CacheFolder(),
            "groups",
            signature + "_" + group + ".json");
    }

    private bool ValidEmotion(string emotion)
    {
        switch (emotion)
        {
            case "sad":
            case "calm":
            case "hope":
            case "happy":
            case "joy":
            case "suspense":
                return true;

            default:
                return false;
        }
    }

    private bool ValidatePredictions(
        List<PassageEmotion> predictions,
        List<AIReadingPassage> passages)
    {
        if (predictions == null ||
            predictions.Count != passages.Count)
            return false;

        Dictionary<int, AIReadingPassage> expected =
            new Dictionary<int, AIReadingPassage>();

        foreach (AIReadingPassage passage in passages)
        {
            if (expected.ContainsKey(passage.id))
                return false;

            expected.Add(passage.id, passage);
        }

        HashSet<int> seen = new HashSet<int>();

        foreach (PassageEmotion item in predictions)
        {
            if (item == null ||
                !expected.ContainsKey(item.id) ||
                !seen.Add(item.id) ||
                expected[item.id].page != item.page ||
                !ValidEmotion(item.emotion))
            {
                return false;
            }
        }

        return seen.Count == passages.Count;
    }

    private bool CurrentBookMatches(
        AIReadingDatabase db,
        string signature)
    {
        AIReadingDatabase current;
        List<AIReadingPassage> passages;

        if (!TryGetBook(out current, out passages))
            return false;

        return current == db &&
            BuildSignature(current, passages) == signature;
    }

    private void SetNotification(
        string message,
        bool isError = false)
    {
        notification = message;
        notificationIsError = isError;
        currentStatus = message;
    }

    private void OnGUI()
    {
        if (!showOnScreenStatus ||
            string.IsNullOrEmpty(notification))
            return;

        float width = Mathf.Min(
            520f,
            Mathf.Max(100f, Screen.width - 20f));

        Rect panel = new Rect(
            10f, 10f, width, 85f);

        Color original = GUI.color;

        GUI.color = notificationIsError
            ? new Color(0.65f, 0.17f, 0.17f, 0.95f)
            : new Color(0.12f, 0.20f, 0.27f, 0.94f);

        GUI.Box(panel, GUIContent.none);

        GUI.color = Color.white;

        GUIStyle style =
            new GUIStyle(GUI.skin.label);

        style.fontSize = 15;
        style.fontStyle = FontStyle.Bold;
        style.wordWrap = true;
        style.normal.textColor = Color.white;

        GUI.Label(
            new Rect(
                panel.x + 12f,
                panel.y + 8f,
                panel.width - 24f,
                45f),
            notification,
            style);

        if (isAnalyzing && totalGroups > 0)
        {
            float progress = Mathf.Clamp01(
                (float)completedGroups / totalGroups);

            Rect bar = new Rect(
                panel.x + 12f,
                panel.y + 62f,
                panel.width - 24f,
                12f);

            GUI.color = Color.gray;

            GUI.DrawTexture(
                bar,
                Texture2D.whiteTexture);

            GUI.color = new Color(
                0.25f, 0.90f, 0.65f, 1f);

            GUI.DrawTexture(
                new Rect(
                    bar.x,
                    bar.y,
                    bar.width * progress,
                    bar.height),
                Texture2D.whiteTexture);
        }

        GUI.color = original;
    }

    // NEW: Show all saved emotions in the Console.
    private void LogAllEmotions(string source)
    {
        if (!logAllEmotionsWhenOpened)
            return;

        Debug.Log(
            "[AI] ========== EMOTION RESULTS ==========");

        Debug.Log(
            "[AI] Book: " + currentBookName);

        Debug.Log(
            "[AI] Source: " + source);

        Debug.Log(
            "[AI] Total saved passages: " +
            results.Count);

        List<PassageEmotion> sorted =
            new List<PassageEmotion>(results);

        sorted.Sort((a, b) =>
        {
            int page = a.page.CompareTo(b.page);

            return page != 0
                ? page
                : a.id.CompareTo(b.id);
        });

        foreach (PassageEmotion item in sorted)
        {
            Debug.Log(
                "[AI] Page " + item.page +
                " | Passage " + item.id +
                " | Emotion: " + item.emotion);
        }

        Debug.Log(
            "[AI] END OF SAVED EMOTION RESULTS");

        Debug.Log(
            "[AI] ===================================");
    }

    private void ApplyResults(
        AIReadingDatabase db,
        List<PassageEmotion> predictions,
        bool fromCache)
    {
        Dictionary<int, string> moods =
            new Dictionary<int, string>();

        foreach (PassageEmotion item in predictions)
            moods[item.id] = item.emotion;

        foreach (AIReadingPassage passage in db.passages)
        {
            if (passage == null)
                continue;

            string emotion;

            if (moods.TryGetValue(
                passage.id,
                out emotion))
            {
                passage.emotion = emotion;
                passage.musicMood = emotion;
            }
        }

        results = new List<PassageEmotion>(predictions);

        analysisComplete = true;
        loadedFromCache = fromCache;

        currentBookName = BookTitle(db);

        LogAllEmotions(
            fromCache
                ? "Saved local cache"
                : "New Groq AI analysis");
    }

    private bool TryLoadFullCache(
        AIReadingDatabase db,
        List<AIReadingPassage> passages,
        string signature)
    {
        string path = FullCachePath(signature);

        if (!File.Exists(path))
            return false;

        try
        {
            CacheData cache =
                JsonUtility.FromJson<CacheData>(
                    File.ReadAllText(path));

            if (cache == null ||
                cache.signature != signature ||
                !ValidatePredictions(
                    cache.passages, passages) ||
                !CurrentBookMatches(db, signature))
            {
                return false;
            }

            totalGroups = Mathf.CeilToInt(
                (float)passages.Count /
                Mathf.Max(1, paragraphsPerEmotion));

            completedGroups = totalGroups;

            ApplyResults(db, cache.passages, true);

            savedSuccessfully = true;

            SetNotification(
                "BOOK READY! Saved AI emotions loaded.");

            Debug.Log(
                "[AI] BOOK ALREADY ANALYZED AND SAVED!");

            Debug.Log(
                "[AI] Loaded " +
                results.Count +
                " saved passage emotions.");

            Debug.Log(
                "[AI] CACHE HIT! No Groq API call.");

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[AI] Full cache read error: " +
                ex.Message);

            return false;
        }
    }

    private bool SaveFullCache(
        AIReadingDatabase db,
        string signature,
        List<PassageEmotion> predictions)
    {
        if (!useCache)
            return false;

        try
        {
            Directory.CreateDirectory(CacheFolder());

            CacheData cache = new CacheData
            {
                signature = signature,
                bookTitle = BookTitle(db),
                passages = predictions
            };

            File.WriteAllText(
                FullCachePath(signature),
                JsonUtility.ToJson(cache, true));

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[AI] Full book save failed: " +
                ex.Message);

            return false;
        }
    }

    private bool TryLoadGroup(
        string signature,
        int group,
        string previous,
        out string emotion)
    {
        emotion = "";

        if (!useCache)
            return false;

        string path =
            GroupCachePath(signature, group);

        if (!File.Exists(path))
            return false;

        try
        {
            GroupCache cache =
                JsonUtility.FromJson<GroupCache>(
                    File.ReadAllText(path));

            if (cache == null ||
                cache.signature != signature ||
                cache.group != group ||
                cache.previousEmotion != previous ||
                !ValidEmotion(cache.emotion))
            {
                return false;
            }

            emotion = cache.emotion;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveGroup(
        string signature,
        int group,
        string previous,
        string emotion)
    {
        if (!useCache)
            return;

        try
        {
            string path =
                GroupCachePath(signature, group);

            Directory.CreateDirectory(
                Path.GetDirectoryName(path));

            GroupCache cache = new GroupCache
            {
                signature = signature,
                group = group,
                previousEmotion = previous,
                emotion = emotion
            };

            File.WriteAllText(
                path,
                JsonUtility.ToJson(cache, true));

            if (showCacheLogs)
            {
                Debug.Log(
                    "[AI] Group " + group +
                    " successfully saved.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[AI] Group cache save failed: " +
                ex.Message);
        }
    }

    private string ReadApiKey()
    {
        string key =
            Environment.GetEnvironmentVariable(
                "GROQ_API_KEY");

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable(
                "GROQ_API_KEY",
                EnvironmentVariableTarget.User);
        }
#endif

        return key == null ? "" : key.Trim();
    }

    private string MakeExcerpt(
        string source,
        int maximum)
    {
        if (string.IsNullOrEmpty(source))
            return "";

        if (source.Length <= maximum)
            return source;

        int first = Mathf.FloorToInt(
            maximum * 0.7f);

        int last = maximum - first;

        return source.Substring(0, first) +
            "\n[Middle omitted]\n" +
            source.Substring(source.Length - last);
    }

    private string BuildGroupText(
        List<AIReadingPassage> passages,
        int start,
        int end)
    {
        int count = end - start;

        int budget = Mathf.Clamp(
            maxCharactersPerGroup,
            300, 2000);

        int perPassage = Mathf.Max(
            100,
            budget / count - 60);

        StringBuilder text = new StringBuilder();

        for (int i = start; i < end; i++)
        {
            text.AppendLine(
                "Passage " + passages[i].id + ":");

            text.AppendLine(
                MakeExcerpt(
                    passages[i].text,
                    perPassage));
        }

        return text.ToString();
    }

    private bool TryParseEmotion(
        string apiResponse,
        out string emotion)
    {
        emotion = "";

        try
        {
            ResponseBody response =
                JsonUtility.FromJson<ResponseBody>(
                    apiResponse);

            if (response == null ||
                response.choices == null ||
                response.choices.Length == 0 ||
                response.choices[0] == null ||
                response.choices[0].message == null)
            {
                return false;
            }

            string content =
                response.choices[0].message.content;

            if (string.IsNullOrWhiteSpace(content))
                return false;

            // Accept JSON with surrounding markdown fences.
            int start = content.IndexOf('{');
            int end = content.LastIndexOf('}');

            if (start < 0 || end < start)
                return false;

            content = content.Substring(
                start, end - start + 1);

            AIEmotionResult parsed =
                JsonUtility.FromJson<AIEmotionResult>(
                    content);

            if (parsed == null ||
                string.IsNullOrWhiteSpace(parsed.emotion))
                return false;

            emotion =
                parsed.emotion.Trim().ToLowerInvariant();

            return ValidEmotion(emotion);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[AI] Response parsing: " +
                ex.Message);

            return false;
        }
    }

    private string BuildRequestJson(
        string passageText,
        string previousEmotion,
        bool strictJsonMode)
    {
        string instruction =
            "You classify the soundtrack mood of " +
            "book passages. " +
            "Return ONE emotion from this list: " +
            "sad, calm, hope, happy, joy, suspense. " +
            "Previous mood: " + previousEmotion + ". " +
            "Keep the previous mood when reasonable. " +
            "Avoid unnecessary dramatic changes. " +
            "Respond only with a simple JSON object " +
            "using exactly this structure: " +
            "{\"emotion\":\"calm\"}.";

        Message[] messages =
        {
            new Message
            {
                role = "system",
                content = instruction
            },
            new Message
            {
                role = "user",
                content = passageText
            }
        };

        if (strictJsonMode)
        {
            StrictRequest body = new StrictRequest
            {
                model = model,
                messages = messages,
                response_format = new ResponseFormat()
            };

            return JsonUtility.ToJson(body);
        }

        // Fallback: remove strict JSON enforcement
        // if Groq cannot validate its generated JSON.
        LooseRequest fallback = new LooseRequest
        {
            model = model,
            messages = messages
        };

        return JsonUtility.ToJson(fallback);
    }

    private IEnumerator AnalyzeGroups(
        AIReadingDatabase db,
        List<AIReadingPassage> passages,
        string signature)
    {
        isAnalyzing = true;
        analysisComplete = false;
        savedSuccessfully = false;
        loadedFromCache = false;
        results.Clear();

        currentBookName = BookTitle(db);

        int groupSize =
            Mathf.Max(1, paragraphsPerEmotion);

        totalGroups = Mathf.CeilToInt(
            (float)passages.Count / groupSize);

        completedGroups = 0;

        string apiKey = ReadApiKey();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetNotification(
                "AI ERROR: GROQ_API_KEY missing.",
                true);

            Debug.LogError(
                "[AI] GROQ_API_KEY is missing.");

            isAnalyzing = false;
            yield break;
        }

        List<PassageEmotion> predictions =
            new List<PassageEmotion>();

        string previousEmotion = "none";
        bool hasMadeApiCall = false;

        Debug.Log(
            "[AI] STARTED: " + currentBookName);

        Debug.Log(
            "[AI] Processing " + passages.Count +
            " passages in " + totalGroups +
            " groups.");

        for (int groupIndex = 0;
             groupIndex < totalGroups;
             groupIndex++)
        {
            if (!CurrentBookMatches(db, signature))
            {
                Debug.LogWarning(
                    "[AI] Book changed. Stopping analysis.");

                SetNotification("Book changed.");

                isAnalyzing = false;
                observedDatabase = null;
                observedSignature = "";
                yield break;
            }

            int group = groupIndex + 1;

            int start = groupIndex * groupSize;

            int end = Mathf.Min(
                start + groupSize,
                passages.Count);

            string emotion;

            bool groupCached = TryLoadGroup(
                signature,
                group,
                previousEmotion,
                out emotion);

            if (groupCached)
            {
                if (showCacheLogs)
                {
                    Debug.Log(
                        "[AI] CACHE HIT: Group " +
                        group + "/" + totalGroups);
                }
            }
            else
            {
                string passageText = BuildGroupText(
                    passages, start, end);

                bool success = false;
                string generatedEmotion = "";

                // Attempt 1: strict JSON.
                // Attempt 2: loose JSON fallback.
                // Attempt 3: additional retry if needed.
                for (int attempt = 0;
                     attempt < 3 && !success;
                     attempt++)
                {
                    if (hasMadeApiCall)
                    {
                        yield return
                            new WaitForSecondsRealtime(
                                Mathf.Max(
                                    5f,
                                    secondsBetweenRequests));
                    }

                    if (!CurrentBookMatches(
                        db, signature))
                    {
                        isAnalyzing = false;
                        observedDatabase = null;
                        observedSignature = "";
                        yield break;
                    }

                    bool strictMode = attempt == 0;

                    string requestJson =
                        BuildRequestJson(
                            passageText,
                            previousEmotion,
                            strictMode);

                    string mode = strictMode
                        ? "Strict JSON"
                        : "Fallback JSON";

                    SetNotification(
                        "Analyzing " + currentBookName +
                        " (" + completedGroups +
                        "/" + totalGroups +
                        " groups complete)");

                    Debug.Log(
                        "[AI] Group " + group +
                        "/" + totalGroups +
                        " | Attempt " + (attempt + 1) +
                        " | " + mode);

                    using (UnityWebRequest request =
                        new UnityWebRequest(ApiUrl, "POST"))
                    {
                        request.uploadHandler =
                            new UploadHandlerRaw(
                                Encoding.UTF8.GetBytes(
                                    requestJson));

                        request.downloadHandler =
                            new DownloadHandlerBuffer();

                        request.SetRequestHeader(
                            "Content-Type",
                            "application/json");

                        request.SetRequestHeader(
                            "Authorization",
                            "Bearer " + apiKey);

                        request.timeout = 120;

                        hasMadeApiCall = true;

                        yield return request.SendWebRequest();

                        long code = request.responseCode;

                        string responseText =
                            request.downloadHandler.text;

                        if (request.result !=
                            UnityWebRequest.Result.Success)
                        {
                            Debug.LogWarning(
                                "[AI] Group " + group +
                                " attempt " +
                                (attempt + 1) +
                                " failed (" + code +
                                "): " + responseText);

                            if (code == 400 &&
                                responseText.Contains(
                                    "json_validate_failed") &&
                                attempt < 2)
                            {
                                Debug.LogWarning(
                                    "[AI] JSON validation failed. " +
                                    "Retrying with fallback mode.");

                                continue;
                            }

                            if ((code == 429 ||
                                 code == 500 ||
                                 code == 502 ||
                                 code == 503 ||
                                 code == 504) &&
                                attempt < 2)
                            {
                                float cooldown =
                                    code == 429 ? 65f : 10f;

                                SetNotification(
                                    "Groq error " + code +
                                    ". Retrying group " +
                                    group + "...");

                                yield return
                                    new WaitForSecondsRealtime(
                                        cooldown);

                                continue;
                            }

                            SetNotification(
                                "AI ERROR: Group " +
                                group + " failed (" +
                                code + ").",
                                true);

                            Debug.LogError(
                                "[AI] Group " + group +
                                " stopped after error " +
                                code + ".");

                            isAnalyzing = false;
                            yield break;
                        }

                        if (TryParseEmotion(
                            responseText,
                            out generatedEmotion))
                        {
                            success = true;
                        }
                        else
                        {
                            Debug.LogWarning(
                                "[AI] Group " + group +
                                " returned invalid JSON. " +
                                "Trying fallback.");
                        }
                    }
                }

                if (!success)
                {
                    Debug.LogError(
                        "[AI] Group " + group +
                        " failed after 3 attempts.");

                    SetNotification(
                        "AI ERROR: Could not analyze " +
                        "group " + group + ".",
                        true);

                    isAnalyzing = false;
                    yield break;
                }

                emotion = generatedEmotion;

                SaveGroup(
                    signature,
                    group,
                    previousEmotion,
                    emotion);
            }

            for (int i = start; i < end; i++)
            {
                predictions.Add(
                    new PassageEmotion
                    {
                        id = passages[i].id,
                        page = passages[i].page,
                        emotion = emotion
                    });
            }

            previousEmotion = emotion;
            completedGroups++;

            SetNotification(
                "Analyzing " + currentBookName +
                " (" + completedGroups +
                "/" + totalGroups +
                " groups complete)");

            Debug.Log(
                "[AI] PROGRESS: " +
                completedGroups + "/" +
                totalGroups +
                " | Emotion: " + emotion);
        }

        if (!CurrentBookMatches(db, signature))
        {
            isAnalyzing = false;
            observedDatabase = null;
            observedSignature = "";
            yield break;
        }

        if (!ValidatePredictions(
            predictions, passages))
        {
            SetNotification(
                "AI ERROR: Validation failed.",
                true);

            Debug.LogError(
                "[AI] Final validation failed.");

            isAnalyzing = false;
            yield break;
        }

        ApplyResults(db, predictions, false);

        savedSuccessfully = SaveFullCache(
            db, signature, predictions);

        isAnalyzing = false;

        if (savedSuccessfully)
        {
            SetNotification(
                "BOOK ANALYZED AND SAVED! " +
                currentBookName);

            Debug.Log(
                "[AI] BOOK ANALYZED AND SAVED!");

            Debug.Log(
                "[AI] Book: " + currentBookName);

            Debug.Log(
                "[AI] Saved " + results.Count +
                " passage emotions.");

            Debug.Log(
                "[AI] Next time: load from cache.");
        }
        else
        {
            SetNotification(
                "AI finished, but full cache not saved.",
                true);

            Debug.LogWarning(
                "[AI] Analysis completed, " +
                "but full cache not saved.");
        }
    }

    private void LoadOrAnalyze(
        AIReadingDatabase db,
        List<AIReadingPassage> passages,
        string signature)
    {
        if (isAnalyzing)
            return;

        currentBookName = BookTitle(db);

        if (useCache &&
            TryLoadFullCache(
                db, passages, signature))
        {
            lastAttemptSignature = signature;
            return;
        }

        lastAttemptSignature = signature;

        StartCoroutine(
            AnalyzeGroups(db, passages, signature));
    }

    [ContextMenu("Analyze Loaded Book")]
    public void AnalyzeBook()
    {
        if (!Application.isPlaying || isAnalyzing)
            return;

        AIReadingDatabase db;
        List<AIReadingPassage> passages;

        if (!TryGetBook(out db, out passages))
        {
            Debug.LogWarning(
                "[AI] Open a book first.");
            return;
        }

        string signature =
            BuildSignature(db, passages);

        observedDatabase = db;
        observedSignature = signature;

        LoadOrAnalyze(db, passages, signature);
    }

    [ContextMenu("Print Saved Emotions")]
    public void PrintSavedEmotions()
    {
        AIReadingDatabase db;
        List<AIReadingPassage> passages;

        if (!TryGetBook(out db, out passages))
        {
            Debug.LogWarning(
                "[AI] Open a book first.");
            return;
        }

        string signature =
            BuildSignature(db, passages);

        if (!TryLoadFullCache(
            db, passages, signature))
        {
            Debug.LogWarning(
                "[AI] No complete saved result " +
                "for this book yet.");
        }
    }

    [ContextMenu("Force Reanalyze Current Book")]
    public void ForceReanalyze()
    {
        if (!Application.isPlaying || isAnalyzing)
            return;

        AIReadingDatabase db;
        List<AIReadingPassage> passages;

        if (!TryGetBook(out db, out passages))
            return;

        string signature =
            BuildSignature(db, passages);

        try
        {
            string full = FullCachePath(signature);

            if (File.Exists(full))
                File.Delete(full);

            string folder = Path.Combine(
                CacheFolder(), "groups");

            if (Directory.Exists(folder))
            {
                foreach (string path in
                    Directory.GetFiles(
                        folder,
                        signature + "_*.json"))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[AI] Cache deletion issue: " +
                ex.Message);
        }

        observedDatabase = db;
        observedSignature = signature;
        lastAttemptSignature = signature;

        StartCoroutine(
            AnalyzeGroups(db, passages, signature));
    }

    [ContextMenu("Show Emotion Cache Folder")]
    public void ShowCacheFolder()
    {
        Debug.Log(
            "[AI] Cache folder: " + CacheFolder());

#if UNITY_EDITOR
        if (Directory.Exists(CacheFolder()))
        {
            UnityEditor.EditorUtility.RevealInFinder(
                CacheFolder());
        }
#endif
    }

    public string GetEmotionForPassage(int id)
    {
        foreach (PassageEmotion item in results)
        {
            if (item.id == id)
                return item.emotion;
        }

        return "unknown";
    }
}
