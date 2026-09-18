/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

 // modified for s3 Fiona Sweet 2026

using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;
using OpenMetaverse.StructuredData;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace OpenSim.Services.FSAssetService
{
    /// <summary>
    /// Minimal S3 v4-signed client, enough for Backblaze B2's S3-compatible API.
    /// Deliberately dependency-free so the service does not drag AWSSDK.S3 (and its
    /// own transitive dependency set) into the OpenSim bin directory.
    /// </summary>
    internal sealed class B2S3Client
    {
        private const string ALGORITHM = "AWS4-HMAC-SHA256";
        private const string SERVICE = "s3";
        private const string EMPTY_PAYLOAD_SHA256 =
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        private static readonly HttpClient m_Http = new HttpClient(
                new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly string m_Scheme;
        private readonly string m_EndpointHost;
        private readonly int m_Port;
        private readonly string m_Region;
        private readonly string m_Bucket;
        private readonly string m_AccessKey;
        private readonly string m_SecretKey;
        private readonly bool m_PathStyle;

        public string Bucket { get { return m_Bucket; } }
        public string Region { get { return m_Region; } }
        public string EndpointHost { get { return m_EndpointHost; } }

        public B2S3Client(string endpoint, string region, string bucket,
                          string accessKey, string secretKey, bool pathStyle)
        {
            Uri uri = new Uri(endpoint);

            m_Scheme = uri.Scheme;
            m_EndpointHost = uri.Host;
            m_Port = uri.IsDefaultPort ? -1 : uri.Port;
            m_Bucket = bucket;
            m_AccessKey = accessKey;
            m_SecretKey = secretKey;
            m_PathStyle = pathStyle;
            m_Region = string.IsNullOrEmpty(region) ? DeriveRegion(m_EndpointHost) : region;

            if (string.IsNullOrEmpty(m_Region))
                throw new Exception("Could not determine S3 region; set B2Region explicitly");
        }

        /// <summary>
        /// Backblaze endpoints look like s3.us-west-004.backblazeb2.com, so the
        /// region is the second label. Anything else must be configured by hand.
        /// </summary>
        private static string DeriveRegion(string host)
        {
            string[] parts = host.Split('.');
            if (parts.Length >= 3 && parts[0].Equals("s3", StringComparison.OrdinalIgnoreCase))
                return parts[1];

            return string.Empty;
        }

        /// <returns>The object bytes, or null if the object does not exist.</returns>
        public byte[] GetObject(string key)
        {
            using (HttpResponseMessage response = Send(HttpMethod.Get, key, null, null))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (!response.IsSuccessStatusCode)
                    throw new Exception(string.Format("S3 GET {0} returned {1}: {2}",
                            key, (int)response.StatusCode, ReadBody(response)));

                return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }

        public bool ObjectExists(string key)
        {
            using (HttpResponseMessage response = Send(HttpMethod.Head, key, null, null))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return false;

                if (!response.IsSuccessStatusCode)
                    throw new Exception(string.Format("S3 HEAD {0} returned {1}",
                            key, (int)response.StatusCode));

                return true;
            }
        }

        public void PutObject(string key, byte[] data, string contentType)
        {
            using (HttpResponseMessage response = Send(HttpMethod.Put, key, data ?? Array.Empty<byte>(), contentType))
            {
                if (!response.IsSuccessStatusCode)
                    throw new Exception(string.Format("S3 PUT {0} returned {1}: {2}",
                            key, (int)response.StatusCode, ReadBody(response)));
            }
        }

        public void DeleteObject(string key)
        {
            using (HttpResponseMessage response = Send(HttpMethod.Delete, key, null, null))
            {
                // S3 delete is idempotent; 404 is not an error.
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                    throw new Exception(string.Format("S3 DELETE {0} returned {1}: {2}",
                            key, (int)response.StatusCode, ReadBody(response)));
            }
        }

        private static string ReadBody(HttpResponseMessage response)
        {
            try
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return body == null || body.Length <= 512 ? body : body.Substring(0, 512);
            }
            catch
            {
                return string.Empty;
            }
        }

        private HttpResponseMessage Send(HttpMethod method, string key, byte[] payload, string contentType)
        {
            string canonicalPath;
            Uri uri = BuildUri(key, out canonicalPath);

            using (HttpRequestMessage request = new HttpRequestMessage(method, uri))
            {
                string payloadHash;

                if (payload != null)
                {
                    payloadHash = HexLower(Sha256(payload));

                    ByteArrayContent content = new ByteArrayContent(payload);
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
                    request.Content = content;
                }
                else
                {
                    payloadHash = EMPTY_PAYLOAD_SHA256;
                }

                Sign(request, method, canonicalPath, uri, payloadHash);

                return m_Http.SendAsync(request, HttpCompletionOption.ResponseContentRead)
                             .GetAwaiter().GetResult();
            }
        }

        private Uri BuildUri(string key, out string canonicalPath)
        {
            string encodedKey = UriEncodePath(key);

            if (m_PathStyle)
            {
                canonicalPath = "/" + UriEncodeSegment(m_Bucket) + "/" + encodedKey;
                return new Uri(BaseUrl(m_EndpointHost) + canonicalPath);
            }

            canonicalPath = "/" + encodedKey;
            return new Uri(BaseUrl(m_Bucket + "." + m_EndpointHost) + canonicalPath);
        }

        private string BaseUrl(string host)
        {
            return m_Port < 0
                ? m_Scheme + "://" + host
                : m_Scheme + "://" + host + ":" + m_Port.ToString(CultureInfo.InvariantCulture);
        }

        private void Sign(HttpRequestMessage request, HttpMethod method,
                          string canonicalPath, Uri uri, string payloadHash)
        {
            DateTime now = DateTime.UtcNow;
            string amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            string dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string host = uri.IsDefaultPort
                    ? uri.Host
                    : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

            // Only host and x-amz-* headers need to be signed; Content-Type and
            // Content-Length are set by HttpClient and left unsigned on purpose.
            const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

            string canonicalHeaders =
                    "host:" + host + "\n" +
                    "x-amz-content-sha256:" + payloadHash + "\n" +
                    "x-amz-date:" + amzDate + "\n";

            string canonicalRequest =
                    method.Method + "\n" +
                    canonicalPath + "\n" +
                    string.Empty + "\n" +          // no query string
                    canonicalHeaders + "\n" +
                    signedHeaders + "\n" +
                    payloadHash;

            string scope = dateStamp + "/" + m_Region + "/" + SERVICE + "/aws4_request";

            string stringToSign =
                    ALGORITHM + "\n" +
                    amzDate + "\n" +
                    scope + "\n" +
                    HexLower(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));

            byte[] signingKey = HmacSha256(
                    HmacSha256(
                        HmacSha256(
                            HmacSha256(Encoding.UTF8.GetBytes("AWS4" + m_SecretKey), dateStamp),
                            m_Region),
                        SERVICE),
                    "aws4_request");

            string signature = HexLower(HmacSha256(signingKey, stringToSign));

            request.Headers.TryAddWithoutValidation("Authorization",
                    ALGORITHM +
                    " Credential=" + m_AccessKey + "/" + scope +
                    ", SignedHeaders=" + signedHeaders +
                    ", Signature=" + signature);
        }

        private static string UriEncodePath(string path)
        {
            StringBuilder sb = new StringBuilder(path.Length + 16);

            foreach (char c in path)
            {
                if (c == '/')
                    sb.Append('/');
                else
                    sb.Append(UriEncodeChar(c));
            }

            return sb.ToString();
        }

        private static string UriEncodeSegment(string segment)
        {
            StringBuilder sb = new StringBuilder(segment.Length + 16);

            foreach (char c in segment)
                sb.Append(UriEncodeChar(c));

            return sb.ToString();
        }

        private static string UriEncodeChar(char c)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            {
                return c.ToString();
            }

            StringBuilder sb = new StringBuilder(12);
            foreach (byte b in Encoding.UTF8.GetBytes(new[] { c }))
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));

            return sb.ToString();
        }

        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        private static byte[] HmacSha256(byte[] key, string data)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string HexLower(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);

            foreach (byte b in data)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));

            return sb.ToString();
        }
    }

    public class FSAssetConnector : ServiceBase, IAssetService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();

        static byte[] ToCString(string s)
        {
            byte[] ret = enc.GetBytes(s);
            Array.Resize(ref ret, ret.Length + 1);
            ret[ret.Length - 1] = 0;

            return ret;
        }

        protected IAssetLoader m_AssetLoader = null;
        protected IFSAssetDataPlugin m_DataConnector = null;
        protected IAssetService m_FallbackService;
        protected Thread m_WriterThread;
        protected Thread m_StatsThread;
        protected string m_SpoolDirectory;
        protected int m_WriteSleepMs;
        protected object m_readLock = new object();
        protected object m_statsLock = new object();
        protected int m_readCount = 0;
        protected int m_readTicks = 0;
        protected int m_missingAssets = 0;
        protected int m_missingAssetsFS = 0;
        protected bool m_useOsgridFormat = false;
        protected bool m_showStats = true;

        // Backblaze B2 (S3-compatible) backing store
        private B2S3Client m_S3;
        protected string m_KeyPrefix = string.Empty;
        protected bool m_Compress = false;
        protected int m_CompressMinBytes = 512;
        protected int m_MaxUploadAttempts = 5;

        // Touched only by the writer thread, so no lock required.
        private readonly Dictionary<string, int> m_SpoolFailures = new Dictionary<string, int>();

        private static bool m_mainInitialized;
        private static object m_initLock = new object();

        private bool m_isMainInstance;

        public FSAssetConnector(IConfigSource config)
            : this(config, "AssetService")
        {
        }

        public FSAssetConnector(IConfigSource config, string configName) : base(config)
        {
            IConfig assetConfig = config.Configs[configName];
            if (assetConfig == null)
                throw new Exception("No AssetService configuration");

            string endpoint = assetConfig.GetString("B2Endpoint", string.Empty);
            string bucket = assetConfig.GetString("B2Bucket", string.Empty);
            string accessKey = assetConfig.GetString("B2AccessKey", string.Empty);
            string secretKey = assetConfig.GetString("B2SecretKey", string.Empty);
            string region = assetConfig.GetString("B2Region", string.Empty);
            bool pathStyle = assetConfig.GetBoolean("B2PathStyle", true);

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(bucket) ||
                string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            {
                m_log.ErrorFormat(
                        "[FSASSETS]: B2Endpoint, B2Bucket, B2AccessKey and B2SecretKey are all required in section {0}",
                        configName);
                throw new Exception("Configuration Error");
            }

            if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = "https://" + endpoint;
            }

            m_KeyPrefix = assetConfig.GetString("B2KeyPrefix", string.Empty).Trim().Trim('/');
            if (m_KeyPrefix.Length > 0)
                m_KeyPrefix += "/";

            m_Compress = assetConfig.GetBoolean("B2Compress", false);
            m_CompressMinBytes = assetConfig.GetInt("B2CompressMinBytes", m_CompressMinBytes);
            m_MaxUploadAttempts = assetConfig.GetInt("MaxUploadAttempts", m_MaxUploadAttempts);

            // Built per instance: secondary instances serve reads and need a working client too.
            m_S3 = new B2S3Client(endpoint.TrimEnd('/'), region, bucket, accessKey, secretKey, pathStyle);

            lock (m_initLock)
            {
                if (!m_mainInitialized)
                {
                    m_mainInitialized = true;
                    m_isMainInstance = !assetConfig.GetBoolean("SecondaryInstance", false);

                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "show assets", "show assets", "Show asset stats",
                            HandleShowAssets);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "show digest", "show digest <ID>", "Show asset digest",
                            HandleShowDigest);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "delete asset", "delete asset <ID>",
                            "Delete asset from database",
                            HandleDeleteAsset);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "import", "import <conn> <table> [<start> <count>]",
                            "Import legacy assets",
                            HandleImportAssets);
                    MainConsole.Instance.Commands.AddCommand("fs", false,
                            "force import", "force import <conn> <table> [<start> <count>]",
                            "Import legacy assets, overwriting current content",
                            HandleImportAssets);
                }
                else
                {
                    m_isMainInstance = false; // yes redundant...
                }
            }

            // Get Database Connector from Asset Config (If present)
            string dllName = assetConfig.GetString("StorageProvider", string.Empty);
            string connectionString = assetConfig.GetString("ConnectionString", string.Empty);
            string realm = assetConfig.GetString("Realm", "fsassets");

            int SkipAccessTimeDays = assetConfig.GetInt("DaysBetweenAccessTimeUpdates", 0);

            // If not found above, fallback to Database defaults
            IConfig dbConfig = config.Configs["DatabaseService"];

            if (dbConfig != null)
            {
                if (dllName.Length == 0)
                    dllName = dbConfig.GetString("StorageProvider", String.Empty);

                if (connectionString.Length == 0)
                    connectionString = dbConfig.GetString("ConnectionString", String.Empty);
            }

            // No databse connection found in either config
            if (string.IsNullOrEmpty(dllName))
                throw new Exception("No StorageProvider configured");

            if (string.IsNullOrEmpty(connectionString))
                throw new Exception("Missing database connection string");

            // Create Storage Provider
            m_DataConnector = LoadPlugin<IFSAssetDataPlugin>(dllName);

            if (m_DataConnector == null)
                throw new Exception(string.Format("Could not find a storage interface in the module {0}", dllName));

            // Initialize DB And perform any migrations required
            m_log.InfoFormat("[FSASSETS]: Connecting to: {0}", connectionString);
            m_DataConnector.Initialise(connectionString, realm, SkipAccessTimeDays);

            // Setup Fallback Service
            string str = assetConfig.GetString("FallbackService", string.Empty);

            if (str.Length > 0)
            {
                object[] args = new object[] { config };
                m_FallbackService = LoadPlugin<IAssetService>(str, args);
                if (m_FallbackService != null)
                {
                    m_log.Info("[FSASSETS]: Fallback service loaded");
                }
                else
                {
                    m_log.Error("[FSASSETS]: Failed to load fallback service");
                }
            }

            // Setup directory structure including temp directory
            m_SpoolDirectory = assetConfig.GetString("SpoolDirectory", "/tmp");

            string spoolTmp = Path.Combine(m_SpoolDirectory, "spool");

            Directory.CreateDirectory(spoolTmp);

            // get write delay default = 1 sec
            m_WriteSleepMs = assetConfig.GetInt("WriteSleepMs", 1000);

            m_useOsgridFormat = assetConfig.GetBoolean("UseOsgridFormat", m_useOsgridFormat);

            // Default is to show stats to retain original behaviour
            m_showStats = assetConfig.GetBoolean("ShowConsoleStats", m_showStats);

            if (m_isMainInstance)
            {
                string loader = assetConfig.GetString("DefaultAssetLoader", string.Empty);
                if (loader.Length > 0)
                {
                    m_AssetLoader = LoadPlugin<IAssetLoader>(loader);
                    string loaderArgs = assetConfig.GetString("AssetLoaderArgs", string.Empty);
                    m_log.InfoFormat("[FSASSETS]: Loading default asset set from {0}", loaderArgs);
                    m_AssetLoader.ForEachDefaultXmlAsset(loaderArgs,
                            delegate(AssetBase a)
                            {
                                Store(a, false);
                            });
                }

                if (m_WriterThread == null)
                {
                    m_WriterThread = new Thread(Writer);
                    m_WriterThread.Start();
                }

                if (m_showStats && m_StatsThread == null)
                {
                    m_StatsThread = new Thread(Stats);
                    m_StatsThread.Start();
                }
            }

            m_log.InfoFormat("[FSASSETS]: FS asset service (B2/S3 MOD) enabled, bucket {0} at {1} region {2}",
                    m_S3.Bucket, m_S3.EndpointHost, m_S3.Region);
        }

        private void Stats()
        {
            while (true)
            {
                Thread.Sleep(60000);

                lock (m_statsLock)
                {
                    if (m_readCount > 0)
                    {
                        double avg = (double)m_readTicks / (double)m_readCount;
//                        if (avg > 10000)
//                            Environment.Exit(0);
                        m_log.InfoFormat("[FSASSETS]: Read stats: {0} files, {1} ticks, avg {2:F2}, missing {3}, FS {4}", m_readCount, m_readTicks, (double)m_readTicks / (double)m_readCount, m_missingAssets, m_missingAssetsFS);
                    }
                    m_readCount = 0;
                    m_readTicks = 0;
                    m_missingAssets = 0;
                    m_missingAssetsFS = 0;
                }
            }
        }

        string GetSHA256Hash(byte[] data)
        {
            return Util.SHA256Hash(data);
        }

        public string HashToPath(string hash)
        {
            if (hash == null || hash.Length < 10)
                return "junkyard";

            if (m_useOsgridFormat)
            {
                /*
                 * The code below is the OSGrid code.
                 */
                return Path.Combine(hash.Substring(0, 3),
                       Path.Combine(hash.Substring(3, 3)));
            }
            else
            {
                /*
                 * The below is what core would normally use.
                 * This is modified to work in OSGrid, as seen
                 * above, because the SRAS data is structured
                 * that way.
                 */
                return Path.Combine(hash.Substring(0, 2),
                       Path.Combine(hash.Substring(2, 2),
                       Path.Combine(hash.Substring(4, 2),
                       hash.Substring(6, 4))));
            }
        }

        /// <summary>
        /// Object key for a given content hash. Mirrors HashToFile, but always uses
        /// '/' separators (Path.Combine would emit '\' on Windows) and normalises the
        /// hash to lower case so a key is never ambiguous.
        /// </summary>
        public string HashToKey(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length < 10)
                return m_KeyPrefix + "junkyard/" + (string.IsNullOrEmpty(hash) ? "unknown" : hash.ToLowerInvariant());

            string h = hash.ToLowerInvariant();

            if (m_useOsgridFormat)
            {
                return m_KeyPrefix +
                       h.Substring(0, 3) + "/" +
                       h.Substring(3, 3) + "/" +
                       h;
            }

            return m_KeyPrefix +
                   h.Substring(0, 2) + "/" +
                   h.Substring(2, 2) + "/" +
                   h.Substring(4, 2) + "/" +
                   h.Substring(6, 4) + "/" +
                   h;
        }

        private bool AssetExists(string hash)
        {
            string key = HashToKey(hash);

            try
            {
                if (m_S3.ObjectExists(key))
                    return true;

                return m_Compress && m_S3.ObjectExists(key + ".gz");
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[FSASSETS]: HEAD failed for key {0}: {1}", key, e.Message);
                return false;
            }
        }

        public virtual bool[] AssetsExist(string[] ids)
        {
            UUID[] uuid = Array.ConvertAll(ids, id => UUID.Parse(id));
            return m_DataConnector.AssetsExist(uuid);
        }

        public string HashToFile(string hash)
        {
            return Path.Combine(HashToPath(hash), hash);
        }

        public virtual AssetBase Get(string id)
        {
            string hash;

            return Get(id, out hash);
        }

        public AssetBase Get(string id, string ForeignAssetService, bool dummy)
        {
            return null;
        }

        private AssetBase Get(string id, out string sha)
        {
            string hash = string.Empty;

            int startTime = System.Environment.TickCount;
            AssetMetadata metadata;

            lock (m_readLock)
            {
                metadata = m_DataConnector.Get(id, out hash);
            }

            sha = hash;

            if (metadata == null)
            {
                AssetBase asset = null;
                if (m_FallbackService != null)
                {
                    asset = m_FallbackService.Get(id);
                    if (asset != null)
                    {
                        asset.Metadata.ContentType =
                                SLUtil.SLAssetTypeToContentType((int)asset.Type);
                        sha = GetSHA256Hash(asset.Data);
                        m_log.InfoFormat("[FSASSETS]: Added asset {0} from fallback to local store", id);
                        Store(asset);
                    }
                }
                if (asset == null && m_showStats)
                {
                    // m_log.InfoFormat("[FSASSETS]: Asset {0} not found", id);
                    m_missingAssets++;
                }
                return asset;
            }
            AssetBase newAsset = new AssetBase();
            newAsset.Metadata = metadata;
            try
            {
                newAsset.Data = GetFsData(id, hash);
                if (newAsset.Data.Length == 0)
                {
                    AssetBase asset = null;
                    if (m_FallbackService != null)
                    {
                        asset = m_FallbackService.Get(id);
                        if (asset != null)
                        {
                            asset.Metadata.ContentType =
                                    SLUtil.SLAssetTypeToContentType((int)asset.Type);
                            sha = GetSHA256Hash(asset.Data);
                            m_log.InfoFormat("[FSASSETS]: Added asset {0} from fallback to local store", id);
                            Store(asset);
                        }
                    }
                    if (asset == null)
                    {
                        if (m_showStats)
                            m_missingAssetsFS++;
                        // m_log.InfoFormat("[FSASSETS]: Asset {0}, hash {1} not found in FS", id, hash);
                    }
                    else
                    {
                        // Deal with bug introduced in Oct. 20 (1eb3e6cc43e2a7b4053bc1185c7c88e22356c5e8)
                        // Fix bad assets before sending them elsewhere
                        if (asset.Type == (int)AssetType.Object && asset.Data != null)
                        {
                            string xml = ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(asset.Data));
                            asset.Data = Utils.StringToBytes(xml);
                        }
                        return asset;
                    }
                }

                if (m_showStats)
                {
                    lock (m_statsLock)
                    {
                        m_readTicks += Environment.TickCount - startTime;
                        m_readCount++;
                    }
                }

                // Deal with bug introduced in Oct. 20 (1eb3e6cc43e2a7b4053bc1185c7c88e22356c5e8)
                // Fix bad assets before sending them elsewhere
                if (newAsset.Type == (int)AssetType.Object && newAsset.Data != null)
                {
                    string xml = ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(newAsset.Data));
                    newAsset.Data = Utils.StringToBytes(xml);
                }

                return newAsset;
            }
            catch (Exception exception)
            {
                m_log.ErrorFormat("[FSASSETS]: Database connection error during Get: {0}", exception.Message);
                // Return null so OpenSim treats it as a 'missing asset' and stays alive
                return null;
            }
        }

        public virtual AssetMetadata GetMetadata(string id)
        {
            string hash;
            return m_DataConnector.Get(id, out hash);
        }

        public virtual byte[] GetData(string id)
        {
            string hash;
            if (m_DataConnector.Get(id, out hash) == null)
                return null;

            return GetFsData(id, hash);
        }

        public bool Get(string id, Object sender, AssetRetrieved handler)
        {
            AssetBase asset = Get(id);

            handler(id, sender, asset);

            return true;
        }

        public byte[] GetFsData(string assetId)
        {
            return GetFsData(assetId, null);
        }

        /// <summary>
        /// Fetch the payload from B2. The object key is taken from the storage-key
        /// column when it holds one, otherwise it is derived from the content hash.
        /// </summary>
        /// <param name="knownHash">
        /// Content hash if the caller already looked it up, to save a round trip.
        /// </param>
        public byte[] GetFsData(string assetId, string knownHash)
        {
            string key = null;

            try
            {
                key = m_DataConnector.GetCid(assetId);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[FSASSETS]: Storage key lookup failed for {0}: {1}", assetId, e.Message);
            }

            // Rows written by the old IPFS build hold a bare CID, which has no '/'
            // in it and is useless against B2. Fall back to the content hash.
            if (string.IsNullOrEmpty(key) || key.IndexOf('/') < 0)
            {
                string hash = knownHash;

                if (string.IsNullOrEmpty(hash))
                {
                    if (m_DataConnector.Get(assetId, out hash) == null)
                        hash = null;
                }

                if (string.IsNullOrEmpty(hash))
                {
                    m_log.WarnFormat("[FSASSETS]: No storage key or hash for asset {0}.", assetId);
                    return Array.Empty<byte>();
                }

                key = HashToKey(hash);
            }

            try
            {
                bool gzipped = key.EndsWith(".gz", StringComparison.Ordinal);
                byte[] raw = m_S3.GetObject(key);

                if (raw == null && !gzipped)
                {
                    // Object may have been stored compressed under a different setting.
                    raw = m_S3.GetObject(key + ".gz");
                    gzipped = raw != null;
                }

                if (raw == null)
                {
                    m_log.WarnFormat("[FSASSETS]: Asset {0} not present in bucket at key {1}", assetId, key);
                    return Array.Empty<byte>();
                }

                return gzipped ? Decompress(raw) : raw;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[FSASSETS]: Failed to fetch {0} from B2: {1}", key, e.Message);
            }

            return Array.Empty<byte>();
        }

        private static byte[] Compress(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                    gz.Write(data, 0, data.Length);

                return ms.ToArray();
            }
        }

        private static byte[] Decompress(byte[] data)
        {
            using (MemoryStream src = new MemoryStream(data))
            using (GZipStream gz = new GZipStream(src, CompressionMode.Decompress))
            using (MemoryStream dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        /// <summary>
        /// Upload a payload to B2 under its content-addressed key.
        /// Returns the object key on success, or an empty string on failure.
        /// </summary>
        private string UploadToB2(byte[] data, string hash, string contentType)
        {
            if (data == null)
                return string.Empty;

            if (data.Length == 0)
                m_log.WarnFormat("[FSASSETS]: Storing zero-length payload for hash {0}", hash);

            string key = HashToKey(hash);
            byte[] body = data;

            if (m_Compress && data.Length >= m_CompressMinBytes)
            {
                byte[] packed = Compress(data);
                if (packed.Length < data.Length)
                {
                    body = packed;
                    key += ".gz";
                }
            }

            try
            {
                // Keys are content-addressed, so an existing object is byte-identical.
                if (m_S3.ObjectExists(key))
                    return key;

                m_S3.PutObject(key, body, contentType);
                return key;
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[FSASSETS]: B2 upload failed for key {0}: {1}", key, e.Message);
            }

            return string.Empty;
        }

        /* Writer thread processes the spool dir, uploads to B2 and saves metadata */

        private void Writer()
        {
            string spoolSubDir = Path.Combine(m_SpoolDirectory, "spool");
            Directory.CreateDirectory(spoolSubDir);

            while (true)
            {
                // We look for .meta files because they are written after the matching .asset file.
                string[] metaFiles = Directory.GetFiles(spoolSubDir, "*.meta");

                foreach (string metaPath in metaFiles)
                {
                    try
                    {
                        string assetId = Path.GetFileNameWithoutExtension(metaPath);
                        string assetPath = Path.Combine(spoolSubDir, assetId + ".asset");

                        if (!File.Exists(assetPath))
                        {
                            m_log.WarnFormat("[FSASSETS]: Metadata exists without asset payload yet: {0}", metaPath);
                            continue;
                        }

                        byte[] data = File.ReadAllBytes(assetPath);
                        OSD osd = OSDParser.DeserializeJson(File.ReadAllText(metaPath));

                        if (!(osd is OSDMap metaMap))
                        {
                            m_log.ErrorFormat("[FSASSETS]: Invalid metadata JSON for {0}", metaPath);
                            continue;
                        }

                        if (!metaMap.ContainsKey("ID") || !metaMap.ContainsKey("Hash"))
                        {
                            m_log.ErrorFormat("[FSASSETS]: Metadata missing required fields for {0}", metaPath);
                            continue;
                        }

                        // Map images etc. don't have a 'Type' field so we have to determine it.
                        sbyte assetType;

                        if (metaMap.ContainsKey("Type"))
                        {
                            assetType = (sbyte)metaMap["Type"].AsInteger();
                        }
                        else if (metaMap.ContainsKey("ContentType") &&
                                 metaMap["ContentType"].AsString().Equals("image/x-j2c", StringComparison.OrdinalIgnoreCase))
                        {
                            assetType = (sbyte)AssetType.Texture;
                        }
                        else if (metaMap.ContainsKey("Name") &&
                                 metaMap["Name"].AsString().StartsWith("terrainImage_", StringComparison.OrdinalIgnoreCase))
                        {
                            assetType = (sbyte)AssetType.Texture;
                        }
                        else
                        {
                            assetType = 0;
                        }

                        AssetMetadata metadata = new AssetMetadata();
                        metadata.ID = metaMap["ID"].AsString();
                        metadata.FullID = metaMap["ID"].AsUUID();
                        // Use the resolved type, not a second lookup that silently yields 0
                        // when the key is absent.
                        metadata.Type = assetType;
                        metadata.Flags = metaMap.ContainsKey("Flags")
                            ? (AssetFlags)metaMap["Flags"].AsInteger()
                            : AssetFlags.Normal;
                        metadata.Name = metaMap.ContainsKey("Name") ? metaMap["Name"].AsString() : string.Empty;
                        metadata.Description = metaMap.ContainsKey("Description") ? metaMap["Description"].AsString() : string.Empty;
                        metadata.ContentType = metaMap.ContainsKey("ContentType")
                            ? metaMap["ContentType"].AsString()
                            : SLUtil.SLAssetTypeToContentType(metadata.Type);

                        string hash = metaMap["Hash"].AsString();

                        m_log.InfoFormat(
                            "[FSASSETS]: Writer processing asset {0}, type={1}, nameLen={2}, descLen={3}, dataLen={4}, hash={5}",
                            metadata.ID,
                            metadata.Type,
                            metadata.Name == null ? -1 : metadata.Name.Length,
                            metadata.Description == null ? -1 : metadata.Description.Length,
                            data == null ? -1 : data.Length,
                            hash);

                        string objectKey = UploadToB2(data, hash, metadata.ContentType);

                        if (string.IsNullOrEmpty(objectKey))
                        {
                            // Never delete the only queued copy on upload failure.
                            NoteUploadFailure(assetId, metadata.ID, assetPath, metaPath);
                            continue;
                        }

                        // Store metadata only after the upload succeeds.
                        m_DataConnector.Store(metadata, hash, objectKey);

                        // Verify the DB row before deleting the only local queued copy.
                        string storedKey = m_DataConnector.GetCid(metadata.ID);
                        if (string.IsNullOrEmpty(storedKey))
                        {
                            m_log.ErrorFormat("[FSASSETS]: DB store did not create a storage key row for asset {0}; leaving spool files for retry", metadata.ID);
                            continue;
                        }

                        if (storedKey != objectKey)
                        {
                            m_log.WarnFormat("[FSASSETS]: Stored key mismatch for asset {0}. Expected {1}, got {2}", metadata.ID, objectKey, storedKey);
                        }

                        m_SpoolFailures.Remove(assetId);

                        File.Delete(assetPath);
                        File.Delete(metaPath);

                        m_log.InfoFormat("[FSASSETS]: Stored asset {0} in B2 as {1}", metadata.ID, objectKey);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat("[FSASSETS]: Writer failed for {0}: {1}", metaPath, e);
                    }
                }
                Thread.Sleep(m_WriteSleepMs);
            }
        }

        /// <summary>
        /// Count a failed upload. Spool files are kept for retry, and quarantined
        /// rather than deleted once MaxUploadAttempts is reached, so a poison asset
        /// cannot stall the queue and nothing is silently lost.
        /// </summary>
        private void NoteUploadFailure(string assetId, string metaId, string assetPath, string metaPath)
        {
            int attempts;
            m_SpoolFailures.TryGetValue(assetId, out attempts);
            attempts++;
            m_SpoolFailures[assetId] = attempts;

            if (m_MaxUploadAttempts > 0 && attempts >= m_MaxUploadAttempts)
            {
                try
                {
                    string failedDir = Path.Combine(m_SpoolDirectory, "failed");
                    Directory.CreateDirectory(failedDir);

                    MoveOverwrite(assetPath, Path.Combine(failedDir, assetId + ".asset"));
                    MoveOverwrite(metaPath, Path.Combine(failedDir, assetId + ".meta"));

                    m_SpoolFailures.Remove(assetId);

                    m_log.ErrorFormat(
                            "[FSASSETS]: Upload of asset {0} failed {1} times; moved to {2} for manual recovery",
                            metaId, attempts, failedDir);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[FSASSETS]: Could not quarantine spool files for {0}: {1}", metaId, e.Message);
                }
            }
            else
            {
                m_log.WarnFormat(
                        "[FSASSETS]: B2 upload failed for asset {0} (attempt {1}); leaving spool files for retry",
                        metaId, attempts);
            }
        }

        private static void MoveOverwrite(string source, string destination)
        {
            if (File.Exists(destination))
                File.Delete(destination);

            File.Move(source, destination);
        }

        public virtual string Store(AssetBase asset)
        {
            return Store(asset, false);
        }

        /* Store puts the payload and metadata in the spool; the writer thread
           uploads to B2 and records the row */

        private string Store(AssetBase asset, bool force)
        {
            if (asset == null || asset.Data == null)
            {
                m_log.Error("[FSASSETS]: Refusing to store null asset or null asset data");
                return UUID.Zero.ToString();
            }

            // Preserve the original FSAssetService ID handling.
            if (string.IsNullOrEmpty(asset.ID))
            {
                if (asset.FullID.IsZero())
                    asset.FullID = UUID.Random();

                asset.ID = asset.FullID.ToString();
            }
            else if (asset.FullID.IsZero())
            {
                UUID uuid = UUID.Zero;
                if (UUID.TryParse(asset.ID, out uuid))
                {
                    asset.FullID = uuid;
                }
                else
                {
                    asset.FullID = UUID.Random();
                    asset.ID = asset.FullID.ToString();
                }
            }

            if (asset.Name == null)
                asset.Name = string.Empty;

            if (asset.Description == null)
                asset.Description = string.Empty;

            // Preserve the original FSAssetService field length checks.
            if (asset.Name.Length > AssetBase.MAX_ASSET_NAME)
            {
                string assetName = asset.Name.Substring(0, AssetBase.MAX_ASSET_NAME);
                m_log.WarnFormat(
                    "[FSASSETS]: Name for asset {0} truncated from {1} to {2} characters on add",
                    asset.ID, asset.Name.Length, assetName.Length);
                asset.Name = assetName;
            }

            if (asset.Description.Length > AssetBase.MAX_ASSET_DESC)
            {
                string assetDescription = asset.Description.Substring(0, AssetBase.MAX_ASSET_DESC);
                m_log.WarnFormat(
                    "[FSASSETS]: Description for asset {0} truncated from {1} to {2} characters on add",
                    asset.ID, asset.Description.Length, assetDescription.Length);
                asset.Description = assetDescription;
            }

            // Deal with bug introduced in Oct. 20 (1eb3e6cc43e2a7b4053bc1185c7c88e22356c5e8)
            // Fix bad object XML before storing on this server.
            if (asset.Type == (int)AssetType.Object && asset.Data != null)
            {
                string xml = ExternalRepresentationUtils.SanitizeXml(Utils.BytesToString(asset.Data));
                asset.Data = Utils.StringToBytes(xml);
            }

            string hash = GetSHA256Hash(asset.Data);

            string spoolSubDir = Path.Combine(m_SpoolDirectory, "spool");
            Directory.CreateDirectory(spoolSubDir);

            string assetFile = Path.Combine(spoolSubDir, asset.ID + ".asset");
            string metaFile = Path.Combine(spoolSubDir, asset.ID + ".meta");
            string assetTempFile = assetFile + ".tmp";
            string metaTempFile = metaFile + ".tmp";

            if (!File.Exists(assetFile) || !File.Exists(metaFile))
            {
                if (File.Exists(assetTempFile))
                    File.Delete(assetTempFile);
                if (File.Exists(metaTempFile))
                    File.Delete(metaTempFile);

                // Write the payload first, then metadata last. The writer only processes .meta files.
                File.WriteAllBytes(assetTempFile, asset.Data);
                if (File.Exists(assetFile))
                    File.Delete(assetFile);
                File.Move(assetTempFile, assetFile);

                OSDMap metaMap = new OSDMap();
                metaMap["ID"] = asset.FullID;
                metaMap["Name"] = asset.Name;
                metaMap["Description"] = asset.Description;
                metaMap["Type"] = asset.Type;
                metaMap["Flags"] = (int)asset.Metadata.Flags;
                metaMap["Hash"] = hash;
                metaMap["ContentType"] = asset.Metadata.ContentType ?? SLUtil.SLAssetTypeToContentType((int)asset.Type);

                File.WriteAllText(metaTempFile, OSDParser.SerializeJsonString(metaMap));
                if (File.Exists(metaFile))
                    File.Delete(metaFile);
                File.Move(metaTempFile, metaFile);

                m_log.InfoFormat(
                    "[FSASSETS]: Queued asset {0}, type={1}, nameLen={2}, descLen={3}, dataLen={4}, hash={5}",
                    asset.ID,
                    asset.Type,
                    asset.Name.Length,
                    asset.Description.Length,
                    asset.Data.Length,
                    hash);
            }

            return asset.ID;
        }

        public bool UpdateContent(string id, byte[] data)
        {
            return false;
        }

        /// <summary>
        /// Removes the database row only. Objects in the bucket are content-addressed
        /// and shared between every asset with identical bytes, so deleting one here
        /// would break the others. Reclaim orphaned objects with a separate sweep that
        /// compares bucket keys against the hashes still referenced in the DB.
        /// </summary>
        public virtual bool Delete(string id)
        {
            m_DataConnector.Delete(id);

            return true;
        }

        private void HandleShowAssets(string module, string[] args)
        {
            int num = m_DataConnector.Count();
            MainConsole.Instance.Output(string.Format("Total asset count: {0}", num));
        }

        private void HandleShowDigest(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: show digest <ID>");
                return;
            }

            string hash;
            AssetBase asset = Get(args[2], out hash);

            if (asset == null || asset.Data.Length == 0)
            {
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            int i;

            MainConsole.Instance.Output(String.Format("Name: {0}", asset.Name));
            MainConsole.Instance.Output(String.Format("Description: {0}", asset.Description));
            MainConsole.Instance.Output(String.Format("Type: {0}", asset.Type));
            MainConsole.Instance.Output(String.Format("Content-type: {0}", asset.Metadata.ContentType));
            MainConsole.Instance.Output(String.Format("Flags: {0}", asset.Metadata.Flags.ToString()));
            MainConsole.Instance.Output(String.Format("Bucket: {0}", m_S3.Bucket));
            MainConsole.Instance.Output(String.Format("Object key: {0}", HashToKey(hash)));

            for (i = 0 ; i < 5 ; i++)
            {
                int off = i * 16;
                if (asset.Data.Length <= off)
                    break;
                int len = 16;
                if (asset.Data.Length < off + len)
                    len = asset.Data.Length - off;

                byte[] line = new byte[len];
                Array.Copy(asset.Data, off, line, 0, len);

                string text = BitConverter.ToString(line);
                MainConsole.Instance.Output(String.Format("{0:x4}: {1}", off, text));
            }
        }

        private void HandleDeleteAsset(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: delete asset <ID>");
                return;
            }

            AssetBase asset = Get(args[2]);

            if (asset == null || asset.Data.Length == 0)
            {
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            m_DataConnector.Delete(args[2]);

            MainConsole.Instance.Output("Asset deleted");
        }

        private void HandleImportAssets(string module, string[] args)
        {
            bool force = false;
            if (args[0] == "force")
            {
                force = true;
                List<string> list = new List<string>(args);
                list.RemoveAt(0);
                args = list.ToArray();
            }
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: import <conn> <table> [<start> <count>]");
            }
            else
            {
                string conn = args[1];
                string table = args[2];
                int start = 0;
                int count = -1;
                if (args.Length > 3)
                {
                    start = Convert.ToInt32(args[3]);
                }
                if (args.Length > 4)
                {
                    count = Convert.ToInt32(args[4]);
                }
                m_DataConnector.Import(conn, table, start, count, force, new FSStoreDelegate(Store));
            }
        }

        public AssetBase GetCached(string id)
        {
            return Get(id);
        }

        public void Get(string id, string ForeignAssetService, bool StoreOnLocalGrid, SimpleAssetRetrieved callBack)
        {
            return;
        }
    }
}

