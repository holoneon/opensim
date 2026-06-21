using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using StackExchange.Redis;
using Nini.Config;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public static class HoloNonceStore
    {
        private static bool m_initialized = false;

        private static ConnectionMultiplexer m_local;
        private static IDatabase m_db;

        private static int TTLSeconds = 30;
        private static string Prefix = "holo:nonce:";

        private static readonly object m_initLock = new object();

        private static void EnsureInit()
        {
            if (m_initialized)
                return;

            lock (m_initLock)
            {
                if (m_initialized)
                    return;

                m_local = ConnectionMultiplexer.Connect("127.0.0.1:6379");
                m_db = m_local.GetDatabase();

                m_initialized = true;
            }
        }

        public static void Configure(IConfigSource config)
        {
            if (m_initialized)
                return;

            lock (m_initLock)
            {
                if (m_initialized)
                    return;

                var sconfig = config.Configs["HoloNonce"];

                string localHost = "127.0.0.1";
                int localPort = 6379;

                if (sconfig != null)
                {
                    localHost = sconfig.GetString("LocalValkeyHost", "127.0.0.1");
                    localPort = sconfig.GetInt("LocalValkeyPort", 6379);
                    TTLSeconds = sconfig.GetInt("TTLSeconds", 30);
                    Prefix = sconfig.GetString("Prefix", "holo:nonce:");
                }

                m_local = ConnectionMultiplexer.Connect($"{localHost}:{localPort}");
                m_db = m_local.GetDatabase();

                m_initialized = true;
            }
        }

        private static string Key(string nonce)
        {
            return Prefix + nonce;
        }

        public static string IssueNonce(string json)
        {
            EnsureInit();

            // Try a few times in the astronomically unlikely event of collision.
            for (int i = 0; i < 2; i++)
            {
                string nonce = CreateNonce64();
                string key = Key(nonce);

                bool ok = m_db.StringSet(
                    key,
                    json,
                    TimeSpan.FromSeconds(TTLSeconds),
                    When.NotExists
                );

                if (ok)
                    return nonce;
            }

            return null;
        }

        public static string PeekNonce(string nonce)
        {
            EnsureInit();

            if (!IsHex64(nonce))
                return null;

            RedisValue val = m_db.StringGet(Key(nonce));

            if (!val.HasValue)
                return null;

            return val.ToString();
        }

        public static string ConsumeNonce(string nonce)
        {
            EnsureInit();

            if (!IsHex64(nonce))
                return null;

            string key = Key(nonce);

            // Redis 6.2+ / Valkey supports GETDEL.
            try
            {
                RedisResult result = m_db.Execute("GETDEL", key);

                if (result.IsNull)
                    return null;

                return result.ToString();
            }
            catch
            {
                // Fallback for older Redis: Lua atomic GET+DEL.
                const string lua = @"
                    local v = redis.call('GET', KEYS[1])
                    if v then
                        redis.call('DEL', KEYS[1])
                    end
                    return v
                ";

                RedisResult result = m_db.ScriptEvaluate(lua, new RedisKey[] { key });

                if (result.IsNull)
                    return null;

                return result.ToString();
            }
        }

        public static bool DeleteNonce(string nonce)
        {
            EnsureInit();

            if (!IsHex64(nonce))
                return false;

            return m_db.KeyDelete(Key(nonce));
        }

        public static bool IsHex64(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 64)
                return false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                bool ok =
                    (c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F');

                if (!ok)
                    return false;
            }

            return true;
        }

        private static string CreateNonce64()
        {
            byte[] bytes = new byte[32];

            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            StringBuilder sb = new StringBuilder(64);

            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));

            return sb.ToString();
        }
    }
}

