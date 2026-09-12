using System.IO;
using Microsoft.Data.Sqlite;
using VoSharp.Telephony.Calls;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services
{
    public class PreferenceDatabaseService : IPreferenceDatabaseService
    {
        private readonly string _connectionString;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _isInitialized = false;

        public PreferenceDatabaseService(string? customDbPath = null)
        {
            string dbPath;
            if (!string.IsNullOrEmpty(customDbPath))
            {
                var dir = Path.GetDirectoryName(customDbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                dbPath = customDbPath;
            }
            else
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");
                Directory.CreateDirectory(folder);
                dbPath = Path.Combine(folder, "preferences.db");
            }
            _connectionString = $"Data Source={dbPath};Cache=Shared";
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await _lock.WaitAsync();
            try
            {
                if (_isInitialized) return;

                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    CREATE TABLE IF NOT EXISTS ModulePreferences (
                        Id TEXT PRIMARY KEY,
                        Imei TEXT,
                        PortName TEXT,
                        CustomName TEXT,
                        DefaultFlightMode INTEGER NOT NULL DEFAULT 0,
                        DefaultVoWifi INTEGER NOT NULL DEFAULT 0,
                        DefaultCellularData INTEGER NOT NULL DEFAULT 0,
                        DefaultDataRoaming INTEGER NOT NULL DEFAULT 0,
                        DefaultProxyUrl TEXT,
                        BaudRate INTEGER NOT NULL DEFAULT 115200,
                        LastSeenAt TEXT
                    );

                    CREATE TABLE IF NOT EXISTS SimPreferences (
                        Iccid TEXT PRIMARY KEY,
                        Imsi TEXT,
                        CardNickname TEXT,
                        DefaultFlightMode INTEGER NOT NULL DEFAULT 0,
                        DefaultVoWifi INTEGER NOT NULL DEFAULT 0,
                        DefaultCellularData INTEGER NOT NULL DEFAULT 0,
                        DefaultDataRoaming INTEGER NOT NULL DEFAULT 0,
                        DedicatedProxyUrl TEXT,
                        CustomEpdg TEXT,
                        LastSeenAt TEXT
                    );

                    CREATE TABLE IF NOT EXISTS CallRecords (
                        Id TEXT PRIMARY KEY,
                        PhoneNumber TEXT NOT NULL,
                        DisplayName TEXT,
                        Direction INTEGER NOT NULL,
                        FinalState INTEGER NOT NULL,
                        Timestamp TEXT NOT NULL,
                        DurationSeconds REAL NOT NULL,
                        Codec TEXT,
                        SlotId TEXT,
                        WavRecordingPath TEXT
                    );
                    CREATE INDEX IF NOT EXISTS IX_CallRecords_Timestamp ON CallRecords(Timestamp DESC);
                    CREATE INDEX IF NOT EXISTS IX_CallRecords_PhoneNumber ON CallRecords(PhoneNumber);

                    CREATE TABLE IF NOT EXISTS SmsMessages (
                        Id TEXT PRIMARY KEY,
                        RemoteNumber TEXT NOT NULL,
                        Text TEXT NOT NULL,
                        Timestamp TEXT NOT NULL,
                        IsOutgoing INTEGER NOT NULL,
                        DeliveryState INTEGER NOT NULL,
                        DeliveryStatus TEXT,
                        MessageReference INTEGER,
                        SlotId TEXT,
                        RawPdu TEXT
                    );
                    CREATE INDEX IF NOT EXISTS IX_SmsMessages_RemoteNumber ON SmsMessages(RemoteNumber);
                    CREATE INDEX IF NOT EXISTS IX_SmsMessages_Timestamp ON SmsMessages(Timestamp ASC);

                    CREATE TABLE IF NOT EXISTS ProxyNodes (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Protocol TEXT NOT NULL,
                        Host TEXT NOT NULL,
                        Port INTEGER NOT NULL,
                        Username TEXT,
                        Password TEXT,
                        LatencyMs INTEGER,
                        IsAvailable INTEGER NOT NULL DEFAULT 1,
                        SortOrder INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS CountryRoutes (
                        CountryCode TEXT PRIMARY KEY,
                        CountryName TEXT NOT NULL,
                        FlagEmoji TEXT,
                        MccList TEXT NOT NULL,
                        ProxyNodeId TEXT,
                        ProxyNodeName TEXT
                    );
                ";

                using var cmd = new SqliteCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                // Safe schema migration for existing databases
                try
                {
                    using var alterCmd1 = new SqliteCommand("ALTER TABLE ModulePreferences ADD COLUMN DefaultCellularData INTEGER NOT NULL DEFAULT 1;", conn);
                    await alterCmd1.ExecuteNonQueryAsync();
                }
                catch { }

                try
                {
                    using var alterCmd2 = new SqliteCommand("ALTER TABLE SimPreferences ADD COLUMN DefaultCellularData INTEGER NOT NULL DEFAULT 0;", conn);
                    await alterCmd2.ExecuteNonQueryAsync();
                }
                catch { }

                await NormalizeStoredTimestampsAsync(conn);

                _isInitialized = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static async Task NormalizeStoredTimestampsAsync(SqliteConnection conn)
        {
            var smsCorrections = new List<(string Id, DateTime TimestampUtc)>();
            var receivedAtUtc = DateTime.UtcNow;

            using (var select = new SqliteCommand(
                "SELECT Id, Timestamp, IsOutgoing FROM SmsMessages;", conn))
            using (var reader = await select.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var storedText = reader.GetString(1);
                    if (!TimestampDisplayHelper.TryParseStoredUtc(storedText, out var storedUtc))
                    {
                        continue;
                    }

                    var normalized = reader.GetInt32(2) == 0
                        ? TimestampDisplayHelper.NormalizeIncomingNetworkTime(storedUtc, receivedAtUtc)
                        : storedUtc;
                    var normalizedText = normalized.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(storedText, normalizedText, StringComparison.Ordinal))
                    {
                        smsCorrections.Add((reader.GetString(0), normalized));
                    }
                }
            }

            foreach (var (id, timestampUtc) in smsCorrections)
            {
                using var update = new SqliteCommand(
                    "UPDATE SmsMessages SET Timestamp = @time WHERE Id = @id;", conn);
                update.Parameters.AddWithValue("@time", timestampUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync();
            }

            var callCorrections = new List<(string Id, DateTime TimestampUtc)>();
            using (var select = new SqliteCommand("SELECT Id, Timestamp FROM CallRecords;", conn))
            using (var reader = await select.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var storedText = reader.GetString(1);
                    if (!TimestampDisplayHelper.TryParseStoredUtc(storedText, out var timestampUtc))
                    {
                        continue;
                    }

                    var normalizedText = timestampUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(storedText, normalizedText, StringComparison.Ordinal))
                    {
                        callCorrections.Add((reader.GetString(0), timestampUtc));
                    }
                }
            }

            foreach (var (id, timestampUtc) in callCorrections)
            {
                using var update = new SqliteCommand(
                    "UPDATE CallRecords SET Timestamp = @time WHERE Id = @id;", conn);
                update.Parameters.AddWithValue("@time", timestampUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync();
            }
        }

        public async Task<ModulePreferenceModel?> GetModulePreferenceAsync(string identifier, string? imei = null)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    SELECT Id, Imei, PortName, CustomName, DefaultFlightMode, DefaultVoWifi, DefaultCellularData, DefaultDataRoaming, DefaultProxyUrl, BaudRate, LastSeenAt
                    FROM ModulePreferences
                    WHERE Id = @id OR (@imei IS NOT NULL AND Imei = @imei) OR PortName = @id
                    ORDER BY CASE
                        WHEN Id = @id THEN 0
                        WHEN @imei IS NOT NULL AND Imei = @imei THEN 1
                        ELSE 2
                    END, LastSeenAt DESC
                    LIMIT 1;
                ";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", identifier);
                cmd.Parameters.AddWithValue("@imei", (object?)imei ?? DBNull.Value);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new ModulePreferenceModel
                    {
                        Id = reader.GetString(0),
                        Imei = reader.IsDBNull(1) ? null : reader.GetString(1),
                        PortName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        CustomName = reader.IsDBNull(3) ? null : reader.GetString(3),
                        DefaultFlightMode = reader.GetInt32(4) == 1,
                        DefaultVoWifi = reader.GetInt32(5) == 1,
                        DefaultCellularData = reader.GetInt32(6) == 1,
                        DefaultDataRoaming = reader.GetInt32(7) == 1,
                        DefaultProxyUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
                        BaudRate = reader.GetInt32(9),
                        LastSeenAt = DateTime.TryParse(reader.IsDBNull(10) ? "" : reader.GetString(10), out var dt) ? dt : DateTime.Now
                    };
                }
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveModulePreferenceAsync(ModulePreferenceModel model)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    INSERT INTO ModulePreferences (Id, Imei, PortName, CustomName, DefaultFlightMode, DefaultVoWifi, DefaultCellularData, DefaultDataRoaming, DefaultProxyUrl, BaudRate, LastSeenAt)
                    VALUES (@id, @imei, @portName, @customName, @flight, @vowifi, @cellular, @roaming, @proxy, @baud, @lastSeen)
                    ON CONFLICT(Id) DO UPDATE SET
                        Imei = excluded.Imei,
                        PortName = excluded.PortName,
                        CustomName = excluded.CustomName,
                        DefaultFlightMode = excluded.DefaultFlightMode,
                        DefaultVoWifi = excluded.DefaultVoWifi,
                        DefaultCellularData = excluded.DefaultCellularData,
                        DefaultDataRoaming = excluded.DefaultDataRoaming,
                        DefaultProxyUrl = excluded.DefaultProxyUrl,
                        BaudRate = excluded.BaudRate,
                        LastSeenAt = excluded.LastSeenAt;
                ";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", model.Id);
                cmd.Parameters.AddWithValue("@imei", (object?)model.Imei ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@portName", (object?)model.PortName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@customName", (object?)model.CustomName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@flight", model.DefaultFlightMode ? 1 : 0);
                cmd.Parameters.AddWithValue("@vowifi", model.DefaultVoWifi ? 1 : 0);
                cmd.Parameters.AddWithValue("@cellular", model.DefaultCellularData ? 1 : 0);
                cmd.Parameters.AddWithValue("@roaming", model.DefaultDataRoaming ? 1 : 0);
                cmd.Parameters.AddWithValue("@proxy", (object?)model.DefaultProxyUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@baud", model.BaudRate);
                cmd.Parameters.AddWithValue("@lastSeen", DateTime.Now.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<ModulePreferenceModel>> GetAllModulePreferencesAsync()
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<ModulePreferenceModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT Id, Imei, PortName, CustomName, DefaultFlightMode, DefaultVoWifi, DefaultCellularData, DefaultDataRoaming, DefaultProxyUrl, BaudRate, LastSeenAt FROM ModulePreferences;";
                using var cmd = new SqliteCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new ModulePreferenceModel
                    {
                        Id = reader.GetString(0),
                        Imei = reader.IsDBNull(1) ? null : reader.GetString(1),
                        PortName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        CustomName = reader.IsDBNull(3) ? null : reader.GetString(3),
                        DefaultFlightMode = reader.GetInt32(4) == 1,
                        DefaultVoWifi = reader.GetInt32(5) == 1,
                        DefaultCellularData = reader.GetInt32(6) == 1,
                        DefaultDataRoaming = reader.GetInt32(7) == 1,
                        DefaultProxyUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
                        BaudRate = reader.GetInt32(9),
                        LastSeenAt = DateTime.TryParse(reader.IsDBNull(10) ? "" : reader.GetString(10), out var dt) ? dt : DateTime.Now
                    });
                }
                return list;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<SimPreferenceModel?> GetSimPreferenceAsync(string iccid)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    SELECT Iccid, Imsi, CardNickname, DefaultFlightMode, DefaultVoWifi, DefaultCellularData, DefaultDataRoaming, DedicatedProxyUrl, CustomEpdg, LastSeenAt
                    FROM SimPreferences
                    WHERE Iccid = @iccid
                    LIMIT 1;
                ";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@iccid", iccid);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new SimPreferenceModel
                    {
                        Iccid = reader.GetString(0),
                        Imsi = reader.IsDBNull(1) ? null : reader.GetString(1),
                        CardNickname = reader.IsDBNull(2) ? null : reader.GetString(2),
                        DefaultFlightMode = reader.GetInt32(3) == 1,
                        DefaultVoWifi = reader.GetInt32(4) == 1,
                        DefaultCellularData = reader.GetInt32(5) == 1,
                        DefaultDataRoaming = reader.GetInt32(6) == 1,
                        DedicatedProxyUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                        CustomEpdg = reader.IsDBNull(8) ? null : reader.GetString(8),
                        LastSeenAt = DateTime.TryParse(reader.IsDBNull(9) ? "" : reader.GetString(9), out var dt) ? dt : DateTime.Now
                    };
                }
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveSimPreferenceAsync(SimPreferenceModel model)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    INSERT INTO SimPreferences (Iccid, Imsi, CardNickname, DefaultFlightMode, DefaultVoWifi, DefaultCellularData, DefaultDataRoaming, DedicatedProxyUrl, CustomEpdg, LastSeenAt)
                    VALUES (@iccid, @imsi, @nickname, @flight, @vowifi, @cellular, @roaming, @proxy, @epdg, @lastSeen)
                    ON CONFLICT(Iccid) DO UPDATE SET
                        Imsi = excluded.Imsi,
                        CardNickname = excluded.CardNickname,
                        DefaultFlightMode = excluded.DefaultFlightMode,
                        DefaultVoWifi = excluded.DefaultVoWifi,
                        DefaultCellularData = excluded.DefaultCellularData,
                        DefaultDataRoaming = excluded.DefaultDataRoaming,
                        DedicatedProxyUrl = excluded.DedicatedProxyUrl,
                        CustomEpdg = excluded.CustomEpdg,
                        LastSeenAt = excluded.LastSeenAt;
                ";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@iccid", model.Iccid);
                cmd.Parameters.AddWithValue("@imsi", (object?)model.Imsi ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@nickname", (object?)model.CardNickname ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@flight", model.DefaultFlightMode ? 1 : 0);
                cmd.Parameters.AddWithValue("@vowifi", model.DefaultVoWifi ? 1 : 0);
                cmd.Parameters.AddWithValue("@cellular", model.DefaultCellularData ? 1 : 0);
                cmd.Parameters.AddWithValue("@roaming", model.DefaultDataRoaming ? 1 : 0);
                cmd.Parameters.AddWithValue("@proxy", (object?)model.DedicatedProxyUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@epdg", (object?)model.CustomEpdg ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@lastSeen", DateTime.Now.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<SimPreferenceModel>> GetAllSimPreferencesAsync()
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<SimPreferenceModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT Iccid, Imsi, CardNickname, DefaultFlightMode, DefaultVoWifi, DefaultCellularData, DefaultDataRoaming, DedicatedProxyUrl, CustomEpdg, LastSeenAt FROM SimPreferences;";
                using var cmd = new SqliteCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new SimPreferenceModel
                    {
                        Iccid = reader.GetString(0),
                        Imsi = reader.IsDBNull(1) ? null : reader.GetString(1),
                        CardNickname = reader.IsDBNull(2) ? null : reader.GetString(2),
                        DefaultFlightMode = reader.GetInt32(3) == 1,
                        DefaultVoWifi = reader.GetInt32(4) == 1,
                        DefaultCellularData = reader.GetInt32(5) == 1,
                        DefaultDataRoaming = reader.GetInt32(6) == 1,
                        DedicatedProxyUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                        CustomEpdg = reader.IsDBNull(8) ? null : reader.GetString(8),
                        LastSeenAt = DateTime.TryParse(reader.IsDBNull(9) ? "" : reader.GetString(9), out var dt) ? dt : DateTime.Now
                    });
                }
                return list;
            }
            finally
            {
                _lock.Release();
            }
        }

        // ─── Call Records ────────────────────────────────────────────────────────────

        public async Task SaveCallRecordAsync(CallRecordModel record)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    INSERT INTO CallRecords (Id, PhoneNumber, DisplayName, Direction, FinalState, Timestamp, DurationSeconds, Codec, SlotId, WavRecordingPath)
                    VALUES (@id, @phone, @name, @dir, @state, @time, @duration, @codec, @slot, @wav)
                    ON CONFLICT(Id) DO UPDATE SET
                        PhoneNumber = excluded.PhoneNumber,
                        DisplayName = excluded.DisplayName,
                        Direction = excluded.Direction,
                        FinalState = excluded.FinalState,
                        Timestamp = excluded.Timestamp,
                        DurationSeconds = excluded.DurationSeconds,
                        Codec = excluded.Codec,
                        SlotId = excluded.SlotId,
                        WavRecordingPath = excluded.WavRecordingPath;
                ";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", record.Id);
                cmd.Parameters.AddWithValue("@phone", record.PhoneNumber);
                cmd.Parameters.AddWithValue("@name", (object?)record.DisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@dir", (int)record.Direction);
                cmd.Parameters.AddWithValue("@state", (int)record.FinalState);
                cmd.Parameters.AddWithValue("@time", TimestampDisplayHelper.ToUtcStorageTime(record.Timestamp).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@duration", record.Duration.TotalSeconds);
                cmd.Parameters.AddWithValue("@codec", (object?)record.Codec ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@slot", (object?)record.SlotId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@wav", (object?)record.WavRecordingPath ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<CallRecordModel>> GetCallRecordsAsync(int limit = 200)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<CallRecordModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT Id, PhoneNumber, DisplayName, Direction, FinalState, Timestamp, DurationSeconds, Codec, SlotId, WavRecordingPath FROM CallRecords ORDER BY Timestamp DESC LIMIT @limit;";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@limit", limit);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new CallRecordModel
                    {
                        Id = reader.GetString(0),
                        PhoneNumber = reader.GetString(1),
                        DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Direction = (CallDirection)reader.GetInt32(3),
                        FinalState = (CallState)reader.GetInt32(4),
                        Timestamp = TimestampDisplayHelper.TryParseStoredUtc(reader.GetString(5), out var dt) ? dt : DateTime.UtcNow,
                        Duration = TimeSpan.FromSeconds(reader.GetDouble(6)),
                        Codec = reader.IsDBNull(7) ? null : reader.GetString(7),
                        SlotId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        WavRecordingPath = reader.IsDBNull(9) ? null : reader.GetString(9)
                    });
                }
                return list;
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteCallRecordAsync(string id)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM CallRecords WHERE Id = @id;", conn);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task ClearCallRecordsAsync()
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM CallRecords;", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        // ─── SMS Messages ────────────────────────────────────────────────────────────

        public async Task SaveSmsMessageAsync(SmsMessageModel message)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    INSERT INTO SmsMessages (Id, RemoteNumber, Text, Timestamp, IsOutgoing, DeliveryState, DeliveryStatus, MessageReference, SlotId, RawPdu)
                    VALUES (@id, @remote, @text, @time, @outgoing, @state, @status, @ref, @slot, @pdu)
                    ON CONFLICT(Id) DO UPDATE SET
                        RemoteNumber = excluded.RemoteNumber,
                        Text = excluded.Text,
                        Timestamp = excluded.Timestamp,
                        IsOutgoing = excluded.IsOutgoing,
                        DeliveryState = excluded.DeliveryState,
                        DeliveryStatus = excluded.DeliveryStatus,
                        MessageReference = excluded.MessageReference,
                        SlotId = excluded.SlotId,
                        RawPdu = excluded.RawPdu;
                ";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", message.Id);
                cmd.Parameters.AddWithValue("@remote", message.SenderOrRecipient);
                cmd.Parameters.AddWithValue("@text", message.Text);
                cmd.Parameters.AddWithValue("@time", TimestampDisplayHelper.ToUtcStorageTime(message.Timestamp).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@outgoing", message.IsOutgoing ? 1 : 0);
                cmd.Parameters.AddWithValue("@state", (int)message.DeliveryState);
                cmd.Parameters.AddWithValue("@status", (object?)message.DeliveryStatus ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ref", (object?)message.MessageReference ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@slot", (object?)message.SlotId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pdu", (object?)message.RawPdu ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<SmsMessageModel>> GetAllSmsMessagesAsync()
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<SmsMessageModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT Id, RemoteNumber, Text, Timestamp, IsOutgoing, DeliveryState, DeliveryStatus, MessageReference, SlotId, RawPdu FROM SmsMessages ORDER BY Timestamp ASC;";
                using var cmd = new SqliteCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new SmsMessageModel
                    {
                        Id = reader.GetString(0),
                        SenderOrRecipient = reader.GetString(1),
                        Text = reader.GetString(2),
                        Timestamp = TimestampDisplayHelper.TryParseStoredUtc(reader.GetString(3), out var dt) ? dt : DateTime.UtcNow,
                        IsOutgoing = reader.GetInt32(4) == 1,
                        DeliveryState = (SmsDeliveryState)reader.GetInt32(5),
                        DeliveryStatus = reader.IsDBNull(6) ? null : reader.GetString(6),
                        MessageReference = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        SlotId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        RawPdu = reader.IsDBNull(9) ? null : reader.GetString(9)
                    });
                }
                return list;
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<SmsMessageModel>> GetMessagesByNumberAsync(string remoteNumber)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<SmsMessageModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT Id, RemoteNumber, Text, Timestamp, IsOutgoing, DeliveryState, DeliveryStatus, MessageReference, SlotId, RawPdu FROM SmsMessages WHERE RemoteNumber = @num ORDER BY Timestamp ASC;";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@num", remoteNumber);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new SmsMessageModel
                    {
                        Id = reader.GetString(0),
                        SenderOrRecipient = reader.GetString(1),
                        Text = reader.GetString(2),
                        Timestamp = TimestampDisplayHelper.TryParseStoredUtc(reader.GetString(3), out var dt) ? dt : DateTime.UtcNow,
                        IsOutgoing = reader.GetInt32(4) == 1,
                        DeliveryState = (SmsDeliveryState)reader.GetInt32(5),
                        DeliveryStatus = reader.IsDBNull(6) ? null : reader.GetString(6),
                        MessageReference = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        SlotId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        RawPdu = reader.IsDBNull(9) ? null : reader.GetString(9)
                    });
                }
                return list;
            }
            finally { _lock.Release(); }
        }

        public async Task UpdateSmsDeliveryStatusAsync(int messageReference, string recipient, SmsDeliveryState state, string? status)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    UPDATE SmsMessages
                    SET DeliveryState = @state, DeliveryStatus = @status
                    WHERE MessageReference = @ref AND RemoteNumber = @num AND IsOutgoing = 1;
                ";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@state", (int)state);
                cmd.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ref", messageReference);
                cmd.Parameters.AddWithValue("@num", recipient);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task UpdateSmsDeliveryStatusAsync(string messageId, SmsDeliveryState state, string? status)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    UPDATE SmsMessages
                    SET DeliveryState = @state, DeliveryStatus = @status
                    WHERE Id = @id;
                ";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@state", (int)state);
                cmd.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", messageId);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteSmsMessageAsync(string id)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM SmsMessages WHERE Id = @id;", conn);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteConversationAsync(string remoteNumber)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM SmsMessages WHERE RemoteNumber = @num;", conn);
                cmd.Parameters.AddWithValue("@num", remoteNumber);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> HasSmsMessageAsync(string senderOrRecipient, DateTime timestamp, string text, string? rawPdu = null)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                if (!string.IsNullOrEmpty(rawPdu))
                {
                    using var pduCmd = new SqliteCommand("SELECT COUNT(1) FROM SmsMessages WHERE RawPdu = @pdu;", conn);
                    pduCmd.Parameters.AddWithValue("@pdu", rawPdu);
                    var pduCount = Convert.ToInt64(await pduCmd.ExecuteScalarAsync());
                    if (pduCount > 0) return true;
                }

                var cleanNumber = senderOrRecipient.Trim();
                using var cmd = new SqliteCommand("SELECT Timestamp FROM SmsMessages WHERE RemoteNumber = @num AND Text = @text;", conn);
                cmd.Parameters.AddWithValue("@num", cleanNumber);
                cmd.Parameters.AddWithValue("@text", text);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (TimestampDisplayHelper.TryParseStoredUtc(reader.GetString(0), out var dbTime))
                    {
                        if (Math.Abs((dbTime - TimestampDisplayHelper.ToUtcStorageTime(timestamp)).TotalSeconds) <= 60)
                            return true;
                    }
                    else
                    {
                        return true;
                    }
                }
                return false;
            }
            finally { _lock.Release(); }
        }

        // ─── Proxy Nodes & Country Routes ────────────────────────────────────────────

        public async Task SaveProxyNodeAsync(ProxyNodeModel node)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    INSERT INTO ProxyNodes (Id, Name, Protocol, Host, Port, Username, Password, LatencyMs, IsAvailable, SortOrder)
                    VALUES (@id, @name, @proto, @host, @port, @user, @pass, @latency, @avail, @order)
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        Protocol = excluded.Protocol,
                        Host = excluded.Host,
                        Port = excluded.Port,
                        Username = excluded.Username,
                        Password = excluded.Password,
                        LatencyMs = excluded.LatencyMs,
                        IsAvailable = excluded.IsAvailable,
                        SortOrder = excluded.SortOrder;
                ";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", node.Id);
                cmd.Parameters.AddWithValue("@name", node.Name);
                cmd.Parameters.AddWithValue("@proto", node.Protocol);
                cmd.Parameters.AddWithValue("@host", node.Host);
                cmd.Parameters.AddWithValue("@port", node.Port);
                cmd.Parameters.AddWithValue("@user", (object?)node.Username ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pass", (object?)node.Password ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@latency", (object?)node.LatencyMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@avail", node.IsEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("@order", 0);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<ProxyNodeModel>> GetAllProxyNodesAsync()
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<ProxyNodeModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT Id, Name, Protocol, Host, Port, Username, Password, LatencyMs, IsAvailable FROM ProxyNodes ORDER BY SortOrder ASC;";
                using var cmd = new SqliteCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new ProxyNodeModel
                    {
                        Id = reader.GetString(0),
                        Name = reader.GetString(1),
                        Protocol = reader.GetString(2),
                        Host = reader.GetString(3),
                        Port = reader.GetInt32(4),
                        Username = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Password = reader.IsDBNull(6) ? null : reader.GetString(6),
                        LatencyMs = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        IsEnabled = reader.GetInt32(8) == 1
                    });
                }
                return list;
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteProxyNodeAsync(string id)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM ProxyNodes WHERE Id = @id;", conn);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task SaveCountryRouteAsync(CountryRouteModel route)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = @"
                    INSERT INTO CountryRoutes (CountryCode, CountryName, FlagEmoji, MccList, ProxyNodeId, ProxyNodeName)
                    VALUES (@code, @name, @flag, @mcc, @nodeId, @nodeName)
                    ON CONFLICT(CountryCode) DO UPDATE SET
                        CountryName = excluded.CountryName,
                        FlagEmoji = excluded.FlagEmoji,
                        MccList = excluded.MccList,
                        ProxyNodeId = excluded.ProxyNodeId,
                        ProxyNodeName = excluded.ProxyNodeName;
                ";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@code", route.CountryCode);
                cmd.Parameters.AddWithValue("@name", route.CountryName);
                cmd.Parameters.AddWithValue("@flag", (object?)route.FlagEmoji ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@mcc", route.MccList);
                cmd.Parameters.AddWithValue("@nodeId", (object?)route.ProxyNodeId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@nodeName", (object?)route.ProxyNodeName ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }

        public async Task<IReadOnlyList<CountryRouteModel>> GetAllCountryRoutesAsync()
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            var list = new List<CountryRouteModel>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var sql = "SELECT CountryCode, CountryName, FlagEmoji, MccList, ProxyNodeId, ProxyNodeName FROM CountryRoutes;";
                using var cmd = new SqliteCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new CountryRouteModel
                    {
                        CountryCode = reader.GetString(0),
                        CountryName = reader.GetString(1),
                        FlagEmoji = reader.IsDBNull(2) ? "🌐" : reader.GetString(2),
                        MccList = reader.GetString(3),
                        ProxyNodeId = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ProxyNodeName = reader.IsDBNull(5) ? "直连模式 (Direct)" : reader.GetString(5)
                    });
                }
                return list;
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteCountryRouteAsync(string countryCode)
        {
            await InitializeAsync();
            await _lock.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM CountryRoutes WHERE CountryCode = @code;", conn);
                cmd.Parameters.AddWithValue("@code", countryCode);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
        }
    }
}
