/*
 * Holoneon Translator Region Module
 *
 * Provider-neutral in-world translation for OpenSimulator.
 *
 * Providers:
 *   libre       - LibreTranslate
 *   google-nmt  - Google Cloud Translation Basic v2 / NMT
 *   google-llm  - Google Cloud Translation Basic v2 / Translation LLM
 *
 * Resident controls:
 *   /884422 en
 *   /884422 pt
 *   /884422 status
 *   /884422 off
 *
 * Preferences are cached in memory and persisted in MariaDB/MySQL.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using log4net;
using Mono.Addins;
using MySql.Data.MySqlClient;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("HoloTranslatorModule", "0.6")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace Holoneon.Translator
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HoloneonTranslatorModule")]
    public class HoloneonTranslatorModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly UUID TRANSLATOR_DIALOG_ID =
            new UUID("6d7fc6e1-83ee-44c5-bf44-6873facd9731");

        private const string PROVIDER_LIBRE = "libre";
        private const string PROVIDER_GOOGLE_NMT = "google-nmt";
        private const string PROVIDER_GOOGLE_LLM = "google-llm";

        // Viewer protocol values used by IClientAPI.SendChatMessage().
        // Chat source: 0=system, 1=agent, 2=object.
        // Audible: -1=not audible, 0=barely, 1=fully.
        private const byte CHAT_SOURCE_AGENT = 1;
        private const byte CHAT_AUDIBLE_FULLY = 1;

        private readonly List<Scene> m_scenes = new List<Scene>();

        // Avatar UUID -> target language. Absence means translator OFF.
        private readonly ConcurrentDictionary<UUID, string> m_languages =
            new ConcurrentDictionary<UUID, string>();

        // Avatar UUID -> load generation. Prevents a late DB load from
        // overwriting a preference the resident just changed.
        private readonly ConcurrentDictionary<UUID, int> m_preferenceGeneration =
            new ConcurrentDictionary<UUID, int>();

        private readonly ConcurrentDictionary<UUID, bool> m_preferenceLoaded =
            new ConcurrentDictionary<UUID, bool>();

        private readonly ConcurrentDictionary<string, CacheEntry> m_cache =
            new ConcurrentDictionary<string, CacheEntry>();

        // Coalesce simultaneous identical translations.
        private readonly ConcurrentDictionary<string, System.Lazy<Task<TranslationResult>>> m_inflight =
            new ConcurrentDictionary<string, System.Lazy<Task<TranslationResult>>>();

        private HttpClient m_http;

        private bool m_enabled = false;

        private int m_controlChannel = 884422;

        private string m_provider = PROVIDER_LIBRE;
        private string m_fallbackProvider = "";

        private string m_libreUrl = "http://127.0.0.1:5400/translate";
        private string m_libreApiKey = "";

        private string m_googleApiKey = "";
        private string m_googleProjectId = "";
        private string m_googleRegion = "us-central1";

        private string m_databaseConnectionString = "";
        private string m_databaseTable = "UserTranslatorSettings";

        private int m_maxMessageLength = 500;
        private int m_cacheSeconds = 3600;
        private int m_maxCacheEntries = 10000;
        private int m_httpTimeoutSeconds = 15;

        private float m_whisperDistance = 10.0f;
        private float m_sayDistance = 20.0f;
        private float m_shoutDistance = 100.0f;

        private class TranslationResult
        {
            public string Text;
            public string SourceLanguage;
        }

        private class CacheEntry
        {
            public string Text;
            public string SourceLanguage;
            public DateTime ExpiresUtc;
        }

        private class Recipient
        {
            public IClientAPI Client;
            public string Language;
        }

        private class PreferenceRow
        {
            public bool Exists;
            public bool Enabled;
            public string Language;
        }

        public string Name
        {
            get { return "Holoneon Translator"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["HoloneonTranslator"];

            m_log.Info(
                "[HOLONEON TRANSLATOR]: Module discovered; Initialise() called."
            );
        
            if (config == null)
            {
                m_enabled = false;
        
                m_log.Warn(
                    "[HOLONEON TRANSLATOR]: " +
                    "[HoloneonTranslator] configuration section not found. " +
                    "Module disabled."
                );
        
                return;
            }
        
            m_enabled =
                config.GetBoolean("Enabled", false);
        
            if (!m_enabled)
            {
                m_log.Info(
                    "[HOLONEON TRANSLATOR]: Module disabled by configuration."
                );
        
                return;
            }

            m_controlChannel =
                config.GetInt("ControlChannel", 884422);

            m_provider =
                NormalizeProvider(config.GetString("Provider", PROVIDER_LIBRE));

            m_fallbackProvider =
                NormalizeProvider(config.GetString("FallbackProvider", ""));

            if (m_fallbackProvider == m_provider)
                m_fallbackProvider = "";

            m_libreUrl =
                config.GetString(
                    "LibreURL",
                    "http://127.0.0.1:5400/translate"
                ).Trim();

            m_libreApiKey =
                config.GetString("LibreAPIKey", "").Trim();

            m_googleApiKey =
                config.GetString("GoogleAPIKey", "").Trim();

            m_googleProjectId =
                config.GetString("GoogleProjectID", "").Trim();

            m_googleRegion =
                config.GetString("GoogleRegion", "us-central1").Trim();

            m_databaseConnectionString =
                config.GetString("DatabaseConnectionString", "").Trim();

            m_databaseTable =
                config.GetString(
                    "DatabaseTable",
                    "UserTranslatorSettings"
                ).Trim();

            if (!IsSafeSqlIdentifier(m_databaseTable))
            {
                m_log.ErrorFormat(
                    "[HOLONEON TRANSLATOR]: Invalid DatabaseTable '{0}'. Module disabled.",
                    m_databaseTable
                );

                m_enabled = false;
                return;
            }

            m_maxMessageLength =
                Math.Max(1, config.GetInt("MaxMessageLength", 500));

            m_cacheSeconds =
                Math.Max(0, config.GetInt("CacheSeconds", 3600));

            m_maxCacheEntries =
                Math.Max(100, config.GetInt("MaxCacheEntries", 10000));

            m_httpTimeoutSeconds =
                Math.Max(2, config.GetInt("HttpTimeoutSeconds", 15));

            IConfig chatConfig = source.Configs["Chat"];

            if (chatConfig != null)
            {
                m_whisperDistance =
                    chatConfig.GetFloat("whisper_distance", 10.0f);

                m_sayDistance =
                    chatConfig.GetFloat("say_distance", 20.0f);

                m_shoutDistance =
                    chatConfig.GetFloat("shout_distance", 100.0f);
            }

            m_http = new HttpClient();
            m_http.Timeout = TimeSpan.FromSeconds(m_httpTimeoutSeconds);

            m_log.InfoFormat(
                "[HOLONEON TRANSLATOR]: Enabled. Provider={0}, fallback={1}, control channel=/{2}, Libre={3}",
                m_provider,
                String.IsNullOrEmpty(m_fallbackProvider) ? "none" : m_fallbackProvider,
                m_controlChannel,
                m_libreUrl
            );

            if (String.IsNullOrWhiteSpace(m_databaseConnectionString))
            {
                m_log.Warn(
                    "[HOLONEON TRANSLATOR]: DatabaseConnectionString is empty. " +
                    "Preferences will work in memory but will not survive simulator restarts."
                );
            }
        }

        public void PostInitialise()
        {
            // No post-initialisation work is required.
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
            {
                if (!m_scenes.Contains(scene))
                    m_scenes.Add(scene);
            }

            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;

            m_log.InfoFormat(
                "[HOLONEON TRANSLATOR]: Attached to region {0}",
                scene.RegionInfo.RegionName
            );
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;

            lock (m_scenes)
                m_scenes.Remove(scene);
        }

        private void OnMakeRootAgent(ScenePresence presence)
        {
            if (presence == null ||
                presence.IsChildAgent ||
                presence.ControllingClient == null)
            {
                return;
            }
        
            // Do not block the teleport/crossing path.
            _ = CheckLanguagePromptAsync(presence);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
            if (m_http != null)
            {
                m_http.Dispose();
                m_http = null;
            }

            m_cache.Clear();
            m_inflight.Clear();
            m_languages.Clear();
            m_preferenceLoaded.Clear();
            m_preferenceGeneration.Clear();
        }

        private async Task CheckLanguagePromptAsync(
            ScenePresence presence)
        {
            try
            {
                // Give the viewer a moment to finish arriving.
                await Task.Delay(1500).ConfigureAwait(false);
        
                if (presence == null ||
                    presence.IsChildAgent ||
                    presence.ControllingClient == null)
                {
                    return;
                }
        
                // Without persistent storage we cannot reliably determine
                // whether the resident has answered before.
                if (String.IsNullOrWhiteSpace(
                    m_databaseConnectionString))
                {
                    return;
                }

                m_log.WarnFormat(
                    "[HOLONEON TRANSLATOR]: " +
                    "Lang Preference Check {0}",
                    presence.UUID
                );

        
                PreferenceRow preference =
                    await Task.Run(
                        delegate
                        {
                            return LoadPreferenceFromDatabase(
                                presence.UUID
                            );
                        }
                    ).ConfigureAwait(false);
        
                // A row means they have already made a choice,
                // including Enabled=0 / Off.
                if (preference.Exists)
                    return;
        
                ShowLanguageDialog(presence);
            }
            catch (Exception e)
            {
                m_log.WarnFormat(
                    "[HOLONEON TRANSLATOR]: " +
                    "Could not check language preference for {0}: {1}",
                    presence.UUID,
                    e.Message
                );
            }
        }

        private void ShowLanguageDialog(
            ScenePresence presence)
        {
            Scene scene = presence.Scene;
        
            if (scene == null)
                return;

            m_log.WarnFormat(
                "[HOLONEON TRANSLATOR]: " +
                "Show Dialog {0}",
                presence.UUID
            );
        
            IDialogModule dialog =
                scene.RequestModuleInterface<IDialogModule>();
        
            if (dialog == null)
            {
                m_log.Warn(
                    "[HOLONEON TRANSLATOR]: " +
                    "IDialogModule is unavailable."
                );
        
                return;
            }
        
            string[] buttons =
            {
                "English",
                "Português",
                "Español",
                "Français",
                "Deutsch",
                "Italiano",
                "Nederlands",
                "Off"
            };
        
            string message =
                "Choose your automatic chat translation language.\n\n" +
                "Escolha o idioma de tradução do chat.\n" +
                "Elija el idioma de traducción.\n" +
                "Choisissez votre langue de traduction.\n\n" +
                "You can change this later with /" +
                m_controlChannel +
                " en, pt, es, etc.";
        
            m_log.WarnFormat(
                "[HOLONEON TRANSLATOR]: " +
                "Send Dialog To User {0}",
                presence.UUID
            );

            dialog.SendDialogToUser(
                presence.UUID,
                "Holoneon Translator",
                TRANSLATOR_DIALOG_ID,
                scene.RegionInfo.EstateSettings.EstateOwner,
                "Holoneon",
                "Translator",
                message,
                UUID.Zero,
                m_controlChannel,
                buttons
            );
        }

        private void OnNewClient(IClientAPI client)
        {
            if (client == null)
                return;

            client.OnChatFromClient += OnChatFromClient;

            int generation =
                m_preferenceGeneration.AddOrUpdate(
                    client.AgentId,
                    1,
                    delegate(UUID id, int value)
                    {
                        return value + 1;
                    }
                );

            m_preferenceLoaded[client.AgentId] = false;

            LoadPreferenceInBackground(client.AgentId, generation);
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            if (!m_enabled || chat == null || chat.Sender == null)
                return;

            if (chat.Channel == m_controlChannel)
            {
                HandleControlCommand(chat.Sender, chat.Message);
                return;
            }

            // Only normal public chat.
            if (chat.Channel != 0)
                return;

            if (String.IsNullOrWhiteSpace(chat.Message))
                return;

            // OpenSim/SL viewer chat values:
            // 0 whisper, 1 say/normal, 2 shout.
            int chatType = (int)chat.Type;

            if (chatType < 0 || chatType > 2)
                return;

            string text = chat.Message.Trim();

            if (text.Length > m_maxMessageLength)
                text = text.Substring(0, m_maxMessageLength);

            // Copy everything we need before leaving the event callback.
            Scene scene = chat.Scene as Scene;

            if (scene == null)
                return;

            UUID senderId = chat.Sender.AgentId;
            string senderName = chat.From;
            Vector3 position = chat.Position;
            int type = chatType;

            _ = ProcessChatAsync(
                scene,
                senderId,
                senderName,
                position,
                type,
                text
            );
        }

        private void HandleControlCommand(IClientAPI client, string message)
        {
            string command = (message ?? "").Trim();

            if (String.IsNullOrEmpty(command) ||
                command.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                bool loaded;

                if (m_preferenceLoaded.TryGetValue(client.AgentId, out loaded) &&
                    !loaded &&
                    !String.IsNullOrWhiteSpace(m_databaseConnectionString))
                {
                    SendSystemMessage(
                        client,
                        "Translator preference is still loading. Try /" +
                        m_controlChannel + " status again."
                    );

                    return;
                }

                string language;

                if (m_languages.TryGetValue(client.AgentId, out language))
                {
                    SendSystemMessage(
                        client,
                        "Translator ON. Target language: " +
                        language.ToUpperInvariant() +
                        ". Provider: " +
                        m_provider +
                        "."
                    );
                }
                else
                {
                    SendSystemMessage(
                        client,
                        "Translator is OFF. Example: /" +
                        m_controlChannel +
                        " en"
                    );
                }

                return;
            }

            if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                SendSystemMessage(
                    client,
                    "Translator commands: /" +
                    m_controlChannel +
                    " en | pt | es | fr | de | it | nl | status | off"
                );

                return;
            }

            if (command.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                string oldLanguage;

                if (!m_languages.TryRemove(client.AgentId, out oldLanguage))
                    oldLanguage = "en";

                MarkPreferenceChanged(client.AgentId);
                SavePreferenceInBackground(client.AgentId, oldLanguage, false);

                SendSystemMessage(client, "Translator OFF.");
                return;
            }

            switch (command.ToLowerInvariant())
            {
                case "english":
                    command = "en";
                    break;
            
                case "português":
                case "portugues":
                case "portuguese":
                    command = "pt";
                    break;
            
                case "español":
                case "espanol":
                case "spanish":
                    command = "es";
                    break;
            
                case "français":
                case "francais":
                case "french":
                    command = "fr";
                    break;
            
                case "deutsch":
                case "german":
                    command = "de";
                    break;
            
                case "italiano":
                case "italian":
                    command = "it";
                    break;
            
                case "nederlands":
                case "dutch":
                    command = "nl";
                    break;
            }

            string languageCode = NormalizeLanguageCode(command);

            if (languageCode == null)
            {
                SendSystemMessage(
                    client,
                    "Invalid language code. Use a code such as en, pt, es, fr, de, it or nl."
                );

                return;
            }

            m_languages[client.AgentId] = languageCode;

            MarkPreferenceChanged(client.AgentId);
            SavePreferenceInBackground(client.AgentId, languageCode, true);

            SendSystemMessage(
                client,
                "Translator ON. Target language: " +
                languageCode.ToUpperInvariant() +
                "."
            );
        }

        private void MarkPreferenceChanged(UUID agentId)
        {
            m_preferenceGeneration.AddOrUpdate(
                agentId,
                1,
                delegate(UUID id, int value)
                {
                    return value + 1;
                }
            );

            m_preferenceLoaded[agentId] = true;
        }

        private async Task ProcessChatAsync(
            Scene scene,
            UUID senderId,
            string senderName,
            Vector3 senderPosition,
            int chatType,
            string text)
        {
            try
            {
                List<Recipient> recipients = new List<Recipient>();

                scene.ForEachScenePresence(
                    delegate(ScenePresence sp)
                    {
                        if (sp == null || sp.IsChildAgent)
                            return;

                        if (sp.UUID == senderId)
                            return;

                        string targetLanguage;

                        if (!m_languages.TryGetValue(
                            sp.UUID,
                            out targetLanguage))
                        {
                            return;
                        }

                        if (!CanHear(
                            chatType,
                            senderPosition,
                            sp.AbsolutePosition))
                        {
                            return;
                        }

                        if (sp.ControllingClient == null)
                            return;

                        recipients.Add(
                            new Recipient
                            {
                                Client = sp.ControllingClient,
                                Language = targetLanguage
                            }
                        );
                    }
                );

                if (recipients.Count == 0)
                    return;

                IEnumerable<IGrouping<string, Recipient>> groups =
                    recipients.GroupBy(
                        delegate(Recipient r)
                        {
                            return r.Language;
                        },
                        StringComparer.OrdinalIgnoreCase
                    );

                string detectedSourceLanguage = null;
                
                foreach (IGrouping<string, Recipient> group in groups)
                {
                    string target = group.Key;
                
                    // Once one translation has told us the source language,
                    // don't make another same-language request for this message.
                    if (!String.IsNullOrWhiteSpace(detectedSourceLanguage) &&
                        String.Equals(
                            detectedSourceLanguage,
                            target,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                
                    TranslationResult result =
                        await TranslateCachedAsync(text, target)
                            .ConfigureAwait(false);
                
                    if (result == null ||
                        String.IsNullOrWhiteSpace(result.Text))
                    {
                        continue;
                    }
                
                    if (!String.IsNullOrWhiteSpace(result.SourceLanguage))
                    {
                        detectedSourceLanguage =
                            result.SourceLanguage.ToLowerInvariant();
                    }
                
                    // The first request itself might have been source -> source.
                    // Don't display that duplicate.
                    if (!String.IsNullOrWhiteSpace(result.SourceLanguage) &&
                        String.Equals(
                            result.SourceLanguage,
                            target,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                
                    string translated = result.Text;

                    // Don't duplicate chat when the "translation"
                    // is effectively identical to the original message.
                    if (String.Equals(
                            translated.Trim(),
                            text.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    translated +=
                        " [" +
                        target.ToUpperInvariant() +
                        "]";

                    //viewer ignores this usually
                    string fromName =
                        "[" +
                        target.ToUpperInvariant() +
                        "] " +
                        senderName;

                    foreach (Recipient recipient in group)
                    {
                        try
                        {
                            recipient.Client.SendChatMessage(
                                translated,
                                (byte)chatType,
                                senderPosition,
                                fromName,
                                senderId,
                                senderId,
                                CHAT_SOURCE_AGENT,
                                CHAT_AUDIBLE_FULLY
                            );
                        }
                        catch (Exception e)
                        {
                            m_log.DebugFormat(
                                "[HOLONEON TRANSLATOR]: Could not deliver translation to {0}: {1}",
                                recipient.Client.AgentId,
                                e.Message
                            );
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat(
                    "[HOLONEON TRANSLATOR]: Chat translation failed: {0}",
                    e.Message
                );

                m_log.Debug(e.ToString());
            }
        }

        private bool CanHear(
            int chatType,
            Vector3 senderPosition,
            Vector3 listenerPosition)
        {
            float distance =
                Vector3.Distance(senderPosition, listenerPosition);

            switch (chatType)
            {
                case 0:
                    return distance <= m_whisperDistance;

                case 1:
                    return distance <= m_sayDistance;

                case 2:
                    return distance <= m_shoutDistance;

                default:
                    return false;
            }
        }

        private Task<TranslationResult> TranslateCachedAsync(
            string text,
            string target)
        {
            string cacheKey =
                m_provider +
                "\n" +
                m_fallbackProvider +
                "\n" +
                target +
                "\n" +
                text;

            CacheEntry cached;

            if (m_cache.TryGetValue(cacheKey, out cached))
            {
                if (cached.ExpiresUtc > DateTime.UtcNow)
                    return Task.FromResult(
                       new TranslationResult
                       {
                           Text = cached.Text,
                           SourceLanguage = cached.SourceLanguage
                       }
                );

                CacheEntry ignored;
                m_cache.TryRemove(cacheKey, out ignored);
            }

            System.Lazy<Task<TranslationResult>> work =
                m_inflight.GetOrAdd(
                    cacheKey,
                    delegate(string key)
                    {
                        return new System.Lazy<Task<TranslationResult>>(
                            delegate
                            {
                                return TranslateAndCacheAsync(
                                    cacheKey,
                                    text,
                                    target
                                );
                            }
                        );
                    }
                );

            return AwaitInflightAsync(cacheKey, work);
        }

        private async Task<TranslationResult> AwaitInflightAsync(
            string cacheKey,
            System.Lazy<Task<TranslationResult>> work)
        {
            try
            {
                return await work.Value.ConfigureAwait(false);
            }
            finally
            {
                System.Lazy<Task<TranslationResult>> ignored;
                m_inflight.TryRemove(cacheKey, out ignored);
            }
        }

        private async Task<TranslationResult> TranslateAndCacheAsync(
            string cacheKey,
            string text,
            string target)
        {
            TranslationResult result = null;

            try
            {
                result =
                    await TranslateUsingProviderAsync(
                        m_provider,
                        text,
                        target
                    ).ConfigureAwait(false);
            }
            catch (Exception primaryError)
            {
                if (!String.IsNullOrEmpty(m_fallbackProvider))
                {
                    m_log.WarnFormat(
                        "[HOLONEON TRANSLATOR]: Provider {0} failed for target {1}: {2}. Trying fallback {3}.",
                        m_provider,
                        target,
                        primaryError.Message,
                        m_fallbackProvider
                    );

                    result =
                        await TranslateUsingProviderAsync(
                            m_fallbackProvider,
                            text,
                            target
                        ).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }

            if (result != null &&
                !String.IsNullOrWhiteSpace(result.Text) &&
                m_cacheSeconds > 0)
            {
                TrimCacheIfNeeded();

                m_cache[cacheKey] =
                    new CacheEntry
                    {
                        Text = result.Text,
                        SourceLanguage = result.SourceLanguage,
                        ExpiresUtc =
                            DateTime.UtcNow.AddSeconds(m_cacheSeconds)
                    };
            }

            return result;
        }

        private Task<TranslationResult> TranslateUsingProviderAsync(
            string provider,
            string text,
            string target)
        {
            switch (provider)
            {
                case PROVIDER_GOOGLE_NMT:
                    return TranslateGoogleAsync(text, target, false);

                case PROVIDER_GOOGLE_LLM:
                    return TranslateGoogleAsync(text, target, true);

                case PROVIDER_LIBRE:
                    return TranslateLibreAsync(text, target);

                default:
                    throw new InvalidOperationException(
                        "Unknown translation provider: " + provider
                    );
            }
        }

        private async Task<TranslationResult> TranslateLibreAsync(
            string text,
            string target)
        {
            if (String.IsNullOrWhiteSpace(m_libreUrl))
                throw new InvalidOperationException(
                    "LibreURL is not configured."
                );

            Dictionary<string, object> payload =
                new Dictionary<string, object>();

            payload["q"] = text;
            payload["source"] = "auto";
            payload["target"] = target;
            payload["format"] = "text";

            if (!String.IsNullOrWhiteSpace(m_libreApiKey))
                payload["api_key"] = m_libreApiKey;

            string json = JsonSerializer.Serialize(payload);

            using (StringContent content =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"))
            {
                using (HttpResponseMessage response =
                    await m_http.PostAsync(
                        m_libreUrl,
                        content
                    ).ConfigureAwait(false))
                {
                    string body =
                        await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception(
                            "LibreTranslate HTTP " +
                            (int)response.StatusCode
                        );
                    }

                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        JsonElement translated;

                        if (doc.RootElement.TryGetProperty(
                            "translatedText",
                            out translated))
                        {
                            string sourceLanguage = null;
                            JsonElement detectedLanguage;

                            if (doc.RootElement.TryGetProperty(
                                "detectedLanguage",
                                out detectedLanguage))
                            {
                                JsonElement language;

                                if (detectedLanguage.ValueKind == JsonValueKind.Object &&
                                    detectedLanguage.TryGetProperty(
                                        "language",
                                        out language))
                                {
                                    sourceLanguage = language.GetString();
                                }
                                else if (detectedLanguage.ValueKind == JsonValueKind.String)
                                {
                                    sourceLanguage = detectedLanguage.GetString();
                                }
                            }

                            return new TranslationResult
                            {
                                Text = translated.GetString(),
                                SourceLanguage = sourceLanguage
                            };
                        }
                    }
                }
            }

            return null;
        }

        private async Task<TranslationResult> TranslateGoogleAsync(
            string text,
            string target,
            bool useLlm)
        {
            if (String.IsNullOrWhiteSpace(m_googleApiKey))
                throw new InvalidOperationException(
                    "GoogleAPIKey is not configured."
                );

            Dictionary<string, object> payload =
                new Dictionary<string, object>();

            // Basic v2 accepts one string or an array. We use an array so the
            // request shape also matches Google's Translation LLM examples.
            payload["q"] = new string[] { text };
            payload["target"] = target;
            payload["format"] = "text";

            if (useLlm)
            {
                if (String.IsNullOrWhiteSpace(m_googleProjectId))
                {
                    throw new InvalidOperationException(
                        "GoogleProjectID is required for google-llm."
                    );
                }

                if (String.IsNullOrWhiteSpace(m_googleRegion))
                {
                    throw new InvalidOperationException(
                        "GoogleRegion is required for google-llm."
                    );
                }

                payload["model"] =
                    "projects/" +
                    m_googleProjectId +
                    "/locations/" +
                    m_googleRegion +
                    "/models/general/translation-llm";
            }
            else
            {
                // Explicitly select NMT.
                payload["model"] = "nmt";
            }

            string json = JsonSerializer.Serialize(payload);

            using (HttpRequestMessage request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://translation.googleapis.com/language/translate/v2"))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-goog-api-key",
                    m_googleApiKey
                );

                request.Content =
                    new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );

                using (HttpResponseMessage response =
                    await m_http.SendAsync(request)
                        .ConfigureAwait(false))
                {
                    string body =
                        await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception(
                            "Google Translation HTTP " +
                            (int)response.StatusCode
                        );
                    }

                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        JsonElement translations =
                            doc.RootElement
                               .GetProperty("data")
                               .GetProperty("translations");

                        if (translations.GetArrayLength() == 0)
                            return null;

                        JsonElement translation = translations[0];

                        string translated =
                            translation
                                .GetProperty("translatedText")
                                .GetString();

                        string sourceLanguage = null;
                        JsonElement detected;

                        if (translation.TryGetProperty(
                            "detectedSourceLanguage",
                            out detected))
                        {
                            sourceLanguage = detected.GetString();
                        }

                        return new TranslationResult
                        {
                            Text = WebUtility.HtmlDecode(translated),
                            SourceLanguage = sourceLanguage
                        };
                    }
                }
            }
        }

        private void TrimCacheIfNeeded()
        {
            if (m_cache.Count < m_maxCacheEntries)
                return;

            DateTime now = DateTime.UtcNow;

            foreach (KeyValuePair<string, CacheEntry> kvp in m_cache)
            {
                if (kvp.Value.ExpiresUtc <= now)
                {
                    CacheEntry ignored;
                    m_cache.TryRemove(kvp.Key, out ignored);
                }
            }

            // If it is still full, remove a small arbitrary slice rather
            // than letting the cache grow forever.
            if (m_cache.Count >= m_maxCacheEntries)
            {
                int removeCount =
                    Math.Max(1, m_maxCacheEntries / 10);

                foreach (string key in m_cache.Keys.Take(removeCount))
                {
                    CacheEntry ignored;
                    m_cache.TryRemove(key, out ignored);
                }
            }
        }

        private void LoadPreferenceInBackground(
            UUID agentId,
            int generation)
        {
            if (String.IsNullOrWhiteSpace(m_databaseConnectionString))
            {
                m_preferenceLoaded[agentId] = true;
                return;
            }

            _ = Task.Run(
                delegate
                {
                    try
                    {
                        PreferenceRow row =
                            LoadPreferenceFromDatabase(agentId);

                        int currentGeneration;

                        if (!m_preferenceGeneration.TryGetValue(
                            agentId,
                            out currentGeneration))
                        {
                            return;
                        }

                        // A newer login or an in-world command happened while
                        // the DB read was running. Do not overwrite it.
                        if (currentGeneration != generation)
                            return;

                        if (row.Exists &&
                            row.Enabled &&
                            !String.IsNullOrWhiteSpace(row.Language))
                        {
                            m_languages[agentId] =
                                row.Language.ToLowerInvariant();
                        }
                        else
                        {
                            string ignored;
                            m_languages.TryRemove(agentId, out ignored);
                        }

                        m_preferenceLoaded[agentId] = true;
                    }
                    catch (Exception e)
                    {
                        m_preferenceLoaded[agentId] = true;

                        m_log.WarnFormat(
                            "[HOLONEON TRANSLATOR]: Failed loading preference for {0}: {1}",
                            agentId,
                            e.Message
                        );

                        m_log.Debug(e.ToString());
                    }
                }
            );
        }

        private PreferenceRow LoadPreferenceFromDatabase(UUID agentId)
        {
            PreferenceRow result =
                new PreferenceRow
                {
                    Exists = false,
                    Enabled = false,
                    Language = null
                };

            using (MySqlConnection connection =
                new MySqlConnection(m_databaseConnectionString))
            {
                connection.Open();

                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT `Language`, `Enabled` " +
                        "FROM `" + m_databaseTable + "` " +
                        "WHERE `PrincipalID` = @PrincipalID LIMIT 1";

                    command.Parameters.AddWithValue(
                        "@PrincipalID",
                        agentId.ToString()
                    );

                    using (MySqlDataReader reader =
                        command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            result.Exists = true;
                            result.Language =
                                Convert.ToString(reader["Language"]);
                            result.Enabled =
                                Convert.ToInt32(reader["Enabled"]) != 0;
                        }
                    }
                }
            }

            return result;
        }

        private void SavePreferenceInBackground(
            UUID agentId,
            string language,
            bool enabled)
        {
            if (String.IsNullOrWhiteSpace(m_databaseConnectionString))
                return;

            _ = Task.Run(
                delegate
                {
                    try
                    {
                        SavePreferenceToDatabase(
                            agentId,
                            language,
                            enabled
                        );
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat(
                            "[HOLONEON TRANSLATOR]: Failed saving preference for {0}: {1}",
                            agentId,
                            e.Message
                        );

                        m_log.Debug(e.ToString());
                    }
                }
            );
        }

        private void SavePreferenceToDatabase(
            UUID agentId,
            string language,
            bool enabled)
        {
            using (MySqlConnection connection =
                new MySqlConnection(m_databaseConnectionString))
            {
                connection.Open();

                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        "INSERT INTO `" + m_databaseTable + "` " +
                        "(`PrincipalID`, `Language`, `Enabled`) " +
                        "VALUES (@PrincipalID, @Language, @Enabled) " +
                        "ON DUPLICATE KEY UPDATE " +
                        "`Language` = @Language, " +
                        "`Enabled` = @Enabled, " +
                        "`Updated` = CURRENT_TIMESTAMP";

                    command.Parameters.AddWithValue(
                        "@PrincipalID",
                        agentId.ToString()
                    );

                    command.Parameters.AddWithValue(
                        "@Language",
                        language
                    );

                    command.Parameters.AddWithValue(
                        "@Enabled",
                        enabled ? 1 : 0
                    );

                    command.ExecuteNonQuery();
                }
            }
        }

        private void SendSystemMessage(
            IClientAPI client,
            string text)
        {
            try
            {
                client.SendChatMessage(
                    text,
                    1,
                    Vector3.Zero,
                    "Holoneon Translator",
                    UUID.Zero,
                    UUID.Zero,
                    0,
                    CHAT_AUDIBLE_FULLY
                );
            }
            catch (Exception e)
            {
                m_log.DebugFormat(
                    "[HOLONEON TRANSLATOR]: Could not send status message to {0}: {1}",
                    client.AgentId,
                    e.Message
                );
            }
        }

        private static string NormalizeProvider(string provider)
        {
            string value =
                (provider ?? "").Trim().ToLowerInvariant();

            switch (value)
            {
                case "":
                    return "";

                case "libre":
                    return PROVIDER_LIBRE;

                case "google":
                case "google-nmt":
                case "nmt":
                    return PROVIDER_GOOGLE_NMT;

                case "google-llm":
                case "translation-llm":
                case "tllm":
                    return PROVIDER_GOOGLE_LLM;

                default:
                    return value;
            }
        }

        private static string NormalizeLanguageCode(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim().ToLowerInvariant();

            if (value.Length < 2 || value.Length > 16)
                return null;

            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];

                if ((c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-')
                {
                    continue;
                }

                return null;
            }

            if (value[0] == '-' ||
                value[value.Length - 1] == '-')
            {
                return null;
            }

            return value;
        }

        private static bool IsSafeSqlIdentifier(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];

                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }
    }
}


